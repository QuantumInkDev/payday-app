using System.Globalization;
using PayDay.Sync.Model;

namespace PayDay.Sync.Ids;

/// <summary>Whether a paycheck gets the deterministic UUIDv5 or a random UUIDv7 (section 3.2).</summary>
public enum PaycheckIdKind
{
    UuidV5,
    UuidV7,
}

/// <summary>
/// Every deterministic ID rule of <c>spec/sync-protocol.md</c> section 3.2 and
/// <c>spec/data-model.md</c> sections 2 and 8. Derivations fail with <c>badInput</c> when an ID is
/// not a canonical lowercase UUID or a date is not a valid <c>YYYY-MM-DD</c>.
/// </summary>
public static class SyncIds
{
    /// <summary>Income-source cadences that get deterministic paycheck IDs.</summary>
    public static readonly IReadOnlyList<string> ScheduledCadences = new[] { "Weekly", "Bi-Weekly", "Semi-Monthly", "Monthly" };

    public static string RegularPaymentName(string billId, string periodKey)
    {
        RequireUuid(billId);
        RequireDate(periodKey);
        return $"payment/regular/{billId}/{periodKey}";
    }

    public static string RegularPaymentId(string billId, string periodKey) => Uuid.V5(RegularPaymentName(billId, periodKey));

    public static string SetAsideName(string billId, string periodKey)
    {
        RequireUuid(billId);
        RequireDate(periodKey);
        return $"payment/setaside/{billId}/{periodKey}";
    }

    public static string SetAsideId(string billId, string periodKey) => Uuid.V5(SetAsideName(billId, periodKey));

    public static string PaycheckName(string incomeSourceId, string payDate)
    {
        RequireUuid(incomeSourceId);
        RequireDate(payDate);
        return $"paycheck/{incomeSourceId}/{payDate}";
    }

    /// <summary>The deterministic paycheck ID; use it only when <see cref="KindForCadence"/> says <see cref="PaycheckIdKind.UuidV5"/>.</summary>
    public static string PaycheckId(string incomeSourceId, string payDate) => Uuid.V5(PaycheckName(incomeSourceId, payDate));

    /// <summary>Scheduled cadences get UUIDv5; <c>Irregular</c> and unknown cadences get a random UUIDv7.</summary>
    public static PaycheckIdKind KindForCadence(string? cadence)
        => cadence is not null && ScheduledCadences.Contains(cadence, StringComparer.Ordinal) ? PaycheckIdKind.UuidV5 : PaycheckIdKind.UuidV7;

    /// <summary>The ID for a newly recorded paycheck, chosen by the source's cadence at the time it is recorded.</summary>
    public static string NewPaycheckId(string incomeSourceId, string payDate, string? cadence, TimeProvider time)
        => KindForCadence(cadence) == PaycheckIdKind.UuidV5 ? PaycheckId(incomeSourceId, payDate) : Uuid.NewV7(time);

    /// <summary>The deterministic payment ID for a <c>Regular</c> or <c>SetAside</c> payment.</summary>
    public static string PaymentIdFor(string kind, string billId, string periodKey)
        => kind switch
        {
            PaymentKinds.Regular => RegularPaymentId(billId, periodKey),
            PaymentKinds.SetAside => SetAsideId(billId, periodKey),
            _ => throw new ArgumentException("Deterministic payment IDs exist only for Regular and SetAside.", nameof(kind)),
        };

    /// <summary><c>legacy-bill/&lt;oldId&gt;</c>, for an old ID that is not UUID-shaped. Fails with <c>notLegacy</c> otherwise.</summary>
    public static string LegacyBillName(string oldId)
    {
        if (string.IsNullOrEmpty(oldId) || Uuid.IsUuidShaped(oldId)) throw new SyncException(SyncErrorCodes.NotLegacy);
        return $"legacy-bill/{oldId}";
    }

    /// <summary>
    /// Bill ID migration (data-model 8.2): a UUID-shaped ID is lowercased; anything else maps to
    /// <c>UUIDv5("legacy-bill/" + oldId)</c> using the old ID exactly as stored.
    /// </summary>
    public static string MigrateBillId(string oldId)
    {
        ArgumentNullException.ThrowIfNull(oldId);
        return Uuid.IsUuidShaped(oldId) ? oldId.ToLowerInvariant() : Uuid.V5(LegacyBillName(oldId));
    }

    /// <summary>
    /// <c>legacy-payment/&lt;billId&gt;/&lt;periodKey&gt;/&lt;amountCents&gt;/&lt;paidAt&gt;/&lt;n&gt;</c>
    /// for extra payments made by the migration. <paramref name="paidAt"/> is a normalized timestamp or
    /// empty; <paramref name="occurrence"/> starts at 1.
    /// </summary>
    public static string LegacyPaymentName(string billId, string periodKey, long amountCents, string paidAt, long occurrence)
    {
        RequireUuid(billId);
        RequireDate(periodKey);
        ArgumentNullException.ThrowIfNull(paidAt);
        if (paidAt.Length != 0 && !SyncDates.IsTimestamp(paidAt)) throw new SyncException(SyncErrorCodes.BadInput);
        RequireOccurrence(occurrence);
        return string.Create(CultureInfo.InvariantCulture, $"legacy-payment/{billId}/{periodKey}/{amountCents}/{paidAt}/{occurrence}");
    }

    public static string LegacyPaymentId(string billId, string periodKey, long amountCents, string paidAt, long occurrence)
        => Uuid.V5(LegacyPaymentName(billId, periodKey, amountCents, paidAt, occurrence));

    /// <summary><c>legacy-snapshot/&lt;snapshotDate&gt;/&lt;totalCents&gt;/&lt;n&gt;</c> for migrated balance snapshots.</summary>
    public static string LegacySnapshotName(string snapshotDate, long totalCents, long occurrence)
    {
        RequireDate(snapshotDate);
        RequireOccurrence(occurrence);
        return string.Create(CultureInfo.InvariantCulture, $"legacy-snapshot/{snapshotDate}/{totalCents}/{occurrence}");
    }

    public static string LegacySnapshotId(string snapshotDate, long totalCents, long occurrence)
        => Uuid.V5(LegacySnapshotName(snapshotDate, totalCents, occurrence));

    private static void RequireUuid(string? s)
    {
        if (!Uuid.IsCanonical(s)) throw new SyncException(SyncErrorCodes.BadInput);
    }

    private static void RequireDate(string? s)
    {
        if (!SyncDates.IsDate(s)) throw new SyncException(SyncErrorCodes.BadInput);
    }

    private static void RequireOccurrence(long n)
    {
        if (n < 1) throw new SyncException(SyncErrorCodes.BadInput);
    }
}
