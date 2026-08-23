using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysMon.App.Services;
using SysMon.Core.Alerts;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.Formatting;
using SysMon.Core.History;
using SysMon.Core.Models;
using SysMon.Monitoring;

namespace SysMon.App.ViewModels;

public enum AppPage
{
    Dashboard,
    Performance,
    Sensors,
    System,
    Settings,
}

/// <summary>
/// The single view model behind the window.
///
/// Snapshots arrive on the monitoring thread and are marshalled to the UI exactly once per tick;
/// from there every card is updated in place. Nothing in the UI polls hardware, and nothing in the
/// monitoring layer knows this class exists.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MonitoringService _monitoring;
    private readonly HistoryStore _history;
    private readonly SettingsStore _settingsStore;
    private readonly AlertEngine _alerts = new();
    private readonly INotificationService _notifications;
    private readonly Dispatcher _dispatcher;

    private AppSettings _settings;
    private bool _uiVisible = true;
    private bool _disposed;

    /// <summary>Guards against queuing a second UI update while one is still pending.</summary>
    private int _applyQueued;

    public MainViewModel(
        MonitoringService monitoring,
        HistoryStore history,
        SettingsStore settingsStore,
        AppSettings settings,
        INotificationService notifications,
        Dispatcher dispatcher)
    {
        _monitoring = monitoring;
        _history = history;
        _settingsStore = settingsStore;
        _settings = settings;
        _notifications = notifications;
        _dispatcher = dispatcher;

        Settings = new SettingsViewModel(settings, settingsStore, ApplySettings);

        Cpu.Series = history.Get(SeriesIds.CpuLoad);
        CpuTemperatureSeries = history.Get(SeriesIds.CpuTemperature);
        Memory.Series = history.Get(SeriesIds.MemoryLoad);
        Storage.Series = history.Get(SeriesIds.DiskActivity);
        Network.Series = history.Get(SeriesIds.NetworkDown);
        Network.UploadSeries = history.Get(SeriesIds.NetworkUp);

        RebuildCards();

        _monitoring.SnapshotAvailable += OnSnapshotAvailable;
    }

    public CpuCardViewModel Cpu { get; } = new();

    public MemoryCardViewModel Memory { get; } = new();

    public ObservableCollection<GpuCardViewModel> Gpus { get; } = [];

    public StorageCardViewModel Storage { get; } = new();

    public NetworkCardViewModel Network { get; } = new();

    public TemperatureCardViewModel Temperatures { get; } = new();

    public SystemCardViewModel System { get; } = new();

    public SettingsViewModel Settings { get; }

    /// <summary>
    /// Every dashboard card in display order. Kept as one flat collection so a single WrapPanel
    /// can reflow them all; nesting an items control per GPU would break the reflow.
    /// Rebuilt only when the set of GPUs changes.
    /// </summary>
    public ObservableCollection<CardViewModel> Cards { get; } = [];

    public HistoryStore History => _history;

    /// <summary>CPU temperature history. Lives on the view model rather than the CPU card
    /// because the card shows load; only the Performance page graphs temperature.</summary>
    public HistorySeries CpuTemperatureSeries { get; }

    /// <summary>Live settings. The window reads these rather than caching its own copy,
    /// so a change made in the settings page takes effect on the next tick.</summary>
    public AppSettings CurrentSettings => _settings;

    [ObservableProperty]
    public partial AppPage CurrentPage { get; set; } = AppPage.Dashboard;

    [ObservableProperty]
    public partial bool IsMonitoring { get; set; }

    /// <summary>Set when the sensor library is loaded but running unelevated.</summary>
    [ObservableProperty]
    public partial bool ShowElevationHint { get; set; }

    [ObservableProperty]
    public partial bool ElevationHintDismissed { get; set; }

    /// <summary>Human-readable summary of any faulted providers, shown in the status strip.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Starting...";

    [ObservableProperty]
    public partial bool HasFaults { get; set; }

    /// <summary>Raised each tick after the cards are updated, so graphs can repaint once.</summary>
    public event Action? SnapshotApplied;

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (_monitoring.IsRunning)
        {
            _monitoring.Stop();
            StatusText = "Monitoring paused";
        }
        else
        {
            _monitoring.Start();
        }

        IsMonitoring = _monitoring.IsRunning;
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        if (Enum.TryParse<AppPage>(page, out var target))
        {
            CurrentPage = target;
        }
    }

    [RelayCommand]
    private void DismissElevationHint()
    {
        ElevationHintDismissed = true;
        ShowElevationHint = false;
    }

    [RelayCommand]
    private static void RestartElevated() => ElevationService.RestartAsAdministrator();

    public void Start()
    {
        _monitoring.Start();
        IsMonitoring = _monitoring.IsRunning;
    }

    /// <summary>
    /// Tells the app whether anyone can see the UI. While hidden, snapshots are still collected
    /// into history but no view model is touched and no graph repaints.
    /// </summary>
    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        _monitoring.SetBackgroundMode(!visible);

        if (visible)
        {
            // Catch up immediately so a restored window is not a second out of date.
            Apply(_monitoring.Latest);
        }
    }

    private void OnSnapshotAvailable(Snapshot snapshot)
    {
        // Alerts are evaluated on the monitoring thread: they must fire whether or not the
        // window is open, and the work is a handful of comparisons.
        RaiseAlerts(snapshot);

        if (!_uiVisible)
        {
            return;
        }

        // One marshal per tick for the entire UI, and never more than one in flight: if the UI
        // thread is busy, later ticks are dropped rather than queued, so a stalled window cannot
        // build a backlog it then has to replay.
        if (Interlocked.Exchange(ref _applyQueued, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _applyQueued, 0);
            Apply(snapshot);
        });
    }

    private void RaiseAlerts(Snapshot snapshot)
    {
        try
        {
            foreach (var alert in _alerts.Evaluate(snapshot, _settings.Alerts, _settings.TemperatureUnit))
            {
                _notifications.Notify(alert.Title, alert.Message);
                Log.Info($"Alert: {alert.Title} — {alert.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Once("alerts", LogLevel.Error, "Alert evaluation failed.", ex);
        }
    }

    private void Apply(Snapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        var unit = _settings.TemperatureUnit;

        Cpu.Update(snapshot.Cpu, unit);
        Memory.Update(snapshot.Memory);
        Storage.Update(snapshot.Drives, unit);
        Network.Update(snapshot.Network);
        Temperatures.Update(snapshot.Temperatures, unit);
        System.Update(snapshot);

        var gpuCountBefore = Gpus.Count;

        StorageCardViewModel.SyncCollection(Gpus, snapshot.Gpus, static g => g.Key,
            key => new GpuCardViewModel(key) { Series = _history.Get(SeriesIds.GpuLoad(key)) },
            (vm, gpu) => vm.Update(gpu, unit));

        if (Gpus.Count != gpuCountBefore)
        {
            RebuildCards();
        }

        Settings.UpdateNetworkInterfaces(snapshot.Network.AvailableInterfaces);

        ShowElevationHint = snapshot.LimitedSensorAccess
            && !ElevationHintDismissed
            && snapshot.Cpu.TemperatureC is null;

        HasFaults = snapshot.FaultedProviders.Count > 0;
        StatusText = HasFaults
            ? $"Degraded: {string.Join(", ", snapshot.FaultedProviders)} unavailable"
            : $"Updated {snapshot.Timestamp:HH:mm:ss} · {Format.Percent(snapshot.Cpu.TotalLoad)} CPU";

        SnapshotApplied?.Invoke();
    }

    /// <summary>Rebuilds the flat card list. Cheap, and only runs when the GPU set changes.</summary>
    private void RebuildCards()
    {
        Cards.Clear();
        Cards.Add(Cpu);

        foreach (var gpu in Gpus)
        {
            Cards.Add(gpu);
        }

        Cards.Add(Memory);
        Cards.Add(Storage);
        Cards.Add(Network);
        Cards.Add(Temperatures);
    }

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _monitoring.ApplySettings(settings);
        _alerts.Reset();
        Log.MinimumLevel = settings.LogLevel;

        // Re-render immediately so a unit or interval change is visible without waiting a tick.
        Apply(_monitoring.Latest);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitoring.SnapshotAvailable -= OnSnapshotAvailable;
        _settingsStore.Flush();
    }
}
