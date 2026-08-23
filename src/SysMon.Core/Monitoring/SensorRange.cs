namespace SysMon.Core.Monitoring;

/// <summary>
/// A plausible range for a physical quantity, used to reject nonsense sensor readings.
///
/// Hardware sensors do not fail cleanly. When a driver cannot be reached, the underlying read
/// often returns zero rather than an error, and that zero then travels through vendor-specific
/// correction maths and arrives as a confident-looking number. A Ryzen X-series CPU applies a
/// 10 degree Tctl offset, so a failed read surfaces as -10 degrees; a CPU that is not being read
/// at all reports 0 watts. Both are impossible for a running machine, and presenting them as
/// measurements is worse than admitting the sensor is unavailable.
/// </summary>
public readonly record struct SensorRange
{
    private SensorRange(double minimum, double maximum, bool minimumInclusive)
    {
        Minimum = minimum;
        Maximum = maximum;
        MinimumInclusive = minimumInclusive;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    /// <summary>
    /// Whether the minimum itself is a real reading. Zero load is meaningful; zero watts from a
    /// running CPU is not.
    /// </summary>
    public bool MinimumInclusive { get; }

    public static SensorRange Between(double minimum, double maximum) => new(minimum, maximum, true);

    /// <summary>A range whose lower bound indicates a failed read rather than a real value.</summary>
    public static SensorRange Above(double minimum, double maximum) => new(minimum, maximum, false);

    public bool Contains(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        var aboveMinimum = MinimumInclusive ? value >= Minimum : value > Minimum;
        return aboveMinimum && value <= Maximum;
    }

    /// <summary>Returns the value when plausible, otherwise null so it renders as "N/A".</summary>
    public double? Validate(double? value) => value is { } v && Contains(v) ? v : null;
}

/// <summary>The ranges every provider validates against.</summary>
public static class SensorRanges
{
    /// <summary>
    /// Degrees Celsius. Zero and below is treated as a failed read: a powered machine is never
    /// at or below freezing, and sub-zero values are the signature of an unreachable driver
    /// rather than of a chilled system.
    /// </summary>
    public static readonly SensorRange Temperature = SensorRange.Above(0, 150);

    /// <summary>Watts. A running component never draws zero.</summary>
    public static readonly SensorRange Power = SensorRange.Above(0, 2000);

    /// <summary>Megahertz. A stopped clock means the sensor was not read.</summary>
    public static readonly SensorRange Clock = SensorRange.Above(0, 20_000);

    /// <summary>Percent. Zero is a genuine reading here, so the lower bound is inclusive.</summary>
    public static readonly SensorRange Percent = SensorRange.Between(0, 100);

    /// <summary>Bytes. Zero is genuine (an idle disk, an empty VRAM pool).</summary>
    public static readonly SensorRange Bytes = SensorRange.Between(0, double.MaxValue);
}
