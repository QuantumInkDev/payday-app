using System.Globalization;
using Microsoft.Data.Sqlite;
using PayDay.Services;
using PayDay.Sync.Engine;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Storage;

namespace PayDay.Sync.Local;

/// <summary>This device's sync settings (never synced): whether local sync is on, and the device ID while it is.</summary>
public sealed record SyncSettings(bool Enabled, string? DeviceId)
{
    public static readonly SyncSettings Off = new(false, null);
}

/// <summary>How <see cref="LocalSync.TurnOnAsync"/> ended.</summary>
public enum TurnOnStatus
{
    /// <summary>The backup was written, the migration ran and sync is on.</summary>
    TurnedOn,

    /// <summary>Sync was already on; nothing happened.</summary>
    AlreadyOn,

    /// <summary>Some rows cannot be migrated (<see cref="TurnOnResult.Problems"/>); nothing was written, not even the backup.</summary>
    Blocked,
}

/// <summary>
/// The result of turning sync on: the migration's summary and the backup file when it ran, or the rows
/// the user has to fix first when it could not (a payment period key or snapshot date that is not a
/// date, two bills that map to one ID). The UI lists <see cref="Problems"/> instead of an exception.
/// </summary>
public sealed record TurnOnResult(TurnOnStatus Status, MigrationResult? Migration, string? BackupFileName, IReadOnlyList<MigrationProblem> Problems)
{
    public bool IsOn => Status is TurnOnStatus.TurnedOn or TurnOnStatus.AlreadyOn;
}

/// <summary>Rows changed by merging another device's files (or by joining a vault); raised after the transaction committed.</summary>
public sealed class SyncDataChangedEventArgs : EventArgs
{
    public SyncDataChangedEventArgs(IReadOnlyCollection<RowKey> rows, bool everything = false)
    {
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        Everything = everything;
    }

    public IReadOnlyCollection<RowKey> Rows { get; }

    /// <summary>Every table was rebuilt (joining a vault): reload everything.</summary>
    public bool Everything { get; }

    /// <summary>True when any row of <paramref name="entity"/> changed.</summary>
    public bool Affects(string entity) => Everything || Rows.Any(r => r.Entity == entity);

    /// <summary>Synced settings changed (type colours, pay anchor, …): <c>TypeColorService</c> and the settings page reload.</summary>
    public bool SettingsChanged => Affects(SyncEntities.Setting);
}

/// <summary>
/// Proof that the caller holds <see cref="LocalSync"/>'s write gate: no app write runs, and sync cannot
/// be turned on, off or joined by anyone else, until it is disposed.
/// </summary>
public sealed class SyncWriteLease : IDisposable
{
    private SemaphoreSlim? _gate;

    internal SyncWriteLease(LocalSync owner, SemaphoreSlim gate)
    {
        Owner = owner;
        _gate = gate;
    }

    internal LocalSync Owner { get; }

    internal bool IsHeld => _gate is not null;

    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
}

