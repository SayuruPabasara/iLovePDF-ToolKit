using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;
using PdfTools.Core.Observability;

namespace PdfTools.Settings;

public sealed record LogRow(
    string Time,
    string Operation,
    string Files,
    string Status,
    string Duration,
    string Details,
    LogEntry Entry);

public partial class MainWindow : Window
{
    private static readonly (string Label, string Value)[] Levels =
    {
        ("Low \u2013 best quality, smaller saving", "low"),
        ("Recommended", "recommended"),
        ("Extreme \u2013 smallest file, lower quality", "extreme")
    };

    private static readonly (string Label, string Value)[] Regions =
    {
        ("Automatic", ""), ("Europe (eu)", "eu"), ("United States (us)", "us"), ("France (fr)", "fr"),
        ("Germany (de)", "de"), ("Poland (pl)", "pl"), ("India (in)", "in"), ("Singapore (sg)", "sg")
    };

    private static readonly string[] OperationOptions =
    {
        "All actions", "Compress", "Merge", "Convert to PDF", "Image to PDF", "Combine images"
    };

    private static readonly string[] StatusOptions =
    {
        "All results", "Success", "NoGain", "Skipped", "Failed"
    };

    private readonly AppSettings _settings = AppSettings.Load();
    private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();
    private int _refreshVersion;

