using LibreHardwareMonitor.Hardware;
using SysMon.Core.Diagnostics;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Physical drives: capacity, free space, throughput, activity, temperature and media type.
///
/// Capacity comes from the mounted volumes and throughput from the sensor library's storage
/// hardware, which are two different views of the same disk. They are joined by matching each
/// physical drive to the volumes that live on it, so a disk with several partitions is reported
/// once with its combined space rather than once per letter.
///
/// This runs on the slow tier: disk free space changes slowly and enumerating volumes touches the
/// filesystem, which is not something to do every second.
/// </summary>
public sealed class StorageProvider : IMetricProvider
{
    private readonly HardwareSession _session;
    private readonly List<DriveSnapshot> _buffer = [];

    public StorageProvider(HardwareSession session) => _session = session;

    public string Name => "Storage";

    public PollTier Tier => PollTier.Slow;

    public void Initialize()
    {
        // Nothing to acquire: volumes are re-enumerated on each slow tick so that drives
        // plugged in while the app runs appear without a restart.
    }

    public void Poll(SnapshotBuilder builder)
    {
        _buffer.Clear();

        var volumes = ReadVolumes();
        var matchedVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hardware in _session.GetHardware(HardwareType.Storage))
        {
            HardwareSession.Update(hardware);

            // The library names storage hardware after the model and the letters it hosts,
            // e.g. "Samsung SSD 970 EVO 1TB (C:)". That is how a drive is matched to its volumes.
            var driveLetters = ExtractDriveLetters(hardware.Name);

            double? total = null;
            double? free = null;
            string? mountPoint = null;
            string? label = null;

            foreach (var letter in driveLetters)
            {
                if (!volumes.TryGetValue(letter, out var volume))
                {
                    continue;
                }

                matchedVolumes.Add(letter);

                total = (total ?? 0) + volume.TotalBytes;
                free = (free ?? 0) + volume.FreeBytes;
                mountPoint ??= volume.MountPoint;
                label ??= volume.Label;
            }

            var used = total is { } t && free is { } f ? Math.Max(0, t - f) : (double?)null;

            _buffer.Add(new DriveSnapshot
            {
                Key = hardware.Identifier.ToString(),
                MountPoint = mountPoint ?? JoinLetters(driveLetters),
                Label = string.IsNullOrWhiteSpace(label) ? hardware.Name : label,
                Model = hardware.Name,
                TotalBytes = total,
                UsedBytes = used,
                FreeBytes = free,
                UsedPercent = total is { } capacity && capacity > 0 && used is { } u
                    ? Math.Clamp(u / capacity * 100d, 0, 100)
                    : null,
                ReadBytesPerSecond = HardwareSession.ReadSensor(hardware, SensorType.Throughput, SensorRanges.Bytes, "Read Rate"),
                WriteBytesPerSecond = HardwareSession.ReadSensor(hardware, SensorType.Throughput, SensorRanges.Bytes, "Write Rate"),
                ActivityPercent = HardwareSession.ReadSensor(hardware, SensorType.Load, SensorRanges.Percent, "Total Activity", "Activity"),
                TemperatureC = HardwareSession.ReadSensor(hardware, SensorType.Temperature, SensorRanges.Temperature, "Temperature"),
                MediaType = DetectMediaType(hardware),
            });
        }

        // Volumes the sensor library never reported (it is unavailable, or the volume is a USB
        // stick or network-backed) still deserve a capacity readout, just without hardware data.
        foreach (var (letter, volume) in volumes)
        {
            if (matchedVolumes.Contains(letter))
            {
                continue;
            }

            var used = Math.Max(0, volume.TotalBytes - volume.FreeBytes);

            _buffer.Add(new DriveSnapshot
            {
                Key = "volume:" + letter,
                MountPoint = volume.MountPoint,
                Label = string.IsNullOrWhiteSpace(volume.Label) ? volume.MountPoint : volume.Label,
                Model = null,
                TotalBytes = volume.TotalBytes,
                UsedBytes = used,
                FreeBytes = volume.FreeBytes,
                UsedPercent = volume.TotalBytes > 0 ? Math.Clamp(used / volume.TotalBytes * 100d, 0, 100) : null,
                MediaType = volume.IsRemovable ? DriveMediaType.Removable : DriveMediaType.Unknown,
            });
        }

