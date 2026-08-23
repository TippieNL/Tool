using LibreHardwareMonitor.Hardware;
using SysMon.Core.Diagnostics;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;
using SysMon.Monitoring.Interop;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Drives: capacity, free space, throughput, activity, temperature and media type.
///
/// Rows are built from mounted volumes rather than from sensor hardware, because a volume always
/// knows its own capacity whereas the sensor library may not be available at all. Hardware
/// readings are then attached to the volume that lives on that disk. This ordering matters: it
/// guarantees a mounted drive never reports an unknown size just because a sensor lookup failed.
///
/// The volume-to-hardware join goes through the physical disk number, not through the hardware's
/// display name. Names are model strings such as "ST1000DM010-2EP102" and frequently mention no
/// drive letter at all, so matching on them silently fails.
///
/// Runs on the slow tier: free space changes slowly and enumerating volumes touches the
/// filesystem, which is not something to do every second.
/// </summary>
public sealed class StorageProvider : IMetricProvider
{
    private readonly HardwareSession _session;
    private readonly VolumeMapper _volumes = new();
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

        var hardware = ReadStorageHardware();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var volume in ReadVolumes())
        {
            var match = MatchHardware(hardware, volume);

            if (match is not null)
            {
                claimed.Add(match.Key);
            }

            var used = Math.Max(0, volume.TotalBytes - volume.FreeBytes);

            _buffer.Add(new DriveSnapshot
            {
                Key = "volume:" + volume.Letter,
                MountPoint = volume.MountPoint,
                Label = string.IsNullOrWhiteSpace(volume.Label) ? match?.Model ?? volume.MountPoint : volume.Label,
                Model = match?.Model,
                TotalBytes = volume.TotalBytes,
                UsedBytes = used,
                FreeBytes = volume.FreeBytes,
                UsedPercent = volume.TotalBytes > 0 ? Math.Clamp(used / volume.TotalBytes * 100d, 0, 100) : null,
                ReadBytesPerSecond = match?.ReadRate,
                WriteBytesPerSecond = match?.WriteRate,
                ActivityPercent = match?.Activity,
                TemperatureC = match?.TemperatureC,
                MediaType = volume.IsRemovable ? DriveMediaType.Removable : match?.MediaType ?? DriveMediaType.Unknown,
            });
        }

        // A physical drive with no mounted volume still deserves a row, so an unformatted or
        // unlettered disk is not invisible. It reports hardware readings but no capacity, which
        // is the truth for something the filesystem cannot see.
        foreach (var drive in hardware)
        {
            if (claimed.Contains(drive.Key))
            {
                continue;
            }

            _buffer.Add(new DriveSnapshot
            {
                Key = drive.Key,
                MountPoint = null,
                Label = drive.Model,
                Model = drive.Model,
                ReadBytesPerSecond = drive.ReadRate,
                WriteBytesPerSecond = drive.WriteRate,
                ActivityPercent = drive.Activity,
                TemperatureC = drive.TemperatureC,
                MediaType = drive.MediaType,
            });
        }

        _buffer.Sort(static (a, b) => string.CompareOrdinal(a.MountPoint ?? "￿", b.MountPoint ?? "￿"));

        builder.SetDrives(_buffer);
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>
    /// Finds the hardware behind a volume: first by physical disk number, then by model name as a
    /// fallback for devices whose disk number could not be read (USB enclosures often refuse).
    /// </summary>
    private StorageHardware? MatchHardware(List<StorageHardware> hardware, VolumeInfo volume)
    {
        if (volume.DiskNumber is { } diskNumber)
        {
            var model = _volumes.GetDiskModel(diskNumber);

            if (!string.IsNullOrEmpty(model))
            {
                foreach (var candidate in hardware)
                {
                    if (candidate.Model.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                        model.Contains(candidate.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            // The sensor library indexes its storage by physical drive number.
            foreach (var candidate in hardware)
            {
                if (candidate.Index == diskNumber)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private List<StorageHardware> ReadStorageHardware()
    {
        var drives = new List<StorageHardware>();

        foreach (var item in _session.GetHardware(HardwareType.Storage))
        {
            HardwareSession.Update(item);

            var identifier = item.Identifier.ToString();

            drives.Add(new StorageHardware
            {
                Key = identifier,
                Index = ParseTrailingIndex(identifier),
                Model = item.Name,
                ReadRate = HardwareSession.ReadSensor(item, SensorType.Throughput, SensorRanges.Bytes, "Read Rate"),
                WriteRate = HardwareSession.ReadSensor(item, SensorType.Throughput, SensorRanges.Bytes, "Write Rate"),
                Activity = HardwareSession.ReadSensor(item, SensorType.Load, SensorRanges.Percent, "Total Activity", "Activity"),
                TemperatureC = HardwareSession.ReadSensor(item, SensorType.Temperature, SensorRanges.Temperature, "Temperature"),
                MediaType = DetectMediaType(item),
            });
        }

        return drives;
    }

    /// <summary>Reads the drive number from an identifier such as "/hdd/2".</summary>
    private static uint? ParseTrailingIndex(string identifier)
    {
        var slash = identifier.LastIndexOf('/');

        return slash >= 0 && slash + 1 < identifier.Length
            && uint.TryParse(identifier[(slash + 1)..], out var index)
                ? index
                : null;
    }

    private static List<VolumeInfo> ReadVolumes()
    {
        var volumes = new List<VolumeInfo>();

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

                volumes.Add(new VolumeInfo(
                    letter,
                    drive.Name,
                    SafeLabel(drive),
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.DriveType == DriveType.Removable,
                    VolumeMapper.GetDiskNumber(letter)));
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

    /// <summary>
    /// SSD or HDD. Rotational drives expose a spin-up or spin-down sensor, and flash exposes wear
    /// and remaining-life counters, so the sensor set identifies the medium when the model string
    /// does not.
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

    private sealed class StorageHardware
    {
        public required string Key { get; init; }

        public uint? Index { get; init; }

        public required string Model { get; init; }

        public double? ReadRate { get; init; }

        public double? WriteRate { get; init; }

        public double? Activity { get; init; }

        public double? TemperatureC { get; init; }

        public DriveMediaType MediaType { get; init; }
    }

    private readonly record struct VolumeInfo(
        string Letter,
        string MountPoint,
        string? Label,
        double TotalBytes,
        double FreeBytes,
        bool IsRemovable,
        uint? DiskNumber);
}
