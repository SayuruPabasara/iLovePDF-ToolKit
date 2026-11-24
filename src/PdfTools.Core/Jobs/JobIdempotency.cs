using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfTools.Core.Jobs;

/// <summary>Deterministic fingerprint helpers for in-flight deduplication and NoGain caching.</summary>
public static class JobIdempotency
{
    /// <summary>
    /// Computes an opaque SHA-256 fingerprint from operation kind, canonical input file identities
    /// (path + size + LastWriteTimeUtc), and operation-relevant settings.
    /// </summary>
    public static string ComputeFingerprint(JobKind kind, IReadOnlyList<string> inputs, AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.Append(kind).Append('|');
        if (kind == JobKind.Compress)
            sb.Append(settings.CompressionLevel).Append('|');

        foreach (var path in inputs)
        {
            var full = SafeFullPath(path).ToLowerInvariant();
            sb.Append(full).Append(':');
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                    sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
            }
            catch (IOException) { }
            sb.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }
}

/// <summary>Thread-safe tracker preventing duplicate concurrent execution of identical work units.</summary>
public sealed class InFlightJobTracker
{
    private readonly object _lock = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public bool IsActive(string fingerprint)
    {
        lock (_lock) return _active.Contains(fingerprint);
    }

    /// <summary>Attempts to claim <paramref name="fingerprint"/>. Returns null if already active.</summary>
    public IDisposable? TryEnter(string fingerprint)
    {
        lock (_lock)
        {
            if (!_active.Add(fingerprint)) return null;
        }
        return new Lease(this, fingerprint);
    }

    private void Release(string fingerprint)
    {
        lock (_lock) _active.Remove(fingerprint);
    }

    private sealed class Lease : IDisposable
    {
        private readonly InFlightJobTracker _owner;
        private readonly string _fingerprint;
        private int _disposed;

        public Lease(InFlightJobTracker owner, string fingerprint)
        {
            _owner = owner;
            _fingerprint = fingerprint;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Release(_fingerprint);
        }
    }
}

public interface INoGainCache
{
    bool Contains(string fingerprint, DateTimeOffset now);
    void Record(string fingerprint, DateTimeOffset expiresUtc);
}

public sealed class NullNoGainCache : INoGainCache
{
    public static NullNoGainCache Instance { get; } = new();
    public bool Contains(string fingerprint, DateTimeOffset now) => false;
    public void Record(string fingerprint, DateTimeOffset expiresUtc) { }
}

/// <summary>
/// Bounded JSON cache of opaque SHA-256 fingerprints for PDFs that previously yielded NoGain on compression.
/// Stores only hashes and expiry timestamps (never filenames or paths).
/// </summary>
public sealed class FileNoGainCache : INoGainCache
{
    private const int MaxEntries = 500;
    private readonly object _lock = new();

    public static FileNoGainCache Shared { get; } = new();

    public bool Contains(string fingerprint, DateTimeOffset now)
    {
        lock (_lock)
        {
            var map = LoadLocked();
            return map.TryGetValue(fingerprint, out var exp) && exp > now;
        }
    }

    public void Record(string fingerprint, DateTimeOffset expiresUtc)
    {
        lock (_lock)
        {
            var map = LoadLocked();
            var now = DateTimeOffset.UtcNow;
            // Prune expired entries first
            foreach (var k in map.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                map.Remove(k);

            map[fingerprint] = expiresUtc;

            // Bound total size by evicting earliest-expiring entries
            if (map.Count > MaxEntries)
            {
                foreach (var k in map.OrderBy(kv => kv.Value).Take(map.Count - MaxEntries).Select(kv => kv.Key).ToList())
                    map.Remove(k);
            }

            try
            {
                AppPaths.EnsureDataDir();
                AtomicFile.WriteAllText(AppPaths.NoGainCacheFile, JsonSerializer.Serialize(map));
            }
            catch (IOException) { }
        }
    }

    private static Dictionary<string, DateTimeOffset> LoadLocked()
    {
        try
        {
            if (!File.Exists(AppPaths.NoGainCacheFile))
                return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var map = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(AppPaths.NoGainCacheFile));
            return map != null
                ? new Dictionary<string, DateTimeOffset>(map, StringComparer.Ordinal)
                : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }
}