        _buffer.Sort(static (a, b) => string.CompareOrdinal(a.MountPoint, b.MountPoint));

        builder.SetDrives(_buffer);
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    private static Dictionary<string, VolumeInfo> ReadVolumes()
    {
        var volumes = new Dictionary<string, VolumeInfo>(StringComparer.OrdinalIgnoreCase);

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            Log.Once("storage:enumerate", LogLevel.Warn, "Could not enumerate volumes.", ex);
            return volumes;
        }

        foreach (var drive in drives)
        {
            try
            {
                // Skipping network and unready drives keeps a disconnected share from stalling
                // the tick on a filesystem call that blocks.
                if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.NoRootDirectory)
                {
                    continue;
                }

                var letter = drive.Name.TrimEnd('\\', ':', '/');
                if (letter.Length == 0)
                {
                    continue;
                }

                volumes[letter] = new VolumeInfo(
                    drive.Name,
                    SafeLabel(drive),
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.DriveType == DriveType.Removable);
            }
            catch (Exception ex)
            {
                // A drive can disappear between enumeration and inspection.
                Log.Once($"storage:volume:{drive.Name}", LogLevel.Debug, $"Skipping volume '{drive.Name}'.", ex);
            }
        }

        return volumes;
    }

    private static string? SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (Exception)
        {
            // Reading the label needs permissions that are not always granted.
            return null;
        }
    }

    /// <summary>Pulls "C", "D" out of a name such as "Samsung SSD 970 EVO 1TB (C:, D:)".</summary>
    private static List<string> ExtractDriveLetters(string hardwareName)
    {
        var letters = new List<string>();

        for (var i = 0; i + 1 < hardwareName.Length; i++)
        {
            if (hardwareName[i + 1] != ':' || !char.IsAsciiLetter(hardwareName[i]))
            {
                continue;
            }

            // Only treat it as a drive letter when it is not part of a longer word.
            if (i > 0 && char.IsAsciiLetterOrDigit(hardwareName[i - 1]))
            {
                continue;
            }

            var letter = hardwareName[i].ToString().ToUpperInvariant();
            if (!letters.Contains(letter))
            {
                letters.Add(letter);
            }
        }

        return letters;
    }

    private static string? JoinLetters(List<string> letters) =>
        letters.Count == 0 ? null : string.Join(", ", letters.Select(static l => l + ":"));

    /// <summary>
    /// SSD or HDD. The library exposes rotational drives through a spin-up or spin-down sensor,
    /// and NVMe/SSD models through their sensor set, so absence of rotation is the signal.
    /// </summary>
    private static DriveMediaType DetectMediaType(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Name.Contains("Spin", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Rotation", StringComparison.OrdinalIgnoreCase))
            {
                return DriveMediaType.Hdd;
            }
        }

        var name = hardware.Name;

        if (name.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SSD", StringComparison.OrdinalIgnoreCase))
        {
            return DriveMediaType.Ssd;
        }

        if (name.Contains("HDD", StringComparison.OrdinalIgnoreCase))
        {
            return DriveMediaType.Hdd;
        }

        // Wear level and remaining life are only meaningful on flash.
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Name.Contains("Wear", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase))
            {
                return DriveMediaType.Ssd;
            }
        }

        return DriveMediaType.Unknown;
    }

    private readonly record struct VolumeInfo(
        string MountPoint,
        string? Label,
        double TotalBytes,
        double FreeBytes,
        bool IsRemovable);
}
