using System.Drawing;
using System.Text;
using System.Windows.Forms;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;

namespace PdfTools.Runner;

/// <summary>
/// Runs queued batches with no window of its own: progress and results are shown as native Windows 10/11
/// Toast notifications (with automatic fallback to tray balloon tips), paired with a tray context menu for
/// live status and graceful cancellation. Exits automatically once the queue drains and the click window expires.
/// </summary>
internal sealed class JobContext : ApplicationContext
{
    private readonly RunnerServer _server;
    private readonly ToastNotificationService _notifications = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _cancelCurrentItem;
    private readonly ToolStripMenuItem _cancelAllItem;
    private readonly Control _ui;
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 15000 };

    private readonly InFlightJobTracker _inFlight = new();
    private readonly object _stateLock = new();

    private ILoveApiClient? _sharedClient;
    private string? _clientKey;
    private string? _clientRegion;
    private CancellationTokenSource? _currentBatchCts;
    private int _batchSeq;
    private string? _activeProgressTag;
    private string _lastOutcomeStatus = "Done";
    private bool _usedBalloonFallback;
    private bool _busy;
    private Action? _onClick;
    private bool _quit;

    public JobContext(RunnerServer server)
    {
        _server = server;

        _ui = new Control();
        _ = _ui.Handle;                      // bind to this (UI) thread so BeginInvoke works

        Icon icon;
        try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { icon = SystemIcons.Application; }

        _statusItem = new ToolStripMenuItem("Preparing\u2026") { Enabled = false };
        _cancelCurrentItem = new ToolStripMenuItem("Cancel current job", null, (_, _) => CancelCurrent()) { Enabled = false };
        _cancelAllItem = new ToolStripMenuItem("Cancel all queued", null, (_, _) => CancelAll()) { Enabled = false };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_cancelCurrentItem);
        _menu.Items.Add(_cancelAllItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open Activity Log", null, (_, _) => Shell.OpenSettings("--logs"));
        _menu.Items.Add("Settings", null, (_, _) => Shell.OpenSettings());

        _tray = new NotifyIcon { Icon = icon, Text = "iLovePDF Tools", ContextMenuStrip = _menu, Visible = true };
        _tray.BalloonTipClicked += (_, _) =>
        {
            var a = _onClick; _onClick = null;
            try { a?.Invoke(); } catch { }
            TryQuitIfIdle();
        };
        _exitTimer.Tick += (_, _) => TryQuitIfIdle();
        _server.BatchArrived += OnBatchArrived;

        ToastNotificationService.EnsureRegistered();
        _ = Task.Run(ProcessQueueAsync);
    }

    private void OnBatchArrived()
    {
        OnUi(() =>
        {
            if (_quit) return;
            _exitTimer.Stop();
            UpdateMenuState();
        });
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var batch in _server.Reader.ReadAllAsync())
            {
                _server.DecrementQueuedCount();
                SetBusy(true);
                await RunBatchAsync(batch);
                SetBusy(false);
            }
        }
        catch (Exception ex)
        {
            var tag = NextBatchTag();
            _lastOutcomeStatus = "Error";
            Show(tag, "iLovePDF Tools", "Unexpected error: " + ex.Message, ToolTipIcon.Error,
                 () => Shell.OpenSettings("--logs"), final: true);
        }
    }

    private async Task RunBatchAsync(QueuedBatch batch)
    {
        var batchTag = NextBatchTag();
        CancellationTokenSource? batchCts = null;
        try
        {
            var settings = AppSettings.Load();
            FileJobLog.CleanOld(settings.LogRetentionDays);

            var key = CredentialStore.LoadPublicKey();
            if (key == null)
            {
                _lastOutcomeStatus = "API key required";
                UpdateStatus(_lastOutcomeStatus);
                Show(batchTag, "iLovePDF Tools needs an API key",
                     "Click here to open Settings and enter your iLoveAPI key.",
                     ToolTipIcon.Warning, () => Shell.OpenSettings(), final: true);
                return;
            }

            var client = GetOrCreateClient(key, settings.Region);
            client.CircuitBreaker.ResetTerminal();

            var startText = StartText(batch.Kind, batch.Files);
            UpdateStatus(startText);
            Show(batchTag, "iLovePDF Tools", startText, ToolTipIcon.Info, null, final: false);

            batchCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            lock (_stateLock) _currentBatchCts = batchCts;
            OnUi(UpdateMenuState);

            var progress = new Progress<JobProgress>(p =>
            {
                var queued = _server.QueuedCount;
                var qSuffix = queued > 0 ? $" (+{queued} queued)" : "";
                UpdateStatus($"{JobService.OperationName(batch.Kind)}: {p.Completed}/{p.Total}{qSuffix}");
            });

            var service = new JobService(
                () => client,
                settings,
                new FileJobLog(),
                inFlight: _inFlight,
                disposeClient: false);

            var results = await service.RunAsync(batch.Kind, batch.Files, batchCts.Token, progress);
            Summarize(batchTag, batch.Kind, results, service.LastRemainingCredits);
        }
        catch (Exception ex)
        {
            _lastOutcomeStatus = "Error";
            Show(batchTag, "iLovePDF Tools", "Unexpected error: " + ex.Message, ToolTipIcon.Error,
                 () => Shell.OpenSettings("--logs"), final: true);
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_currentBatchCts, batchCts)) _currentBatchCts = null;
            }
            batchCts?.Dispose();
            OnUi(UpdateMenuState);
        }
    }

    private string NextBatchTag() => $"job-{Interlocked.Increment(ref _batchSeq)}";

    private ILoveApiClient GetOrCreateClient(string key, string region)
    {
        if (_sharedClient != null && _clientKey == key && _clientRegion == region)
            return _sharedClient;

        _sharedClient?.Dispose();
        _sharedClient = new ILoveApiClient(key, region);
        _clientKey = key;
        _clientRegion = region;
        return _sharedClient;
    }

    private void CancelCurrent()
    {
        CancellationTokenSource? cts;
        lock (_stateLock) cts = _currentBatchCts;
        try { cts?.Cancel(); } catch { }
        UpdateStatus("Cancelling\u2026");
    }

    private void CancelAll()
    {
        _server.DrainQueued();
        CancelCurrent();
    }

    private void SetBusy(bool busy)
    {
        lock (_stateLock) _busy = busy;
        OnUi(() =>
        {
            if (_quit) return;
            UpdateMenuState();
            if (!busy && IsIdle())
            {
                _statusItem.Text = Trunc(_lastOutcomeStatus, 60);
                // Avoid calling Shell_NotifyIcon(NIM_MODIFY) right after a fallback balloon tip,
                // as modifying NotifyIcon.Text immediately can suppress/clear legacy balloons.
                if (!_usedBalloonFallback)
                    _tray.Text = "iLovePDF Tools";
                _exitTimer.Stop();
                _exitTimer.Start();
            }
        });
    }

    private bool IsIdle()
    {
        bool busy;
        lock (_stateLock) busy = _busy;
        return !busy && _server.QueuedCount == 0 && !_server.HasPending;
    }

    private void UpdateStatus(string text)
    {
        OnUi(() =>
        {
            if (_quit) return;
            _statusItem.Text = Trunc(text, 60);
            _tray.Text = Trunc("iLovePDF: " + text, 63);
            UpdateMenuState();
        });
    }

    private void UpdateMenuState()
    {
        if (_quit) return;
        bool hasCts;
        lock (_stateLock) hasCts = _currentBatchCts != null;
        int queued = _server.QueuedCount;
        _cancelCurrentItem.Enabled = hasCts;
        _cancelAllItem.Enabled = hasCts && queued > 0;
        _cancelAllItem.Text = queued > 0 ? $"Cancel all ({queued} queued)" : "Cancel all queued";
    }

    private static string StartText(JobKind kind, List<string> files)
    {
        int n = files.Count;
        string first = Path.GetFileName(files[0]);
        return kind switch
        {
            JobKind.Compress => n == 1 ? $"Compressing {first}\u2026" : $"Compressing {n} PDFs\u2026",
            JobKind.Merge => $"Merging {n} PDFs\u2026",
            JobKind.OfficeToPdf => n == 1 ? $"Converting {first} to PDF\u2026" : $"Converting {n} files to PDF\u2026",
            JobKind.ImageToPdf => n == 1 ? $"Converting {first} to PDF\u2026" : $"Converting {n} images to PDF\u2026",
            JobKind.ImagesCombine => $"Combining {n} images into one PDF\u2026",
            _ => "Working\u2026"
        };
    }

    private void Summarize(string batchTag, JobKind kind, IReadOnlyList<JobItemResult> results, int? credits)
    {
        var ok = results.Where(r => r.Status == JobStatus.Success).ToList();
        var noGain = results.Where(r => r.Status == JobStatus.NoGain).ToList();
        var skipped = results.Where(r => r.Status == JobStatus.Skipped).ToList();
        var failed = results.Where(r => r.Status == JobStatus.Failed).ToList();
        string op = JobService.OperationName(kind);

        string title, text;
        ToolTipIcon icon;
        Action? click = null;

        if (results.Count == 1)
        {
            var r = results[0];
            switch (r.Status)
            {
                case JobStatus.Success:
                    title = "Done \u2013 " + op; text = r.Message; icon = ToolTipIcon.Info;
                    var path = r.OutputPath!; click = () => Shell.Reveal(path);
                    break;
                case JobStatus.NoGain:
                    title = "Already optimized"; text = r.Message; icon = ToolTipIcon.Info;
                    break;
                case JobStatus.Skipped:
                    title = "Already in progress"; text = r.Message; icon = ToolTipIcon.Info;
                    break;
                default:
                    title = op + " failed"; text = r.Message; icon = ToolTipIcon.Error;
                    click = () => Shell.OpenSettings("--logs");
                    break;
            }
        }
        else
        {
            title = failed.Count == 0 && noGain.Count == 0 && skipped.Count == 0
                ? "Done \u2013 " + op
                : $"{op}: {ok.Count} of {results.Count} done";
            var sb = new StringBuilder();
            if (ok.Count > 0) sb.Append($"{ok.Count} saved next to the originals. ");
            if (noGain.Count > 0) sb.Append($"{noGain.Count} already optimized. ");
            if (skipped.Count > 0) sb.Append($"{skipped.Count} already in progress. ");
            if (failed.Count > 0) sb.Append($"{failed.Count} failed: {failed[0].Message}");
            text = sb.ToString().Trim();
            icon = failed.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info;
            if (ok.Count > 0) { var p = ok[0].OutputPath!; click = () => Shell.Reveal(p); }
            else if (failed.Count > 0) click = () => Shell.OpenSettings("--logs");
        }

        if (credits is >= 0 and <= 25) text += $" Credits left: {credits}.";
        _lastOutcomeStatus = title;
        Show(batchTag, title, text, icon, click, final: true);
    }

    private void Show(string tag, string title, string text, ToolTipIcon icon, Action? onClick, bool final)
    {
        lock (_stateLock)
        {
            if (!final)
                _activeProgressTag = tag;
            else if (string.Equals(_activeProgressTag, tag, StringComparison.Ordinal))
                _activeProgressTag = null;
        }

        var shortTitle = Trunc(title, 60);
        var shortText = Trunc(text, 240);

        Action? wrappedClick = onClick == null ? null : () =>
        {
            try { onClick(); } catch { }
            OnUi(TryQuitIfIdle);
        };

        var delivered = _notifications.TryShow(
            shortTitle,
            shortText,
            tag,
            silent: !final,
            isProgress: !final,
            onActivated: wrappedClick,
            onFailedFallback: () => ShowBalloonFallback(shortTitle, shortText, icon, onClick, final));

        if (!delivered)
        {
            ShowBalloonFallback(shortTitle, shortText, icon, onClick, final);
            return;
        }

        if (final)
        {
            OnUi(() =>
            {
                if (_quit) return;
                if (IsIdle()) { _exitTimer.Stop(); _exitTimer.Start(); }
            });
        }
    }

    private void ShowBalloonFallback(string title, string text, ToolTipIcon icon, Action? onClick, bool final)
    {
        OnUi(() =>
        {
            if (_quit) return;
            _usedBalloonFallback = true;
            _onClick = onClick;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(10000);
            if (final && IsIdle()) { _exitTimer.Stop(); _exitTimer.Start(); }
        });
    }

    private void OnUi(Action action)
    {
        try
        {
            if (_ui.InvokeRequired) _ui.BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    private void TryQuitIfIdle()
    {
        if (IsIdle()) Quit();
    }

    private void Quit()
    {
        if (_quit) return;
        _quit = true;
        _server.BatchArrived -= OnBatchArrived;
        _server.StopAccepting();

        string? staleProgress;
        lock (_stateLock)
        {
            staleProgress = _activeProgressTag;
            _activeProgressTag = null;
        }
        if (staleProgress != null)
            _notifications.Remove(staleProgress);

        _exitTimer.Stop();
        _exitTimer.Dispose();
        _sharedClient?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _ui.Dispose();
        ExitThread();
    }
}
