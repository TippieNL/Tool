using LibreHardwareMonitor.Hardware;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Every discrete and integrated GPU the sensor library can see, across NVIDIA, AMD and Intel.
///
/// Vendors expose different sensor names for the same quantity, so each reading tries the known
/// names in order and falls back to any sensor of the right type. A machine with no GPU sensors
/// at all simply produces an empty list, which the UI renders as an absent card rather than an error.
/// </summary>
public sealed class GpuProvider : IMetricProvider
{
    private readonly HardwareSession _session;
    private readonly List<GpuSnapshot> _buffer = [];

    public GpuProvider(HardwareSession session) => _session = session;

    public string Name => "GPU";

    public PollTier Tier => PollTier.Fast;

    public void Initialize()
    {
        // Nothing to acquire: hardware is enumerated by the shared session.
    }

    public void Poll(SnapshotBuilder builder)
    {
        _buffer.Clear();

        var index = 0;

        foreach (var hardware in _session.GetHardware(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel))
        {
            HardwareSession.Update(hardware);

            var vendor = hardware.HardwareType switch
            {
                HardwareType.GpuNvidia => GpuVendor.Nvidia,
                HardwareType.GpuAmd => GpuVendor.Amd,
                HardwareType.GpuIntel => GpuVendor.Intel,
                _ => GpuVendor.Unknown,
            };

            var (vramUsed, vramTotal) = ReadVram(hardware);

            _buffer.Add(new GpuSnapshot
            {
                // Identifier is stable across ticks; the index keeps two identical cards apart.
                Key = $"{hardware.Identifier}#{index++}",
                Name = hardware.Name,
                Vendor = vendor,
                Load = HardwareSession.ReadSensor(hardware, SensorType.Load, "GPU Core", "D3D 3D", "Core"),
                TemperatureC = HardwareSession.ReadSensor(hardware, SensorType.Temperature, "GPU Core", "GPU Hot Spot", "Core", "GPU"),
                CoreClockMhz = HardwareSession.ReadSensor(hardware, SensorType.Clock, "GPU Core", "Core"),
                VramUsedBytes = vramUsed,
                VramTotalBytes = vramTotal,
                PowerW = HardwareSession.ReadSensor(hardware, SensorType.Power, "GPU Package", "GPU Power", "Package"),
                FanPercent = HardwareSession.ReadSensor(hardware, SensorType.Control, "GPU Fan", "Fan"),
            });
        }

        builder.SetGpus(_buffer);
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>
    /// VRAM figures, normalised to bytes.
    ///
    /// The sensor library reports dedicated memory in megabytes under SmallData, so the values are
    /// scaled here. Intel integrated graphics frequently expose neither, which yields nulls.
    /// </summary>
    private static (double? Used, double? Total) ReadVram(IHardware hardware)
    {
        const double MegabytesToBytes = 1024d * 1024d;

        var usedMb = HardwareSession.ReadSensor(hardware, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used", "Memory Used");
        var totalMb = HardwareSession.ReadSensor(hardware, SensorType.SmallData, "GPU Memory Total", "Memory Total");

        // Some drivers report only free and total; derive used from those when needed.
        if (usedMb is null && totalMb is { } total)
        {
            var freeMb = HardwareSession.ReadSensor(hardware, SensorType.SmallData, "GPU Memory Free", "Memory Free");
            if (freeMb is { } free)
            {
                usedMb = Math.Max(0, total - free);
            }
        }

        return (usedMb * MegabytesToBytes, totalMb * MegabytesToBytes);
    }
}
