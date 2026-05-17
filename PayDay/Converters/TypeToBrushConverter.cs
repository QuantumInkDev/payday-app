using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using PayDay.Services;
using Windows.UI;

namespace PayDay.Converters;

public sealed class TypeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var type = value as string ?? string.Empty;
        var hex = TypeColorService.GetHex(type);
        return new SolidColorBrush(ParseHex(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    /// <summary>Parses a "#RRGGBB" string into an opaque <see cref="Color"/>, falling back to gray on malformed input.</summary>
    internal static Color ParseHex(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromArgb(0xFF, r, g, b);
        }
        return Colors.Gray;
    }
}
