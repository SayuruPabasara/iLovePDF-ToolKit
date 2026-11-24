using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;
using PdfTools.Core.Resilience;
using Xunit;

namespace PdfTools.Core.Tests;

/// <summary>Lightweight deterministic TimeProvider for fast, flake-free resilience tests.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
        => _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }

    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += delta;
            due = _timers.Where(t => t.IsDue(_now)).ToList();
        }
        foreach (var t in due) t.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Remove(ManualTimer timer) { lock (_lock) _timers.Remove(timer); }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _nextFire;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool IsDue(DateTimeOffset now) => !_disposed && _nextFire.HasValue && now >= _nextFire.Value;

        public void Fire()
        {
            lock (_owner._lock)
            {
                if (_disposed) return;
                _nextFire = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                    ? null
                    : _owner._now + _period;
            }
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._lock)
            {
                if (_disposed) return false;
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan) { _nextFire = null; return true; }
                _nextFire = _owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                if (dueTime <= TimeSpan.Zero)
                {
                    _nextFire = null;
                    ThreadPool.QueueUserWorkItem(_ => _callback(_state));
                }
                return true;
            }
        }

        public void Dispose() { _disposed = true; _owner.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _handler;
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ScriptedHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler)
        => _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        return _handler(request, n, cancellationToken);
    }
}

internal sealed class FailingBodyStream : Stream
{
    private readonly byte[] _prefix;
    private int _pos;

    public FailingBodyStream(byte[] prefix) => _prefix = prefix;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _prefix.Length + 100;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos < _prefix.Length)
        {
            int take = Math.Min(count, _prefix.Length - _pos);
            Buffer.BlockCopy(_prefix, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }
        throw new HttpRequestException("Simulated mid-stream reset");
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pos < _prefix.Length)
        {
            int take = Math.Min(buffer.Length, _prefix.Length - _pos);
            _prefix.AsSpan(_pos, take).CopyTo(buffer.Span);
            _pos += take;
            return ValueTask.FromResult(take);
        }
        throw new HttpRequestException("Simulated mid-stream reset");
    }
}

[Collection("DataDir")]
public class ResilienceTests : IDisposable
{
    private readonly string _dir;

    public ResilienceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdftools-resilience-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("PDFTOOLS_DATA_DIR", Path.Combine(_dir, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void RetryPolicy_FullJitterExponentialBackoff_RespectsBounds()
    {
        var d1 = RetryPolicy.ComputeBackoff(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), () => 1.0);
        var d2 = RetryPolicy.ComputeBackoff(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), () => 0.5);
        var d5 = RetryPolicy.ComputeBackoff(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), () => 1.0);

