using System.Net.Http.Headers;

namespace PdfTools.Core.Resilience;

/// <summary>Exponential backoff with full jitter and HTTP Retry-After parsing.</summary>
public static class RetryPolicy
{
    /// <summary>
    /// Full-jitter exponential backoff: uniform random in [0, min(maxDelay, baseDelay * 2^(retryNumber - 1))].
    /// </summary>
    public static TimeSpan ComputeBackoff(int retryNumber, TimeSpan baseDelay, TimeSpan maxDelay, Func<double>? nextDouble = null)
    {
        if (retryNumber < 1) retryNumber = 1;
        var factor = Math.Pow(2, Math.Min(retryNumber - 1, 10));
        var ceilingMs = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * factor);
        if (ceilingMs <= 0) return TimeSpan.Zero;

        var u = nextDouble != null ? nextDouble() : Random.Shared.NextDouble();
        u = Math.Clamp(u, 0.0, 1.0);
        return TimeSpan.FromMilliseconds(ceilingMs * u);
    }

    /// <summary>Parses a Retry-After header (delta-seconds or HTTP-date), clamped to [0, maxDelay].</summary>
    public static bool TryParseRetryAfter(HttpResponseHeaders headers, TimeProvider time, TimeSpan maxDelay, out TimeSpan delay)
    {
        var ra = headers.RetryAfter;
        if (ra != null)
        {
            if (ra.Delta.HasValue)
            {
                delay = Clamp(ra.Delta.Value, maxDelay);
                return true;
            }
            if (ra.Date.HasValue)
            {
                delay = Clamp(ra.Date.Value - time.GetUtcNow(), maxDelay);
                return true;
            }
        }
        delay = TimeSpan.Zero;
        return false;
    }

    public static Task DelayAsync(TimeSpan delay, TimeProvider time, CancellationToken ct)
        => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, time, ct);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan max)
    {
        if (value <= TimeSpan.Zero) return TimeSpan.Zero;
        return value > max ? max : value;
    }
}

