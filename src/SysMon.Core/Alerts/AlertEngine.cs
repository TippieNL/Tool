using SysMon.Core.Configuration;
using SysMon.Core.Formatting;
using SysMon.Core.Models;

namespace SysMon.Core.Alerts;

public enum AlertKind
{
    CpuTemperature,
    GpuTemperature,
    MemoryUsage,
    StorageUsage,
}

public sealed record AlertEvent(AlertKind Kind, string Title, string Message, double Value, double Threshold);

/// <summary>
/// Turns snapshots into notifications.
///
/// Two behaviours keep this from becoming a nuisance: hysteresis, so a value hovering on the
/// threshold does not toggle repeatedly, and a re-notify interval, so a genuinely hot CPU
/// produces one toast every few minutes rather than one per second.
/// </summary>
public sealed class AlertEngine
{
    /// <summary>How far below the threshold a value must fall before the alert re-arms.</summary>
    public const double Hysteresis = 3d;

    private readonly Dictionary<string, AlertState> _states = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public AlertEngine(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Evaluates a snapshot and returns the alerts that should be shown right now.
    /// Returns an empty list in the common case, allocating nothing.
    /// </summary>
    public IReadOnlyList<AlertEvent> Evaluate(Snapshot snapshot, AlertSettings settings, TemperatureUnit unit)
    {
        List<AlertEvent>? events = null;

        Check(
            ref events,
            "cpu.temp",
            AlertKind.CpuTemperature,
            settings.CpuTemperature,
            settings.RenotifyMinutes,
            snapshot.Cpu.TemperatureC,
            "CPU temperature high",
            value => $"CPU is at {Format.Temperature(value, unit)} (threshold {Format.Temperature(settings.CpuTemperature.Threshold, unit)}).");

        // Alert per GPU, so a hot second card is not masked by a cool first one.
        foreach (var gpu in snapshot.Gpus)
        {
            Check(
                ref events,
                $"gpu.temp:{gpu.Key}",
                AlertKind.GpuTemperature,
                settings.GpuTemperature,
                settings.RenotifyMinutes,
                gpu.TemperatureC,
                "GPU temperature high",
                value => $"{gpu.Name ?? "GPU"} is at {Format.Temperature(value, unit)} (threshold {Format.Temperature(settings.GpuTemperature.Threshold, unit)}).");
        }

        Check(
            ref events,
            "mem.load",
            AlertKind.MemoryUsage,
            settings.MemoryUsage,
            settings.RenotifyMinutes,
            snapshot.Memory.LoadPercent,
            "Memory usage high",
            value => $"RAM is at {Format.Percent(value)} (threshold {Format.Percent(settings.MemoryUsage.Threshold)}).");

        foreach (var drive in snapshot.Drives)
        {
            Check(
                ref events,
                $"disk.usage:{drive.Key}",
                AlertKind.StorageUsage,
                settings.StorageUsage,
                settings.RenotifyMinutes,
                drive.UsedPercent,
                "Storage almost full",
                value => $"{drive.MountPoint ?? drive.Key} is {Format.Percent(value)} full ({Format.Bytes(drive.FreeBytes)} free).");
        }

        return (IReadOnlyList<AlertEvent>?)events ?? [];
    }

    /// <summary>Drops remembered state, e.g. when the user changes thresholds.</summary>
    public void Reset() => _states.Clear();

    private void Check(
        ref List<AlertEvent>? events,
        string key,
        AlertKind kind,
        AlertRule rule,
        int renotifyMinutes,
        double? value,
        string title,
        Func<double, string> messageFactory)
    {
        if (!rule.Enabled)
        {
            _states.Remove(key);
            return;
        }

        // A sensor that has gone away must not leave an alert latched on.
        if (value is not { } current || double.IsNaN(current))
        {
            _states.Remove(key);
            return;
        }

        ref var state = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_states, key, out _);

        var now = _clock();

        if (current < rule.Threshold - Hysteresis)
        {
            // Comfortably back below the line: re-arm.
            state.IsActive = false;
            return;
        }

        if (current < rule.Threshold)
        {
            // Inside the hysteresis band. Hold whatever state we are in without firing.
            return;
        }

        var renotifyAfter = TimeSpan.FromMinutes(Math.Max(1, renotifyMinutes));
        var shouldFire = !state.IsActive || now - state.LastNotified >= renotifyAfter;

        state.IsActive = true;

        if (!shouldFire)
        {
            return;
        }

        state.LastNotified = now;

        events ??= [];
        events.Add(new AlertEvent(kind, title, messageFactory(current), current, rule.Threshold));
    }

    private struct AlertState
    {
        public bool IsActive;
        public DateTimeOffset LastNotified;
    }
}
