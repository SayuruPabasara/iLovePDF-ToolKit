using System.Net;
using System.Text;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;
using Xunit;

namespace PdfTools.Core.Tests;

[Collection("DataDir")]
public class IdempotencyAndQueueTests : IDisposable
{
    private readonly string _dir;

    public IdempotencyAndQueueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdftools-idem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("PDFTOOLS_DATA_DIR", Path.Combine(_dir, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakePdf(string name, int size = 1000)
    {
        var p = Path.Combine(_dir, name);
        var buf = Enumerable.Repeat((byte)'x', Math.Max(size, 10)).ToArray();
        var hdr = "%PDF-1.4\n"u8.ToArray();
        Buffer.BlockCopy(hdr, 0, buf, 0, hdr.Length);
        File.WriteAllBytes(p, buf);
        return p;
    }

    [Fact]
    public void Fingerprint_ChangesWhenFileOrCompressionLevelChanges_AndIsOpaque()
    {
        var f = MakePdf("confidential-contract.pdf", 500);
        var s1 = new AppSettings { CompressionLevel = "recommended" };
        var s2 = new AppSettings { CompressionLevel = "extreme" };

        var fp1 = JobIdempotency.ComputeFingerprint(JobKind.Compress, new[] { f }, s1);
        var fp1Repeat = JobIdempotency.ComputeFingerprint(JobKind.Compress, new[] { f }, s1);
        var fp2 = JobIdempotency.ComputeFingerprint(JobKind.Compress, new[] { f }, s2);

        Assert.Equal(fp1, fp1Repeat);
        Assert.NotEqual(fp1, fp2);
        Assert.DoesNotContain("confidential", fp1, StringComparison.OrdinalIgnoreCase);

        // Mutating file size changes fingerprint
        MakePdf("confidential-contract.pdf", 600);
        var fp3 = JobIdempotency.ComputeFingerprint(JobKind.Compress, new[] { f }, s1);
        Assert.NotEqual(fp1, fp3);
    }

    [Fact]
    public async Task InFlightTracker_DeduplicatesConcurrentIdenticalRuns()
    {
        var f = MakePdf("dup.pdf", 5000);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int processCalls = 0;

        var handler = new ScriptedHandler(async (req, _, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t\"}", Encoding.UTF8, "application/json") };
            if (path.Contains("/start/"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"server\":\"s1.test\",\"task\":\"TD\"}", Encoding.UTF8, "application/json") };
            if (path.EndsWith("/upload"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"server_filename\":\"srv.pdf\"}", Encoding.UTF8, "application/json") };
            if (path.EndsWith("/process"))
            {
                Interlocked.Increment(ref processCalls);
                started.TrySetResult();
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"TaskSuccess\",\"output_filesize\":100,\"output_filenumber\":1}", Encoding.UTF8, "application/json") };
            }
            if (path.Contains("/download/"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-1.4 ok"u8.ToArray()) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var tracker = new InFlightJobTracker();
        var svc = new JobService(
            () => new ILoveApiClient("pub_k", http: new HttpClient(handler), tokenCache: NullTokenCache.Instance),
            new AppSettings(),
            inFlight: tracker,
            noGainCache: NullNoGainCache.Instance);

        var run1 = svc.RunAsync(JobKind.Compress, new[] { f });
        await started.Task;

        // While run1 is in-flight, run2 on the exact same file must be skipped immediately
        var res2 = await svc.RunAsync(JobKind.Compress, new[] { f });
        Assert.Single(res2);
        Assert.Equal(JobStatus.Skipped, res2[0].Status);

        release.SetResult();
        var res1 = await run1;
        Assert.Single(res1);
        Assert.Equal(JobStatus.Success, res1[0].Status);
        Assert.Equal(1, processCalls);
    }

    [Fact]
    public async Task NoGainCache_ShortCircuitsRepeatedCompress_AndExpiresOrInvalidatesOnChange()
    {
        var time = new ManualTimeProvider();
        var tiny = MakePdf("already-small.pdf", 12); // smaller than FakeApi's output
        var api = new FakeApi();
        var settings = new AppSettings { CompressionLevel = "recommended" };

        var svc = new JobService(() => api.NewClient(), settings, timeProvider: time);

        // 1st run calls API, gets NoGain, and caches the fingerprint
        var r1 = await svc.RunAsync(JobKind.Compress, new[] { tiny });
        Assert.Equal(JobStatus.NoGain, r1[0].Status);
        int apiCallsAfterFirst = api.Requests.Count;
        Assert.True(apiCallsAfterFirst > 0);

        // Cache file must not leak filename or directory path
        var cacheJson = File.ReadAllText(AppPaths.NoGainCacheFile);
        Assert.DoesNotContain("already-small", cacheJson);
        Assert.DoesNotContain(_dir, cacheJson);

        // 2nd run on unchanged file short-circuits with 0 additional API calls
        var r2 = await svc.RunAsync(JobKind.Compress, new[] { tiny });
        Assert.Equal(JobStatus.NoGain, r2[0].Status);
        Assert.Contains("cached", r2[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(apiCallsAfterFirst, api.Requests.Count);

        // Changing compression level to "extreme" misses cache and calls API
        var extremeSvc = new JobService(() => api.NewClient(), new AppSettings { CompressionLevel = "extreme" }, timeProvider: time);
        var r3 = await extremeSvc.RunAsync(JobKind.Compress, new[] { tiny });
        Assert.Equal(JobStatus.NoGain, r3[0].Status);
        Assert.True(api.Requests.Count > apiCallsAfterFirst);

        // Advancing time past 30-day TTL misses cache for original settings
        int callsBeforeExpiry = api.Requests.Count;
        time.Advance(TimeSpan.FromDays(31));
        var r4 = await svc.RunAsync(JobKind.Compress, new[] { tiny });
        Assert.Equal(JobStatus.NoGain, r4[0].Status);
        Assert.True(api.Requests.Count > callsBeforeExpiry);
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelledResultsGracefully_AndLeavesNoTempFiles()
    {
        var files = Enumerable.Range(1, 5).Select(i => MakePdf($"cancel_{i}.pdf", 2000)).ToList();
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(async (req, _, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t\"}", Encoding.UTF8, "application/json") };
            cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = new JobService(
            () => new ILoveApiClient("pub_k", http: new HttpClient(handler), tokenCache: NullTokenCache.Instance),
            new AppSettings());

        var results = await svc.RunAsync(JobKind.Compress, files, cts.Token);
        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal(JobStatus.Failed, r.Status));
        Assert.All(results, r => Assert.Contains("Cancelled", r.Message));
        Assert.Empty(Directory.GetFiles(_dir, ".pdftools-*"));
    }

    [Fact]
    public async Task SharedClient_DisposeFalse_ReusesClientAcrossBatches()
    {
        var api = new FakeApi();
        using var shared = api.NewClient();
        var svc = new JobService(() => shared, new AppSettings(), disposeClient: false);

        var r1 = await svc.RunAsync(JobKind.Compress, new[] { MakePdf("b1.pdf", 4000) });
        var r2 = await svc.RunAsync(JobKind.Compress, new[] { MakePdf("b2.pdf", 4000) });

        Assert.Equal(JobStatus.Success, r1[0].Status);
        Assert.Equal(JobStatus.Success, r2[0].Status);
        Assert.Equal(1, shared.AuthNetworkCalls);
    }
}

