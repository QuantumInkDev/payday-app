using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>Who is setting a bill's Remaining (<c>sync-protocol.md</c> section 5.3).</summary>
public enum RemainingSource
{
    /// <summary>The bill editor.</summary>
    App,

    /// <summary>The Notion connector's pull.</summary>
    Notion,
}

/// <summary>
/// The writer rules of <c>spec/sync-protocol.md</c> section 5.3. Each method evaluates "live",
/// "absorbed" and "Remaining" on <paramref name="state"/> (this device's state at the moment the
/// user acts) and returns the records the action writes, all at <paramref name="hlc"/> (one tick),
/// sorted by entity, id and field. The caller applies them to its own state with
/// <see cref="SyncState.Merge(ChangeRecord)"/> and queues them for upload.
/// </summary>
public static class PaymentWriter
{
    private const string RemainingCheckpoint = "remainingCheckpoint";

    /// <summary>
    /// Mark a <c>Regular</c> or <c>SetAside</c> payment paid, using its deterministic ID. When the
    /// payment is already live only <c>amountPaid</c> and <c>paidAt</c> are written (so the mark HLC
    /// never moves); otherwise all six mark fields, including <c>deleted: false</c>.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> MarkPaid(SyncState state, string kind, string billId, string periodKey, double amount, string? paidAt, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pid = SyncIds.PaymentIdFor(kind, billId, periodKey);
        var amountValue = SyncValue.Number(SyncMoney.Round2(amount));
        var paidAtValue = SyncValue.StringOrNull(paidAt);
        if (state.IsLive(SyncEntities.Payment, pid))
        {
            return Sorted(
                Rec(pid, "amountPaid", amountValue, hlc),
                Rec(pid, "paidAt", paidAtValue, hlc));
        }
        return Sorted(
            Rec(pid, "amountPaid", amountValue, hlc),
            Rec(pid, "billId", SyncValue.String(billId), hlc),
            Rec(pid, SyncEntities.DeletedField, SyncValue.False, hlc),
            Rec(pid, "kind", SyncValue.String(kind), hlc),
            Rec(pid, "paidAt", paidAtValue, hlc),
            Rec(pid, "periodKey", SyncValue.String(periodKey), hlc));
    }

    /// <summary>Unmark a <c>Regular</c> or <c>SetAside</c> payment (see <see cref="DeletePayment"/>).</summary>
    public static IReadOnlyList<ChangeRecord> UnmarkPaid(SyncState state, string kind, string billId, string periodKey, Hlc hlc)
        => DeletePayment(state, SyncIds.PaymentIdFor(kind, billId, periodKey), hlc);

    /// <summary>
    /// Tombstone a payment. Nothing is written when it is not live. When it counts against
    /// Remaining, its bill is live and the checkpoint already absorbed it, the checkpoint is raised
    /// by its amount from the unclamped Remaining, at the same HLC.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> DeletePayment(SyncState state, string paymentId, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(paymentId);
        if (!state.IsLive(SyncEntities.Payment, paymentId)) return Array.Empty<ChangeRecord>();
        var output = new List<ChangeRecord> { Rec(paymentId, SyncEntities.DeletedField, SyncValue.True, hlc) };
        var bill = state.PaymentBillId(paymentId);
        if (PaymentKinds.CountsAgainstRemaining(state.PaymentKind(paymentId))
            && bill is not null
            && state.IsLive(SyncEntities.Bill, bill)
            && state.IsAbsorbed(paymentId))
        {
            var u = state.RemainingUnclampedCents(bill);
            var raised = Math.Max(0, u + state.MoneyCents(SyncEntities.Payment, paymentId, "amountPaid"));
            output.Add(new ChangeRecord(SyncEntities.Bill, bill, RemainingCheckpoint, SyncValue.Number(SyncMoney.FromCents(raised)), hlc));
        }
        return Sorted(output.ToArray());
    }

    /// <summary>
    /// Set a bill's Remaining. Nothing is written when the bill is not live, when the value is the
    /// Notion connector's own last push (an echo), or when it equals the Remaining computed now.
    /// </summary>
    /// <param name="lastPushed">The Remaining this device's connector last wrote to Notion for this bill, if any.</param>
    public static IReadOnlyList<ChangeRecord> SetRemaining(SyncState state, string billId, double value, RemainingSource source, double? lastPushed, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsLive(SyncEntities.Bill, billId)) return Array.Empty<ChangeRecord>();
        var v = SyncMoney.Cents(value);
        if (v < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "Remaining cannot be negative.");
        if (source == RemainingSource.Notion && lastPushed is { } pushed && SyncMoney.Cents(pushed) == v)
        {
            return Array.Empty<ChangeRecord>();
        }
        if (v == state.RemainingCents(billId)) return Array.Empty<ChangeRecord>();
        return new[] { new ChangeRecord(SyncEntities.Bill, billId, RemainingCheckpoint, SyncValue.Number(SyncMoney.FromCents(v)), hlc) };
    }

    /// <summary>
    /// Record an <c>Extra</c> payment: a create with a new random UUIDv7 (section 4.4 and 5.2).
    /// Returns the records; the new payment's ID is their <see cref="ChangeRecord.Id"/>.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> AddExtraPayment(string paymentId, string billId, string periodKey, double amount, string? paidAt, Hlc hlc, string? confirmationNumber = null, string? note = null)
    {
        if (!Uuid.IsCanonical(paymentId)) throw new SyncException(SyncErrorCodes.BadInput);
        if (!Uuid.IsCanonical(billId) || !SyncDates.IsDate(periodKey)) throw new SyncException(SyncErrorCodes.BadInput);
        var fields = new Dictionary<string, SyncValue>(StringComparer.Ordinal)
        {
            ["billId"] = SyncValue.String(billId),
            ["kind"] = SyncValue.String(PaymentKinds.Extra),
            ["periodKey"] = SyncValue.String(periodKey),
            ["amountPaid"] = SyncValue.Number(SyncMoney.Round2(amount)),
            ["paidAt"] = SyncValue.StringOrNull(paidAt),
            ["confirmationNumber"] = SyncValue.StringOrNull(confirmationNumber),
            ["note"] = SyncValue.StringOrNull(note),
        };
        return RowWriter.Create(SyncEntities.Payment, paymentId, fields, hlc);
    }

    private static ChangeRecord Rec(string paymentId, string field, SyncValue value, Hlc hlc)
        => new(SyncEntities.Payment, paymentId, field, value, hlc);

    private static IReadOnlyList<ChangeRecord> Sorted(params ChangeRecord[] records) => ChangeRecord.SortByKey(records);
}
