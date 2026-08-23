using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SysMon.Core.Formatting;
using SysMon.Core.History;
using SysMon.Core.Monitoring;
using SysMon.Core.Models;

namespace SysMon.App.ViewModels;

/// <summary>
/// Base for the dashboard cards.
///
/// Every card is created once and updated in place. Nothing here allocates a new view model per
/// tick, which is what keeps a one-second refresh from generating continuous GC pressure.
/// </summary>
public abstract partial class CardViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string PrimaryValue { get; set; } = Format.NotAvailable;

    /// <summary>0-100 figure driving the card's fill bar, or null to hide it.</summary>
    [ObservableProperty]
    public partial double? BarValue { get; set; }

    [ObservableProperty]
    public partial bool IsAvailable { get; set; } = true;

    /// <summary>Backing data for the card's sparkline, or null for cards without one.</summary>
    public HistorySeries? Series { get; set; }
}

public partial class CpuCardViewModel : CardViewModel
{
    public CpuCardViewModel() => Title = "CPU";

    [ObservableProperty]
    public partial string Clock { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Temperature { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Power { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Cores { get; set; } = Format.NotAvailable;

    /// <summary>Per-thread load, shown as a compact bar strip. Rebuilt only when the count changes.</summary>
    public ObservableCollection<CoreLoadViewModel> CoreLoads { get; } = [];

    public void Update(CpuSnapshot cpu, TemperatureUnit unit)
    {
        Subtitle = Format.TextOrNotAvailable(cpu.Name);
        PrimaryValue = Format.Percent(cpu.TotalLoad);
        BarValue = cpu.TotalLoad;
        Clock = Format.Clock(cpu.ClockMhz);
        Temperature = Format.Temperature(cpu.TemperatureC, unit);
        Power = Format.Watts(cpu.PackagePowerW);

        Cores = cpu.PhysicalCores is { } physical && cpu.LogicalCores is { } logical
            ? $"{physical}C / {logical}T"
            : Format.NotAvailable;

        UpdateCoreLoads(cpu.CoreLoads);
    }

    private void UpdateCoreLoads(IReadOnlyList<double> loads)
    {
        // Growing or shrinking the collection is the only case that touches the visual tree;
        // the common path just assigns new values to existing items.
        while (CoreLoads.Count > loads.Count)
        {
            CoreLoads.RemoveAt(CoreLoads.Count - 1);
        }

        while (CoreLoads.Count < loads.Count)
        {
            CoreLoads.Add(new CoreLoadViewModel(CoreLoads.Count));
        }

        for (var i = 0; i < loads.Count; i++)
        {
            CoreLoads[i].Load = loads[i];
        }
    }
}

public partial class CoreLoadViewModel : ObservableObject
{
    public CoreLoadViewModel(int index) => Label = $"{index}";

    public string Label { get; }

    [ObservableProperty]
    public partial double Load { get; set; }
}

public partial class MemoryCardViewModel : CardViewModel
{
    public MemoryCardViewModel() => Title = "MEMORY";

    [ObservableProperty]
    public partial string Available { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Speed { get; set; } = Format.NotAvailable;

    public void Update(MemorySnapshot memory)
    {
        Subtitle = Format.UsedOfTotal(memory.UsedBytes, memory.TotalBytes);
        PrimaryValue = Format.Percent(memory.LoadPercent);
        BarValue = memory.LoadPercent;
        Available = Format.Bytes(memory.AvailableBytes);

        Speed = memory.SpeedMtps is { } speed
            ? $"{speed:F0} MT/s"
            : Format.TextOrNotAvailable(memory.ModuleSummary);
    }
}

public partial class GpuCardViewModel : CardViewModel
{
    public GpuCardViewModel(string key)
    {
        Key = key;
        Title = "GPU";
    }

    /// <summary>Matches the snapshot's GPU key, and keys this card's history series.</summary>
    public string Key { get; }

    [ObservableProperty]
    public partial string Temperature { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Clock { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Vram { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial double? VramPercent { get; set; }

    [ObservableProperty]
    public partial string Power { get; set; } = Format.NotAvailable;

    public void Update(GpuSnapshot gpu, TemperatureUnit unit)
    {
        Title = gpu.Vendor == GpuVendor.Unknown ? "GPU" : $"GPU · {gpu.Vendor.ToString().ToUpperInvariant()}";
        Subtitle = Format.TextOrNotAvailable(gpu.Name);
        PrimaryValue = Format.Percent(gpu.Load);
        BarValue = gpu.Load;
        Temperature = Format.Temperature(gpu.TemperatureC, unit);
        Clock = Format.Clock(gpu.CoreClockMhz);
        Vram = Format.UsedOfTotal(gpu.VramUsedBytes, gpu.VramTotalBytes);
        Power = Format.Watts(gpu.PowerW);

        VramPercent = gpu.VramTotalBytes is { } total && total > 0 && gpu.VramUsedBytes is { } used
            ? Math.Clamp(used / total * 100d, 0, 100)
            : null;
    }
}

public partial class DriveViewModel : ObservableObject
{
    public DriveViewModel(string key) => Key = key;

    public string Key { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Model { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Usage { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Free { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial double? UsedPercent { get; set; }

    [ObservableProperty]
    public partial string ReadRate { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string WriteRate { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Temperature { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string MediaType { get; set; } = string.Empty;

    public void Update(DriveSnapshot drive, TemperatureUnit unit)
    {
        Name = Format.TextOrNotAvailable(drive.MountPoint ?? drive.Label);
        Model = Format.TextOrNotAvailable(drive.Model ?? drive.Label);
        Usage = Format.UsedOfTotal(drive.UsedBytes, drive.TotalBytes);
        Free = Format.Bytes(drive.FreeBytes) + " free";
        UsedPercent = drive.UsedPercent;
        ReadRate = Format.BytesPerSecond(drive.ReadBytesPerSecond);
        WriteRate = Format.BytesPerSecond(drive.WriteBytesPerSecond);
        Temperature = Format.Temperature(drive.TemperatureC, unit);

        MediaType = drive.MediaType switch
        {
            DriveMediaType.Ssd => "SSD",
            DriveMediaType.Hdd => "HDD",
            DriveMediaType.Removable => "USB",
            _ => string.Empty,
        };
    }
}

public partial class StorageCardViewModel : CardViewModel
{
    public StorageCardViewModel() => Title = "STORAGE";

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public void Update(IReadOnlyList<DriveSnapshot> drives, TemperatureUnit unit)
    {
        SyncCollection(Drives, drives, static d => d.Key, static key => new DriveViewModel(key),
            (vm, drive) => vm.Update(drive, unit));

        IsAvailable = drives.Count > 0;

        // The card's headline is the fullest drive, since that is the one worth knowing about.
        DriveSnapshot? fullest = null;
        foreach (var drive in drives)
        {
            if (drive.UsedPercent is { } percent && (fullest?.UsedPercent is not { } best || percent > best))
            {
                fullest = drive;
            }
        }

        if (fullest is null)
        {
            Subtitle = $"{drives.Count} drives";
            PrimaryValue = Format.NotAvailable;
            BarValue = null;
            return;
        }

        Subtitle = $"{Format.TextOrNotAvailable(fullest.MountPoint)} · {Format.UsedOfTotal(fullest.UsedBytes, fullest.TotalBytes)}";
        PrimaryValue = Format.Percent(fullest.UsedPercent);
        BarValue = fullest.UsedPercent;
    }

    /// <summary>
    /// Reconciles an observable collection against a snapshot list by key, reusing existing view
    /// models. Devices appearing or disappearing mid-session is a normal event, not an error.
    /// </summary>
    internal static void SyncCollection<TSource, TTarget>(
        ObservableCollection<TTarget> target,
        IReadOnlyList<TSource> source,
        Func<TSource, string> keySelector,
        Func<string, TTarget> factory,
        Action<TTarget, TSource> update)
        where TTarget : class
    {
        for (var i = 0; i < source.Count; i++)
        {
            var key = keySelector(source[i]);

            if (i < target.Count && KeyOf(target[i]) == key)
            {
                update(target[i], source[i]);
                continue;
            }

            var existingIndex = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (KeyOf(target[j]) == key)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, factory(key));
            }

            update(target[i], source[i]);
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static string KeyOf(object item) => item switch
    {
        DriveViewModel drive => drive.Key,
        GpuCardViewModel gpu => gpu.Key,
        TemperatureViewModel temp => temp.Key,
        _ => string.Empty,
    };
}

public partial class NetworkCardViewModel : CardViewModel
{
    public NetworkCardViewModel() => Title = "NETWORK";

    [ObservableProperty]
    public partial string Download { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Upload { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string TotalDownloaded { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string TotalUploaded { get; set; } = Format.NotAvailable;

    /// <summary>Upload history, drawn over the download trace.</summary>
    public HistorySeries? UploadSeries { get; set; }

    public void Update(NetworkSnapshot network)
    {
        Subtitle = Format.TextOrNotAvailable(network.InterfaceName);
        PrimaryValue = Format.BytesPerSecond(network.DownloadBytesPerSecond);
        Download = Format.BytesPerSecond(network.DownloadBytesPerSecond);
        Upload = Format.BytesPerSecond(network.UploadBytesPerSecond);
        TotalDownloaded = Format.Bytes(network.TotalDownloadedBytes);
        TotalUploaded = Format.Bytes(network.TotalUploadedBytes);
        IsAvailable = network.InterfaceName is not null;
    }
}

public partial class TemperatureViewModel : ObservableObject
{
    public TemperatureViewModel(string key) => Key = key;

    public string Key { get; }

    [ObservableProperty]
    public partial string Source { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>
    /// Name qualified by its hardware when the sensor name alone is meaningless. Drives commonly
    /// report a sensor called simply "Temperature", which tells the reader nothing in a list that
    /// mixes CPU, GPU and storage.
    /// </summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = Format.NotAvailable;

    /// <summary>Raw Celsius, used to colour the row rather than to display.</summary>
    [ObservableProperty]
    public partial double Celsius { get; set; }

    public void Update(TemperatureReading reading, TemperatureUnit unit)
    {
        Source = reading.Source;
        Name = reading.Name;
        Value = Format.Temperature(reading.Celsius, unit);
        Celsius = reading.Celsius ?? 0;

        DisplayName = SensorNaming.IsGenericTemperatureName(reading.Name) ? reading.Source : reading.Name;
    }
}

public partial class TemperatureCardViewModel : CardViewModel
{
    public TemperatureCardViewModel() => Title = "TEMPERATURES";

    public ObservableCollection<TemperatureViewModel> Sensors { get; } = [];

    [ObservableProperty]
    public partial string HottestName { get; set; } = string.Empty;

    public void Update(IReadOnlyList<TemperatureReading> readings, TemperatureUnit unit)
    {
        StorageCardViewModel.SyncCollection(Sensors, readings, static r => r.Key,
            static key => new TemperatureViewModel(key), (vm, reading) => vm.Update(reading, unit));

        IsAvailable = readings.Count > 0;

        if (readings.Count == 0)
        {
            Subtitle = "No sensors available";
            PrimaryValue = Format.NotAvailable;
            HottestName = string.Empty;
            BarValue = null;
            return;
        }

        TemperatureReading? hottest = null;
        foreach (var reading in readings)
        {
            if (reading.Celsius is { } celsius && (hottest?.Celsius is not { } best || celsius > best))
            {
                hottest = reading;
            }
        }

        Subtitle = $"{readings.Count} sensors";
        PrimaryValue = Format.Temperature(hottest?.Celsius, unit);
        HottestName = hottest is null ? string.Empty : $"{hottest.Source} · {hottest.Name}";

        // Scale the bar so a typical operating range fills it: 30 °C empty, 100 °C full.
        BarValue = hottest?.Celsius is { } value ? Math.Clamp((value - 30) / 70 * 100, 0, 100) : null;
    }
}

public partial class SystemCardViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string HostName { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string OperatingSystem { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Motherboard { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Bios { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Processor { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Cores { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Memory { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Uptime { get; set; } = Format.NotAvailable;

    [ObservableProperty]
    public partial string Elevation { get; set; } = Format.NotAvailable;

    public void Update(Snapshot snapshot)
    {
        var info = snapshot.System;

        HostName = Format.TextOrNotAvailable(info.HostName);
        OperatingSystem = info.OsName is null
            ? Format.NotAvailable
            : $"{info.OsName} (build {Format.TextOrNotAvailable(info.OsVersion)})";
        Motherboard = Format.TextOrNotAvailable(info.Motherboard);
        Bios = Format.TextOrNotAvailable(info.BiosVersion);
        Processor = Format.TextOrNotAvailable(snapshot.Cpu.Name);
        Cores = info.PhysicalCores is { } physical && info.LogicalCores is { } logical
            ? $"{physical} cores / {logical} threads"
            : Format.NotAvailable;
        Memory = Format.Bytes(snapshot.Memory.TotalBytes);
        Uptime = Format.Uptime(info.Uptime);
        Elevation = info.IsElevated ? "Administrator" : "Standard user";
    }
}
