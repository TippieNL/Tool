using SysMon.Core.Models;

namespace SysMon.Core.Monitoring;

/// <summary>
/// Mutable scratch space that providers write into during a tick. Reused across ticks so that
/// steady-state monitoring allocates only the immutable <see cref="Snapshot"/> it publishes.
/// </summary>
public sealed class SnapshotBuilder
{
    private readonly List<GpuSnapshot> _gpus = [];
    private readonly List<DriveSnapshot> _drives = [];
    private readonly List<TemperatureReading> _temperatures = [];
    private readonly List<string> _faultedProviders = [];

    public CpuSnapshot Cpu { get; set; } = new();

    public MemorySnapshot Memory { get; set; } = new();

    public NetworkSnapshot Network { get; set; } = new();

    public SystemInfo System { get; set; } = new();

    public bool LimitedSensorAccess { get; set; }

    public void SetGpus(IEnumerable<GpuSnapshot> gpus)
    {
        _gpus.Clear();
        _gpus.AddRange(gpus);
    }

    public void SetDrives(IEnumerable<DriveSnapshot> drives)
    {
        _drives.Clear();
        _drives.AddRange(drives);
    }

    public void SetTemperatures(IEnumerable<TemperatureReading> temperatures)
    {
        _temperatures.Clear();
        _temperatures.AddRange(temperatures);
    }

    public void ReportFault(string providerName) => _faultedProviders.Add(providerName);

    /// <summary>Clears the per-tick fault list. Values themselves are retained so that a
    /// slow-tier provider's last reading survives the ticks on which it does not run.</summary>
    public void BeginTick() => _faultedProviders.Clear();

    public Snapshot Build() => new()
    {
        Timestamp = DateTimeOffset.Now,
        Cpu = Cpu,
        Memory = Memory,
        Gpus = _gpus.Count == 0 ? [] : _gpus.ToArray(),
        Drives = _drives.Count == 0 ? [] : _drives.ToArray(),
        Network = Network,
        Temperatures = _temperatures.Count == 0 ? [] : _temperatures.ToArray(),
        System = System,
        FaultedProviders = _faultedProviders.Count == 0 ? [] : _faultedProviders.ToArray(),
        LimitedSensorAccess = LimitedSensorAccess,
    };
}
