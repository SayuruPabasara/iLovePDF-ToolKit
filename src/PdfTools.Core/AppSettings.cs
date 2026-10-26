using System.Text.Json;

namespace PdfTools.Core;

/// <summary>Non-secret user settings, stored as JSON in %AppData%\PdfTools.</summary>
public sealed class AppSettings
{
    public static readonly string[] Regions = { "", "eu", "us", "fr", "de", "pl", "in", "sg" };
    public static readonly string[] CompressionLevels = { "low", "recommended", "extreme" };

    /// <summary>Processing region/jurisdiction. Empty = let iLoveAPI choose.</summary>
    public string Region { get; set; } = "";

    /// <summary>low | recommended | extreme</summary>
    public string CompressionLevel { get; set; } = "recommended";

    /// <summary>How many days of log files to keep.</summary>
    public int LogRetentionDays { get; set; } = 30;

    public void Normalize()
    {
        Region = (Region ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(Regions, Region) < 0) Region = "";
        CompressionLevel = (CompressionLevel ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(CompressionLevels, CompressionLevel) < 0) CompressionLevel = "recommended";
        if (LogRetentionDays < 1 || LogRetentionDays > 365) LogRetentionDays = 30;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
                if (s != null) { s.Normalize(); return s; }
            }
        }
        catch { /* corrupt file -> defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        Normalize();
        AppPaths.EnsureDataDir();
        AtomicFile.WriteAllText(AppPaths.SettingsFile,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
