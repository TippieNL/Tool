using System.Collections.Concurrent;

namespace SysMon.Core.History;

/// <summary>
/// Well-known series identifiers. GPU and drive series are suffixed with the device key so that
/// multi-GPU and multi-drive machines each get their own history.
/// </summary>
public static class SeriesIds
{
    public const string CpuLoad = "cpu.load";
    public const string CpuTemperature = "cpu.temp";
    public const string MemoryLoad = "mem.load";
    public const string NetworkDown = "net.down";
    public const string NetworkUp = "net.up";
    public const string DiskActivity = "disk.activity";

    public static string GpuLoad(string gpuKey) => $"gpu.load:{gpuKey}";

    public static string GpuTemperature(string gpuKey) => $"gpu.temp:{gpuKey}";
}

/// <summary>
/// Owns every history series. Series are created on demand so a provider can start recording a
/// new device (a GPU that appears, a drive that is plugged in) without any registration step.
/// </summary>
public sealed class HistoryStore
{
    private readonly ConcurrentDictionary<string, HistorySeries> _series = new(StringComparer.Ordinal);

    public HistoryStore(int capacity) => Capacity = capacity;

    /// <summary>Number of samples each series retains, i.e. history duration / update interval.</summary>
    public int Capacity { get; private set; }

    public HistorySeries Get(string id) =>
        _series.GetOrAdd(id, static (key, capacity) => new HistorySeries(key, capacity), Capacity);

    public bool TryGet(string id, out HistorySeries series) => _series.TryGetValue(id, out series!);

    public void Add(string id, double? value) => Get(id).Add(value);

    /// <summary>
    /// Resizes every series, keeping the newest samples that still fit. Called when the user
    /// changes either the history duration or the update interval, since capacity derives from both.
    /// </summary>
    public void SetCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        if (capacity == Capacity)
        {
            return;
        }

        Capacity = capacity;

        foreach (var series in _series.Values)
        {
            series.Resize(capacity);
        }
    }

    public void Clear()
    {
        foreach (var series in _series.Values)
        {
            series.Clear();
        }
    }

    /// <summary>Computes how many samples cover the requested duration at the given interval.</summary>
    public static int CapacityFor(TimeSpan duration, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return 60;
        }

        var samples = (int)Math.Ceiling(duration.TotalMilliseconds / interval.TotalMilliseconds);
        return Math.Clamp(samples, 10, 10_000);
    }
}