    public MainWindow(bool showLogs)
    {
        InitializeComponent();

        foreach (var l in Levels) CompressionBox.Items.Add(l.Label);
        foreach (var r in Regions) RegionBox.Items.Add(r.Label);
        CompressionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Levels, l => l.Value == _settings.CompressionLevel));
        RegionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Regions, r => r.Value == _settings.Region));
        RetentionBox.Text = _settings.LogRetentionDays.ToString();

        foreach (var op in OperationOptions) OperationFilter.Items.Add(op);
        foreach (var st in StatusOptions) StatusFilter.Items.Add(st);
        OperationFilter.SelectedIndex = 0;
        StatusFilter.SelectedIndex = 0;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        AboutTitle.Text = $"iLovePDF Tools {version}";
        AboutPaths.Text = $"Settings and logs are stored in:\n{AppPaths.DataDir}";

        UpdateKeyStatus();
        if (showLogs) Tabs.SelectedIndex = 1;
        Loaded += async (_, _) => await RefreshLogAsync();
    }

    // ------------------------------------------------------------------ key

    private void UpdateKeyStatus()
    {
        bool has = CredentialStore.HasPublicKey();
        KeyStatus.Text = has ? "\u2714  A key is saved and ready to use." : "\u26A0  No key saved yet \u2013 the right-click actions will not work until you add one.";
        KeyStatus.Foreground = has ? Brushes.DarkGreen : Brushes.DarkOrange;
        RemoveKeyButton.IsEnabled = has;
    }

    private void SetKeyMessage(string text, bool ok)
    {
        KeyMessage.Text = text;
        KeyMessage.Foreground = ok ? Brushes.DarkGreen : Brushes.Firebrick;
    }

    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { SetKeyMessage("Paste your project public key first.", false); return; }

        SaveKeyButton.IsEnabled = false;
        SetKeyMessage("Checking the key with iLoveAPI\u2026", true);
        KeyMessage.Foreground = Brushes.Gray;
        try
        {
            using var client = new ILoveApiClient(key, _settings.Region, tokenCache: NullTokenCache.Instance);
            await client.ValidateKeyAsync();
            CredentialStore.SavePublicKey(key);
            KeyBox.Clear();
            SetKeyMessage("Key verified and saved.", true);
            UpdateKeyStatus();
        }
        catch (ApiException ex) { SetKeyMessage(ex.Message, false); }
        catch (Exception ex) { SetKeyMessage("Could not save the key: " + ex.Message, false); }
        finally { SaveKeyButton.IsEnabled = true; }
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the saved API key? The right-click actions stop working until you add a key again.",
                "Remove key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CredentialStore.Clear();
        SetKeyMessage("Key removed.", true);
        UpdateKeyStatus();
    }

    // ------------------------------------------------------------------ options

    private void SaveOptions_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionBox.Text.Trim(), out var days) || days < 1 || days > 365)
        {
            OptionsMessage.Text = "Log retention must be 1\u2013365 days.";
            OptionsMessage.Foreground = Brushes.Firebrick;
            return;
        }
        try
        {
            _settings.CompressionLevel = Levels[Math.Max(0, CompressionBox.SelectedIndex)].Value;
            _settings.Region = Regions[Math.Max(0, RegionBox.SelectedIndex)].Value;
            _settings.LogRetentionDays = days;
            _settings.Save();
            OptionsMessage.Text = "Saved.";
            OptionsMessage.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex)
        {
            OptionsMessage.Text = "Could not save: " + ex.Message;
            OptionsMessage.Foreground = Brushes.Firebrick;
        }
    }

    // ------------------------------------------------------------------ log & diagnostics

    private async Task RefreshLogAsync()
    {
        int myVersion = Interlocked.Increment(ref _refreshVersion);
        RefreshLogButton.IsEnabled = false;
        try
        {
            var loaded = await LogReader.ReadRecentAsync();
            if (myVersion != Volatile.Read(ref _refreshVersion)) return;
            _entries = loaded;
            UpdateMetrics();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("Settings", "Failed loading activity log", ex);
        }
        finally
        {
            if (myVersion == Volatile.Read(ref _refreshVersion))
                RefreshLogButton.IsEnabled = true;
        }
    }

    private void UpdateMetrics()
    {
        var m = LogMetricsSummary.Compute(_entries);
        MetricJobs.Text = m.TotalJobs.ToString();
        MetricSuccessRate.Text = m.TotalJobs == 0 ? "\u2014" : $"{m.SuccessRatePercent:0.#}%";
        MetricSaved.Text = OutputNaming.FormatSize(m.TotalBytesSaved);
        MetricAvgTime.Text = m.AverageSeconds > 0 ? $"{m.AverageSeconds:0.#} s" : "\u2014";
        MetricRetriesFailures.Text = $"{m.TotalRetries} / {m.FailedCount}";
    }

    private void ApplyFilter()
    {
        string? opFilter = OperationFilter?.SelectedIndex > 0 ? OperationFilter.SelectedItem as string : null;
        string? statusFilter = StatusFilter?.SelectedIndex > 0 ? StatusFilter.SelectedItem as string : null;
        string query = (SearchBox?.Text ?? "").Trim();

        LogGrid.ItemsSource = _entries
            .Where(en => opFilter == null || string.Equals(en.Operation, opFilter, StringComparison.OrdinalIgnoreCase))
            .Where(en => statusFilter == null || string.Equals(en.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
            .Where(en => query.Length == 0 || MatchesQuery(en, query))
            .Select(en => new LogRow(
                en.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                en.Operation,
                string.Join(", ", en.Inputs),
                en.Status,
                en.Seconds.HasValue ? $"{en.Seconds.Value:0.#}" : "",
                en.Message ?? en.Output ?? "",
                en))
            .ToList();

        UpdateDetailPanel(null);
    }

    private static bool MatchesQuery(LogEntry en, string query)
    {
        if (en.Inputs.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))) return true;
        if (en.Output != null && en.Output.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (en.Message != null && en.Message.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (en.CorrelationId != null && en.CorrelationId.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (en.ApiServer != null && en.ApiServer.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (en.ErrorKind != null && en.ErrorKind.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void UpdateDetailPanel(LogEntry? en)
    {
        if (en == null)
        {
            DetailHeader.Text = "Select a log entry to inspect correlation ID, server node, retries, and per-stage timings.";
            DetailTimings.Text = "";
            return;
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(en.CorrelationId)) parts.Add($"ID: {en.CorrelationId}");
        if (!string.IsNullOrEmpty(en.ApiServer)) parts.Add($"Server: {en.ApiServer}");
        if (!string.IsNullOrEmpty(en.FailedStage)) parts.Add($"Failed stage: {en.FailedStage}");
        if (!string.IsNullOrEmpty(en.ErrorKind)) parts.Add($"Kind: {en.ErrorKind}");
        if (en.HttpStatus.HasValue) parts.Add($"HTTP {en.HttpStatus}");
        if (en.RetryCount is > 0) parts.Add($"Retries: {en.RetryCount}");
        if (en.InputBytes.HasValue && en.OutputBytes.HasValue)
            parts.Add($"Size: {OutputNaming.FormatSize(en.InputBytes.Value)} \u2192 {OutputNaming.FormatSize(en.OutputBytes.Value)}");
        else if (en.InputBytes.HasValue)
            parts.Add($"Input: {OutputNaming.FormatSize(en.InputBytes.Value)}");

        DetailHeader.Text = parts.Count > 0 ? string.Join("  \u2022  ", parts) : $"{en.Operation} ({en.Status})";

        var timings = new List<string>();
        if (en.Timings != null)
        {
            if (en.Timings.AuthMs.HasValue) timings.Add($"Auth: {en.Timings.AuthMs} ms");
            if (en.Timings.StartMs.HasValue) timings.Add($"Start: {en.Timings.StartMs} ms");
            if (en.Timings.UploadMs.HasValue) timings.Add($"Upload: {en.Timings.UploadMs} ms");
            if (en.Timings.ProcessMs.HasValue) timings.Add($"Process: {en.Timings.ProcessMs} ms");
            if (en.Timings.DownloadMs.HasValue) timings.Add($"Download: {en.Timings.DownloadMs} ms");
        }
        DetailTimings.Text = timings.Count > 0
            ? "Stage timings: " + string.Join("  |  ", timings)
            : (en.Message ?? "No stage timing breakdown recorded for this entry.");
    }

    private async void RefreshLog_Click(object sender, RoutedEventArgs e) => await RefreshLogAsync();
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) ApplyFilter(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyFilter(); }
    private void LogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDetailPanel((LogGrid.SelectedItem as LogRow)?.Entry);

    private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = DiagnosticReportBuilder.Build(_settings, _entries);
            Clipboard.SetText(report);
            CopyDiagButton.Content = "Copied!";
            await Task.Delay(1500);
            CopyDiagButton.Content = "Copy diagnostics";
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("Settings", "Failed copying diagnostic report", ex);
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("Settings", "Failed opening log folder", ex);
        }
    }

    // ------------------------------------------------------------------ misc

    private void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }
}
