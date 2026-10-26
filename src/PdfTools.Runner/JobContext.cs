using System.Drawing;
using System.Text;
using System.Windows.Forms;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Jobs;

namespace PdfTools.Runner;

/// <summary>
/// Runs one batch with no window of its own: progress and results are shown as Windows notifications
/// (tray balloon -> toast). Clicking the final notification reveals the result, or opens the log on errors.
/// </summary>
internal sealed class JobContext : ApplicationContext
{
    private readonly JobKind _kind;
    private readonly List<string> _files;
    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 9000 };
    private Action? _onClick;
    private bool _quit;

    public JobContext(JobKind kind, List<string> files)
    {
        _kind = kind;
        _files = files;

        _ui = new Control();
        _ = _ui.Handle;                      // bind to this (UI) thread so BeginInvoke works

        Icon icon;
        try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { icon = SystemIcons.Application; }

        _tray = new NotifyIcon { Icon = icon, Text = "iLovePDF Tools", Visible = true };
        _tray.BalloonTipClicked += (_, _) =>
        {
            var a = _onClick; _onClick = null;
            try { a?.Invoke(); } catch { }
            Quit();
        };
        _exitTimer.Tick += (_, _) => Quit();

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            FileJobLog.CleanOld(settings.LogRetentionDays);

            var key = CredentialStore.LoadPublicKey();
            if (key == null)
            {
                Show("iLovePDF Tools needs an API key",
                     "Click here to open Settings and enter your iLoveAPI key.",
                     ToolTipIcon.Warning, () => Shell.OpenSettings(), final: true);
                return;
            }

            Show("iLovePDF Tools", StartText(), ToolTipIcon.Info, null, final: false);

            var service = new JobService(() => new ILoveApiClient(key, settings.Region), settings, new FileJobLog());
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));   // safety net against a stalled network
            var results = await Task.Run(() => service.RunAsync(_kind, _files, cts.Token));
            Summarize(results, service.LastRemainingCredits);
        }
        catch (Exception ex)
        {
            Show("iLovePDF Tools", "Unexpected error: " + ex.Message, ToolTipIcon.Error,
                 () => Shell.OpenSettings("--logs"), final: true);
        }
    }

    private string StartText()
    {
        int n = _files.Count;
        string first = Path.GetFileName(_files[0]);
        return _kind switch
        {
            JobKind.Compress => n == 1 ? $"Compressing {first}\u2026" : $"Compressing {n} PDFs\u2026",
            JobKind.Merge => $"Merging {n} PDFs\u2026",
            JobKind.OfficeToPdf => n == 1 ? $"Converting {first} to PDF\u2026" : $"Converting {n} files to PDF\u2026",
            JobKind.ImageToPdf => n == 1 ? $"Converting {first} to PDF\u2026" : $"Converting {n} images to PDF\u2026",
            JobKind.ImagesCombine => $"Combining {n} images into one PDF\u2026",
            _ => "Working\u2026"
        };
    }

    private void Summarize(IReadOnlyList<JobItemResult> results, int? credits)
    {
        var ok = results.Where(r => r.Status == JobStatus.Success).ToList();
        var noGain = results.Where(r => r.Status == JobStatus.NoGain).ToList();
        var failed = results.Where(r => r.Status == JobStatus.Failed).ToList();
        string op = JobService.OperationName(_kind);

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
                default:
                    title = op + " failed"; text = r.Message; icon = ToolTipIcon.Error;
                    click = () => Shell.OpenSettings("--logs");
                    break;
            }
        }
        else
        {
            title = failed.Count == 0 && noGain.Count == 0 ? "Done \u2013 " + op : $"{op}: {ok.Count} of {results.Count} done";
            var sb = new StringBuilder();
            if (ok.Count > 0) sb.Append($"{ok.Count} saved next to the originals. ");
            if (noGain.Count > 0) sb.Append($"{noGain.Count} already optimized. ");
            if (failed.Count > 0) sb.Append($"{failed.Count} failed: {failed[0].Message}");
            text = sb.ToString().Trim();
            icon = failed.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info;
            if (ok.Count > 0) { var p = ok[0].OutputPath!; click = () => Shell.Reveal(p); }
            else if (failed.Count > 0) click = () => Shell.OpenSettings("--logs");
        }

        if (credits is >= 0 and <= 25) text += $" Credits left: {credits}.";
        Show(title, text, icon, click, final: true);
    }

    private void Show(string title, string text, ToolTipIcon icon, Action? onClick, bool final)
    {
        void Do()
        {
            if (_quit) return;
            _onClick = onClick;
            _tray.BalloonTipTitle = Trunc(title, 60);
            _tray.BalloonTipText = Trunc(text, 240);
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(10000);
            if (final) { _exitTimer.Stop(); _exitTimer.Start(); }
        }
        if (_ui.InvokeRequired) _ui.BeginInvoke(Do); else Do();
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    private void Quit()
    {
        if (_quit) return;
        _quit = true;
        _exitTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
