using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;
using SysMon.Monitoring.Interop;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// CPU utilisation, clock, temperature and package power.
///
/// Utilisation comes from NtQuerySystemInformation rather than PerformanceCounter: one call
/// returns every logical processor's idle/kernel/user times, so per-core load costs the same as
/// total load and neither allocates. Clock, temperature and power come from the sensor library,
/// which is the only source for them, and are simply absent when it cannot load.
/// </summary>
public sealed class CpuProvider : IMetricProvider
{
    private readonly HardwareSession _session;

    private IntPtr _buffer;
    private int _bufferSize;
    private int _processorCount;

    private long[] _previousIdle = [];
    private long[] _previousTotal = [];
    private double[] _coreLoads = [];
    private bool _hasBaseline;

    private string? _cpuName;
    private int? _physicalCores;

    public CpuProvider(HardwareSession session) => _session = session;

    public string Name => "CPU";

    public PollTier Tier => PollTier.Fast;

    public void Initialize()
    {
        _processorCount = Environment.ProcessorCount;
        _bufferSize = Marshal.SizeOf<NativeMethods.SystemProcessorPerformanceInfo>() * _processorCount;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
        }

        _buffer = Marshal.AllocHGlobal(_bufferSize);

        _previousIdle = new long[_processorCount];
        _previousTotal = new long[_processorCount];
        _coreLoads = new double[_processorCount];
        _hasBaseline = false;

        _cpuName = null;
        _physicalCores = null;
    }

    public void Poll(SnapshotBuilder builder)
    {
        var (totalLoad, coreLoads) = ReadUtilization();

        double? clock = null;
        double? temperature = null;
        double? power = null;

        foreach (var hardware in _session.GetHardware(HardwareType.Cpu))
        {
            HardwareSession.Update(hardware);

            _cpuName ??= hardware.Name;

            // Highest per-core clock reads as "what the CPU is doing right now" far better than
            // an average across cores that are parked.
            clock ??= HardwareSession.MaxSensor(hardware, SensorType.Clock, SensorRanges.Clock, "Core");

            // Ryzen exposes Tdie/Tctl; Intel exposes a package sensor. Try the specific names
            // first and fall back to any temperature sensor the CPU offers.
            // Ryzen X-series parts apply a 10 degree Tctl offset, so a failed read arrives as
            // -10 rather than as an error. The range rejects it and the card shows N/A.
            temperature ??= HardwareSession.ReadSensor(
                hardware, SensorType.Temperature, SensorRanges.Temperature,
                "Tdie", "Tctl", "Package", "Core Average", "Core Max", "CPU");

            power ??= HardwareSession.ReadSensor(hardware, SensorType.Power, SensorRanges.Power, "Package", "CPU Package");

            break;
        }

        builder.Cpu = new CpuSnapshot
        {
            Name = _cpuName ?? builder.Cpu.Name,
            TotalLoad = totalLoad,
            CoreLoads = coreLoads,
            ClockMhz = clock,
            TemperatureC = temperature,
            PackagePowerW = power,
            PhysicalCores = _physicalCores ??= SystemInfoProvider.CountPhysicalCores(),
            LogicalCores = _processorCount,
        };
    }

    private (double? Total, IReadOnlyList<double> PerCore) ReadUtilization()
    {
        if (_buffer == IntPtr.Zero)
        {
            return (null, []);
        }

        var status = NativeMethods.NtQuerySystemInformation(
            NativeMethods.SystemProcessorPerformanceInformation, _buffer, _bufferSize, out _);

        if (status != 0)
        {
            return (null, []);
        }

        var stride = Marshal.SizeOf<NativeMethods.SystemProcessorPerformanceInfo>();

        long idleDelta = 0;
        long totalDelta = 0;

        for (var i = 0; i < _processorCount; i++)
        {
            var info = Marshal.PtrToStructure<NativeMethods.SystemProcessorPerformanceInfo>(_buffer + (i * stride));

            // KernelTime already includes IdleTime, so the busy portion is
            // (kernel + user) - idle and the denominator is simply kernel + user.
            var total = info.KernelTime + info.UserTime;
            var idle = info.IdleTime;

            if (_hasBaseline)
            {
                var coreIdle = idle - _previousIdle[i];
                var coreTotal = total - _previousTotal[i];

                _coreLoads[i] = coreTotal > 0
                    ? Math.Clamp(100d * (coreTotal - coreIdle) / coreTotal, 0, 100)
                    : 0;

                idleDelta += coreIdle;
                totalDelta += coreTotal;
            }

            _previousIdle[i] = idle;
            _previousTotal[i] = total;
        }

        if (!_hasBaseline)
        {
            // The very first sample has nothing to diff against.
            _hasBaseline = true;
            return (null, []);
        }

        var totalLoad = totalDelta > 0
            ? Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100)
            : 0d;

        return (totalLoad, _coreLoads);
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
