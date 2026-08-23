namespace SysMon.Core.Models;

/// <summary>
/// One complete reading of the machine, produced once per monitoring tick.
///
/// Every numeric field is nullable. <c>null</c> is the single, universal representation of
/// "this sensor is absent, unsupported, or failed this tick" and is rendered as "N/A" by the UI.
/// There are no sentinel values (no -1, no 0-means-missing) anywhere in this model.
/// </summary>
public sealed record Snapshot
{
    public static readonly Snapshot Empty = new();

    /// <summary>Wall-clock time the sample was taken.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public CpuSnapshot Cpu { get; init; } = new();

    public MemorySnapshot Memory { get; init; } = new();

    public IReadOnlyList<GpuSnapshot> Gpus { get; init; } = [];

    public IReadOnlyList<DriveSnapshot> Drives { get; init; } = [];

    public NetworkSnapshot Network { get; init; } = new();

    /// <summary>Every temperature sensor discovered anywhere in the machine.</summary>
    public IReadOnlyList<TemperatureReading> Temperatures { get; init; } = [];

    public SystemInfo System { get; init; } = new();

    /// <summary>Providers that are currently faulted, for the UI's degraded-state banner.</summary>
    public IReadOnlyList<string> FaultedProviders { get; init; } = [];

    /// <summary>Why driver-backed sensors are or are not available.</summary>
    public SensorAccess SensorAccess { get; init; }
}

/// <summary>
/// How much of the hardware the sensor library can actually reach.
///
/// These are genuinely different situations with different remedies, and collapsing them into one
/// "limited" flag leaves the user with a missing temperature and no idea what to do about it.
/// </summary>
public enum SensorAccess
{
    /// <summary>Driver-backed sensors are readable.</summary>
    Full = 0,

    /// <summary>The user turned hardware monitoring off.</summary>
    MonitoringDisabled,

    /// <summary>Running as a standard user. Elevating will unlock the missing sensors.</summary>
    NotElevated,

    /// <summary>
    /// Elevated, but the kernel driver did not load, so CPU and motherboard sensors are
    /// unreadable. Usually a security feature blocking the driver rather than anything the user
    /// did wrong.
    /// </summary>
    DriverUnavailable,
}

public sealed record CpuSnapshot
{
    public string? Name { get; init; }

    /// <summary>Total utilisation across all logical processors, 0-100.</summary>
    public double? TotalLoad { get; init; }

    /// <summary>Per-logical-processor utilisation, 0-100. Empty when unavailable.</summary>
    public IReadOnlyList<double> CoreLoads { get; init; } = [];

    /// <summary>Highest reported core clock in MHz.</summary>
    public double? ClockMhz { get; init; }

    public double? TemperatureC { get; init; }

    /// <summary>CPU package power draw in watts.</summary>
    public double? PackagePowerW { get; init; }

    public int? PhysicalCores { get; init; }

    public int? LogicalCores { get; init; }
}

public sealed record MemorySnapshot
{
    public double? TotalBytes { get; init; }

    public double? UsedBytes { get; init; }

    public double? AvailableBytes { get; init; }

    /// <summary>Used as a percentage of total, 0-100.</summary>
    public double? LoadPercent { get; init; }

    /// <summary>Configured module speed in MT/s, e.g. 3200.</summary>
    public double? SpeedMtps { get; init; }

    /// <summary>Human-readable module summary, e.g. "2 x 16 GB DDR4".</summary>
    public string? ModuleSummary { get; init; }
}

public sealed record GpuSnapshot
{
    /// <summary>Stable key used to match this GPU across ticks and to key its history series.</summary>
    public required string Key { get; init; }

    public string? Name { get; init; }

    public GpuVendor Vendor { get; init; }

    public double? Load { get; init; }

    public double? TemperatureC { get; init; }

    public double? CoreClockMhz { get; init; }

    public double? VramUsedBytes { get; init; }

    public double? VramTotalBytes { get; init; }

    public double? PowerW { get; init; }

    public double? FanPercent { get; init; }
}

public enum GpuVendor
{
    Unknown = 0,
    Nvidia,
    Amd,
    Intel,
}

public sealed record DriveSnapshot
{
    /// <summary>Stable key used to match this drive across ticks.</summary>
    public required string Key { get; init; }

    /// <summary>Mount point, e.g. "C:\".</summary>
    public string? MountPoint { get; init; }

    /// <summary>Volume label, or the hardware model when no label is set.</summary>
    public string? Label { get; init; }

    /// <summary>Hardware model reported by the drive, e.g. "Samsung SSD 970 EVO 1TB".</summary>
    public string? Model { get; init; }

    public double? TotalBytes { get; init; }

    public double? UsedBytes { get; init; }

    public double? FreeBytes { get; init; }

    public double? UsedPercent { get; init; }

    public double? ReadBytesPerSecond { get; init; }

    public double? WriteBytesPerSecond { get; init; }

    /// <summary>Total activity, 0-100.</summary>
    public double? ActivityPercent { get; init; }

    public double? TemperatureC { get; init; }

    public DriveMediaType MediaType { get; init; }
}

public enum DriveMediaType
{
    Unknown = 0,
    Ssd,
    Hdd,
    Removable,
}

public sealed record NetworkSnapshot
{
    /// <summary>The adapter these figures describe.</summary>
    public string? InterfaceName { get; init; }

    public string? InterfaceDescription { get; init; }

    public double? DownloadBytesPerSecond { get; init; }

    public double? UploadBytesPerSecond { get; init; }

    /// <summary>Bytes received by this adapter since the adapter came up.</summary>
    public double? TotalDownloadedBytes { get; init; }

    public double? TotalUploadedBytes { get; init; }

    /// <summary>Bytes received while this app has been running.</summary>
    public double? SessionDownloadedBytes { get; init; }

    public double? SessionUploadedBytes { get; init; }

    /// <summary>Link speed in bits per second.</summary>
    public double? LinkSpeedBps { get; init; }

    /// <summary>All adapter names available for selection in settings.</summary>
    public IReadOnlyList<string> AvailableInterfaces { get; init; } = [];
}

/// <summary>A single named temperature sensor, grouped under the hardware that exposes it.</summary>
public sealed record TemperatureReading
{
    public required string Key { get; init; }

    /// <summary>Hardware the sensor belongs to, e.g. "AMD Ryzen 7 2700X".</summary>
    public required string Source { get; init; }

    /// <summary>Sensor name, e.g. "Core (Tctl/Tdie)".</summary>
    public required string Name { get; init; }

    public double? Celsius { get; init; }

    public TemperatureGroup Group { get; init; }
}

public enum TemperatureGroup
{
    Other = 0,
    Cpu,
    Gpu,
    Motherboard,
    Storage,
}

/// <summary>Static facts about the machine, gathered once at startup. Uptime refreshes per tick.</summary>
public sealed record SystemInfo
{
    public string? HostName { get; init; }

    public string? OsName { get; init; }

    public string? OsVersion { get; init; }

    public string? Motherboard { get; init; }

    public string? BiosVersion { get; init; }

    public int? PhysicalCores { get; init; }

    public int? LogicalCores { get; init; }

    public TimeSpan? Uptime { get; init; }

    /// <summary>True when the process holds administrator rights.</summary>
    public bool IsElevated { get; init; }
}
