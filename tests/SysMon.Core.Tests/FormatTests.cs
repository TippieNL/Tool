using System.Globalization;
using SysMon.Core.Formatting;
using Xunit;

namespace SysMon.Core.Tests;

public class FormatTests
{
    public FormatTests()
    {
        // Pin the culture so decimal separators are predictable regardless of the CI host.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(14172344320, "13.2 GB")]
    public void Bytes_UsesTheLargestSensibleUnit(double value, string expected) =>
        Assert.Equal(expected, Format.Bytes(value));

    [Fact]
    public void Bytes_Null_IsNotAvailable() => Assert.Equal("N/A", Format.Bytes(null));

    [Fact]
    public void Bytes_Negative_IsNotAvailable() => Assert.Equal("N/A", Format.Bytes(-1));

    [Fact]
    public void BytesPerSecond_AppendsRateSuffix() =>
        Assert.Equal("1.0 MB/s", Format.BytesPerSecond(1024 * 1024));

    [Theory]
    [InlineData(42, "42%")]
    [InlineData(0, "0%")]
    [InlineData(100, "100%")]
    [InlineData(150, "100%")]
    [InlineData(-5, "0%")]
    public void Percent_ClampsToRange(double value, string expected) =>
        Assert.Equal(expected, Format.Percent(value));

    [Theory]
    [InlineData(4050, "4.05 GHz")]
    [InlineData(800, "800 MHz")]
    [InlineData(1000, "1.00 GHz")]
    public void Clock_SwitchesToGigahertzAboveAThousand(double mhz, string expected) =>
        Assert.Equal(expected, Format.Clock(mhz));

    [Fact]
    public void Clock_Zero_IsNotAvailable() => Assert.Equal("N/A", Format.Clock(0));

    [Fact]
    public void Temperature_FormatsInCelsius() =>
        Assert.Equal("57°C", Format.Temperature(57, TemperatureUnit.Celsius));

    [Fact]
    public void Temperature_ConvertsToFahrenheit() =>
        Assert.Equal("135°F", Format.Temperature(57, TemperatureUnit.Fahrenheit));

    [Fact]
    public void Temperature_Null_IsNotAvailable() =>
        Assert.Equal("N/A", Format.Temperature(null, TemperatureUnit.Celsius));

    [Fact]
    public void ToFahrenheit_AndBack_RoundTrips() =>
        Assert.Equal(57d, Format.ToCelsius(Format.ToFahrenheit(57)), 6);

    [Fact]
    public void UsedOfTotal_RendersBothHalvesInTheSameUnit() =>
        Assert.Equal("13.2 / 24.0 GB", Format.UsedOfTotal(13.2 * 1024 * 1024 * 1024, 24d * 1024 * 1024 * 1024));

    [Fact]
    public void UsedOfTotal_MissingValue_IsNotAvailable() =>
        Assert.Equal("N/A", Format.UsedOfTotal(null, 100));

    [Theory]
    [InlineData(0, 45, 30, "45m 30s")]
    [InlineData(0, 90, 0, "1h 30m")]
    [InlineData(3, 244, 0, "3d 4h 4m")]
    public void Uptime_DropsEmptyLeadingUnits(int days, int minutes, int seconds, string expected) =>
        Assert.Equal(expected, Format.Uptime(TimeSpan.FromDays(days) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Uptime_Null_IsNotAvailable() => Assert.Equal("N/A", Format.Uptime(null));

    [Fact]
    public void Watts_FormatsWithOneDecimal() => Assert.Equal("88.5 W", Format.Watts(88.5));

    [Fact]
    public void TextOrNotAvailable_TreatsWhitespaceAsMissing() =>
        Assert.Equal("N/A", Format.TextOrNotAvailable("   "));

    [Fact]
    public void Convert_LeavesCelsiusUntouched() =>
        Assert.Equal(57d, Format.Convert(57, TemperatureUnit.Celsius));
}
