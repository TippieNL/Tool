using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SysMon.App.Converters;

/// <summary>Collapses an element when the bound value is null, empty or false.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    /// <summary>Set to invert the test, i.e. show when the value is absent.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            double d => d > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a 0-100 value onto the ok/warn/critical palette, so load bars and temperature readouts
/// change colour as they climb without any per-card logic.
/// </summary>
public sealed class ThresholdBrushConverter : IValueConverter
{
    public double WarnAt { get; set; } = 75;

    public double CriticalAt { get; set; } = 90;

    public Brush NormalBrush { get; set; } = Brushes.DodgerBlue;

    public Brush WarnBrush { get; set; } = Brushes.Orange;

    public Brush CriticalBrush { get; set; } = Brushes.OrangeRed;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double d || double.IsNaN(d))
        {
            return NormalBrush;
        }

        return d >= CriticalAt ? CriticalBrush : d >= WarnAt ? WarnBrush : NormalBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Equality test used to bind radio buttons and tabs to an enum-valued property.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
