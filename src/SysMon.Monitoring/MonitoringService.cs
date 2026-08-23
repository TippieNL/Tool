using System.Diagnostics;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.History;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;
using SysMon.Monitoring.Providers;

namespace SysMon.Monitoring;

/// <summary>
/// Drives every provider from a single background loop and publishes one immutable snapshot per tick.
///
/// The design targets a small, steady cost:
/// - one timer for the whole app, not one per card
/// - providers split across tiers, so expensive sources (storage, motherboard) run every few
///   seconds while cheap ones run every tick
/// - one event per tick, so the UI marshals once instead of per metric
/// - a background multiplier that slows the loop while the window is hidden
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly HardwareSession _session;
    private readonly SnapshotBuilder _builder = new();
    private readonly List<SafeProvider> _providers = [];
    private readonly HistoryStore _history;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private AppSettings _settings;
    private volatile bool _isBackground;
    private int _slowTickCounter;
    private bool _disposed;

    /// <summary>Slow-tier providers run once every this many fast ticks, at least every 5 seconds.</summary>
    private const int SlowTierSeconds = 5;

    public MonitoringService(AppSettings settings, HardwareSession session, HistoryStore history)
    {
        _settings = settings;
        _session = session;
        _history = history;
    }

    /// <summary>Raised once per tick on the monitoring thread. Handlers must marshal to the UI themselves.</summary>
    public event Action<Snapshot>? SnapshotAvailable;

    public bool IsRunning { get; private set; }

    /// <summary>Most recent snapshot, so a newly-opened window has something to show immediately.</summary>
    public Snapshot Latest { get; private set; } = Snapshot.Empty;

    /// <summary>Wall-clock duration of the last tick, surfaced by the probe for cost measurement.</summary>
    public TimeSpan LastTickDuration { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || IsRunning)
            {
                return;
            }

            _session.Open(_settings);
            BuildProviders();

            _cts = new CancellationTokenSource();
            IsRunning = true;

            var token = _cts.Token;
            _loop = Task.Factory.StartNew(
                () => RunLoopAsync(token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            Log.Info($"Monitoring started with {_providers.Count} providers at {_settings.UpdateIntervalMs} ms.");
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
            DisposeProviders();
        }

        Log.Info("Monitoring stopped.");
    }

    /// <summary>
    /// Applies new settings. Changes that alter which hardware groups are open, or the shape of
    /// the history buffers, are handled by restarting the loop rather than mutating it in flight.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        var needsRestart = _settings.EnableHardwareMonitoring != settings.EnableHardwareMonitoring
            || _settings.EnableCpuSensors != settings.EnableCpuSensors
            || _settings.EnableGpuSensors != settings.EnableGpuSensors
            || _settings.EnableMemorySensors != settings.EnableMemorySensors
            || _settings.EnableStorageSensors != settings.EnableStorageSensors
            || _settings.EnableMotherboardSensors != settings.EnableMotherboardSensors;

        var wasRunning = IsRunning;

        if (needsRestart && wasRunning)
        {
            Stop();
        }

        _settings = settings;

        _history.SetCapacity(HistoryStore.CapacityFor(
            TimeSpan.FromSeconds(settings.GraphHistorySeconds),
            TimeSpan.FromMilliseconds(settings.UpdateIntervalMs)));

        if (needsRestart)
        {
            _session.Reconfigure(settings);

            if (wasRunning)
            {
                Start();
            }
        }
    }

    /// <summary>
    /// Tells the service the window is hidden, so it can slow down. History keeps filling; only
    /// the sampling rate changes.
    /// </summary>
    public void SetBackgroundMode(bool isBackground) => _isBackground = isBackground;

    /// <summary>Runs one tick synchronously. Used by the probe and by tests.</summary>
    public Snapshot PollOnce()
    {
        var stopwatch = Stopwatch.StartNew();

        _builder.BeginTick();

        var runSlowTier = _slowTickCounter <= 0;
        if (runSlowTier)
        {
            _slowTickCounter = Math.Max(1, (int)Math.Round(
                SlowTierSeconds * 1000d / Math.Max(1, _settings.UpdateIntervalMs)));
        }

        _slowTickCounter--;

        foreach (var provider in _providers)
        {
            // Static providers publish cached values every tick; that is a field copy, not work.
            if (provider.Tier == PollTier.Slow && !runSlowTier)
            {
                continue;
            }

            provider.Poll(_builder);
        }

        _builder.LimitedSensorAccess = _session.HasLimitedAccess;

        var snapshot = _builder.Build();
        Latest = snapshot;
        RecordHistory(snapshot);

        LastTickDuration = stopwatch.Elapsed;
        return snapshot;
    }

    /// <summary>Providers currently in a faulted state, for the diagnostics view.</summary>
    public IReadOnlyList<(string Name, string? Error)> FaultedProviders() =>
        _providers.Where(p => p.IsFaulted).Select(p => (p.Name, p.LastError)).ToArray();

    private async Task RunLoopAsync(CancellationToken token)
    {
        var interval = CurrentInterval();
        using var timer = new PeriodicTimer(interval);

        try
        {
            // Publish an immediate first sample so the window is not blank while the first
            // interval elapses. Utilisation needs two samples, so this one carries no CPU load.
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
        var multiplier = _isBackground ? Math.Max(1, _settings.BackgroundIntervalMultiplier) : 1;
        return TimeSpan.FromMilliseconds(_settings.UpdateIntervalMs * multiplier);
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

        // One combined disk-activity series: the busiest drive is what the user cares about.
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

    private void BuildProviders()
    {
        DisposeProviders();

        // Order matters: CPU, GPU and storage refresh their hardware, and the temperature
        // provider then reads the values they made current instead of updating twice.
        var providers = new List<IMetricProvider>
        {
            new SystemInfoProvider(_session),
            new CpuProvider(_session),
            new MemoryProvider(),
            new GpuProvider(_session),
            new MotherboardProvider(_session),
            new StorageProvider(_session),
            new NetworkProvider(() => _settings),
            new TemperatureProvider(_session),
        };

        foreach (var provider in providers)
        {
            var safe = new SafeProvider(provider);
            safe.Initialize();
            _providers.Add(safe);
        }
    }

    private void DisposeProviders()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _providers.Clear();
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
