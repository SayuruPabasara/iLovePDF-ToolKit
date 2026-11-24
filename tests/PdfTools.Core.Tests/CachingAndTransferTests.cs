using System.IO.Compression;
using System.Net;
using System.Text;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;
using Xunit;

namespace PdfTools.Core.Tests;

[Collection("DataDir")]
public class CachingAndTransferTests : IDisposable
{
    private readonly string _dir;

    public CachingAndTransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdftools-cache-" + Guid.NewGuid().ToString("N"));
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
        var buf = Enumerable.Repeat((byte)'x', size).ToArray();
        var hdr = "%PDF-1.4\n"u8.ToArray();
        Buffer.BlockCopy(hdr, 0, buf, 0, hdr.Length);
        File.WriteAllBytes(p, buf);
        return p;
    }

    [Fact]
    public async Task DpapiTokenCache_ReusesTokenAcrossClients_AndInvalidatesOnKeyChange()
    {
        var time = new ManualTimeProvider();
        var api = new FakeApi();

        using (var c1 = new ILoveApiClient("pub_key_A", http: new HttpClient(api), timeProvider: time))
        {
            await c1.StartAsync("compress");
            Assert.Equal(1, c1.AuthNetworkCalls);
        }

        // Second client with same key hits DPAPI token cache (0 /auth network calls)
        using (var c2 = new ILoveApiClient("pub_key_A", http: new HttpClient(api), timeProvider: time))
        {
            await c2.StartAsync("compress");
            Assert.Equal(0, c2.AuthNetworkCalls);
        }
        Assert.Equal(1, api.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/auth")));

        // Client with a DIFFERENT key must miss cache and re-authenticate
        using (var c3 = new ILoveApiClient("pub_key_B", http: new HttpClient(api), timeProvider: time))
        {
            await c3.StartAsync("compress");
            Assert.Equal(1, c3.AuthNetworkCalls);
        }
        Assert.Equal(2, api.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/auth")));

        // Advancing time near expiry forces refresh
        time.Advance(TimeSpan.FromMinutes(49));
        using (var c4 = new ILoveApiClient("pub_key_B", http: new HttpClient(api), timeProvider: time))
        {
            await c4.StartAsync("compress");
            Assert.Equal(1, c4.AuthNetworkCalls);
        }
        Assert.Equal(3, api.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/auth")));
    }

    [Fact]
    public void CredentialStore_Clear_AlsoClearsTokenCache()
    {
        var cacheKey = DpapiTokenCache.ComputeCacheKey("pub_1", ILoveApiClient.DefaultApiBase);
        var now = DateTimeOffset.UtcNow;
        DpapiTokenCache.Shared.Set(cacheKey, "cached_tok", now.AddMinutes(30));
        Assert.True(DpapiTokenCache.Shared.TryGet(cacheKey, now, out _, out _));

        CredentialStore.Clear();
        Assert.False(DpapiTokenCache.Shared.TryGet(cacheKey, now, out _, out _));
    }

    [Fact]
    public async Task PreflightValidator_RejectsCorruptMagicBytes_WithoutCallingApi()
    {
        var fakePdf = Path.Combine(_dir, "not_really.pdf");
        File.WriteAllText(fakePdf, "this is plain text pretending to be a PDF");

        var fakeJpg = Path.Combine(_dir, "not_really.jpg");
        File.WriteAllText(fakeJpg, "not a jpeg");

        var api = new FakeApi();
        var svc = new JobService(() => api.NewClient(), new AppSettings());

        var r1 = await svc.RunAsync(JobKind.Compress, new[] { fakePdf });
        Assert.Equal(JobStatus.Failed, r1[0].Status);
        Assert.Contains("valid PDF", r1[0].Message);

        var r2 = await svc.RunAsync(JobKind.ImageToPdf, new[] { fakeJpg });
        Assert.Equal(JobStatus.Failed, r2[0].Status);
        Assert.Contains("valid JPEG", r2[0].Message);

        Assert.Empty(api.Requests);
    }

    [Fact]
    public void PreflightValidator_AcceptsRtfDocAndPdfWithPreamble()
    {
        var pdfPreamble = Path.Combine(_dir, "preamble.pdf");
        File.WriteAllText(pdfPreamble, "   \r\n%PDF-1.7\nrest");
        Assert.Null(PreflightValidator.ValidateFile(JobKind.Compress, pdfPreamble));

        var rtfDoc = Path.Combine(_dir, "memo.doc");
        File.WriteAllText(rtfDoc, @"{\rtf1\ansi hello}");
        Assert.Null(PreflightValidator.ValidateFile(JobKind.OfficeToPdf, rtfDoc));
    }

    [Fact]
    public async Task Merge_UploadsConcurrently_WhilePreservingNaturalOrder()
    {
        int inFlight = 0;
        int maxObserved = 0;
        int uploadSeq = 0;
        string? processBody = null;

        var handler = new ScriptedHandler(async (req, _, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t\"}", Encoding.UTF8, "application/json") };
            if (path.Contains("/start/"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"server\":\"s1.test\",\"task\":\"TM\"}", Encoding.UTF8, "application/json") };
            if (path.EndsWith("/upload"))
            {
                var cur = Interlocked.Increment(ref inFlight);
                int prev;
                do { prev = Volatile.Read(ref maxObserved); } while (cur > prev && Interlocked.CompareExchange(ref maxObserved, cur, prev) != prev);
                await Task.Delay(60);
                Interlocked.Decrement(ref inFlight);
                var n = Interlocked.Increment(ref uploadSeq);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"server_filename\":\"srv{n}.pdf\"}}", Encoding.UTF8, "application/json") };
            }
            if (path.EndsWith("/process"))
            {
                processBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"TaskSuccess\",\"output_filesize\":100,\"output_filenumber\":1}", Encoding.UTF8, "application/json") };
            }
            if (path.Contains("/download/"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-1.4 merged"u8.ToArray()) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var f10 = MakePdf("page10.pdf");
        var f2 = MakePdf("page2.pdf");
        var f1 = MakePdf("page1.pdf");
        var f3 = MakePdf("page3.pdf");

        var svc = new JobService(() => new ILoveApiClient("pub_k", http: new HttpClient(handler), tokenCache: NullTokenCache.Instance), new AppSettings());
        var res = await svc.RunAsync(JobKind.Merge, new[] { f10, f2, f1, f3 });

        Assert.Single(res);
        Assert.Equal(JobStatus.Success, res[0].Status);
        Assert.True(maxObserved >= 2, $"Expected parallel uploads, observed max concurrency {maxObserved}");
        Assert.NotNull(processBody);
        Assert.True(processBody!.IndexOf("page1.pdf") < processBody.IndexOf("page2.pdf"));
        Assert.True(processBody.IndexOf("page2.pdf") < processBody.IndexOf("page3.pdf"));
        Assert.True(processBody.IndexOf("page3.pdf") < processBody.IndexOf("page10.pdf"));
    }

    [Fact]
    public void ExtractFirstPdf_RejectsOversizedArchiveEntry()
    {
        var zipPath = Path.Combine(_dir, "big.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("huge.pdf");
            using var s = entry.Open();
            s.Write(new byte[2048]);
        }

        var outPath = Path.Combine(_dir, "out.pdf");
        var ex = Assert.Throws<ApiException>(() => JobService.ExtractFirstPdf(zipPath, outPath, maxBytes: 1024));
        Assert.Equal(ApiErrorKind.Rejected, ex.Kind);
    }
}
