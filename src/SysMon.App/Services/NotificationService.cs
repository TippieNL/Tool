using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using SysMon.Core.Diagnostics;

namespace SysMon.App.Services;

public interface INotificationService
{
    void Notify(string title, string message);
}

/// <summary>
/// Shows alerts through the tray icon's balloon.
///
/// This is deliberately not the toast API: a non-packaged desktop app needs a registered AUMID and
/// a Start Menu shortcut before it can raise a real toast, whereas Windows 10 and 11 already
/// surface shell balloons through the same notification centre. Same result, no install-time setup.
/// </summary>
public sealed class TrayNotificationService : INotificationService
{
    private readonly Func<TaskbarIcon?> _iconAccessor;
    private readonly Dispatcher _dispatcher;

    public TrayNotificationService(Func<TaskbarIcon?> iconAccessor, Dispatcher dispatcher)
    {
        _iconAccessor = iconAccessor;
        _dispatcher = dispatcher;
    }

    public void Notify(string title, string message)
    {
        // Alerts are evaluated on the monitoring thread, so the shell call has to be marshalled.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                _iconAccessor()?.ShowNotification(title, message, NotificationIcon.Warning);
            }
            catch (Exception ex)
            {
                // A notification that cannot be shown is not a reason to stop monitoring.
                Log.Once("notify", LogLevel.Warn, "Could not show a notification.", ex);
            }
        });
    }
}

/// <summary>Used when the tray is unavailable, and by the probe. Records to the log only.</summary>
public sealed class LogOnlyNotificationService : INotificationService
{
    public void Notify(string title, string message) => Log.Info($"Notification: {title} — {message}");
}
