using System.Net;
using System.Text;
using System.Text.Json;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;
using Xunit;

namespace PdfTools.Core.Tests;

/// <summary>In-memory stand-in for iLoveAPI that records every request.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Url, string Body, string? Auth)> Requests { get; } = new();
    public int AuthStatus = 200;
    public int ProcessStatus = 200;
    public string ProcessBody = "{\"download_filename\":\"out.pdf\",\"filesize\":1000,\"output_filesize\":400,\"output_filenumber\":1,\"output_extensions\":\"[\\\"pdf\\\"]\",\"timer\":\"0.1\",\"status\":\"TaskSuccess\"}";
    public byte[] Output = Encoding.ASCII.GetBytes("%PDF-1.4 small");
    private int _uploads;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = req.Content == null ? "" : await ReadBody(req.Content);
        lock (Requests) Requests.Add((req.Method, req.RequestUri!, body, req.Headers.Authorization?.ToString()));
        var path = req.RequestUri!.AbsolutePath;

        if (path.EndsWith("/auth"))
            return AuthStatus == 200 ? Json(200, "{\"token\":\"tok123\"}") : Json(AuthStatus, "{\"error\":{\"message\":\"bad key\"}}");
        if (path.Contains("/start/"))
            return Json(200, "{\"server\":\"api1.test\",\"task\":\"TASK1\",\"remaining_credits\":42}");
        if (path.EndsWith("/upload"))
            return Json(200, $"{{\"server_filename\":\"srv{Interlocked.Increment(ref _uploads)}.pdf\"}}");
        if (path.EndsWith("/process"))
            return Json(ProcessStatus, ProcessBody);
        if (path.Contains("/download/"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Output) };
        if (req.Method == HttpMethod.Delete && path.Contains("/task/"))
            return Json(200, "{\"status\":\"TaskDeleted\"}");
        return Json(404, "{}");
    }

    private static async Task<string> ReadBody(HttpContent c)
    {
        // Multipart bodies contain the file bytes; only keep a text rendering for assertions.
        return await c.ReadAsStringAsync();
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public ILoveApiClient NewClient(string region = "") =>
        new("pub_key_123", region, new HttpClient(this));
}

[CollectionDefinition("DataDir", DisableParallelization = true)]
public sealed class DataDirCollection { }

[Collection("DataDir")]
public class CoreTests : IDisposable
{
    private readonly string _dir;

    public CoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdftools-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("PDFTOOLS_DATA_DIR", Path.Combine(_dir, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakeFile(string name, int size = 1000)
    {
        var p = Path.Combine(_dir, name);
        var buf = Enumerable.Repeat((byte)'x', Math.Max(size, 1)).ToArray();
        var ext = Path.GetExtension(name).ToLowerInvariant();
        byte[] prefix = ext switch
        {
            ".pdf" => "%PDF-1.4\n"u8.ToArray(),
            ".jpg" or ".jpeg" => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 },
            ".docx" or ".pptx" => new byte[] { 0x50, 0x4B, 0x03, 0x04 },
            ".doc" or ".ppt" => new byte[] { 0xD0, 0xCF, 0x11, 0xE0 },
            _ => Array.Empty<byte>()
        };
        if (prefix.Length > 0 && size >= prefix.Length)
            Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length);
        File.WriteAllBytes(p, buf);
        return p;
    }

    private static JobService Service(FakeApi api, AppSettings? s = null) =>
        new(() => api.NewClient(s?.Region ?? ""), s ?? new AppSettings());

    // ---------------------------------------------------------------- naming

    [Fact]
    public void UniquePath_NeverOverwrites()
    {
        MakeFile("a.pdf");
        MakeFile("a (1).pdf");
        Assert.Equal(Path.Combine(_dir, "a (2).pdf"), OutputNaming.GetUniquePath(_dir, "a", ".pdf"));
        Assert.Equal(Path.Combine(_dir, "b.pdf"), OutputNaming.GetUniquePath(_dir, "b", ".pdf"));
    }

    [Fact]
    public void NaturalOrder_PutsFile2BeforeFile10()
    {
        var list = new List<string> { "file10.pdf", "file2.pdf", "File1.pdf" };
        list.Sort((a, b) => NaturalComparer.ManualCompare(a, b));
        Assert.Equal("File1.pdf", list[0]);
        Assert.Equal("file2.pdf", list[1]);
        Assert.Equal("file10.pdf", list[2]);
    }

