using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace PayDay.Converters;

public sealed class TypeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var type = value as string ?? string.Empty;
        var key = type switch
        {
            "Cards"         => "TypePillCards",
            "Bills"         => "TypePillBills",
            "Loans"         => "TypePillLoans",
            "Subscriptions" => "TypePillSubscriptions",
            "Business"      => "TypePillBusiness",
            "People"        => "TypePillPeople",
            "Medical"       => "TypePillMedical",
            _               => "TypePillOther",
        };
        return Application.Current.Resources[key] as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
