using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfTools.Core.Observability;

/// <summary>
/// Standard .NET ActivitySource tracing and Meter metrics (zero external dependencies).
/// </summary>
public static class Telemetry
{
    public const string SourceName = "PdfTools.Core";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    public static readonly Counter<long> JobsTotal =
        Meter.CreateCounter<long>("pdftools.jobs.total", description: "Total completed job items by operation and status.");

    public static readonly Histogram<double> JobDurationMs =
        Meter.CreateHistogram<double>("pdftools.jobs.duration_ms", unit: "ms", description: "End-to-end job item latency.");

    public static readonly Counter<long> BytesSaved =
        Meter.CreateCounter<long>("pdftools.bytes.saved", unit: "By", description: "Total bytes saved by PDF compression.");

    public static readonly Counter<long> ApiRetries =
        Meter.CreateCounter<long>("pdftools.api.retries", description: "Total HTTP retries performed against iLoveAPI.");

    public static readonly Counter<long> CircuitTrips =
        Meter.CreateCounter<long>("pdftools.circuit.trips", description: "Total times the API circuit breaker tripped.");

    public static Activity? StartSpan(string name, string? correlationId = null, ActivityKind kind = ActivityKind.Internal)
    {
        var act = ActivitySource.StartActivity(name, kind);
        if (act != null && !string.IsNullOrEmpty(correlationId))
            act.SetTag("pdftools.correlation_id", correlationId);
        return act;
    }
}

/// <summary>
/// Privacy-safe rolling internal diagnostic log (%AppData%\PdfTools\logs\diag-YYYYMMDD.log)
/// capturing internal/recovered errors that were previously silently swallowed.
/// Always redacts full directory paths and API keys/tokens.
/// </summary>
public static partial class DiagnosticsLog
{
    private const long MaxDailyBytes = 2L * 1024 * 1024; // 2 MB cap per day
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record DiagEntry(
        DateTimeOffset Time,
        string Component,
        string Message,
        string? ExceptionType,
        string? CorrelationId);

    public static void Write(string component, string message, Exception? ex = null, string? correlationId = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var now = DateTimeOffset.Now;
            var path = Path.Combine(AppPaths.LogDir, $"diag-{now.LocalDateTime:yyyyMMdd}.log");

            if (File.Exists(path) && new FileInfo(path).Length >= MaxDailyBytes) return;

            var combined = ex == null ? message : $"{message}: {ex.Message}";
            var entry = new DiagEntry(now, component, Redact(combined), ex?.GetType().Name, correlationId);
            var line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
            var bytes = new UTF8Encoding(false).GetBytes(line);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) { Thread.Sleep(25 * (attempt + 1)); }
            }
        }
        catch { /* diagnostics must never throw */ }
    }

    /// <summary>Strips directory prefixes from paths and masks bearer tokens / iLoveAPI keys.</summary>
    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = WindowsPathRegex().Replace(text, m =>
        {
            var leaf = Path.GetFileName(m.Value.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(leaf) ? "<path>" : $"<path>\\{leaf}";
        });
        text = KeyRegex().Replace(text, "<redacted-key>");
        text = BearerRegex().Replace(text, "Bearer <redacted>");
        return text;
    }

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\s""'<>|?*]+\\)+[^\s""'<>|?*]*", RegexOptions.Compiled)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"project_(?:public|secret)_[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BearerRegex();
}

