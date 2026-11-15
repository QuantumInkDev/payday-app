/// A payment as the PayDay page reads it: which bill, which period, how much.
public struct PeriodPayment: Sendable, Hashable {
    public var billId: String
    public var periodKey: String
    public var amountPaid: Double

    public init(billId: String, periodKey: String, amountPaid: Double) {
        self.billId = billId
        self.periodKey = periodKey
        self.amountPaid = amountPaid
    }
}

/// The PayDay home page for the current period (spec/conformance/core/totals.json, kind
/// `payDayPage`), as today's C# `PayDayPageViewModel`.
public struct PayDayPageSummary: Sendable, Hashable {
    public struct PaidBill: Sendable, Hashable {
        public let bill: PeriodBill
        public let amountPaid: Double
    }

    public let period: AssignedPeriod
    public let autoPayBills: [PeriodBill]
    public let unpaidBills: [PeriodBill]
    public let paidBills: [PaidBill]
    /// Sum of manual bills' `payment`.
    public let totalDue: Double
    /// Sum of the counted `amountPaid`.
    public let totalPaid: Double
    /// `max(0, totalDue - totalPaid)`.
    public let remaining: Double
    /// `totalDue > 0 ? min(1, totalPaid / totalDue) : 0`.
    public let progressFraction: Double
    /// No unpaid manual bills, and at least one paid bill or `totalDue == 0`.
    public let isAllPaid: Bool
    public let autoPayTotal: Double

    public var periodKey: String { period.period.key }

    /// `nil` when there is no current period. A manual bill is paid when any payment has its ID
    /// and the period key; only the first in input order counts, and any amount (even 0) counts
    /// (core README quirk 5). Payments against auto-pay bills are ignored.
    public static func compute(anchor: CivilDate, now: LocalDateTime, bills: [Bill], payments: [PeriodPayment]) -> PayDayPageSummary? {
        let assigned = BillAssignment.assign(bills, to: PayPeriods.current(anchor: anchor, now: now))
        guard let current = assigned.first else { return nil }
        let key = current.period.key
        var firstPayment: [String: PeriodPayment] = [:]
        for p in payments where p.periodKey == key && firstPayment[p.billId] == nil {
            firstPayment[p.billId] = p
        }
        var autoPay: [PeriodBill] = [], unpaid: [PeriodBill] = [], paid: [PaidBill] = []
        for pb in current.bills {
            if pb.bill.autoPay {
                autoPay.append(pb)
            } else if let p = firstPayment[pb.bill.id] {
                paid.append(PaidBill(bill: pb, amountPaid: p.amountPaid))
            } else {
                unpaid.append(pb)
            }
        }
        // Same summation order as the reference: unpaid list, then paid list, then added.
        let manualTotal = sequentialSum(unpaid) { $0.bill.payment } + sequentialSum(paid) { $0.bill.bill.payment }
        let totalPaid = sequentialSum(paid) { $0.amountPaid }
        return PayDayPageSummary(
            period: current,
            autoPayBills: autoPay,
            unpaidBills: unpaid,
            paidBills: paid,
            totalDue: manualTotal,
            totalPaid: totalPaid,
            remaining: max(0, manualTotal - totalPaid),
            progressFraction: manualTotal > 0 ? min(1, totalPaid / manualTotal) : 0,
            isAllPaid: unpaid.isEmpty && (!paid.isEmpty || manualTotal == 0),
            autoPayTotal: sequentialSum(autoPay) { $0.bill.payment }
        )
    }
}

/// One period section of the Dashboard.
public struct DashboardSection: Sendable, Hashable {
    public let period: AssignedPeriod
    public let manualBills: [PeriodBill]
    public let autoPayBills: [PeriodBill]
    public let manualTotal: Double
    public let autoPayTotal: Double

    public var label: String { period.period.label ?? "Period" }
    public var grandTotal: Double { manualTotal + autoPayTotal }
    public var billCount: Int { manualBills.count + autoPayBills.count }

    public init(_ period: AssignedPeriod) {
        self.period = period
        manualBills = period.bills.filter { !$0.bill.autoPay }
        autoPayBills = period.bills.filter(\.bill.autoPay)
        manualTotal = sequentialSum(manualBills) { $0.bill.payment }
        autoPayTotal = sequentialSum(autoPayBills) { $0.bill.payment }
    }
}

