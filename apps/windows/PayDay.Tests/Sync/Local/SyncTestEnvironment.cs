using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PayDay.Services;
using PayDay.Sync.Local;
using PayDay.Sync.Model;
using PayDay.Tests.Sync;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// A temporary <c>payday.db</c> with today's tables (through the app's own
/// <see cref="SqliteDatabaseService"/>), local sync over it, the routing write path and an in-memory
/// backup folder. All data is fictional.
/// </summary>
internal sealed class SyncTestEnvironment : IDisposable
{
    public const long T0 = 1747915200000; // 2025-05-22T12:00:00Z, a fixed fictional moment
    /// <summary>With the anchor 2025-03-20 the current pay period is 2025-05-15 to 2025-05-28.</summary>
    public static readonly DateTime Today = new(2025, 5, 22);

    public const string CurrentPeriod = "2025-05-15";

    private readonly SqliteTestFiles _files = new();

    /// <param name="startMs">This device's clock at the start (default <see cref="T0"/>); another start simulates a device whose clock is off.</param>
    /// <param name="path">An existing database file to open (a cloned install) instead of a new one.</param>
    public SyncTestEnvironment(long? startMs = null, string? path = null)
    {
        Time = new ManualTimeProvider(startMs ?? T0);
        Path = path ?? _files.NewPath();
        Db = new SqliteDatabaseService(Path);
        Store = new InMemoryBackupStore();
        Backups = new BackupRotationService(Db, Store, () => DateTime.UnixEpoch.AddMilliseconds(Time.UnixMs));
        Sync = _files.Own(new LocalSync(LocalSync.ConnectionStringFor(Path), Time));
        Routed = new SyncRoutingDatabaseService(Db, Sync, Backups);
    }

    public string Path { get; }

    public ManualTimeProvider Time { get; }

    public SqliteDatabaseService Db { get; }

    public InMemoryBackupStore Store { get; }

    public BackupRotationService Backups { get; }

    public LocalSync Sync { get; }

    public SyncRoutingDatabaseService Routed { get; }

