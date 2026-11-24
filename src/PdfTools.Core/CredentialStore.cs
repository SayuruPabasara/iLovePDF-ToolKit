using System.Security.Cryptography;
using System.Text;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Observability;

namespace PdfTools.Core;

/// <summary>
/// Stores the iLoveAPI project public key encrypted with Windows DPAPI (current-user scope),
/// so it is never written to disk in plain text and cannot be read by other Windows accounts.
/// </summary>
public static class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PdfTools.iLoveApi.v1");

    public static bool HasPublicKey() => !string.IsNullOrEmpty(LoadPublicKey());

    public static string? LoadPublicKey()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!File.Exists(AppPaths.CredentialFile)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.CredentialFile), Entropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plain).Trim();
            return key.Length == 0 ? null : key;
        }
        catch (CryptographicException ex) { DiagnosticsLog.Write("CredentialStore", "Could not decrypt stored API key", ex); return null; }
        catch (IOException ex) { DiagnosticsLog.Write("CredentialStore", "Could not read credentials file", ex); return null; }
    }

    public static void SavePublicKey(string key)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI credential storage requires Windows.");
        key = (key ?? "").Trim();
        if (key.Length == 0) throw new ArgumentException("Key is empty.", nameof(key));
        AppPaths.EnsureDataDir();
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        AtomicFile.WriteAllBytes(AppPaths.CredentialFile, cipher);
        DpapiTokenCache.ClearFile();
    }

    public static void Clear()
    {
        try { if (File.Exists(AppPaths.CredentialFile)) File.Delete(AppPaths.CredentialFile); } catch (IOException) { }
        DpapiTokenCache.ClearFile();
    }
}
