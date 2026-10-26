using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;

namespace PdfTools.Runner;

/// <summary>
/// Entry point for every right-click action.
///   PdfToolsRunner.exe compress   "C:\x\a.pdf"
///   PdfToolsRunner.exe merge      "C:\x\a.pdf"            (Explorer starts one process per selected file)
///   PdfToolsRunner.exe office2pdf | img2pdf | imgcombine  "file"
///   PdfToolsRunner.exe --init --key-file "path" [--skip-validation]   (used by the installer)
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length == 0) { Shell.OpenSettings(); return 0; }
        if (args[0] == "--init") return InitCommand.Run(args);

        if (!TryParseVerb(args[0], out var kind)) return 64;
        var paths = args.Skip(1)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => { try { return Path.GetFullPath(a); } catch { return ""; } })
            .Where(a => a.Length > 0)
            .ToList();
        if (paths.Count == 0) return 64;

        // Explorer launches one process per selected file; the first one becomes the "leader"
        // and gathers the paths of the others so Merge/Combine see the whole selection.
        var batch = BatchCollector.Collect(args[0].ToLowerInvariant(), paths);
        if (batch == null) return 0;     // we were a follower; the leader handles everything

        Application.Run(new JobContext(kind, batch));
        return 0;
    }

    private static bool TryParseVerb(string verb, out JobKind kind)
    {
        switch (verb.ToLowerInvariant())
        {
            case "compress": kind = JobKind.Compress; return true;
            case "merge": kind = JobKind.Merge; return true;
            case "office2pdf": kind = JobKind.OfficeToPdf; return true;
            case "img2pdf": kind = JobKind.ImageToPdf; return true;
            case "imgcombine": kind = JobKind.ImagesCombine; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>Gathers the files of one multi-selection across the per-file processes Explorer starts.</summary>
internal static class BatchCollector
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan MaxCollect = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(6);

    /// <returns>All collected paths if this process is the leader; null if it handed its path to a leader.</returns>
    public static List<string>? Collect(string verb, List<string> initial)
    {
        var mutexName = $@"Local\PdfTools.Batch.{verb}";
        var pipeName = $"PdfTools.Batch.{Process.GetCurrentProcess().SessionId}.{Environment.UserName.Replace('\\', '_')}.{verb}";

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < GiveUp)
        {
            var mutex = new Mutex(true, mutexName, out bool created);
            if (created)
            {
                try { return RunLeader(pipeName, initial); }
                finally { mutex.ReleaseMutex(); mutex.Dispose(); }
            }
            mutex.Dispose();

            if (TrySend(pipeName, initial)) return null;
            Thread.Sleep(40);   // leader exists but its pipe isn't ready yet (or just closed): retry
        }
        return initial;         // could not coordinate; just handle our own file
    }

    private static List<string> RunLeader(string pipeName, List<string> initial)
    {
        var paths = new List<string>(initial);
        var total = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();

        while (total.Elapsed < MaxCollect)
        {
            var remaining = QuietPeriod - quiet.Elapsed;
            if (remaining <= TimeSpan.Zero) break;

            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(remaining);
            try
            {
                server.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
                using var reader = new StreamReader(server, new UTF8Encoding(false));
                string? line;
                while ((line = reader.ReadLine()) != null)
                    if (line.Length > 0) paths.Add(line);
                quiet.Restart();
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* client vanished; keep listening */ }
        }
        return paths;
    }

    private static bool TrySend(string pipeName, List<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(300);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var p in paths) writer.WriteLine(p);
            writer.Flush();
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }
}

internal static class Shell
{
    public static void OpenSettings(string? arg = null)
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "PdfToolsSettings.exe");
            if (!File.Exists(exe)) return;
            Process.Start(new ProcessStartInfo(exe, arg ?? "") { UseShellExecute = false });
        }
        catch { }
    }

    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>Used by the setup wizard: validates the entered key, stores it encrypted, deletes the temp key file.</summary>
internal static class InitCommand
{
    // Exit codes: 0 ok, 1 bad arguments/empty, 2 key rejected, 3 could not verify (network), 4 could not save
    public static int Run(string[] args)
    {
        string? keyFile = null;
        bool skipValidation = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--key-file" && i + 1 < args.Length) keyFile = args[++i];
            else if (args[i] == "--skip-validation") skipValidation = true;
        }
        if (keyFile == null || !File.Exists(keyFile)) return 1;

        string key;
        try { key = File.ReadAllText(keyFile).Trim(); }
        finally { try { File.Delete(keyFile); } catch { } }     // never leave the key lying around
        if (key.Length == 0) return 1;

        if (!skipValidation)
        {
            try
            {
                using var client = new ILoveApiClient(key);
                client.ValidateKeyAsync().GetAwaiter().GetResult();
            }
            catch (ApiException ex) when (ex.Kind == ApiErrorKind.Auth) { return 2; }
            catch (ApiException) { return 3; }
        }

        try
        {
            CredentialStore.SavePublicKey(key);
            if (!File.Exists(AppPaths.SettingsFile)) new AppSettings().Save();
            return 0;
        }
        catch { return 4; }
    }
}
