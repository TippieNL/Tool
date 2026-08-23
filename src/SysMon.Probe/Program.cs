using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Hardware;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.Formatting;
using SysMon.Core.History;
using SysMon.Core.Models;
using SysMon.Monitoring;

namespace SysMon.Probe;

/// <summary>
/// Console diagnostic for the monitoring layer.
///
/// It exercises exactly the same providers the UI uses and prints what they found, so a sensor
/// showing "N/A" in the app can be traced to either the hardware not exposing it or the provider
/// mis-reading it, without involving the UI at all. It also reports the per-tick cost, which is
/// the number that matters for the app's own footprint.
///
/// Usage:  PulseProbe [--ticks N] [--interval MS] [--raw]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var ticks = ArgValue(args, "--ticks", 5);
        var intervalMs = ArgValue(args, "--interval", 1000);
        var showRaw = Array.Exists(args, a => a.Equals("--raw", StringComparison.OrdinalIgnoreCase));

        AppPaths.EnsureDataDirectory();
        Log.MinimumLevel = LogLevel.Debug;
        Log.Start(AppPaths.LogFile);

        Console.WriteLine($"{AppPaths.DisplayName} — sensor probe");
        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"Elevated             : {HardwareSession.IsElevated}");

        if (!HardwareSession.IsElevated)
        {
            Console.WriteLine("  ! Not running as administrator. CPU temperature, CPU package power and");
            Console.WriteLine("    motherboard/fan sensors usually require elevation and may read N/A.");
        }

        var settings = new SettingsStore(AppPaths.SettingsFile).Load();
        settings.UpdateIntervalMs = Math.Clamp(intervalMs, 500, 10_000);

        using var session = new HardwareSession();
        var history = new HistoryStore(HistoryStore.CapacityFor(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(settings.UpdateIntervalMs)));
        using var service = new MonitoringService(settings, session, history);

        var openStopwatch = Stopwatch.StartNew();
        session.Open(settings);
        openStopwatch.Stop();

        Console.WriteLine($"Sensor library       : {(session.IsAvailable ? "open" : "UNAVAILABLE")} ({openStopwatch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"Log file             : {Log.FilePath}");
        Console.WriteLine();

        if (session.IsAvailable && showRaw)
        {
            DumpRawSensors(session);
        }

        Console.WriteLine($"Polling {ticks} ticks at {settings.UpdateIntervalMs} ms...");
        Console.WriteLine();

        Snapshot snapshot = Snapshot.Empty;
        var durations = new List<double>();

        for (var i = 0; i < ticks; i++)
        {
            snapshot = service.PollOnce();
            durations.Add(service.LastTickDuration.TotalMilliseconds);

            if (i < ticks - 1)
            {
                Thread.Sleep(settings.UpdateIntervalMs);
            }
        }

        PrintSnapshot(snapshot, settings.TemperatureUnit);
        PrintCost(durations, service);

        Log.Flush();
        return 0;
    }

    private static void PrintSnapshot(Snapshot s, TemperatureUnit unit)
    {
        Section("CPU");
        Line("Model", Format.TextOrNotAvailable(s.Cpu.Name));
        Line("Load", Format.Percent(s.Cpu.TotalLoad));
        Line("Clock", Format.Clock(s.Cpu.ClockMhz));
        Line("Temperature", Format.Temperature(s.Cpu.TemperatureC, unit));
        Line("Package power", Format.Watts(s.Cpu.PackagePowerW));
        Line("Cores / threads", $"{s.Cpu.PhysicalCores?.ToString(CultureInfo.CurrentCulture) ?? "?"} / {s.Cpu.LogicalCores?.ToString(CultureInfo.CurrentCulture) ?? "?"}");

        if (s.Cpu.CoreLoads.Count > 0)
        {
            Line("Per-core", string.Join("  ", s.Cpu.CoreLoads.Select(static l => $"{l,3:F0}%")));
        }

        Section("Memory");
        Line("Usage", Format.UsedOfTotal(s.Memory.UsedBytes, s.Memory.TotalBytes));
        Line("Load", Format.Percent(s.Memory.LoadPercent));
        Line("Available", Format.Bytes(s.Memory.AvailableBytes));
        Line("Speed", s.Memory.SpeedMtps is { } speed ? $"{speed:F0} MT/s" : Format.NotAvailable);
        Line("Modules", Format.TextOrNotAvailable(s.Memory.ModuleSummary));

        Section($"GPU ({s.Gpus.Count} found)");
        if (s.Gpus.Count == 0)
        {
            Console.WriteLine("  none detected");
        }

        foreach (var gpu in s.Gpus)
        {
            Console.WriteLine($"  [{gpu.Vendor}] {Format.TextOrNotAvailable(gpu.Name)}");
            Line("  Load", Format.Percent(gpu.Load));
            Line("  Temperature", Format.Temperature(gpu.TemperatureC, unit));
            Line("  Core clock", Format.Clock(gpu.CoreClockMhz));
            Line("  VRAM", Format.UsedOfTotal(gpu.VramUsedBytes, gpu.VramTotalBytes));
            Line("  Power", Format.Watts(gpu.PowerW));
            Line("  Fan", Format.Percent(gpu.FanPercent));
        }

        Section($"Storage ({s.Drives.Count} found)");
        foreach (var drive in s.Drives)
        {
            Console.WriteLine($"  {Format.TextOrNotAvailable(drive.MountPoint)}  {Format.TextOrNotAvailable(drive.Model ?? drive.Label)} [{drive.MediaType}]");
            Line("  Space", $"{Format.UsedOfTotal(drive.UsedBytes, drive.TotalBytes)} ({Format.Percent(drive.UsedPercent)} used, {Format.Bytes(drive.FreeBytes)} free)");
            Line("  Read / write", $"{Format.BytesPerSecond(drive.ReadBytesPerSecond)} / {Format.BytesPerSecond(drive.WriteBytesPerSecond)}");
            Line("  Activity", Format.Percent(drive.ActivityPercent));
            Line("  Temperature", Format.Temperature(drive.TemperatureC, unit));
        }

        Section("Network");
        Line("Interface", Format.TextOrNotAvailable(s.Network.InterfaceName));
        Line("Description", Format.TextOrNotAvailable(s.Network.InterfaceDescription));
        Line("Down / up", $"{Format.BytesPerSecond(s.Network.DownloadBytesPerSecond)} / {Format.BytesPerSecond(s.Network.UploadBytesPerSecond)}");
        Line("Adapter totals", $"{Format.Bytes(s.Network.TotalDownloadedBytes)} down, {Format.Bytes(s.Network.TotalUploadedBytes)} up");
        Line("Available", s.Network.AvailableInterfaces.Count == 0 ? Format.NotAvailable : string.Join(", ", s.Network.AvailableInterfaces));

        Section($"Temperatures ({s.Temperatures.Count} sensors)");
        if (s.Temperatures.Count == 0)
        {
            Console.WriteLine("  no temperature sensors readable");
            Console.WriteLine("  (this is the expected result without administrator rights on many systems)");
        }

        foreach (var group in s.Temperatures.GroupBy(static t => t.Group))
        {
            Console.WriteLine($"  {group.Key}:");
            foreach (var reading in group)
            {
                Console.WriteLine($"    {reading.Source} / {reading.Name,-28} {Format.Temperature(reading.Celsius, unit)}");
            }
        }

        Section("System");
        Line("Host", Format.TextOrNotAvailable(s.System.HostName));
        Line("OS", $"{Format.TextOrNotAvailable(s.System.OsName)} ({Format.TextOrNotAvailable(s.System.OsVersion)})");
        Line("Motherboard", Format.TextOrNotAvailable(s.System.Motherboard));
        Line("BIOS", Format.TextOrNotAvailable(s.System.BiosVersion));
        Line("Uptime", Format.Uptime(s.System.Uptime));
        Line("Elevated", s.System.IsElevated.ToString());
    }

    private static void PrintCost(List<double> durations, MonitoringService service)
    {
        Section("Cost");

        if (durations.Count > 1)
        {
            // The first tick includes one-time initialisation, so the steady-state figure
            // is what the app actually pays per second.
            var steady = durations.Skip(1).ToList();
            Line("First tick", $"{durations[0]:F1} ms (includes one-time setup)");
            Line("Steady state", $"{steady.Average():F1} ms avg, {steady.Max():F1} ms max");
        }
        else if (durations.Count == 1)
        {
            Line("Single tick", $"{durations[0]:F1} ms");
        }

        Line("Managed heap", Format.Bytes(GC.GetTotalMemory(forceFullCollection: false)));
        Line("Working set", Format.Bytes(Environment.WorkingSet));

        var faults = service.FaultedProviders();
        Line("Faulted providers", faults.Count == 0 ? "none" : string.Join(", ", faults.Select(static f => $"{f.Name} ({f.Error})")));
    }

    /// <summary>Prints every sensor the library exposes, for diagnosing an unexpected N/A.</summary>
    private static void DumpRawSensors(HardwareSession session)
    {
        Section("Raw sensor dump");

        foreach (var hardware in session.AllHardware())
        {
            HardwareSession.Update(hardware);
            Console.WriteLine($"  {hardware.HardwareType}: {hardware.Name}");

            foreach (var sensor in hardware.Sensors.OrderBy(static x => x.SensorType))
            {
                Console.WriteLine($"    {sensor.SensorType,-12} {sensor.Name,-32} {FormatRaw(sensor)}");
            }

            foreach (var sub in hardware.SubHardware)
            {
                Console.WriteLine($"    -- {sub.Name}");
                foreach (var sensor in sub.Sensors.OrderBy(static x => x.SensorType))
                {
                    Console.WriteLine($"       {sensor.SensorType,-12} {sensor.Name,-32} {FormatRaw(sensor)}");
                }
            }
        }

        Console.WriteLine();
    }

    private static string FormatRaw(ISensor sensor) =>
        sensor.Value is { } value ? value.ToString("F2", CultureInfo.CurrentCulture) : "null";

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', Math.Max(title.Length, 20)));
    }

    private static void Line(string label, string value) =>
        Console.WriteLine($"  {label,-20} {value}");

    private static int ArgValue(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }
}
