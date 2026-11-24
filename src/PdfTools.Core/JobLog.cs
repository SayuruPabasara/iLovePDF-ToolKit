using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfTools.Core;

/// <summary>Per-stage durations in milliseconds for a single job unit.</summary>
public sealed record StageTimings(
    int? AuthMs = null,
    int? StartMs = null,
    int? UploadMs = null,
    int? ProcessMs = null,
    int? DownloadMs = null);

/// <summary>One log line. Only file NAMES are logged (never full directory paths or contents).</summary>
public sealed record LogEntry(
    DateTimeOffset Time,
    string Operation,
    string[] Inputs,
    string? Output,
    string Status,
    string? Message,
    double? Seconds,
    string? CorrelationId = null,
    string? ApiTaskId = null,
    string? ApiServer = null,
    string? FailedStage = null,
    string? ErrorKind = null,
    int? HttpStatus = null,
    int? RetryCount = null,
    long? InputBytes = null,
    long? OutputBytes = null,
    StageTimings? Timings = null);

public interface IJobLog
{
    void Write(LogEntry entry);
}

public sealed class NullJobLog : IJobLog
{
    public void Write(LogEntry entry) { }
}

/// <summary>Daily rolling JSON-lines log in %AppData%\PdfTools\logs. Safe for several processes writing at once.</summary>
public sealed class FileJobLog : IJobLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Write(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var path = Path.Combine(AppPaths.LogDir, $"pdftools-{entry.Time.LocalDateTime:yyyyMMdd}.log");
            var line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
            var bytes = new UTF8Encoding(false).GetBytes(line);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) { Thread.Sleep(40 * (attempt + 1)); }
            }
        }
        catch { /* logging must never break a job */ }
    }

    /// <summary>Deletes activity and diagnostic log files older than <paramref name="retentionDays"/>.</summary>
    public static void CleanOld(int retentionDays)
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var pattern in new[] { "pdftools-*.log", "diag-*.log" })
            {
                foreach (var f in Directory.EnumerateFiles(AppPaths.LogDir, pattern))
                    if (File.GetLastWriteTime(f) < cutoff) { try { File.Delete(f); } catch { } }
            }
        }
        catch { }
    }
}

/// <summary>Aggregated client-side metrics derived from recent activity log entries.</summary>
public sealed record LogMetricsSummary(
    int TotalJobs,
    int SuccessCount,
    int NoGainCount,
    int SkippedCount,
    int FailedCount,
    double SuccessRatePercent,
    long TotalBytesSaved,
    double AverageSeconds,
    int TotalRetries,
    IReadOnlyDictionary<string, int> ErrorsByKind)
{
    public static LogMetricsSummary Compute(IReadOnlyList<LogEntry> entries)
    {
        int ok = 0, noGain = 0, skipped = 0, failed = 0, retries = 0;
        long bytesSaved = 0;
        double secSum = 0;
        int secCount = 0;
        var errorKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            switch (e.Status)
            {
                case "Success":
                    ok++;
                    if (e.InputBytes.HasValue && e.OutputBytes.HasValue && e.InputBytes.Value > e.OutputBytes.Value)
                        bytesSaved += e.InputBytes.Value - e.OutputBytes.Value;
                    break;
                case "NoGain":
                    noGain++;
                    break;
                case "Skipped":
                    skipped++;
                    break;
                default:
                    failed++;
                    var k = string.IsNullOrWhiteSpace(e.ErrorKind) ? "Other" : e.ErrorKind;
                    errorKinds[k] = errorKinds.TryGetValue(k, out var c) ? c + 1 : 1;
                    break;
            }

            if (e.Seconds.HasValue && e.Seconds.Value > 0)
            {
                secSum += e.Seconds.Value;
                secCount++;
            }
            if (e.RetryCount.HasValue && e.RetryCount.Value > 0)
                retries += e.RetryCount.Value;
        }

        int denom = ok + noGain + failed;
        double rate = denom == 0 ? 100.0 : Math.Round(100.0 * (ok + noGain) / denom, 1);
        double avgSec = secCount == 0 ? 0.0 : Math.Round(secSum / secCount, 1);

        return new LogMetricsSummary(
            entries.Count, ok, noGain, skipped, failed, rate, bytesSaved, avgSec, retries, errorKinds);
    }
}

