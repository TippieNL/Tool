using System.Globalization;
using Microsoft.Win32.SafeHandles;
using System.Management;
using System.Runtime.InteropServices;
using SysMon.Core.Diagnostics;

namespace SysMon.Monitoring.Interop;

/// <summary>
/// Joins mounted volumes to the physical disks behind them.
///
/// This exists because a drive letter and a piece of storage hardware are different things, and
/// the sensor library reports only the latter. Its display name is the drive model, with no
/// mention of which letters live on it, so the join has to come from Windows: a volume names its
/// disk through IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, and the disk names its model through WMI.
/// </summary>
internal sealed class VolumeMapper
{
    private static readonly TimeSpan ModelCacheLifetime = TimeSpan.FromSeconds(60);

    private readonly Dictionary<uint, string> _diskModels = [];
    private DateTime _modelsLoadedUtc = DateTime.MinValue;

    /// <summary>
    /// The physical disk number a volume lives on, or null when it cannot be determined
    /// (a spanned volume, a virtual disk, or a device that refuses the query).
    /// </summary>
    public static uint? GetDiskNumber(string driveLetter)
    {
        // The device path takes no trailing backslash, unlike a filesystem path.
        var path = $@"\\.\{driveLetter.TrimEnd('\\', ':')}:";

        SafeFileHandle? handle = null;
        var buffer = IntPtr.Zero;

        try
        {
            handle = NativeMethods.CreateFile(
                path,
                0,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return null;
            }

            // VOLUME_DISK_EXTENTS: a count followed by 24-byte extents. Room for eight is far
            // more than any ordinary volume needs.
            const int extentSize = 24;
            const int bufferSize = 8 + (extentSize * 8);
            buffer = Marshal.AllocHGlobal(bufferSize);

            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.IoctlVolumeGetVolumeDiskExtents,
                    IntPtr.Zero,
                    0,
                    buffer,
                    bufferSize,
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            var extentCount = Marshal.ReadInt32(buffer);
            if (extentCount < 1)
            {
                return null;
            }

            // The first extent is enough: a volume spanning several disks has no single
            // temperature or activity figure to report anyway.
            return (uint)Marshal.ReadInt32(buffer, 8);
        }
        catch (Exception ex)
        {
            Log.Once($"volume:{driveLetter}", LogLevel.Debug, $"Could not resolve the disk behind volume {driveLetter}.", ex);
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            handle?.Dispose();
        }
    }

    /// <summary>
    /// Model reported for a physical disk, used to match it to the sensor library's hardware.
    /// Cached, because WMI is far too slow to query on a polling interval.
    /// </summary>
    public string? GetDiskModel(uint diskNumber)
    {
        RefreshModelsIfStale();
        return _diskModels.TryGetValue(diskNumber, out var model) ? model : null;
    }

    private void RefreshModelsIfStale()
    {
        if (DateTime.UtcNow - _modelsLoadedUtc < ModelCacheLifetime)
        {
            return;
        }

        _modelsLoadedUtc = DateTime.UtcNow;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
            using var results = searcher.Get();

            _diskModels.Clear();

            foreach (var item in results)
            {
                using var disk = (ManagementObject)item;

                if (disk["Index"] is not { } index || disk["Model"] is not { } model)
                {
                    continue;
                }

                var name = model.ToString()?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    _diskModels[Convert.ToUInt32(index, CultureInfo.InvariantCulture)] = name;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Once("storage:diskmodels", LogLevel.Warn, "Could not read physical disk models.", ex);
        }
    }
}
