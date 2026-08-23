using LibreHardwareMonitor.Hardware;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Collects every temperature sensor in the machine into one list for the Temperatures card and
/// the Sensors page.
///
/// This provider deliberately does not know what sensors exist. It walks whatever the library
/// reports, including sub-hardware such as SuperIO chips hanging off the motherboard, so a sensor
/// this code has never heard of still shows up with its own name.
///
/// It relies on the CPU, GPU and storage providers having already refreshed their hardware this
/// tick, so it reads values rather than paying to update the same hardware twice.
/// </summary>
public sealed class TemperatureProvider : IMetricProvider
{
    private readonly HardwareSession _session;
    private readonly List<TemperatureReading> _buffer = [];
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);

    public TemperatureProvider(HardwareSession session) => _session = session;

    public string Name => "Temperatures";

    public PollTier Tier => PollTier.Fast;

    public void Initialize()
    {
        // Nothing to acquire: hardware is enumerated by the shared session.
    }

    public void Poll(SnapshotBuilder builder)
    {
        _buffer.Clear();
        _seenKeys.Clear();

        foreach (var hardware in _session.AllHardware())
        {
            // Motherboard sensors live on sub-hardware and are on the slow tier, so they are
            // updated by the motherboard provider rather than here.
            Collect(hardware, hardware.Name, GroupFor(hardware.HardwareType));

            foreach (var sub in hardware.SubHardware)
            {
                Collect(sub, hardware.Name, GroupFor(hardware.HardwareType));
            }
        }

        builder.SetTemperatures(_buffer);
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    private void Collect(IHardware hardware, string sourceName, TemperatureGroup group)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || SensorNaming.IsThresholdSensor(sensor.Name))
            {
                continue;
            }

            // Unpopulated board headers and unreachable drivers both report values no running
            // machine could produce, so anything outside the plausible range is treated as absent.
            if (sensor.Value is not { } value || !SensorRanges.Temperature.Contains(value))
            {
                continue;
            }

            var key = sensor.Identifier.ToString();
            if (!_seenKeys.Add(key))
            {
                continue;
            }

            _buffer.Add(new TemperatureReading
            {
                Key = key,
                Source = sourceName,
                Name = sensor.Name,
                Celsius = value,
                Group = group,
            });
        }
    }

    private static TemperatureGroup GroupFor(HardwareType type) => type switch
    {
        HardwareType.Cpu => TemperatureGroup.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => TemperatureGroup.Gpu,
        HardwareType.Motherboard or HardwareType.SuperIO => TemperatureGroup.Motherboard,
        HardwareType.Storage => TemperatureGroup.Storage,
        _ => TemperatureGroup.Other,
    };
}
