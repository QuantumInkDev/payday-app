using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace PayDay.Converters;

public sealed class CurrencyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d) return d.ToString("C", CultureInfo.CurrentCulture);
        if (value is decimal m) return m.ToString("C", CultureInfo.CurrentCulture);
        if (value is float f) return f.ToString("C", CultureInfo.CurrentCulture);
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
