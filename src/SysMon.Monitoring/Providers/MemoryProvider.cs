using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SysMon.Core.Diagnostics;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;
using SysMon.Monitoring.Interop;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Physical memory totals and usage.
///
/// Per-tick data comes from GlobalMemoryStatusEx, which is a single struct copy. Module speed and
/// layout come from WMI, which is comparatively slow, so it runs exactly once at startup.
/// </summary>
public sealed class MemoryProvider : IMetricProvider
{
    private double? _speedMtps;
    private string? _moduleSummary;
    private bool _staticInfoLoaded;

    public string Name => "Memory";

    public PollTier Tier => PollTier.Fast;

    public void Initialize()
    {
        if (_staticInfoLoaded)
        {
            return;
        }

        _staticInfoLoaded = true;
        LoadModuleInfo();
    }

    public void Poll(SnapshotBuilder builder)
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            builder.Memory = new MemorySnapshot { SpeedMtps = _speedMtps, ModuleSummary = _moduleSummary };
            return;
        }

        double total = status.ullTotalPhys;
        double available = status.ullAvailPhys;
        var used = Math.Max(0, total - available);

        builder.Memory = new MemorySnapshot
        {
            TotalBytes = total,
            UsedBytes = used,
            AvailableBytes = available,
            LoadPercent = total > 0 ? Math.Clamp(used / total * 100d, 0, 100) : null,
            SpeedMtps = _speedMtps,
            ModuleSummary = _moduleSummary,
        };
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>
    /// Reads module speed and capacity once. WMI is used here and nowhere on the tick path,
    /// because a single query costs tens of milliseconds.
    /// </summary>
    private void LoadModuleInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            using var results = searcher.Get();

            var moduleCount = 0;
            double? capacityPerModule = null;
            var mixedCapacity = false;
            double? speed = null;
            int? memoryType = null;

            foreach (var item in results)
            {
                using var module = (ManagementObject)item;
                moduleCount++;

                if (TryGetDouble(module, "Capacity") is { } capacity)
                {
                    if (capacityPerModule is null)
                    {
                        capacityPerModule = capacity;
                    }
                    else if (Math.Abs(capacityPerModule.Value - capacity) > 1)
                    {
                        mixedCapacity = true;
                    }
                }

                // ConfiguredClockSpeed is what the modules actually run at; Speed is the rated
                // value. Prefer the former and fall back to the latter.
                speed ??= TryGetDouble(module, "ConfiguredClockSpeed") ?? TryGetDouble(module, "Speed");
                memoryType ??= (int?)TryGetDouble(module, "SMBIOSMemoryType");
            }

            if (moduleCount == 0)
            {
                return;
            }

            _speedMtps = speed;

            var typeName = MemoryTypeName(memoryType);
            var summary = mixedCapacity || capacityPerModule is null
                ? $"{moduleCount} modules"
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} x {1:F0} GB",
                    moduleCount,
                    capacityPerModule.Value / (1024d * 1024d * 1024d));

            _moduleSummary = typeName is null ? summary : $"{summary} {typeName}";
        }
        catch (Exception ex)
        {
            // Memory module details are a nice-to-have; totals and usage do not depend on them.
            Log.Once("memory:wmi", LogLevel.Warn, "Could not read memory module details from WMI.", ex);
        }
    }

    private static double? TryGetDouble(ManagementObject item, string property)
    {
        try
        {
            var value = item[property];
            return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? MemoryTypeName(int? smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        _ => null,
    };
}
