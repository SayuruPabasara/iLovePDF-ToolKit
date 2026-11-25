using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;
using PdfTools.Core.Observability;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PdfTools.Runner;

/// <summary>
/// Sends native Windows 10/11 Toast notifications via an explicitly registered per-user AUMID.
/// Unlike legacy WinForms NotifyIcon.ShowBalloonTip(), native toasts:
///   - have a stable identity ("iLovePDF Tools" + app icon) instead of NotifyIconGeneratedAumid_*;
///   - replace in-progress notifications cleanly via Tag/Group;
///   - persist in Windows Notification Center after PdfToolsRunner exits;
///   - are not cleared immediately by Windows 11's legacy balloon manager.
/// </summary>
internal sealed class ToastNotificationService
{
    public const string AppUserModelId = "Internal.iLovePDFTools";
    public const string AppDisplayName = "iLovePDF Tools";
    public const string NotificationGroup = "pdftools";

    private static readonly object RegLock = new();
    private static bool _registered;

    // Hold strong references to active ToastNotification wrappers while the runner is alive
    // so GC cannot collect them before Activated/Failed events fire.
    private readonly ConcurrentDictionary<string, ToastNotification> _activeToasts = new(StringComparer.Ordinal);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Binds this process to our explicit AUMID. Call once at startup before creating UI.</summary>
    public static void InitializeProcess()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("Notifications", "SetCurrentProcessExplicitAppUserModelID failed", ex);
        }
    }

    /// <summary>
    /// Idempotently registers the per-user AUMID in HKCU\Software\Classes\AppUserModelId so Windows
    /// displays the proper app name and icon on native toast banners and in Notification Center.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (RegLock)
        {
            if (_registered) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
                if (key != null)
                {
                    key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
                    key.SetValue("IconBackgroundColor", "0", RegistryValueKind.String);

                    var iconPath = ResolveIconPath();
                    if (iconPath != null)
                        key.SetValue("IconUri", iconPath, RegistryValueKind.String);
                }

                // Ensure notifications are not disabled on our AUMID if no explicit user preference exists.
                _registered = true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("Notifications", "Failed to register AppUserModelId in HKCU", ex);
            }
        }
    }

    /// <summary>
    /// Attempts to show a native Windows Toast notification.
    /// Returns true if handed off to Windows ToastNotificationManager, false if caller should fall back.
    /// </summary>
    public bool TryShow(
        string title,
        string body,
        string tag,
        bool silent,
        bool isProgress,
        Action? onActivated,
        Action? onFailedFallback)
    {
        try
        {
            EnsureRegistered();

            var xmlText = BuildToastXml(title, body, silent);
            var doc = new XmlDocument();
            doc.LoadXml(xmlText);

            var safeTag = SanitizeTag(tag);
            var toast = new ToastNotification(doc)
            {
                Tag = safeTag,
                Group = NotificationGroup,
                SuppressPopup = false
            };

            if (isProgress)
                toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(10);

            int handled = 0;
            toast.Activated += (_, _) =>
            {
                if (Interlocked.Exchange(ref handled, 1) != 0) return;
                _activeToasts.TryRemove(safeTag, out _);
                try { onActivated?.Invoke(); }
                catch (Exception ex) { DiagnosticsLog.Write("Notifications", "Toast Activated callback failed", ex); }
            };

            toast.Dismissed += (_, _) =>
            {
                _activeToasts.TryRemove(safeTag, out _);
            };

            toast.Failed += (_, args) =>
            {
                _activeToasts.TryRemove(safeTag, out _);
                var hr = args.ErrorCode?.HResult ?? 0;
                DiagnosticsLog.Write("Notifications", $"Windows toast failed asynchronously (hr=0x{hr:X8})", args.ErrorCode);
                if (Interlocked.Exchange(ref handled, 1) == 0)
                    onFailedFallback?.Invoke();
            };

            _activeToasts[safeTag] = toast;

            var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            notifier.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("Notifications", "Native toast delivery failed; falling back to tray balloon", ex);
            return false;
        }
    }

    /// <summary>Removes an in-progress toast by tag (e.g., if shutdown happens before completion).</summary>
    public void Remove(string tag)
    {
        try
        {
            var safeTag = SanitizeTag(tag);
            _activeToasts.TryRemove(safeTag, out _);
            ToastNotificationManager.History.Remove(safeTag, NotificationGroup, AppUserModelId);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("Notifications", "Failed to remove toast from history", ex);
        }
    }

    internal static string BuildToastXml(string title, string body, bool silent)
    {
        var binding = new XElement("binding",
            new XAttribute("template", "ToastGeneric"),
            new XElement("text", title),
            new XElement("text", body));

        var toastEl = new XElement("toast",
            new XElement("visual", binding));

        if (silent)
            toastEl.Add(new XElement("audio", new XAttribute("silent", "true")));

        return toastEl.ToString(SaveOptions.DisableFormatting);
    }

    internal static string SanitizeTag(string tag)
    {
        // Windows 10 14393-1809 caps Tag at 16 characters; newer builds cap at 64. Keep <= 16 for universal compatibility.
        if (string.IsNullOrWhiteSpace(tag)) return "default";
        return tag.Length <= 16 ? tag : tag[..16];
    }

    private static string? ResolveIconPath()
    {
        var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(ico)) return ico;

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;

        return null;
    }
}

