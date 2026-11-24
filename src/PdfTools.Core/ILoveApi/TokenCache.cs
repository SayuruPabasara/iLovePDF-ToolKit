using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfTools.Core.ILoveApi;

public interface ITokenCache
{
    bool TryGet(string cacheKey, DateTimeOffset now, out string token, out DateTimeOffset expiresUtc);
    void Set(string cacheKey, string token, DateTimeOffset expiresUtc);
    void Clear();
}

/// <summary>No-op token cache (always misses).</summary>
public sealed class NullTokenCache : ITokenCache
{
    public static NullTokenCache Instance { get; } = new();
    public bool TryGet(string cacheKey, DateTimeOffset now, out string token, out DateTimeOffset expiresUtc)
    { token = ""; expiresUtc = default; return false; }
    public void Set(string cacheKey, string token, DateTimeOffset expiresUtc) { }
    public void Clear() { }
}

/// <summary>Thread-safe in-memory token cache (for tests or non-Windows platforms).</summary>
public sealed class InMemoryTokenCache : ITokenCache
{
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(2);
    private readonly object _lock = new();
    private string? _key;
    private string? _token;
    private DateTimeOffset _expires;

    public bool TryGet(string cacheKey, DateTimeOffset now, out string token, out DateTimeOffset expiresUtc)
    {
        lock (_lock)
        {
            if (_token != null && _key == cacheKey && _expires - now > SafetyMargin)
            {
                token = _token;
                expiresUtc = _expires;
                return true;
            }
        }
        token = "";
        expiresUtc = default;
        return false;
    }

    public void Set(string cacheKey, string token, DateTimeOffset expiresUtc)
    {
        lock (_lock)
        {
            _key = cacheKey;
            _token = token;
            _expires = expiresUtc;
        }
    }

    public void Clear()
    {
        lock (_lock) { _key = null; _token = null; _expires = default; }
    }
}

/// <summary>
/// Cross-process JWT token cache stored DPAPI-encrypted (current-user scope) in %AppData%\PdfTools\token.cache.
/// Keyed by SHA-256(publicKey + apiBase) so changing the API key invalidates old tokens automatically.
/// </summary>
public sealed class DpapiTokenCache : ITokenCache
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PdfTools.iLoveApi.Token.v1");
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(2);

    public static DpapiTokenCache Shared { get; } = new();

    private sealed record CachedToken(string KeyHash, string Token, DateTimeOffset ExpiresUtc);

    public static string ComputeCacheKey(string publicKey, Uri apiBase)
    {
        var raw = Encoding.UTF8.GetBytes(publicKey.Trim() + "|" + apiBase.AbsoluteUri.TrimEnd('/'));
        return Convert.ToHexString(SHA256.HashData(raw));
    }

    public bool TryGet(string cacheKey, DateTimeOffset now, out string token, out DateTimeOffset expiresUtc)
    {
        token = "";
        expiresUtc = default;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var path = AppPaths.TokenCacheFile;
            if (!File.Exists(path)) return false;
            var cipher = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            var entry = JsonSerializer.Deserialize<CachedToken>(plain);
            if (entry == null || string.IsNullOrEmpty(entry.Token)) return false;
            if (!string.Equals(entry.KeyHash, cacheKey, StringComparison.Ordinal)) return false;
            if (entry.ExpiresUtc - now <= SafetyMargin) return false;

            token = entry.Token;
            expiresUtc = entry.ExpiresUtc;
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    public void Set(string cacheKey, string token, DateTimeOffset expiresUtc)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            AppPaths.EnsureDataDir();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new CachedToken(cacheKey, token, expiresUtc));
            var cipher = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            AtomicFile.WriteAllBytes(AppPaths.TokenCacheFile, cipher);
        }
        catch (CryptographicException) { }
        catch (IOException) { }
    }

    public void Clear() => ClearFile();

    public static void ClearFile()
    {
        try
        {
            var path = AppPaths.TokenCacheFile;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
    }
}

