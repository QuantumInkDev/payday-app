using System.Globalization;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Models;
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

    private async void OnManageSnapshotsClick(object sender, RoutedEventArgs e)
    {
        // Build the list inline — a ListView-in-a-ContentDialog is simpler than
        // a dedicated managing page and stays out of the way once dismissed.
        var listView = new ListView
        {
            MaxHeight = 360,
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = ViewModel.SnapshotsList,
            ItemTemplate = (DataTemplate)Resources["SnapshotRowTemplate"],
        };
        var clearAllButton = new Button
        {
            Content = "Clear all snapshots",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        clearAllButton.Click += async (_, _) =>
        {
            var confirm = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Clear every snapshot?",
                Content = $"This will delete all {ViewModel.SnapshotsList.Count} snapshot(s). There is no undo.",
                PrimaryButtonText = "Clear all",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ClearAllSnapshotsAsync();
            RebuildCharts();
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(clearAllButton);
        content.Children.Add(listView);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Manage snapshots",
            Content = content,
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
        RebuildCharts();
    }

    private async void OnDeleteSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long id)
        {
            await ViewModel.DeleteSnapshotAsync(id);
        }
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
