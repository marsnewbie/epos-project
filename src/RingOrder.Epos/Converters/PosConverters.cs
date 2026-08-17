using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RingOrder.Epos.Converters;

public sealed class EqualToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EqualToBrushConverter : IValueConverter
{
    public IBrush MatchBrush { get; set; } = new SolidColorBrush(Color.Parse("#c2410c"));
    public IBrush DefaultBrush { get; set; } = new SolidColorBrush(Color.Parse("#292524"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? MatchBrush : DefaultBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullOrEmptyToBoolConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        return Invert ? !empty : empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Sent kitchen lines appear dimmed (0.55), new lines full opacity.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static BoolToOpacityConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.55 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A shift that did not balance is red; one that did is the ordinary text
/// colour. Red means "broken or wrong" everywhere on this till — see
/// INTERFACE.md — and a drawer that is out is exactly that.
/// </summary>
public sealed class BoolToVarianceBrush : IValueConverter
{
    public static BoolToVarianceBrush Instance { get; } = new();

    private static readonly IBrush Out = new SolidColorBrush(Color.Parse("#b91c1c"));
    private static readonly IBrush Balanced = new SolidColorBrush(Color.Parse("#57534e"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Out : Balanced;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InvertBoolConverter : IValueConverter
{
    public static InvertBoolConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
