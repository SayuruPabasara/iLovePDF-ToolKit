namespace PdfTools.Core.Resilience;

/// <summary>
/// Thread-safe client-side token-bucket rate limiter with adaptive Retry-After cooldown shared
/// across concurrent workers.
/// </summary>
public sealed class AdaptiveRateLimiter
{
    private readonly object _lock = new();
    private readonly double _tokensPerSecond;
    private readonly double _burstCapacity;
    private readonly TimeProvider _time;

    private double _tokens;
    private DateTimeOffset _lastRefill;
    private DateTimeOffset _notBefore;

    public AdaptiveRateLimiter(double maxRequestsPerSecond = 6.0, int burstCapacity = 10, TimeProvider? time = null)
    {
        _tokensPerSecond = Math.Max(0.1, maxRequestsPerSecond);
        _burstCapacity = Math.Max(1, burstCapacity);
        _time = time ?? TimeProvider.System;
        _tokens = _burstCapacity;
        _lastRefill = _time.GetUtcNow();
        _notBefore = DateTimeOffset.MinValue;
    }

    /// <summary>Pauses all concurrent callers until at least <paramref name="delay"/> has elapsed.</summary>
    public void ApplyRetryAfter(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        lock (_lock)
        {
            var target = _time.GetUtcNow() + delay;
            if (target > _notBefore) _notBefore = target;
            // Also drain partial burst so workers ramp back smoothly after cooldown.
            _tokens = Math.Min(_tokens, 1.0);
        }
    }

    /// <summary>Waits until both any active Retry-After cooldown has cleared and a rate token is available.</summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan wait;
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                RefillLocked(now);

                if (now < _notBefore)
                {
                    wait = _notBefore - now;
                }
                else if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }
                else
                {
                    var deficit = 1.0 - _tokens;
                    wait = TimeSpan.FromSeconds(deficit / _tokensPerSecond);
                    if (wait < TimeSpan.FromMilliseconds(1)) wait = TimeSpan.FromMilliseconds(1);
                }
            }

            await RetryPolicy.DelayAsync(wait, _time, ct);
        }
    }

    private void RefillLocked(DateTimeOffset now)
    {
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0) return;
        _tokens = Math.Min(_burstCapacity, _tokens + elapsed * _tokensPerSecond);
        _lastRefill = now;
    }
}

