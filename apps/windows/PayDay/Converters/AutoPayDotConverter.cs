using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PayDay.Converters;

/// <summary>bool AutoPay → green dot if true, amber dot if false (plan §4.8).</summary>
public sealed class AutoPayDotConverter : IValueConverter
{
    private static readonly SolidColorBrush AutoBrush = new(Color.FromArgb(0xFF, 0x00, 0xB8, 0x94));
    private static readonly SolidColorBrush ManualBrush = new(Color.FromArgb(0xFF, 0xFD, 0xCB, 0x6E));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? AutoBrush : ManualBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
