using SysMon.Core.Diagnostics;
using SysMon.Core.Formatting;

namespace SysMon.Core.Configuration;

/// <summary>Which stat the tray icon renders, when tray stats are enabled.</summary>
public enum TrayStat
{
    CpuLoad = 0,
    CpuTemperature = 1,
    MemoryLoad = 2,
    GpuLoad = 3,
    GpuTemperature = 4,
}

/// <summary>Persisted user settings. Defaults here are what a fresh install gets.</summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a migration is needed; lets an old file be upgraded rather than discarded.</summary>
    public int SchemaVersion { get; set; } = 1;

    // --- Startup and window behaviour ---

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    // --- Monitoring ---

    /// <summary>Fast-tier poll interval in milliseconds. Clamped to 500-10000.</summary>
    public int UpdateIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Multiplier applied to the interval while the window is hidden. Keeps history filling
    /// without paying full price for a window nobody is looking at.
    /// </summary>
    public int BackgroundIntervalMultiplier { get; set; } = 2;

    /// <summary>
    /// Master switch for LibreHardwareMonitor. Turning this off drops temperatures, clocks, power
    /// and GPU data but leaves the cheap Win32 metrics (CPU load, RAM, network, disk space) running.
    /// </summary>
    public bool EnableHardwareMonitoring { get; set; } = true;

    public bool EnableCpuSensors { get; set; } = true;

    public bool EnableGpuSensors { get; set; } = true;

    public bool EnableMemorySensors { get; set; } = true;

    public bool EnableStorageSensors { get; set; } = true;

    public bool EnableNetworkSensors { get; set; } = true;

    public bool EnableMotherboardSensors { get; set; } = true;

    /// <summary>Adapter to report on, or null to auto-select the busiest connected adapter.</summary>
    public string? PreferredNetworkInterface { get; set; }

    // --- Presentation ---

    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    /// <summary>Graph window in seconds. The UI offers 30, 60, 120 and 300.</summary>
    public int GraphHistorySeconds { get; set; } = 60;

    // --- Tray ---

    public bool EnableTrayStats { get; set; } = true;

    public TrayStat TrayStat { get; set; } = TrayStat.CpuLoad;

    // --- Alerts ---

    public AlertSettings Alerts { get; set; } = new();

    // --- Diagnostics ---

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>Forces every value into the documented range. Applied after loading from disk.</summary>
    public void Normalize()
    {
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 500, 10_000);
        BackgroundIntervalMultiplier = Math.Clamp(BackgroundIntervalMultiplier, 1, 10);
        GraphHistorySeconds = Math.Clamp(GraphHistorySeconds, 30, 3600);

        if (!Enum.IsDefined(TemperatureUnit))
        {
            TemperatureUnit = TemperatureUnit.Celsius;
        }

        if (!Enum.IsDefined(TrayStat))
        {
            TrayStat = TrayStat.CpuLoad;
        }

        if (!Enum.IsDefined(LogLevel))
        {
            LogLevel = LogLevel.Info;
        }

        Alerts ??= new AlertSettings();
        Alerts.Normalize();
    }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.Alerts = Alerts.Clone();
        return clone;
    }
}

public sealed class AlertSettings
{
    /// <summary>Minutes to wait before re-notifying about a condition that is still active.</summary>
    public int RenotifyMinutes { get; set; } = 5;

    public AlertRule CpuTemperature { get; set; } = new() { Enabled = true, Threshold = 85 };

    public AlertRule GpuTemperature { get; set; } = new() { Enabled = true, Threshold = 85 };

    public AlertRule MemoryUsage { get; set; } = new() { Enabled = true, Threshold = 90 };

    public AlertRule StorageUsage { get; set; } = new() { Enabled = true, Threshold = 90 };

    public void Normalize()
    {
        RenotifyMinutes = Math.Clamp(RenotifyMinutes, 1, 240);

        CpuTemperature ??= new AlertRule { Enabled = true, Threshold = 85 };
        GpuTemperature ??= new AlertRule { Enabled = true, Threshold = 85 };
        MemoryUsage ??= new AlertRule { Enabled = true, Threshold = 90 };
        StorageUsage ??= new AlertRule { Enabled = true, Threshold = 90 };

        CpuTemperature.Normalize(0, 150);
        GpuTemperature.Normalize(0, 150);
        MemoryUsage.Normalize(1, 100);
        StorageUsage.Normalize(1, 100);
    }

    public AlertSettings Clone() => new()
    {
        RenotifyMinutes = RenotifyMinutes,
        CpuTemperature = CpuTemperature.Clone(),
        GpuTemperature = GpuTemperature.Clone(),
        MemoryUsage = MemoryUsage.Clone(),
        StorageUsage = StorageUsage.Clone(),
    };
}

public sealed class AlertRule
{
    public bool Enabled { get; set; }

    /// <summary>Threshold in the metric's native unit: Celsius for temperatures, percent otherwise.</summary>
    public double Threshold { get; set; }

    public void Normalize(double min, double max) => Threshold = Math.Clamp(Threshold, min, max);

    public AlertRule Clone() => new() { Enabled = Enabled, Threshold = Threshold };
}