/// <summary>
/// Local sync on one device: whether it is on, and while it is, the <see cref="SyncReplica"/> over the
/// SQLite sync tables with the <see cref="TableMaterializer"/> that keeps today's tables in step.
/// <para>
/// <b>Off by default.</b> <see cref="Open"/> only reads (a database that never had sync turned on
/// has no sync tables, and none are created); the app's data is not touched until
/// <see cref="TurnOnAsync"/> is called explicitly.
/// </para>
/// <para>
/// <b>Write gate.</b> Every app write (<see cref="SyncRoutingDatabaseService"/>) and every local step
/// of a sync cycle holds the gate (<see cref="EnterWriteAsync"/>). Turning sync on or off and joining a
/// vault hold it for their whole duration, the backup included, so no write can land between the
/// backup and the migration, or on the tables while they are being rewritten.
/// </para>
/// </summary>
public sealed class LocalSync : IDisposable
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SqliteSyncStorage? _storage;

    /// <summary>Local sync for the database behind <paramref name="connectionString"/>, not loaded yet (off). Does no I/O.</summary>
    public LocalSync(string connectionString, TimeProvider? time = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after merged remote changes (or a join) committed; never inside a transaction.</summary>
    public event EventHandler<SyncDataChangedEventArgs>? DataChanged;

    /// <summary>Raised after an app write went through the sync engine (the scheduler syncs 5 seconds later).</summary>
    public event EventHandler? LocalEdited;

    /// <summary>The connection string for the dedicated sync connection to <paramref name="dbPath"/> (private cache, not pooled).</summary>
    public static string ConnectionStringFor(string dbPath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

    public TimeProvider Time => _time;

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
                // Sync tables added by a later version (new tables only).
                storage.RunInTransaction(storage.CreateSchema);
                Attach(storage, new TableMaterializer(storage));
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

    // ---- Write gate -----------------------------------------------------------------------------------

    /// <summary>Waits for the write gate (see the type's remarks). Dispose the lease to release it.</summary>
    public async Task<SyncWriteLease> EnterWriteAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        return new SyncWriteLease(this, _writeGate);
    }

    private async Task<(SyncWriteLease Lease, bool Owned)> LeaseAsync(SyncWriteLease? held, CancellationToken ct)
    {
        if (held is not null)
        {
            if (held.Owner != this || !held.IsHeld) throw new ArgumentException("That lease is not held on this LocalSync.", nameof(held));
            return (held, false);
        }
        return (await EnterWriteAsync(ct).ConfigureAwait(false), true);
    }

    // ---- Turning sync on and off ------------------------------------------------------------------------

    /// <summary>
    /// Turns sync on, holding the write gate throughout: checks that every row can be migrated (if not,
    /// returns <see cref="TurnOnStatus.Blocked"/> with the rows and writes nothing), writes a JSON backup
    /// of today's data under a name rotation never deletes
    /// (<see cref="BackupRotationService.CreatePreSyncAsync"/>; if that fails, nothing else happens),
    /// then runs the one-time migration (<see cref="LegacyMigration"/>) in one transaction and opens the
    /// replica. When sync is already on this does nothing.
    /// </summary>
    /// <param name="held">The caller's lease when it already holds the write gate (a vault being created).</param>
    public async Task<TurnOnResult> TurnOnAsync(BackupRotationService backups, SyncWriteLease? held = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backups);
        var (lease, owned) = await LeaseAsync(held, ct).ConfigureAwait(false);
        try
        {
            if (IsOn) return new TurnOnResult(TurnOnStatus.AlreadyOn, null, null, Array.Empty<MigrationProblem>());
            using (var check = new SqliteSyncStorage(_connectionString))
            {
                LegacyMigration.RequireSchemaV2(check);
                var problems = LegacyMigration.FindProblems(check);
                if (problems.Count > 0) return new TurnOnResult(TurnOnStatus.Blocked, null, null, problems);
            }
            var backup = await backups.CreatePreSyncAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                var storage = new SqliteSyncStorage(_connectionString);
                try
                {
                    var materializer = new TableMaterializer(storage);
                    var result = LegacyMigration.Run(storage, materializer, _time);
                    Attach(storage, materializer);
                    return new TurnOnResult(TurnOnStatus.TurnedOn, result, backup, Array.Empty<MigrationProblem>());
                }
                catch
                {
                    storage.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            if (owned) lease.Dispose();
        }
    }

    /// <summary>
    /// Turns local sync off, holding the write gate: writes go straight to the tables again, which keep
    /// their current (materialized) content. The sync records stay, but turning sync on again starts
    /// fresh from the tables with a new device ID (as <c>sync-protocol.md</c> 12.4 says a rejoin does).
    /// </summary>
    public async Task TurnOffAsync(SyncWriteLease? held = null, CancellationToken ct = default)
    {
        var (lease, owned) = await LeaseAsync(held, ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_storage is not { } storage) return;
                storage.RunInTransaction(() => LegacyMigration.SetDeviceSetting(storage, SqliteSyncSchema.EnabledKey, "0"));
                Detach();
            }
        }
        finally
        {
            if (owned) lease.Dispose();
        }
    }

    /// <summary>
    /// Joins a vault on this device (<c>sync-protocol.md</c> 12.2 steps 3 and 4), holding the write gate:
    /// creates the sync tables if needed, then in <b>one</b> transaction starts fresh sync state with a
    /// new device ID and <paramref name="vaultId"/>, clears every synced record, and rebuilds today's
    /// tables from the (empty) state, so they are cleared too; per-device settings stay. Reading the
    /// vault fills them again. The caller saved a backup first when the device had data.
    /// </summary>
    public void Join(string vaultId, SyncWriteLease held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (held.Owner != this || !held.IsHeld) throw new ArgumentException("That lease is not held on this LocalSync.", nameof(held));
        lock (_gate)
        {
            Detach();
            var storage = new SqliteSyncStorage(_connectionString);
            try
            {
                LegacyMigration.RequireSchemaV2(storage);
                storage.RunInTransaction(storage.CreateSchema);
                var materializer = new TableMaterializer(storage);
                var replica = SyncReplica.Start(storage, _time, vaultId, clearSyncedData: true, materializer, state =>
                {
                    materializer.ClearLocalIds();
                    materializer.RebuildAll(state);
                    LegacyMigration.SetDeviceSetting(storage, SqliteSyncSchema.EnabledKey, "1");
                });
                _storage = storage;
                Materializer = materializer;
                Replica = replica;
            }
            catch
            {
                Detach();
                storage.Dispose();
                throw;
            }
        }
    }

    // ---- Notifications ----------------------------------------------------------------------------------

    /// <summary>Raises <see cref="DataChanged"/>. Call it after the merge's transaction committed.</summary>
    public void RaiseDataChanged(SyncDataChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Rows.Count == 0 && !args.Everything) return;
        DataChanged?.Invoke(this, args);
    }

    /// <summary>Raises <see cref="LocalEdited"/>; the routing write path calls it after each write with sync on.</summary>
    public void NotifyLocalEdit() => LocalEdited?.Invoke(this, EventArgs.Empty);

    // ---- This device's sync settings (sync_device) and Notion echo state ---------------------------------

    /// <summary>One of this device's sync settings, or null. Null while sync is off.</summary>
    public string? GetDeviceSetting(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Replica is not { } replica || _storage is not { } storage) return null;
        return replica.Read((_, _) =>
        {
            using var cmd = storage.CreateCommand("SELECT value FROM sync_device WHERE key = $k;");
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        });
    }

    /// <summary>Sets (or with null clears) one of this device's sync settings. Requires sync on.</summary>
    public void SetDeviceSetting(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (replica, storage) = RequireOn();
        replica.Update(_ =>
        {
            if (value is null)
            {
                using var cmd = storage.CreateCommand("DELETE FROM sync_device WHERE key = $k;");
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
            else
            {
                LegacyMigration.SetDeviceSetting(storage, key, value);
            }
        });
    }

    /// <summary>The Remaining this device's Notion connector last wrote for the bill, or null (<c>sync-protocol.md</c> 5.3).</summary>
    public double? GetNotionLastPushed(string billId)
    {
        ArgumentNullException.ThrowIfNull(billId);
        if (Replica is not { } replica || _storage is not { } storage) return null;
        return replica.Read((_, _) =>
        {
            using var cmd = storage.CreateCommand("SELECT remaining_cents FROM sync_notion_pushed WHERE bill_id = $b;");
            cmd.Parameters.AddWithValue("$b", billId);
            return cmd.ExecuteScalar() is long cents ? SyncMoney.FromCents(cents) : (double?)null;
        });
    }

    /// <summary>Records the Remaining the connector just wrote to Notion for the bill. Does nothing while sync is off.</summary>
    public void SetNotionLastPushed(string billId, double remaining)
    {
        ArgumentNullException.ThrowIfNull(billId);
        if (Replica is not { } replica || _storage is not { } storage) return;
        var cents = SyncMoney.TryCents(remaining, out var c) ? c : 0;
        replica.Update(_ =>
        {
            using var cmd = storage.CreateCommand(
                "INSERT INTO sync_notion_pushed (bill_id, remaining_cents) VALUES ($b, $c) ON CONFLICT (bill_id) DO UPDATE SET remaining_cents = excluded.remaining_cents;");
            cmd.Parameters.AddWithValue("$b", billId);
            cmd.Parameters.AddWithValue("$c", cents);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Forgets every last-pushed value (this device just became the connector's owner and must push before it pulls).</summary>
    public void ClearNotionLastPushed()
    {
        if (Replica is not { } replica || _storage is not { } storage) return;
        replica.Update(_ =>
        {
            using var cmd = storage.CreateCommand("DELETE FROM sync_notion_pushed;");
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// The device creating a vault with a Notion token keeps running the connector it ran before sync
    /// (<c>data-model.md</c> 8.5: it writes its own ID as <c>NotionOwnerDeviceId</c>). It is not a new
    /// owner taking over from another device, and no other device's payment can be waiting to merge,
    /// so instead of a forced first push its last-pushed values start at each bill's current Remaining:
    /// Notion edits made since its last Notion sync still come in, exactly as before sync.
    /// </summary>
    public void ContinueNotionAsCreator()
    {
        var (replica, storage) = RequireOn();
        replica.SetSetting(SyncSettingKeys.NotionOwnerDeviceId, SyncValue.String(replica.DeviceId));
        replica.Update(_ =>
        {
            var s = replica.State; // under the replica's lock
            using (var clear = storage.CreateCommand("DELETE FROM sync_notion_pushed;"))
            {
                clear.ExecuteNonQuery();
            }
            foreach (var bill in s.LiveIds(SyncEntities.Bill))
            {
                using var cmd = storage.CreateCommand("INSERT INTO sync_notion_pushed (bill_id, remaining_cents) VALUES ($b, $c);");
                cmd.Parameters.AddWithValue("$b", bill);
                cmd.Parameters.AddWithValue("$c", s.RemainingCents(bill));
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>True when this device's connector has pushed at least one bill since it became the owner.</summary>
    public bool HasNotionPushes
    {
        get
        {
            if (Replica is not { } replica || _storage is not { } storage) return false;
            return replica.Read((_, _) =>
            {
                using var cmd = storage.CreateCommand("SELECT COUNT(*) FROM sync_notion_pushed;");
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            });
        }
    }

    public void Dispose()
    {
        lock (_gate) Detach();
    }

    private (SyncReplica Replica, SqliteSyncStorage Storage) RequireOn()
        => Replica is { } replica && _storage is { } storage
            ? (replica, storage)
            : throw new InvalidOperationException("Sync is off.");

    private void Attach(SqliteSyncStorage storage, TableMaterializer materializer)
    {
        _storage = storage;
        Materializer = materializer;
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
