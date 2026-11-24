using PdfTools.Core.ILoveApi;

namespace PdfTools.Core.Resilience;

public enum CircuitState { Closed, Open, HalfOpen }

/// <summary>
/// In-memory circuit breaker protecting iLoveAPI calls from cascading retries during outages,
/// invalid keys, or exhausted credits while isolating per-file rejections (e.g. wrong password).
/// </summary>
public sealed class CircuitBreaker
{
    private readonly object _lock = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _breakDuration;
    private readonly TimeProvider _time;

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil;
    private bool _halfOpenProbeInFlight;
    private bool _terminalTrip;
    private ApiErrorKind _tripKind = ApiErrorKind.CircuitOpen;
    private string _tripMessage = "iLoveAPI is temporarily unavailable. Try again in a moment.";
    private int _tripCount;

    public CircuitBreaker(int failureThreshold = 3, TimeSpan? breakDuration = null, TimeProvider? time = null)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _breakDuration = breakDuration ?? TimeSpan.FromSeconds(30);
        _time = time ?? TimeProvider.System;
    }

    public int TripCount { get { lock (_lock) return _tripCount; } }

    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitState.Open && !_terminalTrip && _time.GetUtcNow() >= _openUntil)
                    return CircuitState.HalfOpen;
                return _state;
            }
        }
    }

    /// <summary>
    /// Checks whether a request may proceed. Throws <see cref="ApiException"/> immediately when open.
    /// Returns true if this call is acting as the single Half-Open probe.
    /// </summary>
    public bool BeforeCall(ApiStage stage)
    {
        lock (_lock)
        {
            if (_state == CircuitState.Closed) return false;

            if (_state == CircuitState.Open)
            {
                if (_terminalTrip || _time.GetUtcNow() < _openUntil)
                    throw new ApiException(_tripKind, _tripMessage, stage: stage);

                _state = CircuitState.HalfOpen;
                _halfOpenProbeInFlight = true;
                return true;
            }

            // HalfOpen: allow only one concurrent probe; fail-fast others until the probe resolves.
            if (_halfOpenProbeInFlight)
                throw new ApiException(_tripKind, _tripMessage, stage: stage);

            _halfOpenProbeInFlight = true;
            return true;
        }
    }

    /// <summary>Records a healthy response from the service (including per-file 4xx rejections).</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _halfOpenProbeInFlight = false;
            if (!_terminalTrip)
                _state = CircuitState.Closed;
        }
    }

    /// <summary>
    /// Releases a Half-Open probe slot without counting a failure (e.g. caller cancelled or 429 rate-limited).
    /// </summary>
    public void ReleaseProbe()
    {
        lock (_lock)
        {
            _halfOpenProbeInFlight = false;
        }
    }

    /// <summary>Records a transient upstream/network fault; opens the circuit once threshold is reached.</summary>
    public void RecordTransientFailure(ApiErrorKind kind, string? message = null)
    {
        if (kind is not (ApiErrorKind.Network or ApiErrorKind.Server)) return;

        lock (_lock)
        {
            _halfOpenProbeInFlight = false;
            _consecutiveFailures++;

            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= _failureThreshold)
            {
                if (_state != CircuitState.Open) _tripCount++;
                _state = CircuitState.Open;
                _openUntil = _time.GetUtcNow() + _breakDuration;
                _tripKind = ApiErrorKind.CircuitOpen;
                _tripMessage = message ?? "iLoveAPI is temporarily unreachable. Pausing requests briefly — try again in a moment.";
            }
        }
    }

    /// <summary>
    /// Immediately opens the circuit for account-level terminal faults (Auth / Credits) so remaining
    /// files in a batch fail fast without hammering iLoveAPI.
    /// </summary>
    public void TripTerminal(ApiErrorKind kind, string message)
    {
        if (kind is not (ApiErrorKind.Auth or ApiErrorKind.Credits)) return;

        lock (_lock)
        {
            if (!_terminalTrip) _tripCount++;
            _terminalTrip = true;
            _state = CircuitState.Open;
            _halfOpenProbeInFlight = false;
            _tripKind = kind;
            _tripMessage = message;
        }
    }

    /// <summary>Resets a terminal trip (e.g. when a new batch starts after the user updates their key).</summary>
    public void ResetTerminal()
    {
        lock (_lock)
        {
            if (!_terminalTrip) return;
            _terminalTrip = false;
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
            _halfOpenProbeInFlight = false;
        }
    }
}

