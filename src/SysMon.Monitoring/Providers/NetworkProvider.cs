using System.Net.NetworkInformation;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.Models;
using SysMon.Core.Monitoring;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Throughput and totals for the active network adapter.
///
/// Rates are computed from byte-counter deltas divided by measured elapsed time rather than the
/// nominal interval, so a tick that arrives late reports the correct speed instead of an inflated one.
/// </summary>
public sealed class NetworkProvider : IMetricProvider
{
    private readonly Func<AppSettings> _settings;
    private readonly List<string> _availableInterfaces = [];

    private NetworkInterface? _current;
    private DateTime _lastSampleUtc;
    private long _lastReceived;
    private long _lastSent;
    private bool _hasBaseline;

    private long _sessionReceived;
    private long _sessionSent;

    private DateTime _lastEnumerationUtc;

    public NetworkProvider(Func<AppSettings> settings) => _settings = settings;

    public string Name => "Network";

    public PollTier Tier => PollTier.Fast;

    public void Initialize()
    {
        _hasBaseline = false;
        _current = null;
        _lastEnumerationUtc = DateTime.MinValue;
        SelectInterface();
    }

    public void Poll(SnapshotBuilder builder)
    {
        // Re-enumerating adapters costs real time, so it happens every few seconds rather than
        // every tick. That is still fast enough to notice a cable being plugged in.
        if (DateTime.UtcNow - _lastEnumerationUtc > TimeSpan.FromSeconds(5))
        {
            SelectInterface();
        }

        if (_current is null)
        {
            builder.Network = new NetworkSnapshot { AvailableInterfaces = _availableInterfaces.ToArray() };
            return;
        }

        IPInterfaceStatistics stats;
        try
        {
            stats = _current.GetIPStatistics();
        }
        catch (NetworkInformationException ex)
        {
            // The adapter went away between selection and reading.
            Log.Once($"network:{_current.Id}", LogLevel.Debug, $"Lost statistics for adapter '{_current.Name}'.", ex);
            _current = null;
            _hasBaseline = false;
            builder.Network = new NetworkSnapshot { AvailableInterfaces = _availableInterfaces.ToArray() };
            return;
        }

        var now = DateTime.UtcNow;
        var received = stats.BytesReceived;
        var sent = stats.BytesSent;

        double? downRate = null;
        double? upRate = null;

        if (_hasBaseline)
        {
            var seconds = (now - _lastSampleUtc).TotalSeconds;

            if (seconds > 0.05)
            {
                var receivedDelta = received - _lastReceived;
                var sentDelta = sent - _lastSent;

                // A negative delta means the counter reset (adapter restarted or wrapped).
                // Skip that one sample rather than reporting a nonsensical spike.
                if (receivedDelta >= 0 && sentDelta >= 0)
                {
                    downRate = receivedDelta / seconds;
                    upRate = sentDelta / seconds;

                    _sessionReceived += receivedDelta;
                    _sessionSent += sentDelta;
                }
            }
            else
            {
                // Too little time passed to measure meaningfully; keep the previous baseline.
                return;
            }
        }

        _lastSampleUtc = now;
        _lastReceived = received;
        _lastSent = sent;
        _hasBaseline = true;

        builder.Network = new NetworkSnapshot
        {
            InterfaceName = _current.Name,
            InterfaceDescription = _current.Description,
            DownloadBytesPerSecond = downRate,
            UploadBytesPerSecond = upRate,
            TotalDownloadedBytes = received,
            TotalUploadedBytes = sent,
            SessionDownloadedBytes = _sessionReceived,
            SessionUploadedBytes = _sessionSent,
            LinkSpeedBps = SafeLinkSpeed(_current),
            AvailableInterfaces = _availableInterfaces.ToArray(),
        };
    }

    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>
    /// Picks the adapter to report on: the user's choice when it is present and up, otherwise the
    /// operational adapter that has moved the most traffic, which is reliably the one carrying
    /// real connectivity rather than a virtual or tunnelling adapter.
    /// </summary>
    private void SelectInterface()
    {
        _lastEnumerationUtc = DateTime.UtcNow;

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            Log.Once("network:enumerate", LogLevel.Warn, "Could not enumerate network adapters.", ex);
            return;
        }

        _availableInterfaces.Clear();

        NetworkInterface? best = null;
        long bestTraffic = -1;

        foreach (var candidate in interfaces)
        {
            if (candidate.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                candidate.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                candidate.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            _availableInterfaces.Add(candidate.Name);

            long traffic;
            try
            {
                var stats = candidate.GetIPStatistics();
                traffic = stats.BytesReceived + stats.BytesSent;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            if (traffic > bestTraffic)
            {
                bestTraffic = traffic;
                best = candidate;
            }
        }

        var preferred = _settings().PreferredNetworkInterface;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            foreach (var candidate in interfaces)
            {
                if (candidate.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase) &&
                    candidate.OperationalStatus == OperationalStatus.Up)
                {
                    best = candidate;
                    break;
                }
            }
        }

        if (best is null)
        {
            _current = null;
            _hasBaseline = false;
            return;
        }

        // Switching adapters invalidates the counter baseline: the new adapter's totals are
        // unrelated to the old one's, and diffing across them would report a huge false spike.
        if (_current is null || !string.Equals(_current.Id, best.Id, StringComparison.Ordinal))
        {
            _hasBaseline = false;
            Log.Info($"Monitoring network adapter '{best.Name}'.");
        }

        _current = best;
    }

    private static double? SafeLinkSpeed(NetworkInterface adapter)
    {
        try
        {
            return adapter.Speed > 0 ? adapter.Speed : null;
        }
        catch (Exception)
        {
            // Some virtual adapters throw rather than report a speed.
            return null;
        }
    }
}