    [Fact]
    public void FormatSize_Works()
    {
        Assert.Equal("512 B", OutputNaming.FormatSize(512));
        Assert.Equal("1.5 KB", OutputNaming.FormatSize(1536));
    }

    // ---------------------------------------------------------------- compress

    [Fact]
    public async Task Compress_SavesNextToOriginal_AndCleansUp()
    {
        var input = MakeFile("report.pdf", 5000);
        var api = new FakeApi();
        var settings = new AppSettings { CompressionLevel = "extreme", Region = "eu" };

        var res = await Service(api, settings).RunAsync(JobKind.Compress, new[] { input });

        var r = Assert.Single(res);
        Assert.Equal(JobStatus.Success, r.Status);
        Assert.Equal(Path.Combine(_dir, "report_compressed.pdf"), r.OutputPath);
        Assert.True(File.Exists(r.OutputPath!));
        Assert.True(File.Exists(input));                                   // original untouched
        Assert.Empty(Directory.GetFiles(_dir, ".pdftools-*"));             // temp files removed

        Assert.Contains(api.Requests, q => q.Url.AbsolutePath.EndsWith("/start/compress/eu"));
        var process = api.Requests.Single(q => q.Url.AbsolutePath.EndsWith("/process"));
        Assert.Contains("\"compression_level\":\"extreme\"", process.Body);
        Assert.Contains("\"filename\":\"report.pdf\"", process.Body);
        Assert.Equal("Bearer tok123", process.Auth);
        Assert.Contains(api.Requests, q => q.Method == HttpMethod.Delete);  // server task deleted
        Assert.Contains("%PDF", File.ReadAllText(r.OutputPath!));
    }

    [Fact]
    public async Task Compress_ExistingOutput_GetsNumberedName()
    {
        var input = MakeFile("r.pdf", 5000);
        MakeFile("r_compressed.pdf");
        var res = await Service(new FakeApi()).RunAsync(JobKind.Compress, new[] { input });
        Assert.Equal(Path.Combine(_dir, "r_compressed (1).pdf"), res[0].OutputPath);
    }

    [Fact]
    public async Task Compress_NoGain_DoesNotSaveLargerFile()
    {
        var input = MakeFile("tiny.pdf", 10);                               // smaller than the fake output
        var res = await Service(new FakeApi()).RunAsync(JobKind.Compress, new[] { input });
        Assert.Equal(JobStatus.NoGain, res[0].Status);
        Assert.Null(res[0].OutputPath);
        Assert.False(File.Exists(Path.Combine(_dir, "tiny_compressed.pdf")));
    }

    // ---------------------------------------------------------------- merge

    [Fact]
    public async Task Merge_UsesNaturalOrder_AndSingleTask()
    {
        var f10 = MakeFile("part10.pdf"); var f2 = MakeFile("part2.pdf"); var f1 = MakeFile("part1.pdf");
        var api = new FakeApi();
        var res = await Service(api).RunAsync(JobKind.Merge, new[] { f10, f2, f1 });

        var r = Assert.Single(res);
        Assert.Equal(JobStatus.Success, r.Status);
        Assert.Equal(Path.Combine(_dir, "part1_merged.pdf"), r.OutputPath);
        var body = api.Requests.Single(q => q.Url.AbsolutePath.EndsWith("/process")).Body;
        Assert.True(body.IndexOf("part1.pdf") < body.IndexOf("part2.pdf"));
        Assert.True(body.IndexOf("part2.pdf") < body.IndexOf("part10.pdf"));
        Assert.Equal(1, api.Requests.Count(q => q.Url.AbsolutePath.Contains("/start/")));
    }

    [Fact]
    public async Task Merge_WithOneFile_FailsWithHelpfulMessage()
    {
        var res = await Service(new FakeApi()).RunAsync(JobKind.Merge, new[] { MakeFile("solo.pdf") });
        Assert.Equal(JobStatus.Failed, res[0].Status);
        Assert.Contains("at least two", res[0].Message);
    }

    // ---------------------------------------------------------------- convert

