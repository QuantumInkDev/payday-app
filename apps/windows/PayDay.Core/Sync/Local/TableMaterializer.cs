using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Engine;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Storage;

namespace PayDay.Sync.Local;

/// <summary>
/// Keeps today's tables (<c>Bills</c>, <c>Payments</c>, <c>Snapshots</c> and the synced keys of
/// <c>Settings</c>) equal to the merged state, so every page keeps reading the tables it reads today.
/// It runs on the sync connection inside the replica's transaction, so the tables and the records
/// commit or roll back together.
/// <list type="bullet">
///   <item><c>Bills</c>: live bills, with <c>Remaining</c> = computed Remaining (<c>data-model.md</c> 5.1)
///     and <c>UpdatedAt</c> = the latest HLC wall time of the row's records.</item>
///   <item><c>Payments</c>: live <c>Regular</c> and <c>Extra</c> payments of live bills (set-asides and
///     unknown kinds are not shown, and a deleted bill's payments are hidden, never removed).</item>
///   <item><c>Snapshots</c>: live balance snapshots.</item>
///   <item><c>Settings</c>: <c>PayAnchor</c>, <c>EarlyStart</c>, <c>TypeColors</c> (built from every
///     <c>TypeColor/&lt;type&gt;</c> row), <c>SafeToSpendBuffer</c>, <c>ReminderDaysBefore</c> and the
///     <c>Notion*Db</c> IDs. Per-device keys (<c>Theme</c>, <c>LastNotionSync</c>, <c>SchemaVersion</c>, …)
///     are never touched.</item>
/// </list>
/// Integer <c>Payments.Id</c> and <c>Snapshots.Id</c> values are kept per synced row in
/// <c>sync_local_ids</c>, so a row keeps its local ID while it is shown and gets it back when it is
/// shown again.
/// </summary>
public sealed class TableMaterializer : ISyncMaterializer
{
    /// <summary>The <c>Settings</c> keys the materializer owns while sync is on.</summary>
    public static readonly IReadOnlyList<string> SyncedSettingKeys = new[]
    {
        SyncSettingKeys.PayAnchor,
        SyncSettingKeys.EarlyStart,
        TypeColorService.SettingKey,
        SyncSettingKeys.SafeToSpendBuffer,
        SyncSettingKeys.ReminderDaysBefore,
        SyncSettingKeys.NotionBillsDb,
        SyncSettingKeys.NotionPaymentsDb,
        SyncSettingKeys.NotionSnapshotsDb,
    };

    private readonly SqliteSyncStorage _storage;

