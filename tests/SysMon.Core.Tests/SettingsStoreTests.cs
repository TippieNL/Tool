using SysMon.Core.Configuration;
using SysMon.Core.Formatting;
using Xunit;

namespace SysMon.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sysmon-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup failures are irrelevant to the assertions.
        }
    }

    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        using var store = new SettingsStore(SettingsPath);
        var settings = store.Load();

        Assert.Equal(1000, settings.UpdateIntervalMs);
        Assert.Equal(60, settings.GraphHistorySeconds);
        Assert.True(settings.MinimizeToTray);
        Assert.Equal(TemperatureUnit.Celsius, settings.TemperatureUnit);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEveryField()
    {
        using (var store = new SettingsStore(SettingsPath))
        {
            var settings = new AppSettings
            {
                StartWithWindows = true,
                CloseToTray = false,
                UpdateIntervalMs = 2500,
                TemperatureUnit = TemperatureUnit.Fahrenheit,
                GraphHistorySeconds = 300,
                TrayStat = TrayStat.CpuTemperature,
                PreferredNetworkInterface = "Ethernet",
                EnableGpuSensors = false,
            };
            settings.Alerts.CpuTemperature.Threshold = 78;

            store.Save(settings);
            store.Flush();
        }

        using var reopened = new SettingsStore(SettingsPath);
        var loaded = reopened.Load();

        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.CloseToTray);
        Assert.Equal(2500, loaded.UpdateIntervalMs);
        Assert.Equal(TemperatureUnit.Fahrenheit, loaded.TemperatureUnit);
        Assert.Equal(300, loaded.GraphHistorySeconds);
        Assert.Equal(TrayStat.CpuTemperature, loaded.TrayStat);
        Assert.Equal("Ethernet", loaded.PreferredNetworkInterface);
        Assert.False(loaded.EnableGpuSensors);
        Assert.Equal(78, loaded.Alerts.CpuTemperature.Threshold);
    }

    [Fact]
    public void Load_WithCorruptFile_BacksItUpAndReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        using var store = new SettingsStore(SettingsPath);
        var settings = store.Load();

        Assert.Equal(1000, settings.UpdateIntervalMs);
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Load_WithOutOfRangeValues_ClampsThem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "updateIntervalMs": 50,
              "graphHistorySeconds": 99999,
              "alerts": { "renotifyMinutes": 0, "memoryUsage": { "enabled": true, "threshold": 500 } }
            }
            """);

        using var store = new SettingsStore(SettingsPath);
        var settings = store.Load();

        Assert.Equal(500, settings.UpdateIntervalMs);
        Assert.Equal(3600, settings.GraphHistorySeconds);
        Assert.Equal(1, settings.Alerts.RenotifyMinutes);
        Assert.Equal(100, settings.Alerts.MemoryUsage.Threshold);
    }

    [Fact]
    public void Load_WithPartialFile_KeepsDefaultsForMissingFields()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "updateIntervalMs": 2000 }""");

        using var store = new SettingsStore(SettingsPath);
        var settings = store.Load();

        Assert.Equal(2000, settings.UpdateIntervalMs);
        Assert.True(settings.MinimizeToTray);
        Assert.NotNull(settings.Alerts);
        Assert.Equal(85, settings.Alerts.CpuTemperature.Threshold);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        using var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings());
        store.Flush();

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Clone_DoesNotShareAlertState()
    {
        var settings = new AppSettings();
        var clone = settings.Clone();

        clone.Alerts.CpuTemperature.Threshold = 50;

        Assert.Equal(85, settings.Alerts.CpuTemperature.Threshold);
    }

    [Fact]
    public void Save_IsDebounced_SoRepeatedCallsWriteOnce()
    {
        using var store = new SettingsStore(SettingsPath);

        for (var i = 0; i < 10; i++)
        {
            store.Save(new AppSettings { UpdateIntervalMs = 1000 + i });
        }

        store.Flush();

        // The last value queued is the one that reaches disk.
        Assert.Equal(1009, store.Load().UpdateIntervalMs);
    }
}
