using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PdfTools.Core;
using PdfTools.Core.ILoveApi;

namespace PdfTools.Settings;

public sealed record LogRow(string Time, string Operation, string Files, string Status, string Details);

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

    private readonly AppSettings _settings = AppSettings.Load();
    private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();

    public MainWindow(bool showLogs)
    {
        InitializeComponent();

        foreach (var l in Levels) CompressionBox.Items.Add(l.Label);
        foreach (var r in Regions) RegionBox.Items.Add(r.Label);
        CompressionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Levels, l => l.Value == _settings.CompressionLevel));
        RegionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Regions, r => r.Value == _settings.Region));
        RetentionBox.Text = _settings.LogRetentionDays.ToString();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        AboutTitle.Text = $"iLovePDF Tools {version}";
        AboutPaths.Text = $"Settings and logs are stored in:\n{AppPaths.DataDir}";

        UpdateKeyStatus();
        RefreshLog();
        if (showLogs) Tabs.SelectedIndex = 1;
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
            using var client = new ILoveApiClient(key, _settings.Region);
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

    // ------------------------------------------------------------------ log

    private void RefreshLog()
    {
        _entries = LogReader.ReadRecent();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        bool failuresOnly = FailuresOnly?.IsChecked == true;
        LogGrid.ItemsSource = _entries
            .Where(en => !failuresOnly || en.Status == "Failed")
            .Select(en => new LogRow(
                en.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                en.Operation,
                string.Join(", ", en.Inputs),
                en.Status,
                en.Message ?? en.Output ?? ""))
            .ToList();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();
    private void FilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded) ApplyFilter(); }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ------------------------------------------------------------------ misc

    private void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }
}
