using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using System.Windows.Forms;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;
using PdfTools.Core.Observability;

namespace PdfTools.Runner;

internal sealed record QueuedBatch(JobKind Kind, List<string> Files);

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

        var verb = args[0].ToLowerInvariant();
        if (!TryParseVerb(verb, out var kind)) return 64;
        var paths = args.Skip(1)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => { try { return Path.GetFullPath(a); } catch { return ""; } })
            .Where(a => a.Length > 0)
            .ToList();
        if (paths.Count == 0) return 64;

        var mutexName = @"Local\PdfTools.Runner.Active";
        var pipeName = $"PdfTools.Runner.{Process.GetCurrentProcess().SessionId}.{Environment.UserName.Replace('\\', '_')}";

        // Single-instance coordinator: if an active runner exists, forward (verb, paths) to its queue and exit.
        // Otherwise become the active runner, accept concurrent incoming clicks, and exit automatically when idle.
        Mutex? leaderMutex = AcquireOrForward(mutexName, pipeName, verb, paths);
        if (leaderMutex == null) return 0;

        try
        {
            using var server = new RunnerServer(pipeName);
            server.Start();
            server.EnqueueRaw(kind, paths);
            Application.Run(new JobContext(server));
            return 0;
        }
        finally
        {
            try { leaderMutex.ReleaseMutex(); } catch { }
            leaderMutex.Dispose();
        }
    }

    private static Mutex? AcquireOrForward(string mutexName, string pipeName, string verb, List<string> paths)
    {
        var sw = Stopwatch.StartNew();
        var giveUp = TimeSpan.FromSeconds(6);

        while (sw.Elapsed < giveUp)
        {
            var mutex = new Mutex(true, mutexName, out bool created);
            if (created) return mutex;
            mutex.Dispose();

            if (TrySend(pipeName, verb, paths)) return null;
            Thread.Sleep(40);
        }

        // Could not coordinate within timeout; fall back to running standalone with a local dummy mutex.
        return new Mutex(false);
    }

    private static bool TrySend(string pipeName, string verb, List<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(300);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var p in paths)
                writer.WriteLine($"{verb}\t{p}");
            writer.Flush();
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool TryParseVerb(string verb, out JobKind kind)
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

/// <summary>
/// Concurrent CurrentUserOnly named-pipe receiver + per-verb debouncer feeding a bounded Channel queue.
/// Coalesces Explorer's per-file launch storm (700ms quiet window) and buffers rapid follow-up right-clicks.
/// </summary>
internal sealed class RunnerServer : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan MaxCollect = TimeSpan.FromSeconds(8);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<QueuedBatch> _queue = Channel.CreateBounded<QueuedBatch>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });

    private readonly object _debounceLock = new();
    private readonly Dictionary<JobKind, PendingBucket> _pending = new();
    private int _queuedCount;

    public ChannelReader<QueuedBatch> Reader => _queue.Reader;
    public int QueuedCount => Volatile.Read(ref _queuedCount);
    public event Action? BatchArrived;

    public bool HasPending
    {
        get { lock (_debounceLock) return _pending.Count > 0; }
    }

    public RunnerServer(string pipeName) => _pipeName = pipeName;

    public void Start()
    {
        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(DebounceFlushLoopAsync);
    }

    public void EnqueueRaw(JobKind kind, IEnumerable<string> paths)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_debounceLock)
        {
            if (!_pending.TryGetValue(kind, out var bucket))
            {
                bucket = new PendingBucket(now);
                _pending[kind] = bucket;
            }
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    bucket.Paths.Add(p);
            }
            bucket.LastSeen = now;
        }
        BatchArrived?.Invoke();
    }

    public void DecrementQueuedCount() => Interlocked.Decrement(ref _queuedCount);

    public int DrainQueued()
    {
        int dropped = 0;
        lock (_debounceLock) _pending.Clear();
        while (_queue.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _queuedCount);
            dropped++;
        }
        return dropped;
    }

    public void StopAccepting()
    {
        try { _cts.Cancel(); } catch { }
        _queue.Writer.TryComplete();
    }

    public void Dispose()
    {
        StopAccepting();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(ct);
                var connected = pipe;
                pipe = null; // ownership transferred to reader task
                _ = Task.Run(() => ReadClientAsync(connected), CancellationToken.None);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (IOException ex) { pipe?.Dispose(); DiagnosticsLog.Write("RunnerServer", "Pipe accept I/O error", ex); await Task.Delay(25, CancellationToken.None); }
            catch (Exception ex) { pipe?.Dispose(); DiagnosticsLog.Write("RunnerServer", "Pipe accept error", ex); await Task.Delay(50, CancellationToken.None); }
        }
    }

    private async Task ReadClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false)))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || tab + 1 >= line.Length) continue;
                    var verb = line[..tab];
                    var path = line[(tab + 1)..];
                    if (Program.TryParseVerb(verb, out var kind))
                        EnqueueRaw(kind, new[] { path });
                }
            }
        }
        catch { /* follower disconnected early */ }
    }

    private async Task DebounceFlushLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }

            List<QueuedBatch>? ready = null;
            var now = DateTimeOffset.UtcNow;
            lock (_debounceLock)
            {
                if (_pending.Count > 0)
                {
                    foreach (var kv in _pending.ToList())
                    {
                        var b = kv.Value;
                        if (now - b.LastSeen >= QuietPeriod || now - b.FirstSeen >= MaxCollect)
                        {
                            ready ??= new List<QueuedBatch>();
                            ready.Add(new QueuedBatch(kv.Key, b.Paths));
                            _pending.Remove(kv.Key);
                        }
                    }
                }
            }

            if (ready != null)
            {
                foreach (var batch in ready)
                {
                    try
                    {
                        await _queue.Writer.WriteAsync(batch, ct);
                        Interlocked.Increment(ref _queuedCount);
                        BatchArrived?.Invoke();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ChannelClosedException) { break; }
                }
            }
        }
    }

    private sealed class PendingBucket
    {
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; set; }
        public List<string> Paths { get; } = new();

        public PendingBucket(DateTimeOffset now)
        {
            FirstSeen = now;
            LastSeen = now;
        }
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
        catch (Exception ex) { DiagnosticsLog.Write("Shell", "Failed to launch PdfToolsSettings", ex); }
    }

    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { DiagnosticsLog.Write("Shell", "Failed to reveal output in Explorer", ex); }
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
