using System.Text.Json;

namespace PdfTools.Core;

/// <summary>One log line. Only file NAMES are logged (never paths or contents).</summary>
public sealed record LogEntry(
    DateTimeOffset Time,
    string Operation,
    string[] Inputs,
    string? Output,
    string Status,
    string? Message,
    double? Seconds);

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
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Write(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var path = Path.Combine(AppPaths.LogDir, $"pdftools-{entry.Time.LocalDateTime:yyyyMMdd}.log");
            var line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(line);
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

    /// <summary>Deletes log files older than <paramref name="retentionDays"/>.</summary>
    public static void CleanOld(int retentionDays)
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogDir, "pdftools-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }
}

public static class LogReader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Newest entries first. Corrupt lines are skipped.</summary>
    public static IReadOnlyList<LogEntry> ReadRecent(int maxEntries = 2000)
    {
        var result = new List<LogEntry>();
        try
        {
            if (!Directory.Exists(AppPaths.LogDir)) return result;
            var files = Directory.GetFiles(AppPaths.LogDir, "pdftools-*.log");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(files);
            foreach (var f in files)
            {
                string[] lines;
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                }
                catch (IOException) { continue; }

                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    try
                    {
                        var e = JsonSerializer.Deserialize<LogEntry>(lines[i].Trim(), Json);
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
}