/// Dashboard headline figures and period sections (totals.json, kind `dashboard`), as today's C#
/// `DashboardPageViewModel`. Headline figures use active bills only.
public struct DashboardSummary: Sendable, Hashable {
    public let totalMonthlyObligations: Double
    public let totalRemaining: Double
    public let hasCreditCards: Bool
    /// Percent, rounded to 1 decimal half-to-even (core README quirk 10); 0 without cards.
    public let creditUtilizationPct: Double
    public let billsDueThisPeriod: Int
    public let sections: [DashboardSection]

    public static func compute(anchor: CivilDate, now: LocalDateTime, bills: [Bill]) -> DashboardSummary {
        let active = bills.filter(\.active)
        let cards = active.filter { $0.type == "Cards" && $0.creditLimit > 0 }
        let totalLimit = sequentialSum(cards) { $0.creditLimit }
        let hasCards = !cards.isEmpty && totalLimit > 0
        let pct = hasCards ? roundHalfEven(sequentialSum(cards) { $0.remaining } / totalLimit * 100.0, digits: 1) : 0
        let sections = BillAssignment.assign(bills, to: PayPeriods.current(anchor: anchor, now: now)).map(DashboardSection.init)
        return DashboardSummary(
            totalMonthlyObligations: sequentialSum(active, monthlyEquivalent),
            totalRemaining: sequentialSum(active) { $0.remaining },
            hasCreditCards: hasCards,
            creditUtilizationPct: pct,
            billsDueThisPeriod: sections.first?.billCount ?? 0,
            sections: sections
        )
    }

    /// A bill's payment normalized to a month: Monthly as is, Bi-Weekly × 26 / 12, Yearly / 12,
    /// anything else 0.
    public static func monthlyEquivalent(_ b: Bill) -> Double {
        switch b.rate {
        case BillRate.monthly.rawValue: return b.payment
        case BillRate.biWeekly.rawValue: return b.payment * 26.0 / 12.0
        case BillRate.yearly.rawValue: return b.payment / 12.0
        default: return 0
        }
    }

    /// .NET `Math.Round(value, digits)` (midpoints to even): scale, round, unscale.
    static func roundHalfEven(_ value: Double, digits: Int) -> Double {
        guard abs(value) < 1e16 else { return value }
        var power = 1.0
        for _ in 0..<digits { power *= 10 }
        return (value * power).rounded(.toNearestOrEven) / power
    }
}

/// One All Bills group.
public struct BillGroup: Sendable, Hashable {
    public let type: String
    /// Ordered by name (ordinal, case-insensitive; stable).
    public let bills: [Bill]
    /// Includes inactive bills.
    public var count: Int { bills.count }
    /// Active bills only.
    public let totalPayment: Double
    /// Active bills only.
    public let totalRemaining: Double
}

/// The All Bills page (totals.json, kind `billGroups`), as today's C# `AllBillsPageViewModel`.
public enum BillGroups {
    /// Groups by exact, case-sensitive type. Built-in types first in canonical order, then any
    /// other type by name (ordinal, case-insensitive).
    public static func compute(_ bills: [Bill]) -> [BillGroup] {
        var order: [String] = []
        var byType: [String: [Bill]] = [:]
        for b in bills {
            if byType[b.type] == nil { order.append(b.type) }
            byType[b.type, default: []].append(b)
        }
        func rank(_ t: String) -> Int { BillType.canonicalOrder.firstIndex(of: t) ?? Int.max }
        let sortedTypes = order.stableSorted { a, b in
            let ra = rank(a), rb = rank(b)
            if ra != rb { return ra < rb }
            return StringOrder.ordinalIgnoreCaseLess(a, b)
        }
        return sortedTypes.map { type in
            let members = byType[type] ?? []
            let active = members.filter(\.active)
            return BillGroup(
                type: type,
                bills: members.stableSorted { StringOrder.ordinalIgnoreCaseLess($0.name, $1.name) },
                totalPayment: sequentialSum(active) { $0.payment },
                totalRemaining: sequentialSum(active) { $0.remaining }
            )
        }
    }
}
