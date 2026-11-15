using System.Globalization;
using System.Text;
using System.Text.Json;
using PayDay.Models;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Local;

/// <summary>Today's data cannot be mapped without losing a row; nothing was changed.</summary>
public sealed class LegacyMigrationException : InvalidOperationException
{
    public LegacyMigrationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Synced rows built from today's data: the plan-entity rows by entity and ID (every field except
/// <c>deleted</c>), plus the old integer IDs of the migrated payments and snapshots.
/// </summary>
public sealed class LegacyRows
{
    public Dictionary<string, Dictionary<string, IReadOnlyDictionary<string, SyncValue>>> Rows { get; } = new(StringComparer.Ordinal)
    {
        [SyncEntities.Bill] = new(StringComparer.Ordinal),
        [SyncEntities.Payment] = new(StringComparer.Ordinal),
        [SyncEntities.BalanceSnapshot] = new(StringComparer.Ordinal),
    };

    /// <summary>Old <c>Payments.Id</c> by new payment ID.</summary>
    public Dictionary<string, long> PaymentLocalIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Old <c>Snapshots.Id</c> by new balance-snapshot ID.</summary>
    public Dictionary<string, long> SnapshotLocalIds { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The mapping of today's Windows data onto the synced model (<c>spec/data-model.md</c> section 8),
/// shared by the one-time migration of the tables and by restoring a JSON backup with sync on:
/// legacy bill IDs to UUIDv5, payments and snapshots to content-derived UUIDv5 IDs, references
/// rewritten, money rounded, timestamps normalized.
/// </summary>
public static class LegacyMapping
{
    /// <summary>
    /// Bill ID migration (8.2): a GUID is lowercased, anything else maps to
    /// <c>UUIDv5("legacy-bill/" + oldId)</c>. An empty ID uses the same name rule (<c>legacy-bill/</c>),
    /// which the reference helper rejects; see the spec issues in the task report.
    /// </summary>
    public static string MapBillId(string? oldId)
    {
        var id = oldId ?? string.Empty;
        return id.Length == 0 ? Uuid.V5("legacy-bill/") : SyncIds.MigrateBillId(id);
    }

    /// <summary>
    /// A legacy timestamp (<c>datetime('now')</c> <c>YYYY-MM-DD HH:MM:SS</c> or a C# <c>"O"</c> string)
    /// as a protocol timestamp: parsed as UTC when it has no zone, milliseconds, sub-millisecond digits
    /// truncated (8.2, 8.3). Null when there is none or it cannot be read.
    /// </summary>
    public static string? NormalizeTimestamp(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (SyncDates.IsTimestamp(text)
            && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        {
            return text;
        }
        if (!text.All(c => c < 128)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? SyncDates.FormatTimestamp(parsed)
            : null;
    }

    /// <summary>A money value as a writer stores it (<c>round2</c>); a value that is not finite or out of range becomes 0.</summary>
    public static double Money(double x) => SyncMoney.TryCents(x, out var c) ? SyncMoney.FromCents(c) : 0;

    /// <summary>
    /// The bill's editable fields as synced values (every field of section 4.1 except
    /// <c>remainingCheckpoint</c>, <c>createdAt</c>, <c>targetPayoffDate</c> and <c>deleted</c>).
    /// </summary>
    public static Dictionary<string, SyncValue> EditableBillFields(Bill b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return new Dictionary<string, SyncValue>(StringComparer.Ordinal)
        {
            ["name"] = SyncValue.String(b.Name ?? string.Empty),
            ["type"] = SyncValue.String(b.Type ?? string.Empty),
            ["payment"] = SyncValue.Number(Money(b.Payment)),
            ["available"] = SyncValue.Number(Money(b.Available)),
            ["creditLimit"] = SyncValue.Number(Money(b.CreditLimit)),
            ["dueDay"] = SyncValue.Number(b.DueDay),
            ["rate"] = SyncValue.String(b.Rate ?? "Monthly"),
            ["apr"] = SyncValue.Number(double.IsFinite(b.APR) ? b.APR : 0),
            ["autoPay"] = SyncValue.Bool(b.AutoPay),
            ["active"] = SyncValue.Bool(b.Active),
            ["yearlyDate"] = SyncValue.StringOrNull(b.YearlyDate),
            ["notes"] = SyncValue.StringOrNull(b.Notes),
            ["notionPageId"] = SyncValue.StringOrNull(b.NotionPageId),
        };
    }

    /// <summary>Every field of a bill for a create, a migration or a restore (all but <c>deleted</c>).</summary>
    public static Dictionary<string, SyncValue> AllBillFields(Bill b, string? createdAt)
    {
        var fields = EditableBillFields(b);
        fields["remainingCheckpoint"] = SyncValue.Number(Money(b.Remaining));
        fields["targetPayoffDate"] = SyncValue.Null;
        fields["createdAt"] = SyncValue.StringOrNull(createdAt);
        return fields;
    }

    /// <summary>
    /// Maps today's bills, payments and snapshots (8.2–8.4). Payments are grouped by (new bill ID,
    /// period) in old <c>Id</c> order: the first becomes the <c>Regular</c> payment with the
    /// deterministic ID, the rest <c>Extra</c> payments with
    /// <c>legacy-payment/&lt;billId&gt;/&lt;periodKey&gt;/&lt;amountCents&gt;/&lt;paidAt&gt;/&lt;n&gt;</c>, where
    /// <c>n</c> numbers the rows of that exact content (the Regular one included) in old <c>Id</c>
    /// order. Throws <see cref="LegacyMigrationException"/> rather than drop or merge a row.
    /// </summary>
    public static LegacyRows Map(IReadOnlyList<Bill> bills, IReadOnlyList<Payment> payments, IReadOnlyList<Snapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(snapshots);
        var result = new LegacyRows();

        var billRows = result.Rows[SyncEntities.Bill];
        foreach (var bill in bills)
        {
            var id = MapBillId(bill.Id);
            if (billRows.ContainsKey(id))
            {
                throw new LegacyMigrationException($"Two bills map to the same synced ID {id} (bill \"{bill.Id}\"); rename one before turning sync on.");
            }
            billRows[id] = AllBillFields(bill, NormalizeTimestamp(bill.CreatedAt));
        }

        var paymentRows = result.Rows[SyncEntities.Payment];
        var regularTaken = new HashSet<(string Bill, string Period)>();
        var occurrences = new Dictionary<(string Bill, string Period, long Cents, string PaidAt), long>();
        foreach (var p in payments.OrderBy(p => p.Id))
        {
            var billId = MapBillId(p.BillId);
            if (!SyncDates.IsDate(p.PeriodKey))
            {
                throw new LegacyMigrationException($"Payment {p.Id.ToString(CultureInfo.InvariantCulture)} has a period key that is not a date (\"{p.PeriodKey}\").");
            }
            var cents = SyncMoney.TryCents(p.AmountPaid, out var c) ? c : 0;
            var paidAt = NormalizeTimestamp(p.PaidAt) ?? string.Empty;
            var content = (billId, p.PeriodKey, cents, paidAt);
            var n = occurrences.TryGetValue(content, out var seen) ? seen + 1 : 1;
            occurrences[content] = n;

            string id;
            string kind;
            if (regularTaken.Add((billId, p.PeriodKey)))
            {
                id = SyncIds.RegularPaymentId(billId, p.PeriodKey);
                kind = PaymentKinds.Regular;
            }
            else
            {
                id = SyncIds.LegacyPaymentId(billId, p.PeriodKey, cents, paidAt, n);
                kind = PaymentKinds.Extra;
            }
            paymentRows[id] = new Dictionary<string, SyncValue>(StringComparer.Ordinal)
            {
                ["billId"] = SyncValue.String(billId),
                ["kind"] = SyncValue.String(kind),
                ["periodKey"] = SyncValue.String(p.PeriodKey),
                ["amountPaid"] = SyncValue.Number(SyncMoney.FromCents(cents)),
                ["paidAt"] = paidAt.Length == 0 ? SyncValue.Null : SyncValue.String(paidAt),
                ["confirmationNumber"] = SyncValue.Null,
                ["note"] = SyncValue.Null,
                ["notionPageId"] = SyncValue.StringOrNull(p.NotionPageId),
            };
            result.PaymentLocalIds[id] = p.Id;
        }

        var snapshotRows = result.Rows[SyncEntities.BalanceSnapshot];
        var snapshotOccurrences = new Dictionary<(string Date, long Cents), long>();
        foreach (var s in snapshots.OrderBy(s => s.Id))
        {
            if (!SyncDates.IsDate(s.SnapshotDate))
            {
                throw new LegacyMigrationException($"Snapshot {s.Id.ToString(CultureInfo.InvariantCulture)} has a date that is not a date (\"{s.SnapshotDate}\").");
            }
            var cents = SyncMoney.TryCents(s.TotalRemaining, out var c) ? c : 0;
            var key = (s.SnapshotDate, cents);
            var n = snapshotOccurrences.TryGetValue(key, out var seen) ? seen + 1 : 1;
            snapshotOccurrences[key] = n;
            var id = SyncIds.LegacySnapshotId(s.SnapshotDate, cents, n);
            snapshotRows[id] = new Dictionary<string, SyncValue>(StringComparer.Ordinal)
            {
                ["snapshotDate"] = SyncValue.String(s.SnapshotDate),
                ["totalRemaining"] = SyncValue.Number(SyncMoney.FromCents(cents)),
                ["details"] = SyncValue.StringOrNull(RewriteDetails(s.Details)),
                ["notionPageId"] = SyncValue.StringOrNull(s.NotionPageId),
            };
            result.SnapshotLocalIds[id] = s.Id;
        }
        return result;
    }

    /// <summary>
    /// <c>Snapshots.Details</c> with its keys (bill IDs) migrated like bill IDs. Text that is not a
    /// JSON object is kept as it is.
    /// </summary>
    public static string? RewriteDetails(string? details)
    {
        if (string.IsNullOrEmpty(details)) return details;
        try
        {
            using var doc = JsonDocument.Parse(details);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return details;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(MapBillId(prop.Name));
                    prop.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return details;
        }
    }

    /// <summary>A settings table (or a backup's settings object) as the JSON object <see cref="Merge.BackupSettings"/> reads.</summary>
    public static JsonData SettingsJson(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonData.Object(settings
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, JsonData>(kv.Key, kv.Value is null ? JsonData.Null : JsonData.String(kv.Value))));
    }
}
