using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PdfTools.Core.ILoveApi;

/// <summary>
/// Minimal client for the iLoveAPI REST workflow: auth -> start -> upload -> process -> download (-> delete).
/// Authenticates with the project PUBLIC key through /auth (the method iLoveAPI recommends for client-side
/// code), so the secret key never has to be installed on a PC.
/// </summary>
public sealed class ILoveApiClient : IDisposable
{
    public static readonly Uri DefaultApiBase = new("https://api.ilovepdf.com/v1/");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _publicKey;
    private readonly string _region;
    private readonly Uri _apiBase;
    private readonly Func<string, Uri> _serverBase;

    private string? _token;
    private DateTimeOffset _tokenExpires;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>Credits left, as reported by the last /start call (null if unknown).</summary>
    public int? RemainingCredits { get; private set; }

    public ILoveApiClient(string publicKey, string? region = null, HttpClient? http = null,
                          Uri? apiBase = null, Func<string, Uri>? serverBase = null)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) throw new ArgumentException("API key is required.", nameof(publicKey));
        _publicKey = publicKey.Trim();
        _region = (region ?? "").Trim().ToLowerInvariant();
        _apiBase = apiBase ?? DefaultApiBase;
        _serverBase = serverBase ?? (s => new Uri($"https://{s}/v1/"));
        if (http == null)
        {
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // per-call cancellation instead
            _ownsHttp = true;
        }
        else _http = http;
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    // ------------------------------------------------------------------ auth

    /// <summary>Requests a token. Throws <see cref="ApiException"/> (Auth) if the key is rejected.</summary>
    public async Task ValidateKeyAsync(CancellationToken ct = default) => await GetTokenAsync(ct, force: true);

    private async Task<string> GetTokenAsync(CancellationToken ct, bool force = false)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _token != null && DateTimeOffset.UtcNow < _tokenExpires) return _token;

            var uri = new Uri(_apiBase, "auth");
            // Form-encoded first (what the official libraries send); JSON as fallback.
            using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("public_key", _publicKey) })
            }, ct, authorize: false);

            HttpResponseMessage final = res;
            HttpResponseMessage? second = null;
            if (res.StatusCode is HttpStatusCode.UnsupportedMediaType or HttpStatusCode.BadRequest)
            {
                second = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { public_key = _publicKey }), Encoding.UTF8, "application/json")
                }, ct, authorize: false);
                final = second;
            }

            try
            {
                var json = await ReadJsonAsync(final, ct, authContext: true);
                var token = json.TryGetProperty("token", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(token))
                    throw new ApiException(ApiErrorKind.Auth, "iLoveAPI did not return a token. Check your API key.");
                _token = token;
                _tokenExpires = DateTimeOffset.UtcNow.AddMinutes(50);   // tokens live 1 hour
                return token;
            }
            finally { second?.Dispose(); }
        }
        finally { _tokenLock.Release(); }
    }

    // ------------------------------------------------------------------ workflow

    public async Task<ApiTask> StartAsync(string tool, CancellationToken ct = default)
    {
        var path = _region.Length > 0 ? $"start/{tool}/{_region}" : $"start/{tool}";
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, path)), ct);
        var json = await ReadJsonAsync(res, ct);
        var server = json.TryGetProperty("server", out var s) ? s.GetString() : null;
        var task = json.TryGetProperty("task", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(task))
            throw new ApiException(ApiErrorKind.Rejected, "iLoveAPI returned an unexpected response when starting the task.");
        if (json.TryGetProperty("remaining_credits", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.TryGetInt32(out var credits))
            RemainingCredits = credits;
        return new ApiTask(server!, task!);
    }

    /// <summary>Uploads one local file; returns the server-side file name to use in <see cref="ProcessAsync"/>.</summary>
    public async Task<string> UploadAsync(ApiTask task, string localPath, CancellationToken ct = default)
    {
        var uri = new Uri(_serverBase(task.Server), "upload");
        // The visible name is sent in /process; keep the multipart name ASCII-safe.
        var safeName = "upload" + Path.GetExtension(localPath);

        using var res = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(task.Task), "task");
            var file = new StreamContent(new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", safeName);
            return new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
        }, ct);

        var json = await ReadJsonAsync(res, ct);
        var name = json.TryGetProperty("server_filename", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(name))
            throw new ApiException(ApiErrorKind.Rejected, "iLoveAPI did not accept the uploaded file.");
        return name!;
    }

    public async Task<ProcessResult> ProcessAsync(ApiTask task, string tool, IReadOnlyList<UploadedFile> files,
                                                  IDictionary<string, object?>? extra = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["task"] = task.Task,
            ["tool"] = tool,
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["server_filename"] = f.ServerFilename,
                ["filename"] = f.Filename
            }).ToList(),
            ["ignore_errors"] = false      // fail loudly instead of silently dropping a damaged file
        };
        if (extra != null) foreach (var kv in extra) body[kv.Key] = kv.Value;

        var uri = new Uri(_serverBase(task.Server), "process");
        var payload = JsonSerializer.Serialize(body);
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        }, ct);

        var json = await ReadJsonAsync(res, ct);
        var status = json.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        if (!status.StartsWith("TaskSuccess", StringComparison.Ordinal))
            throw new ApiException(ApiErrorKind.Rejected, $"iLoveAPI could not process the file (status: {(status.Length == 0 ? "unknown" : status)}).");

        return new ProcessResult(
            status,
            json.TryGetProperty("download_filename", out var d) ? d.GetString() : null,
            GetLong(json, "filesize"),
            GetLong(json, "output_filesize"),
            (int)GetLong(json, "output_filenumber"));
    }

    /// <summary>Downloads the task output (a single file, or a ZIP when there is more than one) to <paramref name="destination"/>.</summary>
    public async Task DownloadAsync(ApiTask task, string destination, CancellationToken ct = default)
    {
        var uri = new Uri(_serverBase(task.Server), $"download/{task.Task}");
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        if (!res.IsSuccessStatusCode)
            throw ToException((int)res.StatusCode, await SafeReadAsync(res, ct));

        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await src.CopyToAsync(dst, 81920, ct);
    }

    /// <summary>Best-effort cleanup of the task and its files on the server. Never throws.</summary>
    public async Task DeleteTaskAsync(ApiTask task)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var uri = new Uri(_serverBase(task.Server), $"task/{task.Task}");
            using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, uri), cts.Token);
        }
        catch { /* server deletes tasks after 2 hours anyway */ }
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool authorize = true)
    {
        bool refreshed = false;
        for (int attempt = 1; ; attempt++)
        {
            using var req = make();
            if (authorize)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));

            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(attempt), ct); continue; }
                throw new ApiException(ApiErrorKind.Network, "Cannot reach iLoveAPI. Check your internet connection and try again.", null, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt < 3) continue;
                throw new ApiException(ApiErrorKind.Network, "The request to iLoveAPI timed out.", null, ex);
            }

            if (res.StatusCode == HttpStatusCode.Unauthorized && authorize && !refreshed)
            {
                refreshed = true;
                res.Dispose();
                await GetTokenAsync(ct, force: true);
                continue;
            }
            if ((res.StatusCode == (HttpStatusCode)429 || (int)res.StatusCode >= 500) && attempt < 3)
            {
                res.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);   // simple back-off
                continue;
            }
            return res;
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res, CancellationToken ct, bool authContext = false)
    {
        var body = await SafeReadAsync(res, ct);
        if (!res.IsSuccessStatusCode)
        {
            if (authContext && (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden))
                throw new ApiException(ApiErrorKind.Auth, "iLoveAPI rejected the API key. Check that you copied the project PUBLIC key.", (int)res.StatusCode);
            throw ToException((int)res.StatusCode, body);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ApiException(ApiErrorKind.Server, "iLoveAPI returned an unreadable response.", (int)res.StatusCode, ex);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadAsStringAsync(ct); }
        catch (HttpRequestException) { return ""; }
    }

    private static long GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    /// <summary>Maps an error response to a friendly <see cref="ApiException"/>.</summary>
    internal static ApiException ToException(int status, string body)
    {
        string msg = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        msg = m.GetString() ?? "";
                    else if (e.ValueKind == JsonValueKind.String) msg = e.GetString() ?? "";
                }
                if (msg.Length == 0 && root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
                    msg = m2.GetString() ?? "";
            }
        }
        catch (JsonException) { }

        var lower = body.ToLowerInvariant();
        if (lower.Contains("wrongpassword"))
            return new ApiException(ApiErrorKind.Password, "The PDF is password-protected. Remove the password first.", status);
        if (status == 401)
            return new ApiException(ApiErrorKind.Auth, "iLoveAPI rejected the API key. Open iLovePDF Tools Settings and check it.", status);
        if ((status is 400 or 402 or 403) && (lower.Contains("credit") || lower.Contains("quota") || lower.Contains("limit reached")))
            return new ApiException(ApiErrorKind.Credits, "Your iLoveAPI credits are used up for this period. Check your plan on iloveapi.com.", status);
        if (lower.Contains("damagedfile"))
            return new ApiException(ApiErrorKind.Rejected, "The file looks damaged or cannot be read.", status);
        if (status >= 500)
            return new ApiException(ApiErrorKind.Server, "iLoveAPI had a server problem. Try again in a moment.", status);

        if (msg.Length == 0) msg = body.Length > 200 ? body[..200] : body;
        if (msg.Length == 0) msg = "no details";
        return new ApiException(ApiErrorKind.Rejected, $"iLoveAPI error {status}: {msg}", status);
    }
}
