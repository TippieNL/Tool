using SysMon.Core.History;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;
using Xunit;

namespace SysMon.Core.Tests;

public class MonitoringLoopTests
{
    private sealed class CountingProvider : IMetricProvider
    {
        public CountingProvider(string name, PollTier tier)
        {
            Name = name;
            Tier = tier;
        }

        public string Name { get; }

        public PollTier Tier { get; }

        public int PollCount { get; private set; }

        public Action<SnapshotBuilder>? OnPoll { get; set; }

        public void Initialize()
        {
        }

        public void Poll(SnapshotBuilder builder)
        {
            PollCount++;
            OnPoll?.Invoke(builder);
        }

        public void Dispose()
        {
        }
    }

    private static MonitoringLoop CreateLoop(
        IReadOnlyList<IMetricProvider> providers,
        HistoryStore history,
        TimeSpan? interval = null,
        TimeSpan? slowTier = null) =>
        new(providers, history, () => new MonitoringOptions
        {
            Interval = interval ?? TimeSpan.FromSeconds(1),
            SlowTierInterval = slowTier ?? TimeSpan.FromSeconds(5),
        });

    [Fact]
    public void FastProvidersRunOnEveryTick()
    {
        var fast = new CountingProvider("fast", PollTier.Fast);
        using var loop = CreateLoop([fast], new HistoryStore(60));

        for (var i = 0; i < 10; i++)
        {
            loop.PollOnce();
        }

        Assert.Equal(10, fast.PollCount);
    }

    [Fact]
    public void SlowProvidersRunOnlyOnceEverySlowTierInterval()
    {
        var fast = new CountingProvider("fast", PollTier.Fast);
        var slow = new CountingProvider("slow", PollTier.Slow);
        using var loop = CreateLoop([fast, slow], new HistoryStore(60),
            interval: TimeSpan.FromSeconds(1), slowTier: TimeSpan.FromSeconds(5));

        for (var i = 0; i < 10; i++)
        {
            loop.PollOnce();
        }

        // Ticks 1 and 6 hit the slow tier; the other eight skip it. This is the whole point of
        // tiering, so it is worth asserting exactly rather than approximately.
        Assert.Equal(10, fast.PollCount);
        Assert.Equal(2, slow.PollCount);
    }

    [Fact]
    public void SlowTierScalesWithTheConfiguredInterval()
    {
        var slow = new CountingProvider("slow", PollTier.Slow);
        using var loop = CreateLoop([slow], new HistoryStore(60),
            interval: TimeSpan.FromMilliseconds(500), slowTier: TimeSpan.FromSeconds(5));

        // At 500 ms per tick, five seconds is every tenth tick.
        for (var i = 0; i < 20; i++)
        {
            loop.PollOnce();
        }

        Assert.Equal(2, slow.PollCount);
    }

    [Fact]
    public void StaticProvidersRunOnEveryTickBecauseTheyOnlyRepublishCachedValues()
    {
        var info = new CountingProvider("info", PollTier.Static);
        using var loop = CreateLoop([info], new HistoryStore(60));

        for (var i = 0; i < 5; i++)
        {
            loop.PollOnce();
        }

        Assert.Equal(5, info.PollCount);
    }

    [Fact]
    public void EachTickRecordsOneSampleInHistory()
    {
        var history = new HistoryStore(60);
        var provider = new CountingProvider("cpu", PollTier.Fast)
        {
            OnPoll = builder => builder.Cpu = new CpuSnapshot { TotalLoad = 42 },
        };

        using var loop = CreateLoop([provider], history);

        for (var i = 0; i < 3; i++)
        {
            loop.PollOnce();
        }

        Assert.Equal(3, history.Get(SeriesIds.CpuLoad).Count);
        Assert.Equal(42d, history.Get(SeriesIds.CpuLoad).Latest);
    }

    [Fact]
    public void MissingMetricsAreRecordedAsGapsRatherThanZero()
    {
        var history = new HistoryStore(60);
        using var loop = CreateLoop([new CountingProvider("noop", PollTier.Fast)], history);

        loop.PollOnce();

        // A gap, not a zero: an absent sensor must not draw as idle hardware.
        Assert.Equal(1, history.Get(SeriesIds.CpuTemperature).Count);
        Assert.Null(history.Get(SeriesIds.CpuTemperature).Latest);
    }

    [Fact]
    public void DiskHistoryTracksTheBusiestDrive()
    {
        var history = new HistoryStore(60);
        var provider = new CountingProvider("disk", PollTier.Fast)
        {
            OnPoll = builder => builder.SetDrives(
            [
                new DriveSnapshot { Key = "a", ActivityPercent = 12 },
                new DriveSnapshot { Key = "b", ActivityPercent = 87 },
            ]),
        };

        using var loop = CreateLoop([provider], history);
        loop.PollOnce();

        Assert.Equal(87d, history.Get(SeriesIds.DiskActivity).Latest);
    }

