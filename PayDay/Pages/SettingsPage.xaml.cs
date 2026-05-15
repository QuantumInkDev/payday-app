using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Services;
using PayDay.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PayDay.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = new SettingsPageViewModel(DatabaseService.Instance, App.Notion);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnPageLoaded;
    }

    private async void OnSaveTokenClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveTokenAsync(TokenBox.Password);
        TokenBox.Password = string.Empty;
    }

    private async void OnClearTokenClick(object sender, RoutedEventArgs e)
        => await ViewModel.ClearTokenAsync();

    private async void OnTestNotionClick(object sender, RoutedEventArgs e)
        => await ViewModel.TestConnectionAsync();

    private async void OnSyncNowClick(object sender, RoutedEventArgs e)
        => await ViewModel.SyncNowAsync();

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void OnSavePayAnchorClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SavePayAnchorAsync();
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex updates the VM via two-way binding; persist + apply live.
        await ViewModel.SaveThemeAsync();
        if (App.MainWindow is MainWindow mw)
        {
            mw.ApplyTheme(ViewModel.SelectedTheme);
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ViewModel.FormatExportFileName(),
        };
        picker.FileTypeChoices.Add("JSON backup", new[] { ".json" });
        InitializePicker(picker);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            var json = await ViewModel.ExportAsync();
            await FileIO.WriteTextAsync(file, json);
            ViewModel.StatusMessage = $"Exported to {file.Name}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".json");
        InitializePicker(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Replace all local data?",
            Content = $"Importing '{file.Name}' will overwrite every bill, payment, snapshot, and setting currently on this machine. There is no undo. Continue?",
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var json = await FileIO.ReadTextAsync(file);
            await ViewModel.ImportAsync(json);
            if (App.MainWindow is MainWindow mw)
            {
                mw.ApplyTheme(ViewModel.SelectedTheme);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not Window window) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