    public TableMaterializer(SqliteSyncStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public void RowsChanged(SyncState state, IReadOnlyCollection<RowKey> rows)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rows);
        var bills = new SortedSet<string>(StringComparer.Ordinal);
        var snapshots = new SortedSet<string>(StringComparer.Ordinal);
        var settings = false;
        foreach (var row in rows)
        {
            switch (row.Entity)
            {
                case SyncEntities.Bill:
                    bills.Add(row.Id);
                    break;
                case SyncEntities.Payment:
                    if (state.PaymentBillId(row.Id) is { } billId) bills.Add(billId);
                    if (LocalId(SyncEntities.Payment, row.Id) is { } local)
                    {
                        if (TableBillIdOfPayment(local) is { } shownUnder) bills.Add(shownUnder);
                        DeletePaymentRow(local);
                    }
                    break;
                case SyncEntities.BalanceSnapshot:
                    snapshots.Add(row.Id);
                    break;
                case SyncEntities.Setting:
                    settings = true;
                    break;
            }
        }
        foreach (var bill in bills) WriteBill(state, bill);
        foreach (var snapshot in snapshots) WriteSnapshot(state, snapshot);
        if (settings) WriteSettings(state);
    }

    /// <summary>
    /// Rewrites every materialized table from the state: used by the migration (bill IDs change), and
    /// after joining a vault replaced the records (<c>SyncReplica.Start(..., clearSyncedData: true)</c>
    /// does not call the materializer). Run it inside a sync transaction.
    /// </summary>
    public void RebuildAll(SyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Execute("DELETE FROM Payments;");
        Execute("DELETE FROM Snapshots;");
        Execute("DELETE FROM Bills;");
        foreach (var bill in state.LiveIds(SyncEntities.Bill)) WriteBill(state, bill);
        foreach (var snapshot in state.LiveIds(SyncEntities.BalanceSnapshot)) WriteSnapshot(state, snapshot);
        WriteSettings(state);
    }

    /// <summary>The local integer ID shown for a synced payment or snapshot, if it has one.</summary>
    public long? LocalId(string entity, string syncId)
    {
        var cmd = _storage.Cached("SELECT local_id FROM sync_local_ids WHERE entity = $e AND sync_id = $s;", "$e", "$s");
        cmd.Parameters["$e"].Value = entity;
        cmd.Parameters["$s"].Value = syncId;
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>The synced row behind a local integer ID, if any.</summary>
    public string? SyncIdOf(string entity, long localId)
    {
        var cmd = _storage.Cached("SELECT sync_id FROM sync_local_ids WHERE entity = $e AND local_id = $l;", "$e", "$l");
        cmd.Parameters["$e"].Value = entity;
        cmd.Parameters["$l"].Value = localId;
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Replaces the local-ID map of one entity (the migration keeps today's integer IDs).</summary>
    public void SetLocalIds(string entity, IReadOnlyDictionary<string, long> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        using (var delete = _storage.CreateCommand("DELETE FROM sync_local_ids WHERE entity = $e;"))
        {
            delete.Parameters.AddWithValue("$e", entity);
            delete.ExecuteNonQuery();
        }
        foreach (var (syncId, localId) in map) MapLocalId(entity, syncId, localId);
    }

    // ---- Bills and their payments -------------------------------------------------------------------

    private void WriteBill(SyncState state, string billId)
    {
        using (var delete = _storage.CreateCommand("DELETE FROM Payments WHERE BillId = $b;"))
        {
            delete.Parameters.AddWithValue("$b", billId);
            delete.ExecuteNonQuery();
        }
        if (!state.IsLive(SyncEntities.Bill, billId))
        {
            using var deleteBill = _storage.CreateCommand("DELETE FROM Bills WHERE Id = $b;");
            deleteBill.Parameters.AddWithValue("$b", billId);
            deleteBill.ExecuteNonQuery();
            return;
        }
        var row = BillFromState(state, billId);
        if (LegacyUpdatedAt(state, billId) is { } legacy) row.UpdatedAt = legacy.UpdatedAt;
        UpsertBillRow(row);
        foreach (var paymentId in state.LivePaymentsFor(billId))
        {
            if (!PaymentKinds.CountsAgainstRemaining(state.PaymentKind(paymentId))) continue;
            if (state.Value(SyncEntities.Payment, paymentId, "periodKey") is not { IsString: true } period) continue;
            InsertPaymentRow(paymentId, new Payment
            {
                BillId = billId,
                PeriodKey = period.StringValue,
                AmountPaid = SyncMoney.FromCents(state.MoneyCents(SyncEntities.Payment, paymentId, "amountPaid")),
                PaidAt = NullableString(state, SyncEntities.Payment, paymentId, "paidAt"),
                NotionPageId = NullableString(state, SyncEntities.Payment, paymentId, "notionPageId"),
            });
        }
    }

    /// <summary>The bill as the pages see it: defaults for missing or wrong-type fields, computed Remaining.</summary>
    public static Bill BillFromState(SyncState state, string billId)
    {
        ArgumentNullException.ThrowIfNull(state);
        const string e = SyncEntities.Bill;
        var dueDay = state.Read(e, billId, "dueDay").NumberValue;
        return new Bill
        {
            Id = billId,
            Name = state.Read(e, billId, "name").StringValue,
            Type = state.Read(e, billId, "type").StringValue,
            Payment = SyncMoney.FromCents(state.MoneyCents(e, billId, "payment")),
            Remaining = state.Remaining(billId),
            Available = SyncMoney.FromCents(state.MoneyCents(e, billId, "available")),
            CreditLimit = SyncMoney.FromCents(state.MoneyCents(e, billId, "creditLimit")),
            DueDay = (int)Math.Clamp(dueDay, int.MinValue, int.MaxValue),
            Rate = state.Read(e, billId, "rate").StringValue,
            APR = state.Read(e, billId, "apr").NumberValue,
            AutoPay = state.Read(e, billId, "autoPay").BoolValue,
            Active = state.Read(e, billId, "active").BoolValue,
            YearlyDate = NullableString(state, e, billId, "yearlyDate"),
            Notes = NullableString(state, e, billId, "notes"),
            NotionPageId = NullableString(state, e, billId, "notionPageId"),
            CreatedAt = TableTime(NullableString(state, e, billId, "createdAt")),
            UpdatedAt = state.Store.Fields(e, billId).Select(f => f.Value.Hlc.Wall).DefaultIfEmpty(-1).Max() is var wall and >= 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(wall).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : null,
        };
    }

    /// <summary>
    /// The bill's <c>UpdatedAt</c> from before sync, while nothing has changed it since the migration:
    /// its latest record still carries the migration HLC. Otherwise null, and <c>UpdatedAt</c> is
    /// derived from the HLCs (<c>data-model.md</c> 4.1). Without this every migrated bill would look
    /// edited at the moment sync was turned on, newer than every Notion page.
    /// </summary>
    private LegacyStamp? LegacyUpdatedAt(SyncState state, string billId)
    {
        var cmd = _storage.Cached("SELECT updated_at, hlc FROM sync_legacy_updated WHERE bill_id = $b;", "$b");
        Bind(cmd, "$b", billId);
        string? updatedAt;
        string hlc;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            updatedAt = reader.IsDBNull(0) ? null : reader.GetString(0);
            hlc = reader.GetString(1);
        }
        var latest = state.Store.Fields(SyncEntities.Bill, billId).Select(f => f.Value.Hlc.Text).DefaultIfEmpty(string.Empty).Max(StringComparer.Ordinal);
        return latest == hlc ? new LegacyStamp(updatedAt) : null;
    }

    /// <summary>Remembers each migrated bill's <c>UpdatedAt</c> and the migration HLC (see <see cref="LegacyUpdatedAt"/>). Inside the migration transaction.</summary>
    internal void SetLegacyUpdatedAt(IReadOnlyDictionary<string, string?> updatedAtByBill, Clock.Hlc migrationHlc)
    {
        ArgumentNullException.ThrowIfNull(updatedAtByBill);
        Execute("DELETE FROM sync_legacy_updated;");
        var cmd = _storage.Cached("INSERT INTO sync_legacy_updated (bill_id, updated_at, hlc) VALUES ($b, $u, $h);", "$b", "$u", "$h");
        foreach (var (billId, updatedAt) in updatedAtByBill)
        {
            Bind(cmd, "$b", billId);
            Bind(cmd, "$u", updatedAt);
            Bind(cmd, "$h", migrationHlc.Text);
            cmd.ExecuteNonQuery();
        }
    }

    private void UpsertBillRow(Bill bill)
    {
        var cmd = _storage.Cached(
            """
            INSERT INTO Bills (Id, Name, Type, Payment, Remaining, Available, CreditLimit, DueDay, Rate, APR,
                               AutoPay, Active, YearlyDate, Notes, CreatedAt, UpdatedAt, NotionPageId)
            VALUES ($Id, $Name, $Type, $Payment, $Remaining, $Available, $CreditLimit, $DueDay, $Rate, $APR,
                    $AutoPay, $Active, $YearlyDate, $Notes, $CreatedAt, $UpdatedAt, $NotionPageId)
            ON CONFLICT (Id) DO UPDATE SET
                Name = excluded.Name, Type = excluded.Type, Payment = excluded.Payment, Remaining = excluded.Remaining,
                Available = excluded.Available, CreditLimit = excluded.CreditLimit, DueDay = excluded.DueDay,
                Rate = excluded.Rate, APR = excluded.APR, AutoPay = excluded.AutoPay, Active = excluded.Active,
                YearlyDate = excluded.YearlyDate, Notes = excluded.Notes, CreatedAt = excluded.CreatedAt,
                UpdatedAt = excluded.UpdatedAt, NotionPageId = excluded.NotionPageId;
            """,
            "$Id", "$Name", "$Type", "$Payment", "$Remaining", "$Available", "$CreditLimit", "$DueDay", "$Rate", "$APR",
            "$AutoPay", "$Active", "$YearlyDate", "$Notes", "$CreatedAt", "$UpdatedAt", "$NotionPageId");
        Bind(cmd, "$Id", bill.Id);
        Bind(cmd, "$Name", bill.Name);
        Bind(cmd, "$Type", bill.Type);
        Bind(cmd, "$Payment", bill.Payment);
        Bind(cmd, "$Remaining", bill.Remaining);
        Bind(cmd, "$Available", bill.Available);
        Bind(cmd, "$CreditLimit", bill.CreditLimit);
        Bind(cmd, "$DueDay", bill.DueDay);
        Bind(cmd, "$Rate", bill.Rate);
        Bind(cmd, "$APR", bill.APR);
        Bind(cmd, "$AutoPay", bill.AutoPay ? 1 : 0);
        Bind(cmd, "$Active", bill.Active ? 1 : 0);
        Bind(cmd, "$YearlyDate", bill.YearlyDate);
        Bind(cmd, "$Notes", bill.Notes);
        Bind(cmd, "$CreatedAt", bill.CreatedAt);
        Bind(cmd, "$UpdatedAt", bill.UpdatedAt);
        Bind(cmd, "$NotionPageId", bill.NotionPageId);
        cmd.ExecuteNonQuery();
    }

    private void InsertPaymentRow(string paymentId, Payment payment)
    {
        var local = LocalId(SyncEntities.Payment, paymentId);
        var cmd = _storage.Cached(
            """
            INSERT INTO Payments (Id, BillId, PeriodKey, AmountPaid, PaidAt, NotionPageId)
            VALUES ($Id, $BillId, $PeriodKey, $AmountPaid, $PaidAt, $NotionPageId);
            SELECT last_insert_rowid();
            """,
            "$Id", "$BillId", "$PeriodKey", "$AmountPaid", "$PaidAt", "$NotionPageId");
        Bind(cmd, "$Id", local);
        Bind(cmd, "$BillId", payment.BillId);
        Bind(cmd, "$PeriodKey", payment.PeriodKey);
        Bind(cmd, "$AmountPaid", payment.AmountPaid);
        Bind(cmd, "$PaidAt", payment.PaidAt);
        Bind(cmd, "$NotionPageId", payment.NotionPageId);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (local is null) MapLocalId(SyncEntities.Payment, paymentId, id);
    }

    private string? TableBillIdOfPayment(long localId)
    {
        var cmd = _storage.Cached("SELECT BillId FROM Payments WHERE Id = $Id;", "$Id");
        Bind(cmd, "$Id", localId);
        return cmd.ExecuteScalar() as string;
    }

    private void DeletePaymentRow(long localId)
    {
        var cmd = _storage.Cached("DELETE FROM Payments WHERE Id = $Id;", "$Id");
        Bind(cmd, "$Id", localId);
        cmd.ExecuteNonQuery();
    }

    // ---- Snapshots ----------------------------------------------------------------------------------

    private void WriteSnapshot(SyncState state, string snapshotId)
    {
        const string e = SyncEntities.BalanceSnapshot;
        var local = LocalId(e, snapshotId);
        if (!state.IsLive(e, snapshotId))
        {
            if (local is { } shown)
            {
                var delete = _storage.Cached("DELETE FROM Snapshots WHERE Id = $Id;", "$Id");
                Bind(delete, "$Id", shown);
                delete.ExecuteNonQuery();
            }
            return;
        }
        var cmd = _storage.Cached(
            """
            INSERT INTO Snapshots (Id, SnapshotDate, TotalRemaining, Details, NotionPageId)
            VALUES ($Id, $Date, $Total, $Details, $NotionPageId)
            ON CONFLICT (Id) DO UPDATE SET
                SnapshotDate = excluded.SnapshotDate, TotalRemaining = excluded.TotalRemaining,
                Details = excluded.Details, NotionPageId = excluded.NotionPageId;
            SELECT last_insert_rowid();
            """,
            "$Id", "$Date", "$Total", "$Details", "$NotionPageId");
        Bind(cmd, "$Id", local);
        Bind(cmd, "$Date", state.Read(e, snapshotId, "snapshotDate").StringValue);
        Bind(cmd, "$Total", SyncMoney.FromCents(state.MoneyCents(e, snapshotId, "totalRemaining")));
        Bind(cmd, "$Details", NullableString(state, e, snapshotId, "details"));
        Bind(cmd, "$NotionPageId", NullableString(state, e, snapshotId, "notionPageId"));
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (local is null) MapLocalId(e, snapshotId, id);
    }

    // ---- Settings -----------------------------------------------------------------------------------

    private void WriteSettings(SyncState state)
    {
        foreach (var key in SyncedSettingKeys)
        {
            var text = key == TypeColorService.SettingKey ? TypeColorsJson(state) : SettingText(state, key);
            if (text is null)
            {
                var delete = _storage.Cached("DELETE FROM Settings WHERE Key = $k;", "$k");
                Bind(delete, "$k", key);
                delete.ExecuteNonQuery();
            }
            else
            {
                var upsert = _storage.Cached(
                    "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;",
                    "$k", "$v");
                Bind(upsert, "$k", key);
                Bind(upsert, "$v", text);
                upsert.ExecuteNonQuery();
            }
        }
    }

    /// <summary>The <c>Settings</c> text today's code reads for one synced setting, or null for "not set".</summary>
    internal static string? SettingText(SyncState state, string key)
    {
        if (state.Value(SyncEntities.Setting, key, SyncEntities.ValueField) is not { } v) return null;
        switch (key)
        {
            case SyncSettingKeys.PayAnchor:
                return v.IsString && SyncDates.IsDate(v.StringValue) ? v.StringValue : null;
            case SyncSettingKeys.EarlyStart:
                return v.IsBool ? (v.BoolValue ? "true" : "false") : null;
            case SyncSettingKeys.SafeToSpendBuffer:
                return v.IsNumber && SyncMoney.TryCents(v.NumberValue, out var cents)
                    ? SyncMoney.FromCents(cents).ToString("R", CultureInfo.InvariantCulture)
                    : null;
            case SyncSettingKeys.ReminderDaysBefore:
                return v.IsNumber && v.NumberValue >= 0 && Math.Floor(v.NumberValue) == v.NumberValue && v.NumberValue <= Json.StrictJson.MaxSafeInteger
                    ? ((long)v.NumberValue).ToString(CultureInfo.InvariantCulture)
                    : null;
            default:
                return v.IsString ? v.StringValue : null;
        }
    }

    /// <summary>
    /// Today's <c>TypeColors</c> JSON object from every <c>TypeColor/&lt;type&gt;</c> row with a
    /// <c>#RRGGBB</c> value. Types are matched case-insensitively; between two keys that differ only in
    /// case, the one whose value record has the greater HLC wins (<c>data-model.md</c> 6.1).
    /// </summary>
    internal static string? TypeColorsJson(SyncState state)
    {
        var winners = new Dictionary<string, (string Type, string Hex, Clock.Hlc Hlc)>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in state.Ids(SyncEntities.Setting))
        {
            if (!key.StartsWith(SyncSettingKeys.TypeColorPrefix, StringComparison.Ordinal)) continue;
            var type = key.Substring(SyncSettingKeys.TypeColorPrefix.Length);
            if (type.Length == 0 || !state.TryGet(SyncEntities.Setting, key, SyncEntities.ValueField, out var cell)) continue;
            if (winners.TryGetValue(type, out var current) && current.Hlc.CompareTo(cell.Hlc) > 0) continue;
            if (cell.Value.IsString && IsHexColor(cell.Value.StringValue))
            {
                winners[type] = (type, cell.Value.StringValue.ToUpperInvariant(), cell.Hlc);
            }
            else
            {
                // A reset (null) or unusable value with the greater HLC wins too: that type has no override.
                winners[type] = (type, string.Empty, cell.Hlc);
            }
        }
        var colors = winners.Values
            .Where(w => w.Hex.Length > 0)
            .OrderBy(w => w.Type, StringComparer.Ordinal)
            .ToDictionary(w => w.Type, w => w.Hex, StringComparer.Ordinal);
        return colors.Count == 0 ? null : JsonSerializer.Serialize(colors);
    }

    private static bool IsHexColor(string s)
        => s.Length == 7 && s[0] == '#' && s.Skip(1).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    // ---- Helpers ------------------------------------------------------------------------------------

    /// <summary>A bill's pre-sync <c>UpdatedAt</c> (which may itself be null).</summary>
    private sealed record LegacyStamp(string? UpdatedAt);

    /// <summary>Forgets the local integer IDs (joining a vault replaces every synced row). Inside a sync transaction.</summary>
    public void ClearLocalIds()
    {
        Execute("DELETE FROM sync_local_ids;");
        Execute("DELETE FROM sync_legacy_updated;");
    }

    private void MapLocalId(string entity, string syncId, long localId)
    {
        var cmd = _storage.Cached(
            "INSERT INTO sync_local_ids (entity, sync_id, local_id) VALUES ($e, $s, $l) ON CONFLICT (entity, sync_id) DO UPDATE SET local_id = excluded.local_id;",
            "$e", "$s", "$l");
        Bind(cmd, "$e", entity);
        Bind(cmd, "$s", syncId);
        Bind(cmd, "$l", localId);
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var cmd = _storage.CreateCommand(sql);
        cmd.ExecuteNonQuery();
    }

    private static string? NullableString(SyncState state, string entity, string id, string field)
        => state.Read(entity, id, field) is { IsString: true } s ? s.StringValue : null;

    /// <summary>A protocol timestamp in the <c>datetime('now')</c> form today's tables hold (<c>YYYY-MM-DD HH:MM:SS</c>, UTC).</summary>
    private static string? TableTime(string? timestamp)
        => timestamp is not null && SyncDates.IsTimestamp(timestamp)
            ? timestamp.Substring(0, 10) + " " + timestamp.Substring(11, 8)
            : timestamp;

    private static void Bind(SqliteCommand cmd, string name, object? value)
        => cmd.Parameters[name].Value = value ?? DBNull.Value;
}
