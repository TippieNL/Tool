using SysMon.Core.Monitoring;
using Xunit;

namespace SysMon.Core.Tests;

/// <summary>
/// Hardware sensors fail by returning plausible-looking numbers rather than errors, so these
/// tests pin the boundaries that separate a real reading from a failed one.
/// </summary>
public class SensorRangeTests
{
    [Theory]
    [InlineData(45)]
    [InlineData(0.1)]
    [InlineData(150)]
    public void Temperature_AcceptsRealisticReadings(double celsius) =>
        Assert.Equal(celsius, SensorRanges.Temperature.Validate(celsius));

    [Theory]
    [InlineData(-10)]   // Ryzen X-series: a failed read of 0 minus the 10 degree Tctl offset
    [InlineData(0)]     // an unread sensor
    [InlineData(-40)]
    [InlineData(200)]
    public void Temperature_RejectsImpossibleReadings(double celsius) =>
        Assert.Null(SensorRanges.Temperature.Validate(celsius));

    [Fact]
    public void Temperature_RejectsNaNAndInfinity()
    {
        Assert.Null(SensorRanges.Temperature.Validate(double.NaN));
        Assert.Null(SensorRanges.Temperature.Validate(double.PositiveInfinity));
    }

    [Fact]
    public void Power_RejectsZeroBecauseARunningComponentAlwaysDrawsSome() =>
        Assert.Null(SensorRanges.Power.Validate(0));

    [Fact]
    public void Power_AcceptsARealDraw() => Assert.Equal(88.5, SensorRanges.Power.Validate(88.5));

    [Fact]
    public void Clock_RejectsAStoppedClock() => Assert.Null(SensorRanges.Clock.Validate(0));

    [Fact]
    public void Clock_AcceptsATypicalBoostClock() => Assert.Equal(4050d, SensorRanges.Clock.Validate(4050));

    [Fact]
    public void Percent_AcceptsZeroBecauseAnIdleComponentIsGenuinelyAtZero() =>
        Assert.Equal(0d, SensorRanges.Percent.Validate(0));

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percent_RejectsValuesOutsideTheScale(double value) =>
        Assert.Null(SensorRanges.Percent.Validate(value));

    [Fact]
    public void Bytes_AcceptsZeroBecauseAnIdleDiskGenuinelyMovesNone() =>
        Assert.Equal(0d, SensorRanges.Bytes.Validate(0));

    [Fact]
    public void Bytes_RejectsNegativeValues() => Assert.Null(SensorRanges.Bytes.Validate(-1));

    [Fact]
    public void Validate_PassesNullThrough() => Assert.Null(SensorRanges.Temperature.Validate(null));

    [Fact]
    public void ExclusiveMinimum_ExcludesTheBoundItself()
    {
        var range = SensorRange.Above(0, 100);

        Assert.False(range.Contains(0));
        Assert.True(range.Contains(0.5));
    }

    [Fact]
    public void InclusiveMinimum_IncludesTheBoundItself()
    {
        var range = SensorRange.Between(0, 100);

        Assert.True(range.Contains(0));
    }
}
