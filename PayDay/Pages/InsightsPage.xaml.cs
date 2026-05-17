using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Services;
using PayDay.ViewModels;
using SkiaSharp;

namespace PayDay.Pages;

public sealed partial class InsightsPage : Page
{
    public InsightsPageViewModel ViewModel { get; }

    public InsightsPage()
    {
        ViewModel = new InsightsPageViewModel(DatabaseService.Instance, App.Notion, App.Backups);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        RebuildCharts();
    }

    private async void OnSaveSnapshotClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveSnapshotAsync();
        RebuildCharts();
    }

    private void RebuildCharts()
    {
        HistoryChart.Series = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = ViewModel.History.Select(p => p.TotalRemaining).ToArray(),
                Name = "Total Remaining",
                GeometrySize = 8,
                Stroke = new SolidColorPaint(new SKColor(0x6C, 0x5C, 0xE7), 2),
                Fill = new SolidColorPaint(new SKColor(0x6C, 0x5C, 0xE7, 40)),
                GeometryStroke = new SolidColorPaint(new SKColor(0x6C, 0x5C, 0xE7), 2),
                GeometryFill = new SolidColorPaint(SKColors.White),
            },
        };
        HistoryChart.XAxes = new[]
        {
            new Axis
            {
                Labels = ViewModel.History.Select(p => p.Date.ToString("MMM d")).ToList(),
                LabelsRotation = -25,
                TextSize = 11,
            },
        };
        HistoryChart.YAxes = new[]
        {
            new Axis
            {
                Labeler = v => v.ToString("C0", System.Globalization.CultureInfo.CurrentCulture),
                TextSize = 11,
            },
        };

        BreakdownChart.Series = ViewModel.TypeBreakdown
            .Select(entry => new PieSeries<double>
            {
                Values = new[] { entry.TotalPayment },
                Name = entry.Type,
                Fill = new SolidColorPaint(ColorForType(entry.Type)),
                InnerRadius = 60,
                MaxRadialColumnWidth = 60,
            })
            .Cast<ISeries>()
            .ToArray();
    }

    /// <summary>Maps bill type → SKColor by reading the user-customized hex from <see cref="TypeColorService"/>.</summary>
    private static SKColor ColorForType(string type)
    {
        var c = Converters.TypeToBrushConverter.ParseHex(TypeColorService.GetHex(type));
        return new SKColor(c.R, c.G, c.B);
    }
}
