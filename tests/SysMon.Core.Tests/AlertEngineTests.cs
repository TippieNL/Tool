using SysMon.Core.Alerts;
using SysMon.Core.Configuration;
using SysMon.Core.Formatting;
using SysMon.Core.Models;
using Xunit;

namespace SysMon.Core.Tests;

public class AlertEngineTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private AlertEngine NewEngine() => new(() => _now);

    private static AlertSettings Settings(double cpuThreshold = 85, int renotifyMinutes = 5) => new()
    {
        RenotifyMinutes = renotifyMinutes,
        CpuTemperature = new AlertRule { Enabled = true, Threshold = cpuThreshold },
        GpuTemperature = new AlertRule { Enabled = false },
        MemoryUsage = new AlertRule { Enabled = false },
        StorageUsage = new AlertRule { Enabled = false },
    };

    private static Snapshot WithCpuTemp(double? celsius) =>
        new() { Cpu = new CpuSnapshot { TemperatureC = celsius } };

    [Fact]
    public void BelowThreshold_ProducesNoAlert()
    {
        var engine = NewEngine();
        Assert.Empty(engine.Evaluate(WithCpuTemp(70), Settings(), TemperatureUnit.Celsius));
    }

    [Fact]
    public void CrossingThreshold_RaisesOneAlert()
    {
        var engine = NewEngine();
        var events = engine.Evaluate(WithCpuTemp(90), Settings(), TemperatureUnit.Celsius);

        var alert = Assert.Single(events);
        Assert.Equal(AlertKind.CpuTemperature, alert.Kind);
        Assert.Equal(90d, alert.Value);
        Assert.Equal(85d, alert.Threshold);
    }

    [Fact]
    public void StayingAboveThreshold_DoesNotRepeatBeforeTheRenotifyInterval()
    {
        var engine = NewEngine();
        var settings = Settings();

        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));

        _now = _now.AddMinutes(4);
        Assert.Empty(engine.Evaluate(WithCpuTemp(92), settings, TemperatureUnit.Celsius));
    }

    [Fact]
    public void StayingAboveThreshold_RepeatsOnceTheRenotifyIntervalElapses()
    {
        var engine = NewEngine();
        var settings = Settings();

        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));

        _now = _now.AddMinutes(5);
        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));
    }

    [Fact]
    public void InsideTheHysteresisBand_DoesNotReArm()
    {
        var engine = NewEngine();
        var settings = Settings();

        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));

        // 83 is below the 85 threshold but within the 3-degree hysteresis band, so the alert
        // stays latched: dropping back to 90 must not fire a second notification.
        Assert.Empty(engine.Evaluate(WithCpuTemp(83), settings, TemperatureUnit.Celsius));
        Assert.Empty(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));
    }

    [Fact]
    public void FallingBelowTheHysteresisBand_ReArmsTheAlert()
    {
        var engine = NewEngine();
        var settings = Settings();

        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));
        Assert.Empty(engine.Evaluate(WithCpuTemp(70), settings, TemperatureUnit.Celsius));

        // Re-armed, so this fires immediately without waiting for the renotify interval.
        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));
    }

    [Fact]
    public void DisabledRule_NeverFires()
    {
        var engine = NewEngine();
        var settings = Settings();
        settings.CpuTemperature.Enabled = false;

        Assert.Empty(engine.Evaluate(WithCpuTemp(120), settings, TemperatureUnit.Celsius));
    }

    [Fact]
    public void MissingSensor_ProducesNoAlertAndClearsLatchedState()
    {
        var engine = NewEngine();
        var settings = Settings();

        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));

        // Sensor disappears (driver reload, hardware removed).
        Assert.Empty(engine.Evaluate(WithCpuTemp(null), settings, TemperatureUnit.Celsius));

        // Because the latch was cleared, the alert fires again as soon as the sensor returns hot.
        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));
    }

    [Fact]
    public void NaNSensorValue_IsTreatedAsMissing()
    {
        var engine = NewEngine();
        Assert.Empty(engine.Evaluate(WithCpuTemp(double.NaN), Settings(), TemperatureUnit.Celsius));
    }

    [Fact]
    public void EachGpuIsTrackedSeparately()
    {
        var engine = NewEngine();
        var settings = Settings();
        settings.GpuTemperature = new AlertRule { Enabled = true, Threshold = 85 };

        var snapshot = new Snapshot
        {
            Gpus =
            [
                new GpuSnapshot { Key = "gpu-0", Name = "Cool card", TemperatureC = 60 },
                new GpuSnapshot { Key = "gpu-1", Name = "Hot card", TemperatureC = 95 },
            ],
        };

        var alert = Assert.Single(engine.Evaluate(snapshot, settings, TemperatureUnit.Celsius));
        Assert.Contains("Hot card", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EachDriveIsTrackedSeparately()
    {
        var engine = NewEngine();
        var settings = Settings();
        settings.StorageUsage = new AlertRule { Enabled = true, Threshold = 90 };

        var snapshot = new Snapshot
        {
            Drives =
            [
                new DriveSnapshot { Key = "C", MountPoint = @"C:\", UsedPercent = 50, FreeBytes = 100 },
                new DriveSnapshot { Key = "D", MountPoint = @"D:\", UsedPercent = 95, FreeBytes = 100 },
            ],
        };

        var alert = Assert.Single(engine.Evaluate(snapshot, settings, TemperatureUnit.Celsius));
        Assert.Contains(@"D:\", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageUsesTheConfiguredTemperatureUnit()
    {
        var engine = NewEngine();
        var alert = Assert.Single(engine.Evaluate(WithCpuTemp(90), Settings(), TemperatureUnit.Fahrenheit));

        Assert.Contains("194°F", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_ClearsLatchedState()
    {
        var engine = NewEngine();
        var settings = Settings();

        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));
        engine.Reset();

        Assert.Single(engine.Evaluate(WithCpuTemp(90), settings, TemperatureUnit.Celsius));
    }
}
