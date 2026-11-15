using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PayDay.Services;
using PayDay.Sync.Local;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PayDay;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>The single top-level window. Exposed so file pickers can call WinRT InitializeWithWindow.</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>Process-wide credential store backed by Windows Credential Manager.</summary>
    public static ICredentialStore Credentials { get; } = new WindowsCredentialStore();

    // Static initializers run in textual order: Sync and Backups before Database, Database before Notion.

    /// <summary>
    /// Local sync. Off unless it was turned on in this database; <see cref="LocalSync.Load"/> (after
    /// the database is initialized) only reads. Nothing in the app turns it on yet.
    /// </summary>
    public static LocalSync Sync { get; } = new(LocalSync.ConnectionStringFor(DatabaseService.Instance.DbPath));

    /// <summary>Process-wide auto-backup rotation service writing to <c>LocalFolder/backups/</c>. Reads the tables directly.</summary>
    public static BackupRotationService Backups { get; } = new(DatabaseService.Instance, new WindowsBackupStore());

    /// <summary>
    /// The database every page and service uses: passes straight through to the tables while sync is
    /// off, and sends writes through the sync engine while it is on.
    /// </summary>
    public static IDatabaseService Database { get; } = new SyncRoutingDatabaseService(DatabaseService.Instance, Sync, Backups);

    /// <summary>Process-wide Notion sync service. Disposed when the app shuts down.</summary>
    public static NotionSyncService Notion { get; } = new(Database, Credentials);

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        await DatabaseService.Instance.InitializeAsync();
        Sync.Load();
        await TypeColorService.LoadAsync(Database);
        // Colours merged from another device (raised after the merge committed, off the UI thread).
        Sync.DataChanged += OnSyncDataChanged;

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static async void OnSyncDataChanged(object? sender, SyncDataChangedEventArgs e)
    {
        if (!e.SettingsChanged) return;
        try
        {
            await TypeColorService.LoadAsync(Database);
        }
        catch (Exception)
        {
            // A failed reload keeps the colours already loaded; the next change or start reloads them.
        }
    }
}
