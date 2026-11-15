using System.Globalization;
using Microsoft.Data.Sqlite;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Storage;

namespace PayDay.Sync.Local;

/// <summary>What the one-time migration did.</summary>
public sealed record MigrationResult(
    string DeviceId,
    Hlc Hlc,
    int Bills,
    int Payments,
    int Snapshots,
    int Settings,
    int Records);

/// <summary>
/// The one-time migration of today's tables into synced records (<c>spec/data-model.md</c> section 8,
/// <c>sync-protocol.md</c> 12.1 step 4), in <b>one</b> transaction on the sync connection:
/// <list type="number">
///   <item>create the sync tables (new tables only);</item>
///   <item>start fresh sync state: a new device ID, no clock, cursors and queue empty, no records;</item>
///   <item>read <c>Bills</c>, <c>Payments</c>, <c>Snapshots</c> and <c>Settings</c>, map them
///     (<see cref="LegacyMapping"/>, <see cref="BackupSettings.MapForMigration"/>) and write every field
///     of every row plus <c>deleted: false</c>, and every mapped setting, at <b>one</b> HLC (8.1);</item>
///   <item>queue those records for upload and save the clock state;</item>
///   <item>rewrite the tables from the merged state (new bill IDs, rewritten references, local
///     integer IDs kept) and check that no row went missing;</item>
///   <item>turn sync on.</item>
/// </list>
/// If anything fails, the transaction rolls back and the database is exactly as it was. Running it
/// again maps the same data to the same IDs and values (it is deterministic), so it is idempotent; it
/// never runs on app start, only when sync is turned on (<see cref="LocalSync.TurnOnAsync"/>), which
/// first writes a JSON backup.
/// </summary>
public static class LegacyMigration
{
    /// <summary>
    /// The rows of today's tables the migration could not carry over (<see cref="LegacyMapping.FindProblems"/>).
    /// Read-only; run it on a storage that is not in a transaction (or inside one).
    /// </summary>
    public static IReadOnlyList<MigrationProblem> FindProblems(SqliteSyncStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return LegacyMapping.FindProblems(
            ReadAll(storage, "SELECT * FROM Bills;", SqliteDatabaseService.MapBill),
            ReadAll(storage, "SELECT * FROM Payments;", SqliteDatabaseService.MapPayment),
            ReadAll(storage, "SELECT * FROM Snapshots;", SqliteDatabaseService.MapSnapshot));
    }

    public static MigrationResult Run(SqliteSyncStorage storage, TableMaterializer materializer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(materializer);
        ArgumentNullException.ThrowIfNull(time);
        MigrationResult? result = null;
        storage.RunInTransaction(() =>
        {
            storage.CreateSchema();
            RequireSchemaV2(storage);

            storage.Metadata.Reset();
            storage.Records.Clear();
            var deviceId = DeviceIds.NewRandom();
            storage.Metadata.DeviceId = deviceId;

            var bills = ReadAll(storage, "SELECT * FROM Bills;", SqliteDatabaseService.MapBill);
            var payments = ReadAll(storage, "SELECT * FROM Payments;", SqliteDatabaseService.MapPayment);
            var snapshots = ReadAll(storage, "SELECT * FROM Snapshots;", SqliteDatabaseService.MapSnapshot);
            var settingsTable = ReadSettings(storage);

            var rows = LegacyMapping.Map(bills, payments, snapshots);
            var settings = BackupSettings.MapForMigration(LegacyMapping.SettingsJson(settingsTable));

            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var hlc = HlcClock.Tick(null, now, deviceId);
            var records = new List<ChangeRecord>();
            foreach (var (entity, byId) in rows.Rows)
            {
                foreach (var (id, fields) in byId)
                {
                    foreach (var (field, value) in fields) records.Add(new ChangeRecord(entity, id, field, value, hlc));
                    records.Add(new ChangeRecord(entity, id, SyncEntities.DeletedField, SyncValue.False, hlc));
                }
            }
            foreach (var (key, value) in settings)
            {
                records.Add(new ChangeRecord(SyncEntities.Setting, key, SyncEntities.ValueField, value, hlc));
            }
            var sorted = ChangeRecord.SortByKey(records);

            var state = new SyncState(storage.Records);
            state.MergeAll(sorted);
            if (sorted.Count > 0)
            {
                storage.Metadata.EnqueuePending(sorted);
                storage.Metadata.ClockState = hlc;
            }

            materializer.SetLocalIds(SyncEntities.Payment, rows.PaymentLocalIds);
            materializer.SetLocalIds(SyncEntities.BalanceSnapshot, rows.SnapshotLocalIds);
            materializer.SetLegacyUpdatedAt(
                bills.GroupBy(b => LegacyMapping.MapBillId(b.Id), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().UpdatedAt, StringComparer.Ordinal),
                hlc);
            materializer.RebuildAll(state);
            RequireNothingLost(storage, bills, payments, snapshots);

            SetDeviceSetting(storage, SqliteSyncSchema.EnabledKey, "1");
            SetDeviceSetting(storage, SqliteSyncSchema.MigratedAtKey, now.ToString(CultureInfo.InvariantCulture));
            result = new MigrationResult(deviceId, hlc, bills.Count, payments.Count, snapshots.Count, settings.Count, sorted.Count);
        });
        return result!;
    }

