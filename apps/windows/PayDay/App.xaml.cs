using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PayDay.Services;
using PayDay.Sync.Coordination;
using PayDay.Sync.Drive;
using PayDay.Sync.Drive.OneDrive;
using PayDay.Sync.Keys;
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
    /// the database is initialized) only reads. Only the Sync page's buttons turn it on.
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

    /// <summary>The OneDrive account (sign in / out). Not configured in a build without a client ID.</summary>
    public static IDriveAccount? DriveAccount { get; private set; }

    /// <summary>The sync loop against OneDrive; null when this build has no OneDrive client ID.</summary>
    public static SyncCoordinator? SyncCoordinator { get; private set; }

    /// <summary>When to sync (launch, foreground, 5 s after an edit, every 5 minutes, "Sync now").</summary>
    public static SyncScheduler? SyncScheduler { get; private set; }

    private static OneDriveDriveProvider? _drive;

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
        // Merged changes from another device (raised after each merge committed, off the UI thread).
        Sync.DataChanged += OnSyncDataChanged;
        await StartSyncLoopAsync();

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>
    /// Builds the OneDrive provider, the coordinator and the scheduler, and starts the scheduler. With
    /// sync off each scheduled sync returns at once without touching the network or the database, so
    /// the app behaves exactly as before until the user turns sync on from the Sync page.
    /// </summary>
    private static async Task StartSyncLoopAsync()
    {
        try
        {
            var clientId = OneDriveConfig.Resolve(
                Environment.GetEnvironmentVariable(OneDriveConfig.EnvironmentVariable),
                await DatabaseService.Instance.GetSettingAsync(OneDriveConfig.SettingKey));
            var auth = new OneDriveAuth(clientId, Credentials);
            DriveAccount = auth;
            if (!auth.IsConfigured) return;
            _drive = new OneDriveDriveProvider(auth);
            var coordinator = new SyncCoordinator(
                Sync,
                _drive,
                new CredentialVaultKeyStore(Credentials),
                Backups,
                DatabaseService.Instance,
                new DeviceInfo(Environment.MachineName, "windows", AppVersion()),
                () => Notion.HasToken());
            var scheduler = new SyncScheduler(ct => coordinator.SyncAsync(ct));
            scheduler.Attach(Sync);
            SyncCoordinator = coordinator;
            SyncScheduler = scheduler;
            scheduler.NotifyLaunch();
            scheduler.Start();
        }
        catch (Exception)
        {
            // Sync is unavailable in this run; everything else works as before.
            SyncCoordinator = null;
            SyncScheduler = null;
        }
    }

    /// <summary>The window came to (or left) the foreground: snapshots are written only in the foreground, and returning syncs.</summary>
    public static void NotifyForeground(bool foreground)
    {
        if (SyncCoordinator is { } coordinator) coordinator.InForeground = foreground;
        SyncScheduler?.NotifyForeground(foreground);
    }

    /// <summary>The window is closing: stop the background loop (a sync in flight is safe to abandon; every local step is transactional).</summary>
    public static void StopSyncLoop()
    {
        if (SyncScheduler is not { } scheduler) return;
        try
        {
            scheduler.StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }
    }

    private static string? AppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch (Exception)
        {
            return null; // not running packaged
        }
    }

    private static async void OnSyncDataChanged(object? sender, SyncDataChangedEventArgs e)
    {
        if (MainWindow is MainWindow window)
        {
            // Pages and the type colours reload on the UI thread, coalesced.
            window.DispatcherQueue.TryEnqueue(() => window.ScheduleSyncReload(e.SettingsChanged || e.Everything));
            return;
        }
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