    /// <summary>Creates today's tables the way the app does on start (no seed data).</summary>
    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
    }

    public async Task<TurnOnResult> TurnOnAsync()
    {
        Time.Advance(1000);
        return await Sync.TurnOnAsync(Backups);
    }

    // ---- Fictional legacy data covering every shape today's tables hold ----------------------------

    public const string GuidUpper = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    public const string GuidLower = "c56a4180-65aa-42ec-a945-5fd21dec0538";

    /// <summary>
    /// Seeded IDs ("1", "13", "7", "27"), an uppercase and a lowercase GUID, an inactive bill, an
    /// Installments bill, a custom type, a yearly bill with a Notion page, duplicate payments for one
    /// bill and period, every PaidAt format, duplicate snapshots, Details with legacy keys, type colours
    /// with one invalid entry, Notion database IDs and per-device keys.
    /// </summary>
    public void InsertLegacyData()
    {
        Exec("""
            INSERT INTO Bills (Id, Name, Type, Payment, Remaining, Available, CreditLimit, DueDay, Rate, APR, AutoPay, Active, YearlyDate, Notes, CreatedAt, UpdatedAt, NotionPageId) VALUES
            ('1',  'Woodgrove Rewards',     'Cards',        55,    1200,   1800, 3000, 18, 'Monthly',   24.99, 0, 1, NULL,    NULL,                  '2025-03-01 10:00:00', '2025-05-01 09:00:00', NULL),
            ('13', 'Electric',              'Bills',        120,   0,      0,    0,    3,  'Monthly',   0,     0, 1, NULL,    'Variable - estimate', '2025-03-01 10:00:01', '2025-05-01 09:00:00', NULL),
            ('7',  'Northwind – Cashback',  'Cards',        30,    0,      1000, 1000, 2,  'Monthly',   0,     0, 0, NULL,    NULL,                  '2025-03-01 10:00:02', '2025-05-01 09:00:00', NULL),
            ('6F9619FF-8B86-D011-B42D-00C04FC964FF', 'Contoso Phone Plan', 'Installments', 45.5, 910.25, 0, 0, 20, 'Monthly', 0, 0, 1, NULL, NULL, '2025-04-02 08:30:00', '2025-05-01 09:00:00', NULL),
            ('c56a4180-65aa-42ec-a945-5fd21dec0538', 'Fabrikam Gym', 'Fitness', 25, 0, 0, 0, 22, 'Bi-Weekly', 0, 1, 1, NULL, NULL, NULL, NULL, NULL),
            ('27', 'Tailspin Insurance',    'Bills',        600,   0,      0,    0,    1,  'Yearly',    0,     0, 1, '03-15', NULL,                  '2025-03-01 10:00:03', '2025-05-01 09:00:00', 'fictional-notion-page-27');
            """);
        Exec("""
            INSERT INTO Payments (Id, BillId, PeriodKey, AmountPaid, PaidAt, NotionPageId) VALUES
            (1, '1',  '2025-05-01', 55,   '2025-05-02 15:04:05',           NULL),
            (2, '13', '2025-05-01', 120,  '2025-05-02T15:04:05.1234567Z',  'fictional-notion-payment-2'),
            (3, '1',  '2025-05-01', 55,   '2025-05-02 15:04:05',           NULL),
            (4, '1',  '2025-05-01', 20,   NULL,                            NULL),
            (5, '6F9619FF-8B86-D011-B42D-00C04FC964FF', '2025-05-15', 45.5, '2025-05-16T08:00:00.0000000', NULL),
            (7, '7',  '2025-05-15', 30,   '2025-05-16T08:00:00.0000000-05:00', NULL);
            """);
        Exec("""
            INSERT INTO Snapshots (Id, SnapshotDate, TotalRemaining, Details, NotionPageId) VALUES
            (1, '2025-05-01', 3000.5, '{"1":1200.00,"6F9619FF-8B86-D011-B42D-00C04FC964FF":910.25}', NULL),
            (2, '2025-05-01', 3000.5, NULL, 'fictional-notion-snapshot-2'),
            (3, '2025-05-15', 2800,   'not json', NULL);
            """);
        Exec("""
            INSERT INTO Settings (Key, Value) VALUES
            ('PayAnchor', '2025-03-20'),
            ('EarlyStart', 'false'),
            ('TypeColors', '{"Cards":"#fd79a8","Fitness":"#00B894","Bad":"red"}'),
            ('NotionBillsDb', '00000000-0000-0000-0000-000000000102'),
            ('NotionPaymentsDb', '00000000-0000-0000-0000-000000000202'),
            ('NotionSnapshotsDb', '00000000-0000-0000-0000-000000000302'),
            ('LastNotionSync', '2025-05-20T12:00:00.0000000Z'),
            ('Theme', 'Dark')
            ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;
            """);
    }

    // ---- SQL helpers --------------------------------------------------------------------------------

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(SqliteTestFiles.ConnectionString(Path));
        conn.Open();
        return conn;
    }

    public void Exec(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public long Count(string table) => Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {table};"), CultureInfo.InvariantCulture);

    public IReadOnlyList<string> Tables()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>Every row of today's four tables as text, in key order, optionally without some columns.</summary>
    public string Dump(params string[] skipColumns)
    {
        var sb = new StringBuilder();
        foreach (var (table, order) in new[] { ("Bills", "Id"), ("Payments", "Id"), ("Snapshots", "Id"), ("Settings", "Key") })
        {
            sb.Append("== ").Append(table).Append('\n');
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY {order};";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (skipColumns.Contains(reader.GetName(i), StringComparer.Ordinal)) continue;
                    var v = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    sb.Append(reader.GetName(i)).Append('=').Append(v).Append(" | ");
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>The sync records' (entity, id, field, value), without HLCs, sorted.</summary>
    public IReadOnlyList<string> RecordValues()
        => Sync.Replica!.Read((s, _) => s.AllRecords().Select(r => $"{r.Entity}/{r.Id}/{r.Field}={r.Value}").ToList());

    public SyncValue? Value(string entity, string id, string field)
        => Sync.Replica!.Read((s, _) => s.Value(entity, id, field));

    public void Dispose()
    {
        Sync.Dispose();
        _files.Dispose();
    }
}
