using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysMon.App.Services;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.Formatting;

namespace SysMon.App.ViewModels;

/// <summary>
/// Editable view over <see cref="AppSettings"/>.
///
/// Every change is applied live and persisted through the debounced store, so there is no Save
/// button and no way to end up with the UI and the file disagreeing.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly Action<AppSettings> _onChanged;
    private AppSettings _settings;
    private bool _suppressUpdates;

    public SettingsViewModel(AppSettings settings, SettingsStore store, Action<AppSettings> onChanged)
    {
        _settings = settings;
        _store = store;
        _onChanged = onChanged;

        LoadFrom(settings);
    }

    public IReadOnlyList<int> IntervalOptions { get; } = [500, 1000, 2000, 5000];

    public IReadOnlyList<int> HistoryOptions { get; } = [30, 60, 120, 300];

    public IReadOnlyList<TrayStat> TrayStatOptions { get; } = Enum.GetValues<TrayStat>();

    public IReadOnlyList<TemperatureUnit> TemperatureUnitOptions { get; } = Enum.GetValues<TemperatureUnit>();

    public IReadOnlyList<LogLevel> LogLevelOptions { get; } = Enum.GetValues<LogLevel>();

    /// <summary>Adapters available for the network card, refreshed as they come and go.</summary>
    public ObservableCollection<string> NetworkInterfaces { get; } = [];

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial int UpdateIntervalMs { get; set; }

    [ObservableProperty]
    public partial bool EnableHardwareMonitoring { get; set; }

    [ObservableProperty]
    public partial bool EnableCpuSensors { get; set; }

    [ObservableProperty]
    public partial bool EnableGpuSensors { get; set; }

    [ObservableProperty]
    public partial bool EnableMemorySensors { get; set; }

    [ObservableProperty]
    public partial bool EnableStorageSensors { get; set; }

    [ObservableProperty]
    public partial bool EnableMotherboardSensors { get; set; }

    [ObservableProperty]
    public partial TemperatureUnit TemperatureUnit { get; set; }

    [ObservableProperty]
    public partial int GraphHistorySeconds { get; set; }

    [ObservableProperty]
    public partial bool EnableTrayStats { get; set; }

    [ObservableProperty]
    public partial TrayStat TrayStat { get; set; }

    [ObservableProperty]
    public partial LogLevel LogLevel { get; set; }

    [ObservableProperty]
    public partial bool AlertCpuTemperatureEnabled { get; set; }

    [ObservableProperty]
    public partial double AlertCpuTemperatureThreshold { get; set; }

    [ObservableProperty]
    public partial bool AlertGpuTemperatureEnabled { get; set; }

    [ObservableProperty]
    public partial double AlertGpuTemperatureThreshold { get; set; }

    [ObservableProperty]
    public partial bool AlertMemoryEnabled { get; set; }

    [ObservableProperty]
    public partial double AlertMemoryThreshold { get; set; }

    [ObservableProperty]
    public partial bool AlertStorageEnabled { get; set; }

    [ObservableProperty]
    public partial double AlertStorageThreshold { get; set; }

    /// <summary>Set when writing the Run registry key failed, so the UI can explain why.</summary>
    [ObservableProperty]
    public partial string? StartupError { get; set; }

    public string SettingsFilePath => _store.Path;

    public string LogFilePath => Log.FilePath ?? Format.NotAvailable;

    [RelayCommand]
    private static void OpenDataFolder() => ShellService.OpenFolder(AppPaths.DataDirectory);

    [RelayCommand]
    private void ResetToDefaults()
    {
        LoadFrom(new AppSettings());
        Persist();
    }

    /// <summary>Refreshes the adapter list shown in the network setting.</summary>
    public void UpdateNetworkInterfaces(IReadOnlyList<string> interfaces)
    {
        if (NetworkInterfaces.Count == interfaces.Count)
        {
            var identical = true;
            for (var i = 0; i < interfaces.Count; i++)
            {
                if (!string.Equals(NetworkInterfaces[i], interfaces[i], StringComparison.Ordinal))
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                return;
            }
        }

        NetworkInterfaces.Clear();
        foreach (var name in interfaces)
        {
            NetworkInterfaces.Add(name);
        }
    }

    private void LoadFrom(AppSettings settings)
    {
        _suppressUpdates = true;

        try
        {
            _settings = settings.Clone();

            StartWithWindows = settings.StartWithWindows;
            StartMinimized = settings.StartMinimized;
            MinimizeToTray = settings.MinimizeToTray;
            CloseToTray = settings.CloseToTray;
            UpdateIntervalMs = settings.UpdateIntervalMs;
            EnableHardwareMonitoring = settings.EnableHardwareMonitoring;
            EnableCpuSensors = settings.EnableCpuSensors;
            EnableGpuSensors = settings.EnableGpuSensors;
            EnableMemorySensors = settings.EnableMemorySensors;
            EnableStorageSensors = settings.EnableStorageSensors;
            EnableMotherboardSensors = settings.EnableMotherboardSensors;
            TemperatureUnit = settings.TemperatureUnit;
            GraphHistorySeconds = settings.GraphHistorySeconds;
            EnableTrayStats = settings.EnableTrayStats;
            TrayStat = settings.TrayStat;
            LogLevel = settings.LogLevel;

            AlertCpuTemperatureEnabled = settings.Alerts.CpuTemperature.Enabled;
            AlertCpuTemperatureThreshold = settings.Alerts.CpuTemperature.Threshold;
            AlertGpuTemperatureEnabled = settings.Alerts.GpuTemperature.Enabled;
            AlertGpuTemperatureThreshold = settings.Alerts.GpuTemperature.Threshold;
            AlertMemoryEnabled = settings.Alerts.MemoryUsage.Enabled;
            AlertMemoryThreshold = settings.Alerts.MemoryUsage.Threshold;
            AlertStorageEnabled = settings.Alerts.StorageUsage.Enabled;
            AlertStorageThreshold = settings.Alerts.StorageUsage.Threshold;
        }
        finally
        {
            _suppressUpdates = false;
        }
    }

    /// <summary>
    /// Every generated property setter routes here. Rather than wiring 20 individual handlers,
    /// the whole settings object is rebuilt from the current values and re-applied.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_suppressUpdates || e.PropertyName is nameof(StartupError))
        {
            return;
        }

        Persist();
    }

    private void Persist()
    {
        var settings = new AppSettings
        {
            StartWithWindows = StartWithWindows,
            StartMinimized = StartMinimized,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            UpdateIntervalMs = UpdateIntervalMs,
            BackgroundIntervalMultiplier = _settings.BackgroundIntervalMultiplier,
            EnableHardwareMonitoring = EnableHardwareMonitoring,
            EnableCpuSensors = EnableCpuSensors,
            EnableGpuSensors = EnableGpuSensors,
            EnableMemorySensors = EnableMemorySensors,
            EnableStorageSensors = EnableStorageSensors,
            EnableMotherboardSensors = EnableMotherboardSensors,
            EnableNetworkSensors = _settings.EnableNetworkSensors,
            PreferredNetworkInterface = _settings.PreferredNetworkInterface,
            TemperatureUnit = TemperatureUnit,
            GraphHistorySeconds = GraphHistorySeconds,
            EnableTrayStats = EnableTrayStats,
            TrayStat = TrayStat,
            LogLevel = LogLevel,
            Alerts = new AlertSettings
            {
                RenotifyMinutes = _settings.Alerts.RenotifyMinutes,
                CpuTemperature = new AlertRule { Enabled = AlertCpuTemperatureEnabled, Threshold = AlertCpuTemperatureThreshold },
                GpuTemperature = new AlertRule { Enabled = AlertGpuTemperatureEnabled, Threshold = AlertGpuTemperatureThreshold },
                MemoryUsage = new AlertRule { Enabled = AlertMemoryEnabled, Threshold = AlertMemoryThreshold },
                StorageUsage = new AlertRule { Enabled = AlertStorageEnabled, Threshold = AlertStorageThreshold },
            },
        };

        settings.Normalize();

        if (settings.StartWithWindows != _settings.StartWithWindows)
        {
            StartupError = StartupService.SetRunAtLogin(settings.StartWithWindows);

            // Registry write refused (policy, roaming profile): reflect reality rather than
            // leaving a checkbox that claims something untrue.
            if (StartupError is not null)
            {
                settings.StartWithWindows = StartupService.IsRunAtLoginEnabled();
                _suppressUpdates = true;
                StartWithWindows = settings.StartWithWindows;
                _suppressUpdates = false;
            }
        }

        _settings = settings;
        _store.Save(settings);
        _onChanged(settings);
    }
}
