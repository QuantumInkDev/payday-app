using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PayDay.Pages;

/// <summary>
/// Which build is running (version, commit, build time, platform), where the data lives, and links
/// to the privacy policy and source.
/// </summary>
public sealed partial class AboutPage : Page
{
    private readonly BuildInfo _build = BuildInfo.FromAssembly(typeof(App).Assembly);
    private readonly string _architecture = RuntimeInformation.ProcessArchitecture.ToString();
    private readonly string _windows = Environment.OSVersion.Version.ToString();
    private readonly string _runtime = RuntimeInformation.FrameworkDescription;

    public AboutPage()
    {
        InitializeComponent();

        VersionText.Text = _build.Version;
        CommitText.Text = _build.Commit ?? "unknown";
        BuiltText.Text = _build.FormatBuiltAt(TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        ArchitectureText.Text = _architecture;
        WindowsText.Text = _windows;
        RuntimeText.Text = _runtime;
        DataFolderText.Text = ApplicationData.Current.LocalFolder.Path;
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        var lines = _build.DetailLines(_architecture, _windows, _runtime, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, lines));
        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush(); // keep the text after the app closes
            CopiedText.Text = "Copied";
        }
        catch (COMException)
        {
            // Another app has the clipboard open (CLIPBRD_E_CANT_OPEN); don't crash, say so.
            CopiedText.Text = "Couldn't copy. Try again.";
        }
        CopiedText.Visibility = Visibility.Visible;
    }

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchFolderAsync(ApplicationData.Current.LocalFolder);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; the path is shown above for copying by hand.
            System.Diagnostics.Debug.WriteLine($"Open data folder failed: {ex.Message}");
        }
    }
}
