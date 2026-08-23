using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.History;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;
using SysMon.Monitoring.Providers;

namespace SysMon.Monitoring;

/// <summary>
/// The Windows-specific half of monitoring: owns the sensor library session, builds the providers,
/// and applies settings. The scheduling itself lives in <see cref="MonitoringLoop"/>, which has no
/// platform dependency.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly HardwareSession _session;
    private readonly HistoryStore _history;
    private readonly List<SafeProvider> _providers = [];
    private readonly Lock _gate = new();

    private MonitoringLoop? _loop;
    private AppSettings _settings;
    private bool _isBackground;
    private bool _disposed;

    public MonitoringService(AppSettings settings, HardwareSession session, HistoryStore history)
    {
        _settings = settings;
        _session = session;
        _history = history;
    }

    /// <summary>Raised once per tick on the monitoring thread.</summary>
    public event Action<Snapshot>? SnapshotAvailable;

    public bool IsRunning => _loop?.IsRunning ?? false;

    public Snapshot Latest => _loop?.Latest ?? Snapshot.Empty;

    public TimeSpan LastTickDuration => _loop?.LastTickDuration ?? TimeSpan.Zero;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || IsRunning)
            {
                return;
            }

            _session.Open(_settings);
            EnsureLoop();

            _loop!.SetSensorAccess(_session.GetSensorAccess());
            _loop.Start();

            Log.Info($"Monitoring started with {_providers.Count} providers at {_settings.UpdateIntervalMs} ms.");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_loop is null)
            {
                return;
            }

            _loop.Stop();
            _loop.SnapshotAvailable -= OnSnapshot;
            _loop.Dispose();
            _loop = null;

            DisposeProviders();
        }

        Log.Info("Monitoring stopped.");
    }

    /// <summary>Runs one tick synchronously, opening the session first if needed. Used by the probe.</summary>
    public Snapshot PollOnce()
    {
        lock (_gate)
        {
            _session.Open(_settings);
            EnsureLoop();
            _loop!.SetSensorAccess(_session.GetSensorAccess());
            return _loop.PollOnce();
        }
    }

    /// <summary>
    /// Applies new settings. Interval and history changes are picked up in flight; changes to
    /// which sensor groups are open require reopening the library, so the loop is restarted.
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

        if (!needsRestart)
        {
            return;
        }

        _session.Reconfigure(settings);

        if (wasRunning)
        {
            Start();
        }
    }

    public void SetBackgroundMode(bool isBackground)
    {
        _isBackground = isBackground;
        _loop?.SetBackgroundMode(isBackground);
    }

    /// <summary>Providers currently faulted, for the diagnostics view and the probe.</summary>
    public IReadOnlyList<(string Name, string? Error)> FaultedProviders() =>
        _providers.Where(static p => p.IsFaulted).Select(static p => (p.Name, p.LastError)).ToArray();

    private void EnsureLoop()
    {
        if (_loop is not null)
        {
            return;
        }

        BuildProviders();

        _loop = new MonitoringLoop(_providers, _history, () => new MonitoringOptions
        {
            Interval = TimeSpan.FromMilliseconds(_settings.UpdateIntervalMs),
            BackgroundMultiplier = _settings.BackgroundIntervalMultiplier,
        });

        _loop.SetBackgroundMode(_isBackground);
        _loop.SnapshotAvailable += OnSnapshot;
    }

    private void OnSnapshot(Snapshot snapshot) => SnapshotAvailable?.Invoke(snapshot);

    private void BuildProviders()
    {
        DisposeProviders();

        // Order matters: CPU, GPU and storage refresh their hardware, and the temperature
        // provider then reads the values they made current instead of updating twice.
        var providers = new IMetricProvider[]
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
