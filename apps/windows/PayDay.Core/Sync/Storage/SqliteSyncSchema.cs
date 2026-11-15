using Microsoft.Data.Sqlite;

namespace PayDay.Sync.Storage;

/// <summary>
/// The sync tables inside <c>payday.db</c>. They are new tables only: nothing here alters
/// <c>Bills</c>, <c>Payments</c>, <c>Snapshots</c> or <c>Settings</c>, and nothing creates them on
/// app start. They are created by <see cref="SqliteSyncStorage.CreateSchema"/>, which turning sync on
/// calls inside its migration transaction.
/// <list type="table">
///   <item><term><c>sync_records</c></term><description>The merged state: the winning (value, HLC) of every (entity, id, field), known and unknown (<c>data-model.md</c> section 7).</description></item>
///   <item><term><c>sync_meta</c></term><description>Device ID, vault ID, clock state, next sequence number.</description></item>
///   <item><term><c>sync_outbox</c></term><description>Records written locally and not yet sealed.</description></item>
///   <item><term><c>sync_batches</c>, <c>sync_batch_records</c></term><description>Sealed batches and their records, in order.</description></item>
///   <item><term><c>sync_cursors</c>, <c>sync_merged_ahead</c>, <c>sync_snapshot_seen</c></term><description>Reading state (sections 10.2, 11.1).</description></item>
///   <item><term><c>sync_device</c></term><description>This device's sync settings (on/off, migration done); kept by <see cref="ISyncMetadata.Reset"/>.</description></item>
///   <item><term><c>sync_local_ids</c></term><description>The integer <c>Payments.Id</c> / <c>Snapshots.Id</c> the materializer gave each synced row, so local IDs stay stable.</description></item>
/// </list>
/// </summary>
public static class SqliteSyncSchema
{
    /// <summary><c>sync_device</c> key: "1" while local sync is on.</summary>
    public const string EnabledKey = "enabled";

    /// <summary><c>sync_device</c> key: when the one-time migration of today's tables last ran (Unix ms).</summary>
    public const string MigratedAtKey = "migratedAt";

    internal const string Ddl = """
        CREATE TABLE IF NOT EXISTS sync_records (
            entity TEXT NOT NULL,
            row_id TEXT NOT NULL,
            field  TEXT NOT NULL,
            value  TEXT NOT NULL,
            hlc    TEXT NOT NULL,
            PRIMARY KEY (entity, row_id, field)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS sync_records_by_value ON sync_records (entity, field, value);
        CREATE TABLE IF NOT EXISTS sync_meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        CREATE TABLE IF NOT EXISTS sync_outbox (
            pos    INTEGER PRIMARY KEY AUTOINCREMENT,
            entity TEXT NOT NULL,
            row_id TEXT NOT NULL,
            field  TEXT NOT NULL,
            value  TEXT NOT NULL,
            hlc    TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sync_batches (
            seq      INTEGER PRIMARY KEY,
            uploaded INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS sync_batch_records (
            seq    INTEGER NOT NULL,
            pos    INTEGER NOT NULL,
            entity TEXT NOT NULL,
            row_id TEXT NOT NULL,
            field  TEXT NOT NULL,
            value  TEXT NOT NULL,
            hlc    TEXT NOT NULL,
            PRIMARY KEY (seq, pos)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS sync_cursors (
            device_id TEXT PRIMARY KEY,
            seq       INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sync_merged_ahead (
            device_id TEXT NOT NULL,
            seq       INTEGER NOT NULL,
            PRIMARY KEY (device_id, seq)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS sync_snapshot_seen (
            name         TEXT PRIMARY KEY,
            first_listed INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sync_device (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        CREATE TABLE IF NOT EXISTS sync_local_ids (
            entity   TEXT NOT NULL,
            sync_id  TEXT NOT NULL,
            local_id INTEGER NOT NULL,
            PRIMARY KEY (entity, sync_id)
        ) WITHOUT ROWID;
        CREATE UNIQUE INDEX IF NOT EXISTS sync_local_ids_by_local ON sync_local_ids (entity, local_id);
        """;

    /// <summary>True when the sync tables exist in the database <paramref name="connection"/> is open on. Read-only.</summary>
    public static bool SchemaExists(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_device';";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// True when local sync is turned on in this database. Read-only: a database that never had sync
    /// turned on has no sync tables and gives false without any table being created.
    /// </summary>
    public static bool IsSyncEnabled(SqliteConnection connection)
    {
        if (!SchemaExists(connection)) return false;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_device WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", EnabledKey);
        return cmd.ExecuteScalar() is string v && v == "1";
    }
}
