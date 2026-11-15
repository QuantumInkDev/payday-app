/// Derived values (spec/data-model.md section 5, spec/sync-protocol.md section 5): computed from
/// the merged cells, never stored. All sums are in integer cents.
extension RecordStore {
    /// `cents()` of a money cell; 0 when missing, not a number, or out of range.
    func moneyCents(entity: String, id: String, field: String) -> Int64 {
        guard let d = value(entity: entity, id: id, field: field)?.numberValue, let c = try? Money.cents(d) else { return 0 }
        return c
    }

    /// Live payment IDs whose `billId` is `bill`.
    func livePaymentIDs(bill: String) -> [String] {
        rowIDs(entity: Entity.payment).filter {
            isLive(entity: Entity.payment, id: $0)
                && value(entity: Entity.payment, id: $0, field: "billId") == .string(bill)
        }
    }

    /// The payment's kind; a missing or wrong-type kind reads as `Regular`.
    public func paymentKind(_ paymentId: String) -> PaymentKind {
        PaymentKind(value(entity: Entity.payment, id: paymentId, field: "kind"))
    }

    /// The HLC of the payment's winning `deleted` record.
    public func markHLC(_ paymentId: String) -> HLC? {
        cell(entity: Entity.payment, id: paymentId, field: "deleted")?.hlc
    }

    /// The bill's checkpoint HLC: that of its winning `remainingCheckpoint` record.
    public func checkpointHLC(bill: String) -> HLC? {
        cell(entity: Entity.bill, id: bill, field: "remainingCheckpoint")?.hlc
    }

    /// A payment whose mark HLC is at or before its bill's checkpoint HLC: the checkpoint already
    /// includes it.
    public func isAbsorbed(_ paymentId: String) -> Bool {
        guard let bill = value(entity: Entity.payment, id: paymentId, field: "billId")?.stringValue,
              let cp = checkpointHLC(bill: bill), let mark = markHLC(paymentId) else { return false }
        return mark <= cp
    }

    /// `cents(checkpoint) − counted payments after the checkpoint`, before the clamp at zero.
    public func remainingUnclampedCents(bill: String) -> Int64 {
        let cpHLC = checkpointHLC(bill: bill)
        var total = moneyCents(entity: Entity.bill, id: bill, field: "remainingCheckpoint")
        for pid in livePaymentIDs(bill: bill) where paymentKind(pid).countsAgainstRemaining {
            if let cpHLC, let mark = markHLC(pid), mark <= cpHLC { continue } // absorbed
            total -= moneyCents(entity: Entity.payment, id: pid, field: "amountPaid")
        }
        return total
    }

    /// Remaining in cents: `max(0, checkpoint − payments marked strictly after it)`.
    public func remainingCents(bill: String) -> Int64 { max(0, remainingUnclampedCents(bill: bill)) }

    /// Set-aside balance in cents: live SetAside payments with a valid `periodKey` after the
    /// latest valid period of a live Regular payment (string order equals date order).
    public func setAsideBalanceCents(bill: String) -> Int64 {
        var lastRegular: String?
        var setAsides: [(period: String, id: String)] = []
        for pid in livePaymentIDs(bill: bill) {
            guard let period = value(entity: Entity.payment, id: pid, field: "periodKey")?.stringValue,
                  CivilDate.isValid(period) else { continue }
            switch paymentKind(pid) {
            case .regular:
                if lastRegular.map({ StringOrder.utf8Less($0, period) }) ?? true { lastRegular = period }
            case .setAside:
                setAsides.append((period, pid))
            default:
                break
            }
        }
        return setAsides.reduce(Int64(0)) { sum, s in
            guard lastRegular.map({ StringOrder.utf8Less($0, s.period) }) ?? true else { return sum }
            return sum + moneyCents(entity: Entity.payment, id: s.id, field: "amountPaid")
        }
    }

    /// Remaining of every live bill (money value).
    public func remainingByBill() -> [String: Double] {
        Dictionary(uniqueKeysWithValues: liveIDs(entity: Entity.bill).map { ($0, Money.value(cents: remainingCents(bill: $0))) })
    }

    /// Set-aside balance of every live bill (money value).
    public func setAsideBalanceByBill() -> [String: Double] {
        Dictionary(uniqueKeysWithValues: liveIDs(entity: Entity.bill).map { ($0, Money.value(cents: setAsideBalanceCents(bill: $0))) })
    }
}
