using System.Runtime.InteropServices;

namespace PdfTools.Core;

/// <summary>Explorer-style natural ordering (file2 before file10); managed fallback off Windows.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "StrCmpLogicalW")]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        if (OperatingSystem.IsWindows())
        {
            try { return StrCmpLogicalW(x, y); } catch (DllNotFoundException) { }
        }
        return ManualCompare(x, y);
    }

    public static int ManualCompare(string x, string y)
    {
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
                int c = string.CompareOrdinal(a, b);
                if (c != 0) return c < 0 ? -1 : 1;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c < 0 ? -1 : 1;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}

/// <summary>Output naming. Results are written next to the original and never overwrite anything.</summary>
public static class OutputNaming
{
    public static string Candidate(string dir, string baseName, string ext, int index)
        => Path.Combine(dir, index == 0 ? baseName + ext : $"{baseName} ({index}){ext}");

    /// <summary>First free path: "name.ext", "name (1).ext", "name (2).ext" ...</summary>
    public static string GetUniquePath(string dir, string baseName, string ext)
    {
        for (int i = 0; i < 10_000; i++)
        {
            var p = Candidate(dir, baseName, ext, i);
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
        throw new IOException("Could not find a free output file name.");
    }

    /// <summary>Moves a finished temp file to its final, collision-free name (race-safe).</summary>
    public static string MoveUnique(string tempFile, string dir, string baseName, string ext)
    {
        for (int i = 0; i < 10_000; i++)
        {
            var p = Candidate(dir, baseName, ext, i);
            if (File.Exists(p) || Directory.Exists(p)) continue;
            try { File.Move(tempFile, p, overwrite: false); return p; }
            catch (IOException) when (File.Exists(p)) { /* lost a race, try next name */ }
        }
        throw new IOException("Could not find a free output file name.");
    }

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }
}
