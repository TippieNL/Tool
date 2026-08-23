using SysMon.Core.History;
using Xunit;

namespace SysMon.Core.Tests;

public class HistorySeriesTests
{
    private static float[] Snapshot(HistorySeries series)
    {
        var buffer = new float[series.Capacity];
        var count = series.CopyTo(buffer);
        return buffer[..count];
    }

    [Fact]
    public void CopyTo_ReturnsSamplesOldestFirst()
    {
        var series = new HistorySeries("t", 5);
        series.Add(1);
        series.Add(2);
        series.Add(3);

        Assert.Equal([1f, 2f, 3f], Snapshot(series));
        Assert.Equal(3, series.Count);
    }

    [Fact]
    public void Add_BeyondCapacity_WrapsAndDropsOldest()
    {
        var series = new HistorySeries("t", 3);
        for (var i = 1; i <= 5; i++)
        {
            series.Add(i);
        }

        Assert.Equal(3, series.Count);
        Assert.Equal([3f, 4f, 5f], Snapshot(series));
        Assert.Equal(5d, series.Latest);
    }

    [Fact]
    public void Add_Null_IsStoredAsGap()
    {
        var series = new HistorySeries("t", 3);
        series.Add(1);
        series.Add(null);

        var values = Snapshot(series);
        Assert.Equal(1f, values[0]);
        Assert.True(float.IsNaN(values[1]));
        Assert.Null(series.Latest);
    }

    [Fact]
    public void Add_NonFiniteValues_AreStoredAsGaps()
    {
        var series = new HistorySeries("t", 3);
        series.Add(double.NaN);
        series.Add(double.PositiveInfinity);

        Assert.True(Snapshot(series).All(float.IsNaN));
    }

    [Fact]
    public void CopyTo_SmallerDestination_KeepsNewestSamples()
    {
        var series = new HistorySeries("t", 5);
        for (var i = 1; i <= 5; i++)
        {
            series.Add(i);
        }

        var destination = new float[3];
        var count = series.CopyTo(destination);

        Assert.Equal(3, count);
        Assert.Equal([3f, 4f, 5f], destination);
    }

    [Fact]
    public void Max_IgnoresGaps()
    {
        var series = new HistorySeries("t", 5);
        series.Add(10);
        series.Add(null);
        series.Add(4);

        Assert.Equal(10d, series.Max());
    }

    [Fact]
    public void Max_AllGaps_ReturnsNull()
    {
        var series = new HistorySeries("t", 3);
        series.Add(null);
        series.Add(null);

        Assert.Null(series.Max());
    }

    [Fact]
    public void Resize_Smaller_KeepsNewestSamples()
    {
        var series = new HistorySeries("t", 5);
        for (var i = 1; i <= 5; i++)
        {
            series.Add(i);
        }

        series.Resize(3);

        Assert.Equal(3, series.Capacity);
        Assert.Equal(3, series.Count);
        Assert.Equal([3f, 4f, 5f], Snapshot(series));
    }

    [Fact]
    public void Resize_Larger_KeepsExistingSamplesAndAcceptsMore()
    {
        var series = new HistorySeries("t", 3);
        series.Add(1);
        series.Add(2);

        series.Resize(6);
        series.Add(3);

        Assert.Equal(6, series.Capacity);
        Assert.Equal([1f, 2f, 3f], Snapshot(series));
    }

    [Fact]
    public void Resize_AfterWrap_PreservesChronologicalOrder()
    {
        // Regression guard: resizing a wrapped buffer must not reorder samples.
        var series = new HistorySeries("t", 4);
        for (var i = 1; i <= 6; i++)
        {
            series.Add(i);
        }

        series.Resize(8);
        series.Add(7);

        Assert.Equal([3f, 4f, 5f, 6f, 7f], Snapshot(series));
    }

    [Fact]
    public void Resize_ToSameCapacity_IsNoOp()
    {
        var series = new HistorySeries("t", 4);
        series.Add(1);
        series.Resize(4);

        Assert.Equal([1f], Snapshot(series));
    }

    [Fact]
    public void Clear_EmptiesTheSeries()
    {
        var series = new HistorySeries("t", 4);
        series.Add(1);
        series.Clear();

        Assert.Equal(0, series.Count);
        Assert.Null(series.Latest);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistorySeries("t", 0));
}

public class HistoryStoreTests
{
    [Fact]
    public void Get_CreatesSeriesOnDemandAndReturnsTheSameInstance()
    {
        var store = new HistoryStore(10);
        var first = store.Get("a");
        var second = store.Get("a");

        Assert.Same(first, second);
        Assert.Equal(10, first.Capacity);
    }

    [Fact]
    public void SetCapacity_ResizesEverySeries()
    {
        var store = new HistoryStore(10);
        store.Add("a", 1);
        store.Add("b", 2);

        store.SetCapacity(20);

        Assert.Equal(20, store.Get("a").Capacity);
        Assert.Equal(20, store.Get("b").Capacity);
        Assert.Equal(1d, store.Get("a").Latest);
    }

    [Fact]
    public void CapacityFor_ComputesSampleCountFromDurationAndInterval() =>
        Assert.Equal(60, HistoryStore.CapacityFor(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1)));

    [Fact]
    public void CapacityFor_RoundsUpPartialSamples() =>
        Assert.Equal(120, HistoryStore.CapacityFor(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500)));

    [Fact]
    public void CapacityFor_ZeroInterval_FallsBackToASafeDefault() =>
        Assert.Equal(60, HistoryStore.CapacityFor(TimeSpan.FromSeconds(60), TimeSpan.Zero));

    [Fact]
    public void SeriesIds_AreDistinctPerDevice() =>
        Assert.NotEqual(SeriesIds.GpuLoad("gpu-0"), SeriesIds.GpuLoad("gpu-1"));
}
