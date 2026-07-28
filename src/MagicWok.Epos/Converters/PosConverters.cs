using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MagicWok.Epos.Converters;

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