public static class LogReader
{
    private const int ChunkSize = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Newest entries first. Reads files from the end in bounded chunks; corrupt lines are skipped.</summary>
    public static IReadOnlyList<LogEntry> ReadRecent(int maxEntries = 2000)
    {
        var result = new List<LogEntry>();
        if (maxEntries <= 0) return result;
        try
        {
            if (!Directory.Exists(AppPaths.LogDir)) return result;
            var files = Directory.GetFiles(AppPaths.LogDir, "pdftools-*.log");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(files);
            foreach (var f in files)
            {
                foreach (var line in ReadLinesReverse(f))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<LogEntry>(trimmed, Json);
                        if (e != null) result.Add(e);
                    }
                    catch (JsonException) { }
                    if (result.Count >= maxEntries) return result;
                }
            }
        }
        catch { }
        return result;
    }

    public static Task<IReadOnlyList<LogEntry>> ReadRecentAsync(int maxEntries = 2000, CancellationToken ct = default)
        => Task.Run(() => ReadRecent(maxEntries), ct);

    /// <summary>Streams UTF-8 lines in reverse order without loading the entire file into memory.</summary>
    internal static IEnumerable<string> ReadLinesReverse(string path)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            yield break;
        }

        using (fs)
        {
            long pos = fs.Length;
            if (pos == 0) yield break;

            var buf = new byte[ChunkSize];
            byte[] carry = Array.Empty<byte>(); // bytes belonging to the start of the line from the next (later) chunk

            while (pos > 0)
            {
                int toRead = (int)Math.Min(ChunkSize, pos);
                pos -= toRead;
                fs.Seek(pos, SeekOrigin.Begin);
                int read = ReadExact(fs, buf, toRead);
                if (read == 0) break;

                int lineEnd = read;
                for (int i = read - 1; i >= 0; i--)
                {
                    if (buf[i] == (byte)'\n')
                    {
                        int segStart = i + 1;
                        int segLen = lineEnd - segStart;
                        if (segLen > 0 || carry.Length > 0)
                            yield return DecodeLine(buf, segStart, segLen, carry);
                        carry = Array.Empty<byte>();
                        lineEnd = i;
                    }
                }

                // Prefix before the first '\n' in this chunk connects to the previous (earlier) chunk.
                if (lineEnd > 0)
                {
                    var nextCarry = new byte[lineEnd + carry.Length];
                    Buffer.BlockCopy(buf, 0, nextCarry, 0, lineEnd);
                    if (carry.Length > 0)
                        Buffer.BlockCopy(carry, 0, nextCarry, lineEnd, carry.Length);
                    carry = nextCarry;
                }
            }

            if (carry.Length > 0)
                yield return Encoding.UTF8.GetString(carry).TrimEnd('\r');
        }
    }

    private static int ReadExact(FileStream fs, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = fs.Read(buf, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static string DecodeLine(byte[] chunk, int offset, int count, byte[] carry)
    {
        if (carry.Length == 0)
            return Encoding.UTF8.GetString(chunk, offset, count).TrimEnd('\r');

        var combined = new byte[count + carry.Length];
        if (count > 0) Buffer.BlockCopy(chunk, offset, combined, 0, count);
        Buffer.BlockCopy(carry, 0, combined, count, carry.Length);
        return Encoding.UTF8.GetString(combined).TrimEnd('\r');
    }
}

/// <summary>Builds a privacy-safe diagnostic summary (zero paths, zero keys) for clipboard export.</summary>
public static class DiagnosticReportBuilder
{
    public static string Build(AppSettings settings, IReadOnlyList<LogEntry> entries, int maxRecentFailures = 10)
    {
        var summary = LogMetricsSummary.Compute(entries);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        var sb = new StringBuilder();
        sb.AppendLine($"iLovePDF Tools Diagnostics (v{version})");
        sb.AppendLine($"OS: {Environment.OSVersion} | .NET: {Environment.Version}");
        sb.AppendLine($"Settings: Region={(string.IsNullOrEmpty(settings.Region) ? "auto" : settings.Region)}, Compression={settings.CompressionLevel}, Retention={settings.LogRetentionDays}d");
        sb.AppendLine($"Summary ({summary.TotalJobs} jobs): {summary.SuccessCount} ok, {summary.NoGainCount} no-gain, {summary.SkippedCount} skipped, {summary.FailedCount} failed ({summary.SuccessRatePercent:0.#}% success)");
        sb.AppendLine($"Metrics: Saved={OutputNaming.FormatSize(summary.TotalBytesSaved)}, AvgTime={summary.AverageSeconds:0.#}s, Retries={summary.TotalRetries}");

        if (summary.ErrorsByKind.Count > 0)
        {
            sb.Append("Errors by kind: ");
            sb.AppendLine(string.Join(", ", summary.ErrorsByKind.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        var failures = entries.Where(e => e.Status == "Failed").Take(maxRecentFailures).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent failures:");
            foreach (var f in failures)
            {
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(f.CorrelationId)) meta.Add($"cid={f.CorrelationId}");
                if (!string.IsNullOrEmpty(f.ApiServer)) meta.Add($"srv={f.ApiServer}");
                if (!string.IsNullOrEmpty(f.FailedStage)) meta.Add($"stage={f.FailedStage}");
                if (!string.IsNullOrEmpty(f.ErrorKind)) meta.Add($"kind={f.ErrorKind}");
                if (f.HttpStatus.HasValue) meta.Add($"http={f.HttpStatus}");
                if (f.RetryCount is > 0) meta.Add($"retries={f.RetryCount}");
                var metaText = meta.Count > 0 ? $" [{string.Join(" ", meta)}]" : "";
                sb.AppendLine($"- {f.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss} | {f.Operation}{metaText}: {Observability.DiagnosticsLog.Redact(f.Message ?? "")}");
            }
        }
        return sb.ToString();
    }
}
