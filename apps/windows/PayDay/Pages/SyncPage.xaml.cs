using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PayDay.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PayDay.Pages;

/// <summary>
/// The Sync page. Everything it decides lives in <see cref="SyncPageViewModel"/> (PayDay.Core, unit
/// tested); this code-behind only shows dialogs, saves the recovery phrase file, draws the QR image
/// and runs the one-second pairing timer. The view model lives for the whole run
/// (<see cref="App.SyncPageModel"/>), so leaving the page never loses a recovery phrase on screen or
/// an operation still running; leaving the page closes an open pairing code.
/// </summary>
public sealed partial class SyncPage : Page, ISyncReloadable
{
    private readonly DispatcherTimer _pairingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _consentShowing;

    public SyncPageViewModel ViewModel { get; }

    public SyncPage()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        ViewModel = App.SyncPageModel ?? new SyncPageViewModel(App.SyncCoordinator, App.DriveAccount, App.Notion, dispatch: a => queue.TryEnqueue(() => a()));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        _pairingTimer.Tick += async (_, _) => await ViewModel.TickAsync();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        if (ViewModel.IsPairing) _pairingTimer.Start();
        await ShowQrAsync(ViewModel.PairingQrPng);
    }

    private async void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _pairingTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        // The view model stays (App.SyncPageModel); only an open pairing code is closed.
        await ViewModel.ClosePairingAsync();
    }

    public Task ReloadAfterSyncAsync(bool settingsChanged)
    {
        ViewModel.RefreshStatus();
        return Task.CompletedTask;
    }

    private async void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SyncPageViewModel.Screen):
                if (ViewModel.IsPairing) _pairingTimer.Start();
                else _pairingTimer.Stop();
                if (ViewModel.IsRecoveryPhrase) PhraseSavedBox.IsChecked = false;
                break;
            case nameof(SyncPageViewModel.PairingQrPng):
                await ShowQrAsync(ViewModel.PairingQrPng);
                break;
            case nameof(SyncPageViewModel.IsConsentPending):
                if (ViewModel.IsConsentPending) await AskConsentAsync();
                break;
        }
    }

    private async Task ShowQrAsync(byte[]? png)
    {
        if (png is null)
        {
            QrImage.Source = null;
            return;
        }
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        QrImage.Source = bitmap;
    }

    private async Task AskConsentAsync()
    {
        if (_consentShowing) return;
        _consentShowing = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Replace this PC's data?",
                Content = new TextBlock { Text = ViewModel.ConsentMessage, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Back up and replace",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await App.ShowDialogAsync(dialog) == ContentDialogResult.Primary) await ViewModel.ConfirmReplaceAsync();
            else ViewModel.CancelReplace();
        }
        finally
        {
            _consentShowing = false;
        }
    }

    private async void OnTurnOnClick(object sender, RoutedEventArgs e) => await ViewModel.TurnOnAsync();

    private async void OnCreateVaultClick(object sender, RoutedEventArgs e) => await ViewModel.CreateVaultAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private async void OnJoinWithPhraseClick(object sender, RoutedEventArgs e) => await ViewModel.JoinWithPhraseAsync();

    private async void OnJoinWithCodeClick(object sender, RoutedEventArgs e) => await ViewModel.JoinWithPairingCodeAsync();

    private async void OnUnlockWithPhraseClick(object sender, RoutedEventArgs e) => await ViewModel.UnlockWithPhraseAsync();

    private async void OnUnlockWithCodeClick(object sender, RoutedEventArgs e) => await ViewModel.UnlockWithPairingCodeAsync();

    private async void OnSyncNowClick(object sender, RoutedEventArgs e) => await ViewModel.SyncNowAsync();

    private async void OnAddDeviceClick(object sender, RoutedEventArgs e) => await ViewModel.StartPairingAsync();

    private async void OnClosePairingClick(object sender, RoutedEventArgs e) => await ViewModel.ClosePairingAsync();

    private async void OnRejoinClick(object sender, RoutedEventArgs e) => await ViewModel.RejoinAsync();

    private async void OnSignInAgainClick(object sender, RoutedEventArgs e) => await ViewModel.SignInAgainAsync();

    private void OnStartNewVaultClick(object sender, RoutedEventArgs e) => ViewModel.StartNewVault();

    private async void OnRunNotionHereClick(object sender, RoutedEventArgs e) => await ViewModel.RunNotionHereAsync();

    private void OnPhraseSavedClick(object sender, RoutedEventArgs e) => ViewModel.PhraseSaved = PhraseSavedBox.IsChecked == true;

    private async void OnFinishPhraseClick(object sender, RoutedEventArgs e) => await ViewModel.FinishRecoveryPhraseAsync();

    private void OnShowPhraseAgainClick(object sender, RoutedEventArgs e) => ViewModel.ShowRecoveryPhraseAgain();

    private async void OnSavePhraseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RecoveryPhraseFileText is not { } text) return;
        var advice = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Where to keep the phrase",
            Content = new TextBlock { Text = SyncPageViewModel.PhraseFileAdvice, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Choose a place…",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await App.ShowDialogAsync(advice) != ContentDialogResult.Primary) return;

        // Not Documents or Desktop: OneDrive usually backs those up, next to the vault.
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            SuggestedFileName = "FutureCheck recovery phrase",
        };
        picker.FileTypeChoices.Add("Text file", new[] { ".txt" });
        if (App.MainWindow is Window window)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (SyncPageViewModel.IsInFolder(file.Path, SyncPageViewModel.OneDriveFolders(Environment.GetEnvironmentVariable)))
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "That folder is in OneDrive",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "OneDrive backs this folder up to the same account as your vault, so anyone who gets into that account would have both your data and its key. Choose a USB drive or another folder instead.",
                },
                PrimaryButtonText = "Save here anyway",
                CloseButtonText = "Choose another place",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await App.ShowDialogAsync(confirm) != ContentDialogResult.Primary)
            {
                await DeleteIfEmptyAsync(file);
                return;
            }
        }
        try
        {
            await FileIO.WriteTextAsync(file, text);
            ViewModel.InfoMessage = $"Saved to {file.Name}. Keep it somewhere private.";
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"The file could not be saved: {ex.Message}";
        }
    }

    /// <summary>The picker may have created an empty file; never delete one that has content.</summary>
    private static async Task DeleteIfEmptyAsync(StorageFile file)
    {
        try
        {
            var props = await file.GetBasicPropertiesAsync();
            if (props.Size == 0) await file.DeleteAsync();
        }
        catch (Exception)
        {
        }
    }

    private async void OnTurnOffClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Turn off sync on this PC?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This PC stops syncing, forgets the vault key and signs out of OneDrive. Your data stays on this PC, "
                    + "and your vault stays on OneDrive for your other devices. Changes made on this PC from now on stay here. "
                    + "To sync again later you join the vault with your recovery phrase or a pairing code, and this PC's data is then replaced by the vault's (after a backup).",
            },
            PrimaryButtonText = "Turn off sync",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await App.ShowDialogAsync(dialog) == ContentDialogResult.Primary) await ViewModel.TurnOffAsync();
    }
}
