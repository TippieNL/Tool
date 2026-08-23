using SysMon.Core.Monitoring;
using Xunit;

namespace SysMon.Core.Tests;

public class SafeProviderTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeProvider : IMetricProvider
    {
        public string Name => "fake";

        public PollTier Tier => PollTier.Fast;

        public bool ThrowOnInitialize { get; set; }

        public bool ThrowOnPoll { get; set; }

        public bool ThrowOnDispose { get; set; }

        public int InitializeCount { get; private set; }

        public int PollCount { get; private set; }

        public void Initialize()
        {
            InitializeCount++;
            if (ThrowOnInitialize)
            {
                throw new InvalidOperationException("init failed");
            }
        }

        public void Poll(SnapshotBuilder builder)
        {
            PollCount++;
            if (ThrowOnPoll)
            {
                throw new InvalidOperationException("sensor exploded");
            }

            builder.LimitedSensorAccess = true;
        }

        public void Dispose()
        {
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }

    private SafeProvider Wrap(IMetricProvider inner) => new(inner, () => _now);

    [Fact]
    public void HealthyProvider_PollsNormally()
    {
        var inner = new FakeProvider();
        var safe = Wrap(inner);
        var builder = new SnapshotBuilder();

        safe.Initialize();
        safe.Poll(builder);

        Assert.False(safe.IsFaulted);
        Assert.True(builder.Build().LimitedSensorAccess);
        Assert.Empty(builder.Build().FaultedProviders);
    }

    [Fact]
    public void ThrowingPoll_IsSwallowedAndReported()
    {
        var inner = new FakeProvider { ThrowOnPoll = true };
        var safe = Wrap(inner);
        var builder = new SnapshotBuilder();

        safe.Initialize();
        safe.Poll(builder);

        Assert.Equal("fake", Assert.Single(builder.Build().FaultedProviders));
        Assert.Equal("sensor exploded", safe.LastError);
    }

    [Fact]
    public void ProviderIsParkedAfterRepeatedFailures()
    {
        var inner = new FakeProvider { ThrowOnPoll = true };
        var safe = Wrap(inner);
        safe.Initialize();

        for (var i = 0; i < SafeProvider.MaxConsecutiveFailures; i++)
        {
            safe.Poll(new SnapshotBuilder());
        }

        Assert.True(safe.IsFaulted);

        // Parked: further ticks cost nothing because the inner provider is not called.
        var pollsBefore = inner.PollCount;
        safe.Poll(new SnapshotBuilder());
        Assert.Equal(pollsBefore, inner.PollCount);
    }

    [Fact]
    public void ParkedProviderIsRetriedAfterTheRetryInterval()
    {
        var inner = new FakeProvider { ThrowOnPoll = true };
        var safe = Wrap(inner);
        safe.Initialize();

        for (var i = 0; i < SafeProvider.MaxConsecutiveFailures; i++)
        {
            safe.Poll(new SnapshotBuilder());
        }

        Assert.True(safe.IsFaulted);

        // The hardware comes back.
        inner.ThrowOnPoll = false;
        _now += SafeProvider.RetryInterval;

        safe.Poll(new SnapshotBuilder());

        Assert.False(safe.IsFaulted);
        Assert.Null(safe.LastError);
    }

    [Fact]
    public void RetryReinitializesBeforePolling()
    {
        var inner = new FakeProvider { ThrowOnPoll = true };
        var safe = Wrap(inner);
        safe.Initialize();

        for (var i = 0; i < SafeProvider.MaxConsecutiveFailures; i++)
        {
            safe.Poll(new SnapshotBuilder());
        }

        var initializesBefore = inner.InitializeCount;
        inner.ThrowOnPoll = false;
        _now += SafeProvider.RetryInterval;
        safe.Poll(new SnapshotBuilder());

        // Reacquiring handles matters when the fault was a GPU reset or driver restart.
        Assert.True(inner.InitializeCount > initializesBefore);
    }

    [Fact]
    public void FailedInitialize_DoesNotThrowAndIsRetriedOnPoll()
    {
        var inner = new FakeProvider { ThrowOnInitialize = true };
        var safe = Wrap(inner);

        safe.Initialize();
        safe.Poll(new SnapshotBuilder());

        Assert.Equal("init failed", safe.LastError);
        Assert.True(inner.InitializeCount >= 2);
    }

    [Fact]
    public void FailedInitialize_RecoversOnceInitializeSucceeds()
    {
        var inner = new FakeProvider { ThrowOnInitialize = true };
        var safe = Wrap(inner);
        safe.Initialize();

        inner.ThrowOnInitialize = false;
        var builder = new SnapshotBuilder();
        safe.Poll(builder);

        Assert.False(safe.IsFaulted);
        Assert.True(builder.Build().LimitedSensorAccess);
    }

    [Fact]
    public void ThrowingDispose_IsSwallowed()
    {
        var safe = Wrap(new FakeProvider { ThrowOnDispose = true });
        safe.Dispose();
    }

    [Fact]
    public void OneFailingProvider_DoesNotStopTheOthers()
    {
        // This is the property the whole design hangs on: a broken sensor must not
        // prevent the rest of the machine from being reported.
        var broken = Wrap(new FakeProvider { ThrowOnPoll = true });
        var healthy = Wrap(new FakeProvider());
        var builder = new SnapshotBuilder();

        foreach (var provider in new[] { broken, healthy })
        {
            provider.Initialize();
            provider.Poll(builder);
        }

        var snapshot = builder.Build();
        Assert.Single(snapshot.FaultedProviders);
        Assert.True(snapshot.LimitedSensorAccess);
    }
}
