using System.Globalization;

namespace SysMon.Core.Formatting;

public enum TemperatureUnit
{
    Celsius = 0,
    Fahrenheit = 1,
}

/// <summary>
/// Display formatting. Every helper takes a nullable value and returns "N/A" for null, which is
/// how a missing sensor reaches the screen without any special-casing in the view models.
/// </summary>
public static class Format
{
    public const string NotAvailable = "N/A";

    private const double Kilo = 1024d;

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats bytes with a sensible unit, e.g. "13.2 GB".</summary>
    public static string Bytes(double? value, int decimals = 1)
    {
        if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v) || v < 0)
        {
            return NotAvailable;
        }

        var unit = 0;
        while (v >= Kilo && unit < ByteUnits.Length - 1)
        {
            v /= Kilo;
            unit++;
        }

        // Whole bytes and kilobytes read better without a decimal point.
        var places = unit <= 1 ? 0 : decimals;
        return string.Create(CultureInfo.CurrentCulture, $"{Math.Round(v, places).ToString($"F{places}", CultureInfo.CurrentCulture)} {ByteUnits[unit]}");
    }

    /// <summary>Formats a throughput, e.g. "12.4 MB/s".</summary>
    public static string BytesPerSecond(double? value)
    {
        if (value is not { } v || double.IsNaN(v) || v < 0)
        {
            return NotAvailable;
        }

        return Bytes(v) + "/s";
    }

    /// <summary>Formats a 0-100 percentage, e.g. "42%".</summary>
    public static string Percent(double? value, int decimals = 0)
    {
        if (value is not { } v || double.IsNaN(v))
        {
            return NotAvailable;
        }

        return Math.Round(Math.Clamp(v, 0, 100), decimals).ToString($"F{decimals}", CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>Formats a clock in MHz as GHz once it passes 1000, e.g. "4.05 GHz".</summary>
    public static string Clock(double? megahertz)
    {
        if (megahertz is not { } mhz || double.IsNaN(mhz) || mhz <= 0)
        {
            return NotAvailable;
        }

        return mhz >= 1000
            ? (mhz / 1000d).ToString("F2", CultureInfo.CurrentCulture) + " GHz"
            : Math.Round(mhz).ToString("F0", CultureInfo.CurrentCulture) + " MHz";
    }

    /// <summary>Converts and formats a Celsius reading in the user's chosen unit, e.g. "57°C".</summary>
    public static string Temperature(double? celsius, TemperatureUnit unit)
    {
        if (celsius is not { } c || double.IsNaN(c))
        {
            return NotAvailable;
        }

        return unit == TemperatureUnit.Fahrenheit
            ? Math.Round(ToFahrenheit(c)).ToString("F0", CultureInfo.CurrentCulture) + "°F"
            : Math.Round(c).ToString("F0", CultureInfo.CurrentCulture) + "°C";
    }

    public static double ToFahrenheit(double celsius) => (celsius * 9d / 5d) + 32d;

    public static double ToCelsius(double fahrenheit) => (fahrenheit - 32d) * 5d / 9d;

    /// <summary>Converts a Celsius value into the display unit without formatting it.</summary>
    public static double? Convert(double? celsius, TemperatureUnit unit) =>
        celsius is not { } c ? null : unit == TemperatureUnit.Fahrenheit ? ToFahrenheit(c) : c;

    public static string UnitSuffix(TemperatureUnit unit) => unit == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    public static string Watts(double? value)
    {
        if (value is not { } v || double.IsNaN(v) || v < 0)
        {
            return NotAvailable;
        }

        return v.ToString("F1", CultureInfo.CurrentCulture) + " W";
    }

    /// <summary>Formats a "used / total" pair, e.g. "13.2 / 24.0 GB".</summary>
    public static string UsedOfTotal(double? used, double? total)
    {
        if (used is null || total is null)
        {
            return NotAvailable;
        }

        // Render both halves in the larger value's unit so the pair reads consistently.
        var unit = 0;
        var scale = total.Value;
        while (scale >= Kilo && unit < ByteUnits.Length - 1)
        {
            scale /= Kilo;
            unit++;
        }

        var divisor = Math.Pow(Kilo, unit);
        var places = unit <= 1 ? 0 : 1;

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{(used.Value / divisor).ToString($"F{places}", CultureInfo.CurrentCulture)} / {scale.ToString($"F{places}", CultureInfo.CurrentCulture)} {ByteUnits[unit]}");
    }

    /// <summary>Formats an uptime as "3d 4h 12m", dropping empty leading units.</summary>
    public static string Uptime(TimeSpan? value)
    {
        if (value is not { } t || t < TimeSpan.Zero)
        {
            return NotAvailable;
        }

        if (t.TotalDays >= 1)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m");
        }

        return t.TotalHours >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)t.TotalHours}h {t.Minutes}m")
            : string.Create(CultureInfo.CurrentCulture, $"{t.Minutes}m {t.Seconds}s");
    }

    public static string TextOrNotAvailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? NotAvailable : value;
}