    [Fact]
    public void EachGpuGetsItsOwnHistorySeries()
    {
        var history = new HistoryStore(60);
        var provider = new CountingProvider("gpu", PollTier.Fast)
        {
            OnPoll = builder => builder.SetGpus(
            [
                new GpuSnapshot { Key = "gpu-0", Load = 30 },
                new GpuSnapshot { Key = "gpu-1", Load = 70 },
            ]),
        };

        using var loop = CreateLoop([provider], history);
        loop.PollOnce();

        Assert.Equal(30d, history.Get(SeriesIds.GpuLoad("gpu-0")).Latest);
        Assert.Equal(70d, history.Get(SeriesIds.GpuLoad("gpu-1")).Latest);
    }

    [Fact]
    public void SlowTierValuesPersistAcrossTicksThatSkipIt()
    {
        var slow = new CountingProvider("slow", PollTier.Slow)
        {
            OnPoll = builder => builder.SetDrives([new DriveSnapshot { Key = "c", UsedPercent = 55 }]),
        };

        using var loop = CreateLoop([slow], new HistoryStore(60));

        loop.PollOnce();
        var later = loop.PollOnce();

        // The drive card must not blank out on the ticks where storage does not run.
        var drive = Assert.Single(later.Drives);
        Assert.Equal(55d, drive.UsedPercent);
    }

    [Fact]
    public void ASubscriberThatThrowsDoesNotStopMonitoring()
    {
        var provider = new CountingProvider("fast", PollTier.Fast);
        using var loop = CreateLoop([provider], new HistoryStore(60));

        loop.SnapshotAvailable += _ => throw new InvalidOperationException("subscriber blew up");

        // PollOnce does not publish, so drive the publish path through the real loop.
        loop.Start();
        SpinWait.SpinUntil(() => provider.PollCount >= 1, TimeSpan.FromSeconds(2));
        loop.Stop();

        Assert.True(provider.PollCount >= 1);
    }

    [Fact]
    public void StartPublishesImmediatelyAndStopEndsTheLoop()
    {
        var provider = new CountingProvider("fast", PollTier.Fast);
        using var loop = CreateLoop([provider], new HistoryStore(60), interval: TimeSpan.FromMilliseconds(60));

        var published = 0;
        loop.SnapshotAvailable += _ => Interlocked.Increment(ref published);

        loop.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref published) >= 3, TimeSpan.FromSeconds(5)));
        loop.Stop();

        var afterStop = Volatile.Read(ref published);
        Thread.Sleep(200);

        Assert.False(loop.IsRunning);
        Assert.Equal(afterStop, Volatile.Read(ref published));
    }

    [Fact]
    public void BackgroundModeSlowsThePollingRate()
    {
        var provider = new CountingProvider("fast", PollTier.Fast);
        using var loop = new MonitoringLoop([provider], new HistoryStore(60), () => new MonitoringOptions
        {
            Interval = TimeSpan.FromMilliseconds(50),
            BackgroundMultiplier = 10,
        });

        loop.SetBackgroundMode(true);
        loop.Start();
        Thread.Sleep(400);
        loop.Stop();

        // At 500 ms per tick the loop cannot have run more than a handful of times in 400 ms;
        // at the foreground rate of 50 ms it would have run roughly eight.
        Assert.InRange(provider.PollCount, 1, 3);
    }

    [Fact]
    public void LatestExposesTheMostRecentSnapshot()
    {
        var load = 0d;
        var provider = new CountingProvider("cpu", PollTier.Fast)
        {
            OnPoll = builder => builder.Cpu = new CpuSnapshot { TotalLoad = load },
        };

        using var loop = CreateLoop([provider], new HistoryStore(60));

        load = 10;
        loop.PollOnce();
        load = 90;
        loop.PollOnce();

        Assert.Equal(90d, loop.Latest.Cpu.TotalLoad);
    }

    [Fact]
    public void FaultedProvidersAreReportedOnTheSnapshot()
    {
        var throwing = new SafeProvider(new ThrowingProvider());
        var healthy = new CountingProvider("healthy", PollTier.Fast);
        using var loop = CreateLoop([throwing, healthy], new HistoryStore(60));

        var snapshot = loop.PollOnce();

        Assert.Equal("throwing", Assert.Single(snapshot.FaultedProviders));
        Assert.Equal(1, healthy.PollCount);
    }

    private sealed class ThrowingProvider : IMetricProvider
    {
        public string Name => "throwing";

        public PollTier Tier => PollTier.Fast;

        public void Initialize()
        {
        }

        public void Poll(SnapshotBuilder builder) => throw new InvalidOperationException("sensor gone");

        public void Dispose()
        {
        }
    }
}
