using System.Windows;

namespace PdfTools.Settings;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool showLogs = e.Args.Any(a => string.Equals(a, "--logs", StringComparison.OrdinalIgnoreCase));
        new MainWindow(showLogs).Show();
    }
}
