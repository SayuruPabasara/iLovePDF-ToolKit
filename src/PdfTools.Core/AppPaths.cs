namespace PdfTools.Core;

/// <summary>Well-known per-user locations. Override with PDFTOOLS_DATA_DIR (used by tests).</summary>
public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var o = Environment.GetEnvironmentVariable("PDFTOOLS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(o)) return o;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfTools");
        }
    }

    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string CredentialFile => Path.Combine(DataDir, "credentials.dat");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
