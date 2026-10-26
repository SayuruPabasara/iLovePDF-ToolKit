using System.Diagnostics;
using System.IO.Compression;
using PdfTools.Core.ILoveApi;

namespace PdfTools.Core.Jobs;

public enum JobKind
{
    Compress,       // PDF -> smaller PDF           (tool: compress)
    Merge,          // N PDFs -> one PDF            (tool: merge)
    OfficeToPdf,    // Word/PowerPoint -> PDF       (tool: officepdf)
    ImageToPdf,     // each JPG -> its own PDF      (tool: imagepdf)
    ImagesCombine   // N JPGs -> one PDF            (tool: imagepdf, merge_after)
}

public enum JobStatus { Success, NoGain, Failed }

/// <param name="Inputs">Full paths of the files this result is about.</param>
/// <param name="OutputPath">Saved file (null when nothing was saved).</param>
public sealed record JobItemResult(
    IReadOnlyList<string> Inputs,
    string? OutputPath,
    JobStatus Status,
    string Message);

/// <summary>Runs one batch of files for one operation. UI-agnostic; the Runner shows the notifications.</summary>
public sealed class JobService
{
    private const int MaxParallel = 3;

    private static readonly string[] PdfExt = { ".pdf" };
    private static readonly string[] OfficeExt = { ".doc", ".docx", ".ppt", ".pptx" };
    private static readonly string[] ImageExt = { ".jpg", ".jpeg" };

    private readonly Func<ILoveApiClient> _clientFactory;
    private readonly AppSettings _settings;
    private readonly IJobLog _log;

    public int? LastRemainingCredits { get; private set; }

    public JobService(Func<ILoveApiClient> clientFactory, AppSettings settings, IJobLog? log = null)
    {
        _clientFactory = clientFactory;
        _settings = settings;
        _log = log ?? new NullJobLog();
    }

    public static string OperationName(JobKind k) => k switch
    {
        JobKind.Compress => "Compress",
        JobKind.Merge => "Merge",
        JobKind.OfficeToPdf => "Convert to PDF",
        JobKind.ImageToPdf => "Image to PDF",
        JobKind.ImagesCombine => "Combine images",
        _ => k.ToString()
    };

    private static string[] AllowedExt(JobKind k) => k switch
    {
        JobKind.Compress or JobKind.Merge => PdfExt,
        JobKind.OfficeToPdf => OfficeExt,
        _ => ImageExt
    };

    // ------------------------------------------------------------------ public entry point

    public async Task<IReadOnlyList<JobItemResult>> RunAsync(JobKind kind, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var results = new List<JobItemResult>();
        var valid = new List<string>();

        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var problem = Validate(kind, f);
            if (problem != null) results.Add(Fail(kind, new[] { f }, problem, 0));
            else valid.Add(f);
        }
        if (valid.Count == 0) return results;

        // Build the units of work.
        var units = new List<WorkUnit>();
        switch (kind)
        {
            case JobKind.Merge:
            case JobKind.ImagesCombine:
                if (valid.Count < 2)
                {
                    results.Add(Fail(kind, valid, kind == JobKind.Merge
                        ? "Select at least two PDF files to merge."
                        : "Select at least two JPG files to combine.", 0));
                    return results;
                }
                valid.Sort(NaturalComparer.Instance);     // Explorer order = predictable page order
                units.Add(kind == JobKind.Merge
                    ? new WorkUnit(kind, valid, "merge", null, "_merged", false)
                    : new WorkUnit(kind, valid, "imagepdf",
                        new Dictionary<string, object?> { ["merge_after"] = true, ["pagesize"] = "fit", ["orientation"] = "portrait" },
                        "_combined", false));
                break;

            case JobKind.Compress:
                foreach (var f in valid)
                    units.Add(new WorkUnit(kind, new[] { f }, "compress",
                        new Dictionary<string, object?> { ["compression_level"] = _settings.CompressionLevel },
                        "_compressed", requireSmaller: true));
                break;

            case JobKind.OfficeToPdf:
                foreach (var f in valid)
                    units.Add(new WorkUnit(kind, new[] { f }, "officepdf", null, "", false));
                break;

            case JobKind.ImageToPdf:
                foreach (var f in valid)
                    units.Add(new WorkUnit(kind, new[] { f }, "imagepdf",
                        new Dictionary<string, object?> { ["merge_after"] = false, ["pagesize"] = "fit", ["orientation"] = "portrait" },
                        "", false));
                break;
        }

        using var client = _clientFactory();
        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = units.Select(async u =>
        {
            await gate.WaitAsync(ct);
            try { return await ExecuteAsync(u, client, ct); }
            finally { gate.Release(); }
        }).ToList();

