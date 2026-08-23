using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;

namespace SysMon.Monitoring;

/// <summary>
/// Owns the single LibreHardwareMonitor <see cref="Computer"/> instance shared by every provider.
///
/// Opening the library is expensive and must happen exactly once; updating individual hardware is
/// the per-tick cost, and that is deliberately driven by the providers so the slow groups
/// (storage, motherboard) can run on a slower tier than CPU and GPU.
/// </summary>
public sealed class HardwareSession : IDisposable
{
    private readonly Lock _gate = new();
    private Computer? _computer;
    private bool _disposed;

    /// <summary>True once the library is open and reporting hardware.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>True when the process holds administrator rights.</summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>
    /// Works out how much of the hardware is actually reachable, so the UI can explain a missing
    /// temperature instead of merely omitting it.
    ///
    /// Being elevated is not sufficient on its own: the sensor library needs a kernel driver, and
    /// that driver is commonly blocked by Windows security features such as Memory Integrity even
    /// for an administrator. That case is detectable — the CPU is enumerated but exposes no
    /// temperature or clock sensors at all — and it needs a different remedy from simply elevating.
    /// </summary>
    public SensorAccess GetSensorAccess()
    {
        if (!IsAvailable)
        {
            return SensorAccess.MonitoringDisabled;
        }

        if (HasReadableCpuSensors())
        {
            return SensorAccess.Full;
        }

        return IsElevated ? SensorAccess.DriverUnavailable : SensorAccess.NotElevated;
    }

    /// <summary>True when any CPU exposes a temperature or clock, which the driver is required for.</summary>
    private bool HasReadableCpuSensors()
    {
        foreach (var hardware in GetHardware(HardwareType.Cpu))
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType is SensorType.Temperature or SensorType.Clock && sensor.Value is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Opens the library with only the sensor groups the user has enabled.</summary>
    public void Open(AppSettings settings)
    {
        lock (_gate)
        {
            if (_disposed || _computer is not null)
            {
                return;
            }

            if (!settings.EnableHardwareMonitoring)
            {
                Log.Info("Hardware monitoring is disabled in settings; sensor library not opened.");
                return;
            }

            var computer = new Computer
            {
                IsCpuEnabled = settings.EnableCpuSensors,
                IsGpuEnabled = settings.EnableGpuSensors,
                IsMemoryEnabled = settings.EnableMemorySensors,
                IsStorageEnabled = settings.EnableStorageSensors,
                IsMotherboardEnabled = settings.EnableMotherboardSensors,

                // Deliberately off. These groups are slow to enumerate and nothing in the UI
                // consumes them, so paying for them would be pure overhead.
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
                IsPowerMonitorEnabled = false,
            };

            try
            {
                computer.Open();
                _computer = computer;
                IsAvailable = true;

                Log.Info($"Sensor library opened ({computer.Hardware.Count} hardware items, elevated={IsElevated}).");
            }
            catch (Exception ex)
            {
                // Typically a driver that will not load: on a locked-down machine, or without
                // administrator rights. Everything backed by plain Win32 keeps working.
                Log.Error("Could not open the hardware sensor library; falling back to OS-level metrics only.", ex);
                IsAvailable = false;

                try
                {
                    computer.Close();
                }
                catch (Exception closeEx)
                {
                    Log.Warn("Failed to close the sensor library after a failed open.", closeEx);
                }
            }
        }
    }

    /// <summary>Closes and reopens the library, e.g. after the user changes which sensors are enabled.</summary>
    public void Reconfigure(AppSettings settings)
    {
        lock (_gate)
        {
            CloseCore();
        }

        Open(settings);
    }

    /// <summary>
    /// Returns every top-level hardware item of the given types. The list is a snapshot, so
    /// hardware appearing or disappearing mid-iteration cannot throw.
    /// </summary>
    public IReadOnlyList<IHardware> GetHardware(params HardwareType[] types)
    {
        lock (_gate)
        {
            if (_computer is null)
            {
                return [];
            }

            var results = new List<IHardware>();

            foreach (var hardware in _computer.Hardware)
            {
                if (Array.IndexOf(types, hardware.HardwareType) >= 0)
                {
                    results.Add(hardware);
                }
            }

            return results;
        }
    }

    public IReadOnlyList<IHardware> AllHardware()
    {
        lock (_gate)
        {
            return _computer is null ? [] : _computer.Hardware.ToArray();
        }
    }

    /// <summary>
    /// Refreshes one hardware item and its sub-hardware. Failures are logged once and swallowed:
    /// a GPU that has been reset must not take the tick down with it.
    /// </summary>
    public static void Update(IHardware hardware)
    {
        try
        {
            hardware.Update();

            foreach (var sub in hardware.SubHardware)
            {
                try
                {
                    sub.Update();
                }
                catch (Exception ex)
                {
                    Log.Once($"update:{sub.Identifier}", LogLevel.Warn, $"Failed to update sub-hardware '{sub.Name}'.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Once($"update:{hardware.Identifier}", LogLevel.Warn, $"Failed to update hardware '{hardware.Name}'.", ex);
        }
    }

    /// <summary>
    /// Value of the first matching sensor that falls inside <paramref name="range"/>, or null.
    /// Named fragments are tried in order, so callers can express "prefer Tdie, fall back to any
    /// temperature".
    ///
    /// Implausible readings are skipped rather than returned, so a sensor that is failing does not
    /// mask a working one further down the list, and a value that reaches a card is one the
    /// hardware could actually have produced.
    /// </summary>
    public static double? ReadSensor(IHardware hardware, SensorType type, SensorRange range, params string[] nameFragments)
    {
        foreach (var fragment in nameFragments)
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType == type &&
                    sensor.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase) &&
                    sensor.Value is { } value && range.Contains(value))
                {
                    return value;
                }
            }
        }

        // No preferred name matched: accept any sensor of the right type with a plausible value.
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == type && sensor.Value is { } value && range.Contains(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Highest plausible value among the sensors of a type. Used for "fastest core clock".</summary>
    public static double? MaxSensor(IHardware hardware, SensorType type, SensorRange range, params string[] nameFragments)
    {
        double? max = null;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != type || sensor.Value is not { } value || !range.Contains(value))
            {
                continue;
            }

            if (nameFragments.Length > 0)
            {
                var matches = false;
                foreach (var fragment in nameFragments)
                {
                    if (sensor.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }
            }

            if (max is null || value > max)
            {
                max = value;
            }
        }

        return max;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseCore();
        }
    }

    private void CloseCore()
    {
        if (_computer is null)
        {
            return;
        }

        try
        {
            _computer.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to close the hardware sensor library.", ex);
        }
        finally
        {
            _computer = null;
            IsAvailable = false;
        }
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not determine whether the process is elevated.", ex);
            return false;
        }
    }
}
