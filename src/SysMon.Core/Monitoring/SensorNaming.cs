namespace SysMon.Core.Monitoring;

/// <summary>
/// Decisions about sensor names that affect what the user sees.
///
/// Lives here rather than in the Windows-specific providers so the rules are testable on any
/// platform: which sensors are readings versus configured limits, and which names carry no
/// meaning without their hardware.
/// </summary>
public static class SensorNaming
{
    /// <summary>
    /// Names identifying a configured limit rather than a measurement. Drives expose warning and
    /// critical thresholds as temperature sensors, and listing those beside live readings both
    /// inflates the sensor count and lets a threshold masquerade as the hottest part of the machine.
    /// </summary>
    private static readonly string[] ThresholdMarkers =
        ["warning", "critical", "threshold", "limit", "slowdown", "shutdown", "target"];

    /// <summary>
    /// Names that say nothing on their own. A drive reporting a sensor called "Temperature" is
    /// meaningless in a list mixing CPU, GPU and storage, so it is shown qualified by its hardware.
    /// </summary>
    private static readonly string[] GenericTemperatureNames = ["temperature", "temp"];

    public static bool IsThresholdSensor(string sensorName)
    {
        foreach (var marker in ThresholdMarkers)
        {
            if (sensorName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsGenericTemperatureName(string sensorName)
    {
        foreach (var generic in GenericTemperatureNames)
        {
            if (sensorName.Equals(generic, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