        results.AddRange(await Task.WhenAll(tasks));
        LastRemainingCredits = client.RemainingCredits;
        return results;
    }

    // ------------------------------------------------------------------ one unit of work

    private sealed class WorkUnit
    {
        public JobKind Kind { get; }
        public IReadOnlyList<string> Inputs { get; }
        public string Tool { get; }
        public IDictionary<string, object?>? Extra { get; }
        public string Suffix { get; }
        public bool RequireSmaller { get; }

        public WorkUnit(JobKind kind, IReadOnlyList<string> inputs, string tool,
                        IDictionary<string, object?>? extra, string suffix, bool requireSmaller)
        {
            Kind = kind; Inputs = inputs; Tool = tool; Extra = extra; Suffix = suffix; RequireSmaller = requireSmaller;
        }
    }

    private async Task<JobItemResult> ExecuteAsync(WorkUnit u, ILoveApiClient client, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var first = u.Inputs[0];
        var dir = Path.GetDirectoryName(first)!;
        var baseName = Path.GetFileNameWithoutExtension(first) + u.Suffix;
        string? temp = null, extracted = null;
        ApiTask? task = null;

        try
        {
            task = await client.StartAsync(u.Tool, ct);

            var uploaded = new List<UploadedFile>();
            foreach (var f in u.Inputs)
                uploaded.Add(new UploadedFile(await client.UploadAsync(task, f, ct), Path.GetFileName(f)));

            await client.ProcessAsync(task, u.Tool, uploaded, u.Extra, ct);

            temp = Path.Combine(dir, $".pdftools-{Guid.NewGuid():N}.tmp");
            await client.DownloadAsync(task, temp, ct);

            // Multiple outputs come back as a ZIP; we only ever expect one PDF.
            var pdfFile = temp;
            if (LooksLikeZip(temp))
            {
                extracted = Path.Combine(dir, $".pdftools-{Guid.NewGuid():N}.tmp");
                if (!ExtractFirstPdf(temp, extracted))
                    throw new ApiException(ApiErrorKind.Rejected, "iLoveAPI returned no PDF file.");
                pdfFile = extracted;
            }

            var outLen = new FileInfo(pdfFile).Length;
            if (outLen == 0) throw new ApiException(ApiErrorKind.Rejected, "iLoveAPI returned an empty file.");

            if (u.RequireSmaller)
            {
                var inLen = new FileInfo(first).Length;
                if (outLen >= inLen)
                {
                    var msg = $"{Path.GetFileName(first)} is already well compressed; no smaller file was produced.";
                    Log(u, null, "NoGain", msg, sw);
                    return new JobItemResult(u.Inputs, null, JobStatus.NoGain, msg);
                }
            }

            var finalPath = OutputNaming.MoveUnique(pdfFile, dir, baseName, ".pdf");
            if (pdfFile == extracted) extracted = null; else temp = null;   // moved

            string detail = Path.GetFileName(finalPath);
            if (u.RequireSmaller)
            {
                var inLen = new FileInfo(first).Length;
                var outLen2 = new FileInfo(finalPath).Length;
                var pct = (int)Math.Round(100.0 * (inLen - outLen2) / inLen);
                detail += $" ({OutputNaming.FormatSize(inLen)} \u2192 {OutputNaming.FormatSize(outLen2)}, {pct}% smaller)";
            }
            Log(u, finalPath, "Success", null, sw);
            return new JobItemResult(u.Inputs, finalPath, JobStatus.Success, detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail(u.Kind, u.Inputs, "Cancelled.", sw.Elapsed.TotalSeconds);
        }
        catch (ApiException ex)
        {
            return Fail(u.Kind, u.Inputs, ex.Message, sw.Elapsed.TotalSeconds);
        }
        catch (IOException ex)
        {
            return Fail(u.Kind, u.Inputs, "File error: " + ex.Message, sw.Elapsed.TotalSeconds);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(u.Kind, u.Inputs, "Access denied: " + ex.Message, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            return Fail(u.Kind, u.Inputs, "Unexpected error: " + ex.Message, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            TryDelete(temp);
            TryDelete(extracted);
            if (task != null) await client.DeleteTaskAsync(task);   // don't leave the user's files on the server
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string? Validate(JobKind kind, string path)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path)) return $"{name}: file not found.";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(AllowedExt(kind), ext) < 0)
            return $"{name}: unsupported file type for \"{OperationName(kind)}\".";
        try { if (new FileInfo(path).Length == 0) return $"{name}: file is empty."; }
        catch (IOException) { return $"{name}: cannot read file."; }
        return null;
    }

    private JobItemResult Fail(JobKind kind, IReadOnlyList<string> inputs, string message, double seconds)
    {
        _log.Write(new LogEntry(DateTimeOffset.Now, OperationName(kind),
            inputs.Select(Path.GetFileName).Select(n => n ?? "").ToArray(), null, "Failed", message, Math.Round(seconds, 1)));
        return new JobItemResult(inputs, null, JobStatus.Failed, message);
    }

    private void Log(WorkUnit u, string? output, string status, string? message, Stopwatch sw) =>
        _log.Write(new LogEntry(DateTimeOffset.Now, OperationName(u.Kind),
            u.Inputs.Select(Path.GetFileName).Select(n => n ?? "").ToArray(),
            output == null ? null : Path.GetFileName(output), status, message, Math.Round(sw.Elapsed.TotalSeconds, 1)));

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;
        }
        catch (IOException) { return false; }
    }

    private static bool ExtractFirstPdf(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (entry == null) return false;
        entry.ExtractToFile(destination, overwrite: true);
        return true;
    }

    private static void TryDelete(string? path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
    }
}