    /// <summary>Sets one of this device's sync settings (<c>sync_device</c>) inside the running transaction.</summary>
    internal static void SetDeviceSetting(SqliteSyncStorage storage, string key, string? value)
    {
        using var cmd = storage.CreateCommand(
            "INSERT INTO sync_device (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;");
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The app migrates schema v1 (Cost/Owed/TotalOwed) to v2 on every start; sync needs v2. Throws
    /// <see cref="LegacyMigrationException"/> otherwise. Read-only; also used before the backup.
    /// </summary>
    public static void RequireSchemaV2(SqliteSyncStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        foreach (var (table, column) in new[] { ("Bills", "Payment"), ("Bills", "Remaining"), ("Payments", "AmountPaid"), ("Snapshots", "TotalRemaining"), ("Settings", "Value") })
        {
            if (!HasColumn(storage, table, column))
            {
                throw new LegacyMigrationException($"The database is not at schema version 2 ({table}.{column} is missing); start the app once to upgrade it, then turn sync on.");
            }
        }
    }

    private static bool HasColumn(SqliteSyncStorage storage, string table, string column)
    {
        using var cmd = storage.CreateCommand($"PRAGMA table_info({table});");
        using var reader = cmd.ExecuteReader();
        var name = reader.GetOrdinal("name");
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(name), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static List<T> ReadAll<T>(SqliteSyncStorage storage, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = storage.CreateCommand(sql);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    private static Dictionary<string, string?> ReadSettings(SqliteSyncStorage storage)
    {
        using var cmd = storage.CreateCommand("SELECT Key, Value FROM Settings;");
        using var reader = cmd.ExecuteReader();
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (reader.Read()) dict[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return dict;
    }

    /// <summary>
    /// After the rewrite every bill and snapshot is back, and every payment whose bill exists (a
    /// payment of a missing bill is kept in the records but, as today, cannot be shown).
    /// </summary>
    private static void RequireNothingLost(SqliteSyncStorage storage, IReadOnlyList<Bill> bills, IReadOnlyList<Payment> payments, IReadOnlyList<Snapshot> snapshots)
    {
        var billIds = bills.Select(b => LegacyMapping.MapBillId(b.Id)).ToHashSet(StringComparer.Ordinal);
        var shownPayments = payments.Count(p => billIds.Contains(LegacyMapping.MapBillId(p.BillId)));
        Require(storage, "Bills", bills.Count);
        Require(storage, "Payments", shownPayments);
        Require(storage, "Snapshots", snapshots.Count);
    }

    private static void Require(SqliteSyncStorage storage, string table, int expected)
    {
        using var cmd = storage.CreateCommand($"SELECT COUNT(*) FROM {table};");
        var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count != expected)
        {
            throw new LegacyMigrationException($"Migration check failed: {table} would have {count} rows instead of {expected}; nothing was changed.");
        }
    }
}
