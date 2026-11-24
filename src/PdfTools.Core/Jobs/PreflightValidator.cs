namespace PdfTools.Core.Jobs;

/// <summary>
/// Fast local pre-flight checks run before calling iLoveAPI so corrupt, misnamed, or unwritable jobs
/// fail immediately without wasting bandwidth or API credits.
/// </summary>
public static class PreflightValidator
{
    public const long DefaultMaxFileBytes = 250L * 1024 * 1024; // 250 MB sanity ceiling

    private static readonly string[] PdfExt = { ".pdf" };
    private static readonly string[] OfficeExt = { ".doc", ".docx", ".ppt", ".pptx" };
    private static readonly string[] ImageExt = { ".jpg", ".jpeg" };

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] Ole2Magic = { 0xD0, 0xCF, 0x11, 0xE0 };
    private static readonly byte[] RtfMagic = "{\\rtf"u8.ToArray();

    public static string[] AllowedExtensions(JobKind k) => k switch
    {
        JobKind.Compress or JobKind.Merge => PdfExt,
        JobKind.OfficeToPdf => OfficeExt,
        _ => ImageExt
    };

    /// <summary>Returns null when valid, or a user-friendly error message.</summary>
    public static string? ValidateFile(JobKind kind, string path, long maxFileBytes = DefaultMaxFileBytes)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path)) return $"{name}: file not found.";

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(AllowedExtensions(kind), ext) < 0)
            return $"{name}: unsupported file type for \"{JobService.OperationName(kind)}\".";

        long len;
        try { len = new FileInfo(path).Length; }
        catch (IOException) { return $"{name}: cannot read file."; }
        catch (UnauthorizedAccessException) { return $"{name}: access denied."; }

        if (len == 0) return $"{name}: file is empty.";
        if (len > maxFileBytes)
            return $"{name}: file is too large ({OutputNaming.FormatSize(len)}; limit is {OutputNaming.FormatSize(maxFileBytes)}).";

        return ValidateMagicBytes(path, name, ext);
    }

    private static string? ValidateMagicBytes(string path, string name, string ext)
    {
        byte[] header = new byte[1024];
        int read;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = fs.Read(header, 0, header.Length);
        }
        catch (UnauthorizedAccessException) { return $"{name}: access denied."; }
        catch (IOException) { return $"{name}: cannot read file."; }

        var span = header.AsSpan(0, read);

        switch (ext)
        {
            case ".pdf":
                if (span.IndexOf(PdfMagic) < 0)
                    return $"{name}: does not appear to be a valid PDF file.";
                return null;

            case ".jpg":
            case ".jpeg":
                if (read < 3 || span[0] != 0xFF || span[1] != 0xD8 || span[2] != 0xFF)
                    return $"{name}: does not appear to be a valid JPEG image.";
                return null;

            case ".docx":
            case ".pptx":
                if (read < 4 || !span[..4].SequenceEqual(ZipMagic))
                    return $"{name}: does not appear to be a valid Office Open XML file.";
                return null;

            case ".doc":
            case ".ppt":
                bool isOle = read >= 4 && span[..4].SequenceEqual(Ole2Magic);
                bool isZip = read >= 4 && span[..4].SequenceEqual(ZipMagic);
                bool isRtf = read >= 5 && span[..5].SequenceEqual(RtfMagic);
                if (!isOle && !isZip && !isRtf)
                    return $"{name}: does not appear to be a valid Office document.";
                return null;

            default:
                return null;
        }
    }

    /// <summary>Verifies the target directory accepts file creation before starting remote API work.</summary>
    public static string? ValidateDirectoryWritable(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return "Output folder does not exist.";

        var probe = Path.Combine(dir, $".pdftools-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Cannot save output here: folder is read-only or access is denied.";
        }
        catch (IOException ex)
        {
            return "Cannot save output here: " + ex.Message;
        }
    }
}
