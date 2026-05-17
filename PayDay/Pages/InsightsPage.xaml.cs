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

    /// <summary>Maps bill type → SKColor mirroring the pill brushes in TypeBrushes.xaml.</summary>
    private static SKColor ColorForType(string type) => type switch
    {
        "Cards" => new SKColor(0xFD, 0x79, 0xA8),
        "Bills" => new SKColor(0xB2, 0x94, 0x5B),
        "Loans" => new SKColor(0x6C, 0x5C, 0xE7),
        "Subscriptions" => new SKColor(0xE1, 0x70, 0x55),
        "Business" => new SKColor(0x74, 0xB9, 0xFF),
        "People" => new SKColor(0x00, 0xB8, 0x94),
        "Medical" => new SKColor(0x55, 0xEF, 0xC4),
        _ => new SKColor(0x8B, 0x8F, 0xA3),
    };
}
