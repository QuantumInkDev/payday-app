using Microsoft.Data.Sqlite;
using PayDay.Services;
using PayDay.Sync.Engine;
using PayDay.Sync.Storage;

namespace PayDay.Sync.Local;

/// <summary>This device's sync settings (never synced): whether local sync is on, and the device ID while it is.</summary>
public sealed record SyncSettings(bool Enabled, string? DeviceId)
{
    public static readonly SyncSettings Off = new(false, null);
}

/// <summary>
/// Local sync on one device: whether it is on, and while it is, the <see cref="SyncReplica"/> over the
/// SQLite sync tables with the <see cref="TableMaterializer"/> that keeps today's tables in step.
/// <para>
/// <b>Off by default.</b> <see cref="Open"/> only reads (a database that never had sync turned on
/// has no sync tables, and none are created); the app's data is not touched until
/// <see cref="TurnOnAsync"/> is called explicitly. No drive and no UI are involved here.
/// </para>
/// </summary>
public sealed class LocalSync : IDisposable
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private SqliteSyncStorage? _storage;

    /// <summary>Local sync for the database behind <paramref name="connectionString"/>, not loaded yet (off). Does no I/O.</summary>
    public LocalSync(string connectionString, TimeProvider? time = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The connection string for the dedicated sync connection to <paramref name="dbPath"/> (private cache, not pooled).</summary>
    public static string ConnectionStringFor(string dbPath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

    /// <summary>
    /// Reads whether sync is on and, if it is, opens the replica (and raises the clock to every HLC this
    /// device stamped, the startup safety net of <c>sync-protocol.md</c> 2.3, which writes only sync
    /// state). With sync off nothing is written and no table is created.
    /// </summary>
    public static LocalSync Open(string connectionString, TimeProvider? time = null)
    {
        var sync = new LocalSync(connectionString, time);
        sync.Load();
        return sync;
    }

    /// <summary>
    /// <see cref="Open"/> for an instance created earlier (the app creates it before the database file
    /// exists and loads it after <c>InitializeAsync</c>). Does nothing when sync is already on.
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            if (IsOn) return;
            var storage = new SqliteSyncStorage(_connectionString);
            try
            {
                if (!storage.IsSyncEnabled())
                {
                    storage.Dispose();
                    return;
                }
                Attach(storage);
                Replica!.RecoverClock();
            }
            catch
            {
                Detach();
                storage.Dispose();
                throw;
            }
        }
    }

    /// <summary>True while local sync is on: writes go through <see cref="Replica"/>.</summary>
    public bool IsOn => Replica is not null;

    public SyncReplica? Replica { get; private set; }

    public TableMaterializer? Materializer { get; private set; }

    public SyncSettings Settings => Replica is { } r ? new SyncSettings(true, r.DeviceId) : SyncSettings.Off;

    /// <summary>
    /// Turns sync on: first writes a JSON backup of today's data through <paramref name="backups"/>
    /// (if that fails, nothing else happens), then runs the one-time migration
    /// (<see cref="LegacyMigration"/>) in one transaction and opens the replica. When sync is already on
    /// this does nothing and returns null.
    /// </summary>
    public async Task<MigrationResult?> TurnOnAsync(BackupRotationService backups, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backups);
        if (IsOn) return null;
        using (var check = new SqliteSyncStorage(_connectionString))
        {
            LegacyMigration.RequireSchemaV2(check);
        }
        await backups.CreateAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (IsOn) return null;
            var storage = new SqliteSyncStorage(_connectionString);
            try
            {
                var materializer = new TableMaterializer(storage);
                var result = LegacyMigration.Run(storage, materializer, _time);
                Attach(storage, materializer);
                return result;
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Turns local sync off: writes go straight to the tables again, which keep their current
    /// (materialized) content. The sync records stay, but turning sync on again starts fresh from the
    /// tables with a new device ID (as <c>sync-protocol.md</c> 12.4 says a rejoin does).
    /// </summary>
    public void TurnOff()
    {
        lock (_gate)
        {
            if (_storage is not { } storage) return;
            storage.RunInTransaction(() => LegacyMigration.SetDeviceSetting(storage, SqliteSyncSchema.EnabledKey, "0"));
            Detach();
        }
    }

    public void Dispose()
    {
        lock (_gate) Detach();
    }

    private void Attach(SqliteSyncStorage storage, TableMaterializer? materializer = null)
    {
        _storage = storage;
        Materializer = materializer ?? new TableMaterializer(storage);
        Replica = new SyncReplica(storage, _time, Materializer);
    }

    private void Detach()
    {
        Replica = null;
        Materializer = null;
        _storage?.Dispose();
        _storage = null;
    }
}
