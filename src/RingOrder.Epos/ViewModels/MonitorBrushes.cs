using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RingOrder.Epos.ViewModels;

/// <summary>
/// Green when a thing is working, red when it is not — read from across a room,
/// which is the only way anyone looks at a machine in a corner.
/// </summary>
public sealed class MonitorBrushes : IValueConverter
{
    public static MonitorBrushes OnOff { get; } = new();

    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#15803d"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#b91c1c"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Good : Bad;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
