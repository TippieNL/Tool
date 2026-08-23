using LibreHardwareMonitor.Hardware;
using SysMon.Core.Monitoring;

namespace SysMon.Monitoring.Providers;

/// <summary>
/// Refreshes motherboard and SuperIO hardware so their temperature sensors stay current.
///
/// It publishes nothing itself; the temperature provider picks the values up. It exists purely to
/// keep this comparatively slow update off the fast tier, since board sensors change slowly and
/// polling them every second is wasted work.
/// </summary>
public sealed class MotherboardProvider : IMetricProvider
{
    private readonly HardwareSession _session;

    public MotherboardProvider(HardwareSession session) => _session = session;

    public string Name => "Motherboard";

    public PollTier Tier => PollTier.Slow;

    public void Initialize()
    {
        // Nothing to acquire: hardware is enumerated by the shared session.
    }

    public void Poll(SnapshotBuilder builder)
    {
        foreach (var hardware in _session.GetHardware(HardwareType.Motherboard, HardwareType.SuperIO))
        {
            HardwareSession.Update(hardware);
        }
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}
