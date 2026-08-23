using System.Globalization;
using System.Management;
using LibreHardwareMonitor.Hardware;
using SysMon.Core.Diagnostics;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;
using SysMon.Monitoring.Interop;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Facts about the machine that do not change while it is running: hostname, OS build,
/// motherboard, BIOS and core counts. Gathered once; only uptime is recomputed, and that is a
/// single kernel call.
/// </summary>
public sealed class SystemInfoProvider : IMetricProvider
{
    private readonly HardwareSession _session;
    private SystemInfo _cached = new();
    private bool _loaded;

    public SystemInfoProvider(HardwareSession session) => _session = session;

    public string Name => "System info";

    public PollTier Tier => PollTier.Static;

    public void Initialize()
    {
        if (_loaded)
        {
            return;
        }

        _cached = new SystemInfo
        {
            HostName = SafeHostName(),
            OsName = ReadOsName(),
            OsVersion = ReadOsVersion(),
            Motherboard = ReadMotherboard(),
            BiosVersion = ReadBiosVersion(),
            PhysicalCores = CountPhysicalCores(),
            LogicalCores = Environment.ProcessorCount,
            IsElevated = HardwareSession.IsElevated,
        };

        _loaded = true;
    }

    public void Poll(SnapshotBuilder builder) =>
        builder.System = _cached with { Uptime = TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64()) };

    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>
    /// Physical (not logical) core count. Shared with the CPU provider so both report the same
    /// figure, and null when the platform will not say.
    /// </summary>
    internal static int? CountPhysicalCores()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            using var results = searcher.Get();

            var cores = 0;
            foreach (var item in results)
            {
                using var processor = (ManagementObject)item;
                if (processor["NumberOfCores"] is { } value)
                {
                    cores += Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }

            return cores > 0 ? cores : null;
        }
        catch (Exception ex)
        {
            Log.Once("system:cores", LogLevel.Warn, "Could not read the physical core count.", ex);
            return null;
        }
    }

    private static string? SafeHostName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the machine name.", ex);
            return null;
        }
    }

    /// <summary>
    /// Windows 11 reports itself as major version 10, so the build number is what distinguishes
    /// it: builds from 22000 onwards are Windows 11.
    /// </summary>
    private static string? ReadOsName()
    {
        try
        {
            var info = new NativeMethods.OsVersionInfoEx
            {
                dwOSVersionInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.OsVersionInfoEx>(),
            };

            if (NativeMethods.RtlGetVersion(ref info) != 0)
            {
                return Environment.OSVersion.VersionString;
            }

            return info.dwMajorVersion switch
            {
                >= 10 when info.dwBuildNumber >= 22000 => "Windows 11",
                >= 10 => "Windows 10",
                6 => "Windows 8 or earlier",
                _ => "Windows",
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the OS name.", ex);
            return null;
        }
    }

    private static string? ReadOsVersion()
    {
        try
        {
            var info = new NativeMethods.OsVersionInfoEx
            {
                dwOSVersionInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.OsVersionInfoEx>(),
            };

            return NativeMethods.RtlGetVersion(ref info) == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{info.dwMajorVersion}.{info.dwMinorVersion}.{info.dwBuildNumber}")
                : Environment.OSVersion.Version.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the OS version.", ex);
            return null;
        }
    }

    /// <summary>Motherboard name, preferring the sensor library and falling back to WMI.</summary>
    private string? ReadMotherboard()
    {
        foreach (var hardware in _session.GetHardware(HardwareType.Motherboard))
        {
            if (!string.IsNullOrWhiteSpace(hardware.Name))
            {
                return hardware.Name;
            }
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var results = searcher.Get();

            foreach (var item in results)
            {
                using var board = (ManagementObject)item;
                var manufacturer = board["Manufacturer"]?.ToString()?.Trim();
                var product = board["Product"]?.ToString()?.Trim();

                if (!string.IsNullOrEmpty(manufacturer) || !string.IsNullOrEmpty(product))
                {
                    return string.Join(' ', new[] { manufacturer, product }.Where(s => !string.IsNullOrEmpty(s)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Once("system:baseboard", LogLevel.Warn, "Could not read motherboard details.", ex);
        }

        return null;
    }

    private static string? ReadBiosVersion()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            using var results = searcher.Get();

            foreach (var item in results)
            {
                using var bios = (ManagementObject)item;
                var version = bios["SMBIOSBIOSVersion"]?.ToString()?.Trim();

                if (!string.IsNullOrEmpty(version))
                {
                    return version;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Once("system:bios", LogLevel.Warn, "Could not read BIOS details.", ex);
        }

        return null;
    }
}
