using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SysMon.App.Services;
using SysMon.App.ViewModels;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.History;
using SysMon.Monitoring;

namespace SysMon.App;

/// <summary>
/// Application entry point and composition root.
///
/// Everything is constructed here and handed down; there is no container and no service locator,
/// which keeps startup to a few milliseconds and makes the dependency graph readable.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\PulseMonitor.SingleInstance";

    private Mutex? _instanceMutex;
    private SettingsStore? _settingsStore;
    private HardwareSession? _session;
    private MonitoringService? _monitoring;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second launch should surface the running window rather than start a rival monitor.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            SingleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        AppPaths.EnsureDataDirectory();

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        var settings = settingsStore.Load();

        Log.MinimumLevel = settings.LogLevel;
        Log.Start(AppPaths.LogFile);
        Log.Info($"{AppPaths.DisplayName} starting (elevated={HardwareSession.IsElevated}).");

        // Any exception that escapes a handler is logged and swallowed. A monitoring utility
        // that vanishes because one sensor misbehaved is worse than one showing "N/A".
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var history = new HistoryStore(HistoryStore.CapacityFor(
            TimeSpan.FromSeconds(settings.GraphHistorySeconds),
            TimeSpan.FromMilliseconds(settings.UpdateIntervalMs)));

        var session = new HardwareSession();
        var monitoring = new MonitoringService(settings, session, history);

        var window = new MainWindow();

        var notifications = new TrayNotificationService(() => window.TrayIcon, Dispatcher);
        var viewModel = new MainViewModel(monitoring, history, settingsStore, settings, notifications, Dispatcher);

        _settingsStore = settingsStore;
        _session = session;
        _monitoring = monitoring;
        _viewModel = viewModel;

        window.Initialize(viewModel);
        MainWindow = window;

        // Reflect reality: the registry is the source of truth for autostart, not the settings file.
        viewModel.Settings.StartWithWindows = StartupService.IsRunAtLoginEnabled();

        if (!settings.StartMinimized)
        {
            window.Show();
        }
        else
        {
            Log.Info("Starting minimised to the tray.");
            viewModel.SetUiVisible(false);
        }

        SingleInstance.Listen(() => Dispatcher.BeginInvoke(window.RestoreFromTray));

        viewModel.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Shutting down.");

        _viewModel?.Dispose();
        _monitoring?.Dispose();
        _session?.Dispose();
        _settingsStore?.Dispose();

        Log.Flush();

        _instanceMutex?.Dispose();

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the UI thread.", e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Error("Unhandled exception on a background thread.", ex);
        }

        Log.Flush();
    }
}
