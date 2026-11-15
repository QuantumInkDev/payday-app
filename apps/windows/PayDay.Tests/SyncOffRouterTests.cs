using System.IO;
using System.Runtime.CompilerServices;
using PayDay.Services;
using PayDay.Sync.Local;

namespace PayDay.Tests;

/// <summary>
/// The app always talks to the sync write path (<see cref="SyncRoutingDatabaseService"/>), even with
/// sync off. These classes run every Notion, auto-sync and Settings-Notion test again with the fake
/// database behind that router and local sync off: the connector must behave exactly as it does over
/// the plain database (PR #6 review). The local sync instance is never loaded, so it touches no file.
/// </summary>
internal static class SyncOffRouterTests
{
    private static readonly ConditionalWeakTable<FakeDatabaseService, SyncRoutingDatabaseService> Routers = new();

    /// <summary>One router per fake, so the services and view models of one test share it, as in the app.</summary>
    public static IDatabaseService Wrap(FakeDatabaseService db)
        => Routers.GetValue(db, fake => new SyncRoutingDatabaseService(
            fake,
            new LocalSync(LocalSync.ConnectionStringFor(Path.Combine(Path.GetTempPath(), "payday-sync-off-never-opened.db")))));
}

public sealed class NotionSyncServiceThroughSyncOffRouterTests : NotionSyncServiceTests
{
    private protected override IDatabaseService Wrap(FakeDatabaseService db) => SyncOffRouterTests.Wrap(db);
}

public sealed class AutoSyncThroughSyncOffRouterTests : AutoSyncTests
{
    private protected override IDatabaseService Wrap(FakeDatabaseService db) => SyncOffRouterTests.Wrap(db);
}

public sealed class SettingsPageNotionThroughSyncOffRouterTests : SettingsPageNotionTests
{
    private protected override IDatabaseService Wrap(FakeDatabaseService db) => SyncOffRouterTests.Wrap(db);
}
