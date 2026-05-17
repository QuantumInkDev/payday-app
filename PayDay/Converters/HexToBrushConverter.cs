using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace PayDay.Converters;

/// <summary>Converts a "#RRGGBB" hex string to a <see cref="SolidColorBrush"/>.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => new SolidColorBrush(TypeToBrushConverter.ParseHex(value as string ?? "#8B8FA3"));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
