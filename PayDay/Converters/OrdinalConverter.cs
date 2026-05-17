using System;
using Microsoft.UI.Xaml.Data;

namespace PayDay.Converters;

/// <summary>
/// Formats an integer as an English ordinal — 1 → "1st", 2 → "2nd", 3 → "3rd",
/// 11/12/13 → "11th"/"12th"/"13th" (the irregular teens), 21 → "21st", etc.
/// </summary>
public sealed class OrdinalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int i) return ToOrdinal(i);
        if (value is long l) return ToOrdinal((int)l);
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static string ToOrdinal(int n)
    {
        var abs = Math.Abs(n);
        var lastTwo = abs % 100;
        if (lastTwo >= 11 && lastTwo <= 13) return $"{n}th";
        return (abs % 10) switch
        {
            1 => $"{n}st",
            2 => $"{n}nd",
            3 => $"{n}rd",
            _ => $"{n}th",
        };
    }
}
