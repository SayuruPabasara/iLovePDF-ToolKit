namespace PdfTools.Core.Resilience;

/// <summary>Configuration for HTTP retries, per-stage timeouts, circuit breaking, and client-side rate limiting.</summary>
public sealed record ResilienceOptions
{
    public static ResilienceOptions Default { get; } = new();

    /// <summary>Total attempts for a single API operation (1 initial + MaxAttempts - 1 retries).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Initial delay for exponential backoff with full jitter.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for computed exponential backoff.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound when honoring a server Retry-After header.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Per-request timeout for /auth and /start calls.</summary>
    public TimeSpan AuthAndStartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Per-request timeout for /process calls.</summary>
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Per-request timeout for /upload and /download transfers.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Per-request timeout for best-effort /task DELETE calls.</summary>
    public TimeSpan DeleteTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Consecutive transient service/network failures before the circuit opens.</summary>
    public int CircuitFailureThreshold { get; init; } = 3;

    /// <summary>How long the circuit stays open before allowing a single half-open probe.</summary>
    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Sustained outgoing request rate per second.</summary>
    public double MaxRequestsPerSecond { get; init; } = 6.0;

    /// <summary>Maximum immediate burst tokens before pacing kicks in.</summary>
    public int RateLimiterBurst { get; init; } = 10;
}

