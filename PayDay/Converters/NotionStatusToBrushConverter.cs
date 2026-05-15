using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using PayDay.Services;

namespace PayDay.Converters;

/// <summary>
/// <see cref="NotionPushStatus"/> → status-dot <see cref="Brush"/>.
/// NotConfigured → gray, Ok → green, Failed → red.
/// </summary>
public sealed class NotionStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value is NotionPushStatus s ? s : NotionPushStatus.NotConfigured;
        var color = status switch
        {
            NotionPushStatus.Ok => Windows.UI.Color.FromArgb(0xFF, 0x00, 0xB8, 0x94),    // green
            NotionPushStatus.Failed => Windows.UI.Color.FromArgb(0xFF, 0xE1, 0x70, 0x55), // red/orange
            _ => Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x8F, 0xA3),                       // gray
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
