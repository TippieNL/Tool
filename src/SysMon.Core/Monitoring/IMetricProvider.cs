using SysMon.Core.Models;

namespace SysMon.Core.Monitoring;

/// <summary>
/// How often a provider needs to run. Keeping expensive sources off the fast tier is the
/// main lever the app has on its own CPU cost.
/// </summary>
public enum PollTier
{
    /// <summary>Runs once at startup. Names, capacities, OS facts.</summary>
    Static = 0,

    /// <summary>Runs on every tick. CPU, memory, GPU, network.</summary>
    Fast = 1,

    /// <summary>Runs every few seconds. Storage, motherboard sensors.</summary>
    Slow = 2,
}

/// <summary>
/// A source of metrics. Implementations live in the platform-specific monitoring assembly;
/// nothing in the UI ever references one directly.
/// </summary>
public interface IMetricProvider : IDisposable
{
    /// <summary>Stable name used in logs and in the degraded-state banner.</summary>
    string Name { get; }

    PollTier Tier { get; }

    /// <summary>
    /// Acquires handles and caches static data. Called once before the first poll, and again
    /// if the provider is being retried after a fault.
    /// </summary>
    void Initialize();

    /// <summary>Reads current values into the builder. Must not throw for a missing sensor.</summary>
    void Poll(SnapshotBuilder builder);
}
