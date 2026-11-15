using PayDay.Sync.Clock;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>Derived values (<c>spec/data-model.md</c> section 5), all computed in integer cents.</summary>
public sealed partial class SyncState
{
    private const string RemainingCheckpointField = "remainingCheckpoint";
    private const string AmountPaidField = "amountPaid";
    private const string BillIdField = "billId";
    private const string KindField = "kind";
    private const string PeriodKeyField = "periodKey";

    /// <summary>
    /// The payment's kind: the stored string, or <c>Regular</c> when it is missing or not a string
    /// (<c>data-model.md</c> 1.2). An unknown string is returned as is and counts for nothing.
    /// </summary>
    public string PaymentKind(string paymentId)
        => Value(SyncEntities.Payment, paymentId, KindField) is { IsString: true } k ? k.StringValue : PaymentKinds.Regular;

    /// <summary>The payment's <c>billId</c>, or null when it is missing or not a string.</summary>
    public string? PaymentBillId(string paymentId)
        => Value(SyncEntities.Payment, paymentId, BillIdField) is { IsString: true } b ? b.StringValue : null;

    /// <summary>The HLC of the payment's winning <c>deleted</c> record (its mark HLC while live).</summary>
    public Hlc? MarkHlc(string paymentId)
        => Store.TryGet(SyncEntities.Payment, paymentId, SyncEntities.DeletedField, out var cell) ? cell.Hlc : null;

    /// <summary>The bill's checkpoint HLC: the HLC of its winning <c>remainingCheckpoint</c> record.</summary>
    public Hlc? CheckpointHlc(string billId)
        => Store.TryGet(SyncEntities.Bill, billId, RemainingCheckpointField, out var cell) ? cell.Hlc : null;

    /// <summary>A live payment whose mark HLC is at or before its bill's checkpoint HLC (section 5.1 of the protocol).</summary>
    public bool IsAbsorbed(string paymentId)
    {
        var bill = PaymentBillId(paymentId);
        if (bill is null || CheckpointHlc(bill) is not { } cp || MarkHlc(paymentId) is not { } mark) return false;
        return mark.CompareTo(cp) <= 0;
    }

    /// <summary>The live payments whose <c>billId</c> is <paramref name="billId"/>, sorted by ID (uses the store's value index).</summary>
    public IReadOnlyList<string> LivePaymentsFor(string billId)
    {
        ArgumentNullException.ThrowIfNull(billId);
        var ids = Store.IdsWhere(SyncEntities.Payment, BillIdField, SyncValue.String(billId))
            .Where(p => IsLive(SyncEntities.Payment, p))
            .ToArray();
        Array.Sort(ids, Json.Utf8Order.Instance);
        return ids;
    }

    /// <summary>Remaining before the clamp at zero, in cents: checkpoint minus counted payments after it.</summary>
    public long RemainingUnclampedCents(string billId)
    {
        var cpHlc = CheckpointHlc(billId);
        var cpCents = MoneyCents(SyncEntities.Bill, billId, RemainingCheckpointField);
        long paid = 0;
        foreach (var pid in LivePaymentsFor(billId))
        {
            if (!PaymentKinds.CountsAgainstRemaining(PaymentKind(pid))) continue;
            if (cpHlc is null || MarkHlc(pid)!.Value.CompareTo(cpHlc.Value) > 0)
            {
                paid += MoneyCents(SyncEntities.Payment, pid, AmountPaidField);
            }
        }
        return cpCents - paid;
    }

    /// <summary>Remaining (<c>data-model.md</c> 5.1), in cents: never below zero.</summary>
    public long RemainingCents(string billId) => Math.Max(0, RemainingUnclampedCents(billId));

    public double Remaining(string billId) => SyncMoney.FromCents(RemainingCents(billId));

    /// <summary>
    /// The set-aside balance (<c>data-model.md</c> 5.2), in cents: live SetAside payments in periods
    /// after the bill's latest live Regular payment. Payments whose <c>periodKey</c> is not a valid
    /// date are ignored here.
    /// </summary>
    public long SetAsideBalanceCents(string billId)
    {
        string? last = null;
        var setAsides = new List<(string Period, string Id)>();
        foreach (var pid in LivePaymentsFor(billId))
        {
            var period = Value(SyncEntities.Payment, pid, PeriodKeyField) is { IsString: true } p ? p.StringValue : null;
            if (!SyncDates.IsDate(period)) continue;
            var kind = PaymentKind(pid);
            if (kind == PaymentKinds.Regular)
            {
                if (last is null || string.CompareOrdinal(period, last) > 0) last = period;
            }
            else if (kind == PaymentKinds.SetAside)
            {
                setAsides.Add((period!, pid));
            }
        }
        long total = 0;
        foreach (var (period, pid) in setAsides)
        {
            if (last is null || string.CompareOrdinal(period, last) > 0)
            {
                total += MoneyCents(SyncEntities.Payment, pid, AmountPaidField);
            }
        }
        return total;
    }

    public double SetAsideBalance(string billId) => SyncMoney.FromCents(SetAsideBalanceCents(billId));

    /// <summary>A money field in cents, or 0 when it is missing, not a number, or out of range.</summary>
    public long MoneyCents(string entity, string id, string field)
        => Value(entity, id, field) is { IsNumber: true } v && SyncMoney.TryCents(v.NumberValue, out var c) ? c : 0;
}