    [Fact]
    public async Task OfficeToPdf_NamesOutputAfterSource()
    {
        var api = new FakeApi();
        var res = await Service(api).RunAsync(JobKind.OfficeToPdf, new[] { MakeFile("letter.docx"), MakeFile("deck.pptx") });
        Assert.All(res, r => Assert.Equal(JobStatus.Success, r.Status));
        Assert.True(File.Exists(Path.Combine(_dir, "letter.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "deck.pdf")));
        Assert.Equal(2, api.Requests.Count(q => q.Url.AbsolutePath.EndsWith("/start/officepdf")));
    }

    [Fact]
    public async Task ImagesCombine_SendsMergeAfter()
    {
        var api = new FakeApi();
        var res = await Service(api).RunAsync(JobKind.ImagesCombine, new[] { MakeFile("a.jpg"), MakeFile("b.jpeg") });
        Assert.Equal(JobStatus.Success, res[0].Status);
        Assert.Equal(Path.Combine(_dir, "a_combined.pdf"), res[0].OutputPath);
        Assert.Contains("\"merge_after\":true", api.Requests.Single(q => q.Url.AbsolutePath.EndsWith("/process")).Body);
    }

    [Fact]
    public async Task ImageToPdf_OnePdfPerImage()
    {
        var res = await Service(new FakeApi()).RunAsync(JobKind.ImageToPdf, new[] { MakeFile("x.jpg"), MakeFile("y.JPG") });
        Assert.True(File.Exists(Path.Combine(_dir, "x.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "y.pdf")));
        Assert.Equal(2, res.Count);
    }

    [Fact]
    public async Task WrongFileType_IsRejectedWithoutCallingApi()
    {
        var api = new FakeApi();
        var res = await Service(api).RunAsync(JobKind.Compress, new[] { MakeFile("notes.txt") });
        Assert.Equal(JobStatus.Failed, res[0].Status);
        Assert.Empty(api.Requests);
    }

    // ---------------------------------------------------------------- errors

    [Fact]
    public async Task BadKey_ReportsAuthError()
    {
        var api = new FakeApi { AuthStatus = 401 };
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.NewClient().ValidateKeyAsync());
        Assert.Equal(ApiErrorKind.Auth, ex.Kind);
    }

    [Fact]
    public async Task BadKey_DuringJob_BecomesFailedResult()
    {
        var api = new FakeApi { AuthStatus = 401 };
        var res = await Service(api).RunAsync(JobKind.Compress, new[] { MakeFile("a.pdf") });
        Assert.Equal(JobStatus.Failed, res[0].Status);
        Assert.Contains("API key", res[0].Message);
    }

    [Fact]
    public async Task PasswordProtectedPdf_GivesClearMessage()
    {
        var api = new FakeApi
        {
            ProcessStatus = 400,
            ProcessBody = "{\"error\":{\"type\":\"ProcessingError\",\"message\":\"x\",\"param\":[{\"status\":\"WrongPassword\"}]}}"
        };
        var res = await Service(api).RunAsync(JobKind.Compress, new[] { MakeFile("locked.pdf") });
        Assert.Equal(JobStatus.Failed, res[0].Status);
        Assert.Contains("password", res[0].Message);
        Assert.Empty(Directory.GetFiles(_dir, "*_compressed*"));
    }

    [Fact]
    public async Task Credits_AreReportedAfterJob()
    {
        var svc = Service(new FakeApi());
        await svc.RunAsync(JobKind.Compress, new[] { MakeFile("c.pdf", 5000) });
        Assert.Equal(42, svc.LastRemainingCredits);
    }

    // ---------------------------------------------------------------- log + settings

    [Fact]
    public async Task Log_RecordsNamesOnly()
    {
        var input = MakeFile("secret-report.pdf", 5000);
        var svc = new JobService(() => new FakeApi().NewClient(), new AppSettings(), new FileJobLog());
        await svc.RunAsync(JobKind.Compress, new[] { input });

        var entries = LogReader.ReadRecent();
        var e = Assert.Single(entries);
        Assert.Equal("Success", e.Status);
        Assert.Equal("secret-report.pdf", e.Inputs[0]);
        Assert.DoesNotContain(_dir, JsonSerializer.Serialize(e));           // no directory paths in the log
    }

    [Fact]
    public void Settings_InvalidValuesFallBackToDefaults()
    {
        var s = new AppSettings { Region = "mars", CompressionLevel = "ultra", LogRetentionDays = 0 };
        s.Normalize();
        Assert.Equal("", s.Region);
        Assert.Equal("recommended", s.CompressionLevel);
        Assert.Equal(30, s.LogRetentionDays);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        new AppSettings { Region = "sg", CompressionLevel = "low" }.Save();
        var s = AppSettings.Load();
        Assert.Equal("sg", s.Region);
        Assert.Equal("low", s.CompressionLevel);
    }
}
