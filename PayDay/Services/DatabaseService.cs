using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PayDay.Models;
using Windows.Storage;

namespace PayDay.Services;

public sealed class DatabaseService : IDatabaseService
{
    private const int CurrentSchemaVersion = 1;
    private const string SchemaVersionKey = "SchemaVersion";

    private static readonly Lazy<DatabaseService> _instance = new(() => new DatabaseService());
    public static DatabaseService Instance => _instance.Value;

    public string DbPath { get; }
    public string ConnectionString { get; }

    private DatabaseService()
    {
        DbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "payday.db");
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task<SqliteConnection> OpenConnectionAsync()
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        return conn;
    }

    public async Task InitializeAsync()
    {
        await using (var conn = await OpenConnectionAsync().ConfigureAwait(false))
        await using (var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false))
        {
            await ExecuteAsync(conn, tx, SchemaBills).ConfigureAwait(false);
            await ExecuteAsync(conn, tx, SchemaPayments).ConfigureAwait(false);
            await ExecuteAsync(conn, tx, SchemaSnapshots).ConfigureAwait(false);
            await ExecuteAsync(conn, tx, SchemaSettings).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }

        await RunMigrationsAsync().ConfigureAwait(false);

        if (await CountBillsAsync().ConfigureAwait(false) == 0)
        {
            await SeedAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Migrations
    // ------------------------------------------------------------------

    private async Task RunMigrationsAsync()
    {
        var existing = await GetSettingAsync(SchemaVersionKey).ConfigureAwait(false);
        var version = int.TryParse(existing, out var v) ? v : 0;

        // Future schema bumps: while (version < CurrentSchemaVersion) { ... apply migration ...; version++; }
        if (version == CurrentSchemaVersion) return;

        await SetSettingAsync(SchemaVersionKey, CurrentSchemaVersion.ToString()).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------

    public async Task<string?> GetSettingAsync(string key)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result == null || result is DBNull ? null : (string)result;
    }

    public async Task SetSettingAsync(string key, string? value)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Settings(Key, Value) VALUES($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Bills CRUD
    // ------------------------------------------------------------------

    public async Task<int> CountBillsAsync()
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Bills;";
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public async Task<Bill?> GetBillAsync(string id)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Bills WHERE Id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? MapBill(reader) : null;
    }

    public async Task<IReadOnlyList<Bill>> GetAllBillsAsync()
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Bills ORDER BY Type, Name;";
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Bill>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add(MapBill(reader));
        }
        return list;
    }

    public async Task UpsertBillAsync(Bill bill)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
        await UpsertBillInternalAsync(conn, tx, bill).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
    }

    public async Task UpsertBillsAsync(IEnumerable<Bill> bills)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var bill in bills)
        {
            await UpsertBillInternalAsync(conn, tx, bill).ConfigureAwait(false);
        }
        await tx.CommitAsync().ConfigureAwait(false);
    }

    public async Task DeleteBillAsync(string id)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Bills WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task UpsertBillInternalAsync(SqliteConnection conn, SqliteTransaction tx, Bill bill)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Bills(Id, Name, Type, Cost, Owed, Available, CreditLimit, DueDay, Rate, APR,
                              AutoPay, Active, YearlyDate, Notes, NotionPageId)
            VALUES($Id, $Name, $Type, $Cost, $Owed, $Available, $CreditLimit, $DueDay, $Rate, $APR,
                   $AutoPay, $Active, $YearlyDate, $Notes, $NotionPageId)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Type = excluded.Type,
                Cost = excluded.Cost,
                Owed = excluded.Owed,
                Available = excluded.Available,
                CreditLimit = excluded.CreditLimit,
                DueDay = excluded.DueDay,
                Rate = excluded.Rate,
                APR = excluded.APR,
                AutoPay = excluded.AutoPay,
                Active = excluded.Active,
                YearlyDate = excluded.YearlyDate,
                Notes = excluded.Notes,
                NotionPageId = excluded.NotionPageId,
                UpdatedAt = datetime('now');
            """;
        cmd.Parameters.AddWithValue("$Id", bill.Id);
        cmd.Parameters.AddWithValue("$Name", bill.Name);
        cmd.Parameters.AddWithValue("$Type", bill.Type);
        cmd.Parameters.AddWithValue("$Cost", bill.Cost);
        cmd.Parameters.AddWithValue("$Owed", bill.Owed);
        cmd.Parameters.AddWithValue("$Available", bill.Available);
        cmd.Parameters.AddWithValue("$CreditLimit", bill.CreditLimit);
        cmd.Parameters.AddWithValue("$DueDay", bill.DueDay);
        cmd.Parameters.AddWithValue("$Rate", bill.Rate);
        cmd.Parameters.AddWithValue("$APR", bill.APR);
        cmd.Parameters.AddWithValue("$AutoPay", bill.AutoPay ? 1 : 0);
        cmd.Parameters.AddWithValue("$Active", bill.Active ? 1 : 0);
        cmd.Parameters.AddWithValue("$YearlyDate", (object?)bill.YearlyDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Notes", (object?)bill.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$NotionPageId", (object?)bill.NotionPageId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static Bill MapBill(SqliteDataReader r) => new()
    {
        Id           = r.GetString(r.GetOrdinal("Id")),
        Name         = r.GetString(r.GetOrdinal("Name")),
        Type         = r.GetString(r.GetOrdinal("Type")),
        Cost         = r.GetDouble(r.GetOrdinal("Cost")),
        Owed         = r.GetDouble(r.GetOrdinal("Owed")),
        Available    = r.GetDouble(r.GetOrdinal("Available")),
        CreditLimit  = r.GetDouble(r.GetOrdinal("CreditLimit")),
        DueDay       = r.GetInt32(r.GetOrdinal("DueDay")),
        Rate         = r.GetString(r.GetOrdinal("Rate")),
        APR          = r.GetDouble(r.GetOrdinal("APR")),
        AutoPay      = r.GetInt32(r.GetOrdinal("AutoPay")) != 0,
        Active       = r.GetInt32(r.GetOrdinal("Active")) != 0,
        YearlyDate   = r.IsDBNull(r.GetOrdinal("YearlyDate"))   ? null : r.GetString(r.GetOrdinal("YearlyDate")),
        Notes        = r.IsDBNull(r.GetOrdinal("Notes"))        ? null : r.GetString(r.GetOrdinal("Notes")),
        CreatedAt    = r.IsDBNull(r.GetOrdinal("CreatedAt"))    ? null : r.GetString(r.GetOrdinal("CreatedAt")),
        UpdatedAt    = r.IsDBNull(r.GetOrdinal("UpdatedAt"))    ? null : r.GetString(r.GetOrdinal("UpdatedAt")),
        NotionPageId = r.IsDBNull(r.GetOrdinal("NotionPageId")) ? null : r.GetString(r.GetOrdinal("NotionPageId")),
    };

    // ------------------------------------------------------------------
    // Payments CRUD
    // ------------------------------------------------------------------

    public async Task<long> InsertPaymentAsync(Payment payment)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Payments(BillId, PeriodKey, AmountPaid, NotionPageId)
            VALUES($BillId, $PeriodKey, $AmountPaid, $NotionPageId);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$BillId", payment.BillId);
        cmd.Parameters.AddWithValue("$PeriodKey", payment.PeriodKey);
        cmd.Parameters.AddWithValue("$AmountPaid", payment.AmountPaid);
        cmd.Parameters.AddWithValue("$NotionPageId", (object?)payment.NotionPageId ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task DeletePaymentAsync(long id)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Payments WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<int> DeletePaymentsForBillInPeriodAsync(string periodKey, string billId)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Payments WHERE PeriodKey = $k AND BillId = $b;";
        cmd.Parameters.AddWithValue("$k", periodKey);
        cmd.Parameters.AddWithValue("$b", billId);
        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Payment>> GetPaymentsByPeriodAsync(string periodKey)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Payments WHERE PeriodKey = $k ORDER BY PaidAt;";
        cmd.Parameters.AddWithValue("$k", periodKey);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Payment>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add(MapPayment(reader));
        }
        return list;
    }

    private static Payment MapPayment(SqliteDataReader r) => new()
    {
        Id           = r.GetInt64(r.GetOrdinal("Id")),
        BillId       = r.GetString(r.GetOrdinal("BillId")),
        PeriodKey    = r.GetString(r.GetOrdinal("PeriodKey")),
        AmountPaid   = r.GetDouble(r.GetOrdinal("AmountPaid")),
        PaidAt       = r.IsDBNull(r.GetOrdinal("PaidAt"))       ? null : r.GetString(r.GetOrdinal("PaidAt")),
        NotionPageId = r.IsDBNull(r.GetOrdinal("NotionPageId")) ? null : r.GetString(r.GetOrdinal("NotionPageId")),
    };

    // ------------------------------------------------------------------
    // Snapshots CRUD
    // ------------------------------------------------------------------

    public async Task<long> InsertSnapshotAsync(Snapshot snapshot)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Snapshots(SnapshotDate, TotalOwed, Details, NotionPageId)
            VALUES($Date, $Total, $Details, $NotionPageId);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$Date", snapshot.SnapshotDate);
        cmd.Parameters.AddWithValue("$Total", snapshot.TotalOwed);
        cmd.Parameters.AddWithValue("$Details", (object?)snapshot.Details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$NotionPageId", (object?)snapshot.NotionPageId ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<Snapshot>> GetAllSnapshotsAsync()
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Snapshots ORDER BY SnapshotDate;";
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var list = new List<Snapshot>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add(MapSnapshot(reader));
        }
        return list;
    }

    private static Snapshot MapSnapshot(SqliteDataReader r) => new()
    {
        Id           = r.GetInt64(r.GetOrdinal("Id")),
        SnapshotDate = r.GetString(r.GetOrdinal("SnapshotDate")),
        TotalOwed    = r.GetDouble(r.GetOrdinal("TotalOwed")),
        Details      = r.IsDBNull(r.GetOrdinal("Details"))      ? null : r.GetString(r.GetOrdinal("Details")),
        NotionPageId = r.IsDBNull(r.GetOrdinal("NotionPageId")) ? null : r.GetString(r.GetOrdinal("NotionPageId")),
    };

    // ------------------------------------------------------------------
    // Seed (called from InitializeAsync when Bills table is empty)
    // ------------------------------------------------------------------

    private async Task SeedAsync()
    {
        await UpsertBillsAsync(SeedData.Bills).ConfigureAwait(false);
        foreach (var (key, value) in SeedData.DefaultSettings)
        {
            await SetSettingAsync(key, value).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Helpers / schema constants
    // ------------------------------------------------------------------

    private static async Task ExecuteAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private const string SchemaBills = """
        CREATE TABLE IF NOT EXISTS Bills (
            Id           TEXT PRIMARY KEY,
            Name         TEXT NOT NULL,
            Type         TEXT NOT NULL,
            Cost         REAL NOT NULL DEFAULT 0,
            Owed         REAL DEFAULT 0,
            Available    REAL DEFAULT 0,
            CreditLimit  REAL DEFAULT 0,
            DueDay       INTEGER DEFAULT 1,
            Rate         TEXT DEFAULT 'Monthly',
            APR          REAL DEFAULT 0,
            AutoPay      INTEGER DEFAULT 0,
            Active       INTEGER DEFAULT 1,
            YearlyDate   TEXT,
            Notes        TEXT,
            CreatedAt    TEXT DEFAULT (datetime('now')),
            UpdatedAt    TEXT DEFAULT (datetime('now')),
            NotionPageId TEXT
        );
        """;

    private const string SchemaPayments = """
        CREATE TABLE IF NOT EXISTS Payments (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            BillId       TEXT NOT NULL REFERENCES Bills(Id),
            PeriodKey    TEXT NOT NULL,
            AmountPaid   REAL NOT NULL,
            PaidAt       TEXT DEFAULT (datetime('now')),
            NotionPageId TEXT
        );
        """;

    private const string SchemaSnapshots = """
        CREATE TABLE IF NOT EXISTS Snapshots (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            SnapshotDate TEXT NOT NULL,
            TotalOwed    REAL NOT NULL,
            Details      TEXT,
            NotionPageId TEXT
        );
        """;

    private const string SchemaSettings = """
        CREATE TABLE IF NOT EXISTS Settings (
            Key   TEXT PRIMARY KEY,
            Value TEXT
        );
        """;
}
