using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;
using PdfTools.Core.Observability;
using PdfTools.Core.Resilience;
using Xunit;

namespace PdfTools.Core.Tests;

[Collection("DataDir")]
public class ObservabilityTests : IDisposable
{
    private readonly string _dir;

    public ObservabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdftools-obs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("PDFTOOLS_DATA_DIR", Path.Combine(_dir, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakePdf(string name, int size = 5000)
    {
        var p = Path.Combine(_dir, name);
        var buf = Enumerable.Repeat((byte)'x', size).ToArray();
        var hdr = "%PDF-1.4\n"u8.ToArray();
        Buffer.BlockCopy(hdr, 0, buf, 0, hdr.Length);
        File.WriteAllBytes(p, buf);
        return p;
    }

    [Fact]
    public async Task JobLogV2_RecordsCorrelationId_Server_StageTimings_AndByteCounts_WithoutLeakingPaths()
    {
        var input = MakePdf("quarterly-report.pdf", 5000);
        var api = new FakeApi();
        var svc = new JobService(() => api.NewClient(), new AppSettings(), new FileJobLog());
        await svc.RunAsync(JobKind.Compress, new[] { input });

        var entries = await LogReader.ReadRecentAsync();
        var e = Assert.Single(entries);
        Assert.Equal("Success", e.Status);
        Assert.False(string.IsNullOrWhiteSpace(e.CorrelationId));
        Assert.Equal("TASK1", e.ApiTaskId);
        Assert.Equal("api1.test", e.ApiServer);
        Assert.Equal(5000, e.InputBytes);
        Assert.Equal(api.Output.Length, e.OutputBytes);
        Assert.NotNull(e.Timings);
        Assert.NotNull(e.Timings!.StartMs);
        Assert.NotNull(e.Timings.UploadMs);
        Assert.NotNull(e.Timings.ProcessMs);
        Assert.NotNull(e.Timings.DownloadMs);
        Assert.DoesNotContain(_dir, JsonSerializer.Serialize(e));
    }

    [Fact]
    public async Task JobLogV2_RecordsFailureStage_ErrorKind_HttpStatus_AndRetries()
    {
        var time = new ManualTimeProvider();
        int processCalls = 0;
        var handler = new ScriptedHandler((req, _, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t\"}", Encoding.UTF8, "application/json") });
            if (path.Contains("/start/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"server\":\"worker9.test\",\"task\":\"T99\"}", Encoding.UTF8, "application/json") });
            if (path.EndsWith("/upload"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"server_filename\":\"u.pdf\"}", Encoding.UTF8, "application/json") });
            if (path.EndsWith("/process"))
            {
                processCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"busy\"}", Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        });

        var options = new ResilienceOptions { MaxAttempts = 2, BaseBackoff = TimeSpan.Zero };
        var svc = new JobService(
            () => new ILoveApiClient("pub_k", http: new HttpClient(handler), resilience: options, timeProvider: time, jitterSource: () => 0.0, tokenCache: NullTokenCache.Instance),
            new AppSettings(),
            new FileJobLog());

        await svc.RunAsync(JobKind.Compress, new[] { MakePdf("fail.pdf", 2000) });

        var e = Assert.Single(LogReader.ReadRecent());
        Assert.Equal("Failed", e.Status);
        Assert.Equal("Process", e.FailedStage);
        Assert.Equal("Server", e.ErrorKind);
        Assert.Equal(503, e.HttpStatus);
        Assert.Equal("worker9.test", e.ApiServer);
        Assert.Equal("T99", e.ApiTaskId);
        Assert.Equal(1, e.RetryCount);
        Assert.Equal(2, processCalls);
    }

    [Fact]
    public void LogReader_ReadsLegacyV1Entries_AndHandlesMultiChunkReverseReading()
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        var path = Path.Combine(AppPaths.LogDir, "pdftools-20261124.log");

        // Write >150KB of UTF-8 JSONL lines (exceeding 64KB chunk size) including legacy v1 schema + Unicode
        var sb = new StringBuilder();
        for (int i = 0; i < 1200; i++)
        {
            sb.Append("{\"time\":\"2026-11-24T10:00:00+00:00\",\"operation\":\"Compress\",\"inputs\":[\"doc-\u2192-");
            sb.Append(i);
            sb.Append(".pdf\"],\"output\":\"out-");
            sb.Append(i);
            sb.Append(".pdf\",\"status\":\"Success\",\"message\":null,\"seconds\":1.2}\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var recent = LogReader.ReadRecent(maxEntries: 50);
        Assert.Equal(50, recent.Count);
        Assert.Equal("doc-\u2192-1199.pdf", recent[0].Inputs[0]);
        Assert.Equal("doc-\u2192-1150.pdf", recent[49].Inputs[0]);
        Assert.Null(recent[0].CorrelationId); // legacy v1 entry deserializes cleanly
    }

    [Fact]
    public void DiagnosticsLog_RedactsPathsAndSecrets()
    {
        var raw = @"Failed opening C:\Users\alice\Documents\Secret\tax.pdf with project_public_abc123XYZ and Bearer eyJhbGciOi.payload.sig";
        var redacted = DiagnosticsLog.Redact(raw);

        Assert.DoesNotContain(@"C:\Users\alice", redacted);
        Assert.Contains(@"<path>\tax.pdf", redacted);
        Assert.DoesNotContain("project_public_abc123XYZ", redacted);
        Assert.Contains("<redacted-key>", redacted);
        Assert.DoesNotContain("eyJhbGciOi", redacted);
        Assert.Contains("Bearer <redacted>", redacted);
    }

    [Fact]
    public void LogMetricsSummary_AndDiagnosticReport_ComputeAccurately()
    {
        var now = DateTimeOffset.Now;
        var entries = new List<LogEntry>
        {
            new(now, "Compress", new[] { "a.pdf" }, "a_compressed.pdf", "Success", null, 2.0, "c1", "T1", "s1.test", null, null, null, 1, 10000, 4000),
            new(now, "Compress", new[] { "b.pdf" }, null, "NoGain", "Already optimized", 1.0, "c2", "T2", "s1.test", null, null, null, null, 5000, 5000),
            new(now, "Merge", new[] { "c.pdf" }, null, "Failed", "Server down", 3.0, "c3", "T3", "s2.test", "Process", "Server", 503, 2, 8000, null),
            new(now, "Compress", new[] { "a.pdf" }, null, "Skipped", "In progress", 0.0, "c4")
        };

        var summary = LogMetricsSummary.Compute(entries);
        Assert.Equal(4, summary.TotalJobs);
        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(1, summary.NoGainCount);
        Assert.Equal(1, summary.SkippedCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(66.7, summary.SuccessRatePercent);
        Assert.Equal(6000, summary.TotalBytesSaved);
        Assert.Equal(2.0, summary.AverageSeconds);
        Assert.Equal(3, summary.TotalRetries);
        Assert.Equal(1, summary.ErrorsByKind["Server"]);

        var report = DiagnosticReportBuilder.Build(new AppSettings { Region = "eu" }, entries);
        Assert.Contains("Region=eu", report);
        Assert.Contains("cid=c3", report);
        Assert.Contains("stage=Process", report);
        Assert.Contains("http=503", report);
    }

    [Fact]
    public async Task ActivitySource_EmitsJobAndApiSpans()
    {
        var recorded = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = act => { lock (recorded) recorded.Add(act.OperationName); }
        };
        ActivitySource.AddActivityListener(listener);

        var svc = new JobService(() => new FakeApi().NewClient(), new AppSettings());
        await svc.RunAsync(JobKind.Compress, new[] { MakePdf("traced.pdf", 4000) });

        Assert.Contains("job.unit", recorded);
        Assert.Contains("api.start", recorded);
        Assert.Contains("api.upload", recorded);
        Assert.Contains("api.process", recorded);
        Assert.Contains("api.download", recorded);
    }
}

