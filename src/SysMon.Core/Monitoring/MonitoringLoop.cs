using System.Diagnostics;
using SysMon.Core.Diagnostics;
using SysMon.Core.History;
using SysMon.Core.Models;

namespace SysMon.Core.Monitoring;

/// <summary>How often the loop polls, and how that changes when nobody is looking.</summary>
public sealed record MonitoringOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Interval multiplier applied while the UI is hidden.</summary>
    public int BackgroundMultiplier { get; init; } = 2;

    /// <summary>Minimum spacing between runs of the slow tier.</summary>
    public TimeSpan SlowTierInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Drives a set of providers from one background loop and publishes an immutable snapshot per tick.
///
/// This holds no Windows dependency on purpose: the scheduling policy — tiering, background
/// slowdown, history recording, fault reporting — is the part worth testing, and keeping it here
/// means it can be tested on any platform with fake providers.
/// </summary>
public sealed class MonitoringLoop : IDisposable
{
    private readonly IReadOnlyList<IMetricProvider> _providers;
    private readonly HistoryStore _history;
    private readonly SnapshotBuilder _builder = new();
    private readonly Lock _gate = new();

    private Func<MonitoringOptions> _options;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _isBackground;
    private int _fastTicksUntilSlowTier;
    private bool _disposed;

    public MonitoringLoop(IReadOnlyList<IMetricProvider> providers, HistoryStore history, Func<MonitoringOptions> options)
    {
        _providers = providers;
        _history = history;
        _options = options;
    }

    /// <summary>Raised once per tick on the monitoring thread. Handlers marshal to the UI themselves.</summary>
    public event Action<Snapshot>? SnapshotAvailable;

    public bool IsRunning { get; private set; }

    /// <summary>Most recent snapshot, so a newly-shown window has something to display at once.</summary>
    public Snapshot Latest { get; private set; } = Snapshot.Empty;

    /// <summary>Duration of the last tick. Reported by the probe as the app's own cost.</summary>
    public TimeSpan LastTickDuration { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || IsRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            IsRunning = true;

            var token = _cts.Token;
            _loop = Task.Factory.StartNew(
                () => RunAsync(token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }
    }

    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        try
        {
            cts?.Cancel();
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // Cancellation is the expected way out of the loop.
        }
        finally
        {
            cts?.Dispose();
        }
    }

    /// <summary>Replaces the source of polling options, e.g. after the user changes the interval.</summary>
    public void SetOptions(Func<MonitoringOptions> options) => _options = options;

    /// <summary>
    /// Tells the loop nobody can see the UI, so it can slow down. History keeps filling either way.
    /// </summary>
    public void SetBackgroundMode(bool isBackground) => _isBackground = isBackground;

    /// <summary>Runs exactly one tick synchronously. Used by the probe and by tests.</summary>
    public Snapshot PollOnce()
    {
        var stopwatch = Stopwatch.StartNew();

        _builder.BeginTick();

        var runSlowTier = _fastTicksUntilSlowTier <= 0;
        if (runSlowTier)
        {
            var options = _options();
            var interval = options.Interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : options.Interval;

            _fastTicksUntilSlowTier = Math.Max(1, (int)Math.Round(
                options.SlowTierInterval.TotalMilliseconds / interval.TotalMilliseconds));
        }

        _fastTicksUntilSlowTier--;

        foreach (var provider in _providers)
        {
            // Static providers republish cached values each tick, which is a field copy, not work.
            if (provider.Tier == PollTier.Slow && !runSlowTier)
            {
                continue;
            }

            provider.Poll(_builder);
        }

        var snapshot = _builder.Build();
        Latest = snapshot;
        RecordHistory(snapshot);

        LastTickDuration = stopwatch.Elapsed;
        return snapshot;
    }

    /// <summary>Sets a flag carried on every snapshot, used for the limited-sensor-access hint.</summary>
    public void SetLimitedSensorAccess(bool limited) => _builder.LimitedSensorAccess = limited;

    private async Task RunAsync(CancellationToken token)
    {
        var interval = CurrentInterval();
        using var timer = new PeriodicTimer(interval);

        try
        {
            // Publish an immediate first sample so the window is not blank while the first
            // interval elapses. CPU utilisation needs two samples, so this one carries none.
            Publish(PollOnce());

            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var desired = CurrentInterval();
                if (desired != interval)
                {
                    interval = desired;
                    timer.Period = desired;
                }

                Publish(PollOnce());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The loop itself failing is the one thing SafeProvider cannot cover.
            Log.Error("The monitoring loop stopped unexpectedly.", ex);
            IsRunning = false;
        }
    }

    private void Publish(Snapshot snapshot)
    {
        try
        {
            SnapshotAvailable?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            // A subscriber throwing must not stop monitoring.
            Log.Once("publish", LogLevel.Error, "A snapshot subscriber threw.", ex);
        }
    }

    private TimeSpan CurrentInterval()
    {
        var options = _options();
        var interval = options.Interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : options.Interval;
        var multiplier = _isBackground ? Math.Max(1, options.BackgroundMultiplier) : 1;

        return interval * multiplier;
    }

    private void RecordHistory(Snapshot snapshot)
    {
        _history.Add(SeriesIds.CpuLoad, snapshot.Cpu.TotalLoad);
        _history.Add(SeriesIds.CpuTemperature, snapshot.Cpu.TemperatureC);
        _history.Add(SeriesIds.MemoryLoad, snapshot.Memory.LoadPercent);
        _history.Add(SeriesIds.NetworkDown, snapshot.Network.DownloadBytesPerSecond);
        _history.Add(SeriesIds.NetworkUp, snapshot.Network.UploadBytesPerSecond);

        foreach (var gpu in snapshot.Gpus)
        {
            _history.Add(SeriesIds.GpuLoad(gpu.Key), gpu.Load);
            _history.Add(SeriesIds.GpuTemperature(gpu.Key), gpu.TemperatureC);
        }

        // One combined disk series: the busiest drive is what the graph should show.
        double? diskActivity = null;
        foreach (var drive in snapshot.Drives)
        {
            if (drive.ActivityPercent is { } activity && (diskActivity is null || activity > diskActivity))
            {
                diskActivity = activity;
            }
        }

        _history.Add(SeriesIds.DiskActivity, diskActivity);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
