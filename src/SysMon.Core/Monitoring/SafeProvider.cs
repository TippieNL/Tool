using SysMon.Core.Diagnostics;

namespace SysMon.Core.Monitoring;

/// <summary>
/// Wraps a provider so that no hardware fault can escape into the monitoring loop.
///
/// Behaviour: exceptions are caught and logged once per distinct message; after
/// <see cref="MaxConsecutiveFailures"/> consecutive failures the provider is parked and stops
/// costing anything, and it is re-initialised and retried every <see cref="RetryInterval"/>.
/// A provider that starts working again resets its counters and clears the fault.
/// </summary>
public sealed class SafeProvider : IMetricProvider
{
    public const int MaxConsecutiveFailures = 3;

    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    private readonly IMetricProvider _inner;
    private readonly Func<DateTimeOffset> _clock;

    private int _consecutiveFailures;
    private bool _initialized;
    private DateTimeOffset _parkedUntil;

    public SafeProvider(IMetricProvider inner, Func<DateTimeOffset>? clock = null)
    {
        _inner = inner;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Name => _inner.Name;

    public PollTier Tier => _inner.Tier;

    /// <summary>True when the provider has failed repeatedly and is waiting to be retried.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>Last error message, surfaced in the UI's degraded-state detail.</summary>
    public string? LastError { get; private set; }

    public void Initialize()
    {
        try
        {
            _inner.Initialize();
            _initialized = true;
            ClearFault();
        }
        catch (Exception ex)
        {
            RecordFailure(ex, "initialize");
        }
    }

    public void Poll(SnapshotBuilder builder)
    {
        var now = _clock();

        if (IsFaulted)
        {
            if (now < _parkedUntil)
            {
                builder.ReportFault(Name);
                return;
            }

            // Retry window reached: re-initialise before trying to poll again, since the
            // failure may have been a handle that needs reacquiring (driver restart, GPU reset).
            Log.Info($"Provider '{Name}' retrying after fault.");
            Log.ResetOnce(FaultKey("initialize"));
            Log.ResetOnce(FaultKey("poll"));

            try
            {
                _inner.Initialize();
                _initialized = true;
            }
            catch (Exception ex)
            {
                RecordFailure(ex, "initialize");
                builder.ReportFault(Name);
                return;
            }
        }

        if (!_initialized)
        {
            Initialize();
            if (!_initialized)
            {
                builder.ReportFault(Name);
                return;
            }
        }

        try
        {
            _inner.Poll(builder);

            if (_consecutiveFailures > 0 || IsFaulted)
            {
                Log.Info($"Provider '{Name}' recovered.");
            }

            ClearFault();
        }
        catch (Exception ex)
        {
            RecordFailure(ex, "poll");
            builder.ReportFault(Name);
        }
    }

    public void Dispose()
    {
        try
        {
            _inner.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Provider '{Name}' threw while disposing.", ex);
        }
    }

    private void ClearFault()
    {
        _consecutiveFailures = 0;
        IsFaulted = false;
        LastError = null;
    }

    private void RecordFailure(Exception ex, string stage)
    {
        _consecutiveFailures++;
        LastError = ex.Message;

        Log.Once(FaultKey(stage), LogLevel.Warn, $"Provider '{Name}' failed to {stage}.", ex);

        if (_consecutiveFailures >= MaxConsecutiveFailures && !IsFaulted)
        {
            IsFaulted = true;
            _initialized = false;
            Log.Error($"Provider '{Name}' disabled after {_consecutiveFailures} consecutive failures; retrying in {RetryInterval.TotalSeconds:F0}s.");
        }

        if (IsFaulted)
        {
            _parkedUntil = _clock() + RetryInterval;
        }
    }

    private string FaultKey(string stage) => $"provider:{Name}:{stage}";
}