        Assert.Equal(TimeSpan.FromSeconds(1), d1);
        Assert.Equal(TimeSpan.FromSeconds(1), d2);  // 2s * 0.5
        Assert.Equal(TimeSpan.FromSeconds(10), d5); // clamped to MaxBackoff
    }

    [Fact]
    public async Task Retry_HonorsRetryAfterHeader_On429()
    {
        var time = new ManualTimeProvider();
        int startCalls = 0;
        var handler = new ScriptedHandler((req, _, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth"))
                return Task.FromResult(Json(200, "{\"token\":\"tok\"}"));
            startCalls++;
            if (startCalls == 1)
            {
                var r = Json(429, "{\"error\":{\"message\":\"slow down\"}}");
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                return Task.FromResult(r);
            }
            return Task.FromResult(Json(200, "{\"server\":\"s1.test\",\"task\":\"T1\"}"));
        });

        using var client = new ILoveApiClient("pub_k", http: new HttpClient(handler),
            resilience: new ResilienceOptions { BaseBackoff = TimeSpan.FromMilliseconds(10), MaxAttempts = 3 },
            timeProvider: time, jitterSource: () => 1.0);

        var startTask = client.StartAsync("compress");
        // Should still be waiting during Retry-After cooldown (4s < 5s)
        await Task.Delay(30);
        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(30);
        Assert.False(startTask.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));
        var res = await startTask;
        Assert.Equal("T1", res.Task);
        Assert.Equal(2, startCalls);
        Assert.Equal(1, client.TotalRetries);
    }

    [Fact]
    public async Task AuthRefresh_401_DoesNotConsumeTransientRetryBudget()
    {
        var time = new ManualTimeProvider();
        int startCalls = 0;
        int authCalls = 0;
        var handler = new ScriptedHandler((req, _, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth"))
            {
                var n = Interlocked.Increment(ref authCalls);
                return Task.FromResult(Json(200, $"{{\"token\":\"tok{n}\"}}"));
            }
            startCalls++;
            // Call 1: 401 expired token -> refresh (must not consume retry budget)
            // Call 2: 503 transient failure (attempt 1)
            // Call 3: 503 transient failure (attempt 2)
            // Call 4: 200 OK (attempt 3)
            return Task.FromResult(startCalls switch
            {
                1 => Json(401, "{}"),
                2 => Json(503, "{}"),
                3 => Json(503, "{}"),
                _ => Json(200, "{\"server\":\"s1.test\",\"task\":\"T_OK\"}")
            });
        });

        var options = new ResilienceOptions
        {
            MaxAttempts = 3,
            BaseBackoff = TimeSpan.Zero,
            CircuitFailureThreshold = 5
        };
        using var client = new ILoveApiClient("pub_k", http: new HttpClient(handler),
            resilience: options, timeProvider: time, jitterSource: () => 0.0);

        var task = await client.StartAsync("compress");
        Assert.Equal("T_OK", task.Task);
        Assert.Equal(2, authCalls);
        Assert.Equal(4, startCalls);
        Assert.Equal(2, client.TotalRetries);
    }

    [Fact]
    public async Task PerStageTimeout_TriggersRetryInsteadOfHanging()
    {
        var time = new ManualTimeProvider();
        int startCalls = 0;
        var handler = new ScriptedHandler(async (req, _, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth"))
                return Json(200, "{\"token\":\"tok\"}");
            startCalls++;
            if (startCalls == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // stalls until stage timeout cancels ct
            }
            return Json(200, "{\"server\":\"s1.test\",\"task\":\"T2\"}");
        });

        var options = new ResilienceOptions
        {
            AuthAndStartTimeout = TimeSpan.FromSeconds(5),
            BaseBackoff = TimeSpan.Zero,
            MaxAttempts = 2
        };
        using var client = new ILoveApiClient("pub_k", http: new HttpClient(handler),
            resilience: options, timeProvider: time, jitterSource: () => 0.0);

        var startTask = client.StartAsync("compress");
        await Task.Delay(30);
        Assert.False(startTask.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(6)); // trips 5s per-stage timeout
        var result = await startTask;
        Assert.Equal("T2", result.Task);
        Assert.Equal(2, startCalls);
    }

    [Fact]
    public async Task DownloadAsync_RetriesInterruptedStreamAndSucceeds()
    {
        var time = new ManualTimeProvider();
        int dlCalls = 0;
        var expected = Encoding.ASCII.GetBytes("%PDF-1.4 complete-body");
        var handler = new ScriptedHandler((req, _, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth"))
                return Task.FromResult(Json(200, "{\"token\":\"tok\"}"));
            dlCalls++;
            if (dlCalls == 1)
            {
                var broken = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FailingBodyStream(Encoding.ASCII.GetBytes("%PDF-")))
                };
                return Task.FromResult(broken);
            }
            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) };
            ok.Content.Headers.ContentLength = expected.Length;
            return Task.FromResult(ok);
        });

        var dest = Path.Combine(_dir, "dl.pdf");
        using var client = new ILoveApiClient("pub_k", http: new HttpClient(handler),
            resilience: new ResilienceOptions { BaseBackoff = TimeSpan.Zero, MaxAttempts = 3 },
            timeProvider: time, jitterSource: () => 0.0);

        await client.DownloadAsync(new ApiTask("s1.test", "T1"), dest);
        Assert.Equal(2, dlCalls);
        Assert.Equal(expected, File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task CircuitBreaker_OpensOnConsecutiveServerErrors_AndRecoversAfterBreak()
    {
        var time = new ManualTimeProvider();
        int startCalls = 0;
        bool failServer = true;
        var handler = new ScriptedHandler((req, _, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth"))
                return Task.FromResult(Json(200, "{\"token\":\"tok\"}"));
            startCalls++;
            return Task.FromResult(failServer
                ? Json(503, "{}")
                : Json(200, "{\"server\":\"s1.test\",\"task\":\"RECOVERED\"}"));
        });

        var options = new ResilienceOptions
        {
            MaxAttempts = 3,
            BaseBackoff = TimeSpan.Zero,
            CircuitFailureThreshold = 3,
            CircuitBreakDuration = TimeSpan.FromSeconds(30)
        };
        using var client = new ILoveApiClient("pub_k", http: new HttpClient(handler),
            resilience: options, timeProvider: time, jitterSource: () => 0.0);

        // First call exhausts 3 attempts -> 3 consecutive 503s -> opens circuit
        var ex1 = await Assert.ThrowsAsync<ApiException>(() => client.StartAsync("compress"));
        Assert.Equal(ApiErrorKind.Server, ex1.Kind);
        Assert.Equal(CircuitState.Open, client.CircuitBreaker.State);
        Assert.Equal(3, startCalls);

        // Second call fast-fails with CircuitOpen and zero extra HTTP calls
        var ex2 = await Assert.ThrowsAsync<ApiException>(() => client.StartAsync("compress"));
        Assert.Equal(ApiErrorKind.CircuitOpen, ex2.Kind);
        Assert.Equal(3, startCalls);

        // Advance past break duration -> HalfOpen probe succeeds -> Closed
        failServer = false;
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(CircuitState.HalfOpen, client.CircuitBreaker.State);

        var ok = await client.StartAsync("compress");
        Assert.Equal("RECOVERED", ok.Task);
        Assert.Equal(CircuitState.Closed, client.CircuitBreaker.State);
    }

    [Fact]
    public async Task CircuitBreaker_DoesNotTripOnPerFileRejections_LikeWrongPassword()
    {
        var breaker = new CircuitBreaker(failureThreshold: 2);
        var api = new FakeApi
        {
            ProcessStatus = 400,
            ProcessBody = "{\"error\":{\"type\":\"ProcessingError\",\"message\":\"x\",\"param\":[{\"status\":\"WrongPassword\"}]}}"
        };

        var pdfBytes = "%PDF-1.4 dummy-content"u8.ToArray();
        var f1 = Path.Combine(_dir, "a.pdf"); File.WriteAllBytes(f1, pdfBytes);
        var f2 = Path.Combine(_dir, "b.pdf"); File.WriteAllBytes(f2, pdfBytes);
        var f3 = Path.Combine(_dir, "c.pdf"); File.WriteAllBytes(f3, pdfBytes);

        using var client = new ILoveApiClient("pub_k", http: new HttpClient(api), circuitBreaker: breaker);
        var svc = new JobService(() => new ILoveApiClient("pub_k", http: new HttpClient(api), circuitBreaker: breaker), new AppSettings());
        var results = await svc.RunAsync(JobKind.Compress, new[] { f1, f2, f3 });

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Contains("password", r.Message));
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.TripCount);
    }

    [Fact]
    public async Task CircuitBreaker_TerminalAuthError_FastFailsRemainingBatchFiles()
    {
        var api = new FakeApi { AuthStatus = 401 };
        var pdfBytes = "%PDF-1.4 dummy-content"u8.ToArray();
        var files = Enumerable.Range(1, 5).Select(i =>
        {
            var p = Path.Combine(_dir, $"f{i}.pdf");
            File.WriteAllBytes(p, pdfBytes);
            return p;
        }).ToList();

        var svc = new JobService(() => api.NewClient(), new AppSettings());
        var results = await svc.RunAsync(JobKind.Compress, files);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal(JobStatus.Failed, r.Status));
        // Only 1 /auth call should be made (cached/locked + terminal circuit trip fast-fails the rest)
        Assert.Equal(1, api.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/auth")));
    }

    [Fact]
    public async Task AdaptiveRateLimiter_PacesBurstAndHonorsCooldown()
    {
        var time = new ManualTimeProvider();
        var limiter = new AdaptiveRateLimiter(maxRequestsPerSecond: 2.0, burstCapacity: 2, time: time);

        // First 2 tokens succeed immediately
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        // 3rd token must wait 500ms
        var third = limiter.WaitAsync();
        await Task.Delay(25);
        Assert.False(third.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(500));
        await third;

        // ApplyRetryAfter blocks all callers for 3s
        limiter.ApplyRetryAfter(TimeSpan.FromSeconds(3));
        var fourth = limiter.WaitAsync();
        await Task.Delay(25);
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(25);
        Assert.False(fourth.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        await fourth;
    }
}

