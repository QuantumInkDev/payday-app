/// A bill as the core rules (pay periods, totals, payoff, backup) see it: the shape of today's
/// C# `PayDay.Models.Bill`, with `remaining` being the **computed** Remaining.
///
/// The synced form of a bill is `BillEntity` (spec/data-model.md section 4.1), which stores a
/// balance checkpoint instead; `BillEntity.coreBill(remaining:)` builds this from it.
///
/// Defaults match spec/conformance/core/README.md ("Shared input: Bill").
public struct Bill: Sendable, Hashable, Identifiable {
    public var id: String
    public var name: String = ""
    /// Category, case-sensitive (`Cards`, `Bills`, ..., or any custom string).
    public var type: String = ""
    /// Amount due each time the bill comes up.
    public var payment: Double = 0
    /// Outstanding balance (computed Remaining).
    public var remaining: Double = 0
    public var available: Double = 0
    public var creditLimit: Double = 0
    /// Day of month. Not validated: days past the month's end overflow (core README, quirk 2).
    public var dueDay: Int = 1
    /// `Monthly`, `Bi-Weekly`, `Yearly`, `Once`; case-sensitive, anything else never lands.
    public var rate: String = BillRate.monthly.rawValue
    /// Annual percentage: `24.99` means 24.99%.
    public var apr: Double = 0
    public var autoPay: Bool = false
    public var active: Bool = true
    /// `MM-DD`, used when `rate` is `Yearly`.
    public var yearlyDate: String? = nil
    public var notes: String? = nil
    public var createdAt: String? = nil
    public var updatedAt: String? = nil
    public var notionPageId: String? = nil

    public init(
        id: String, name: String = "", type: String = "", payment: Double = 0, remaining: Double = 0,
        available: Double = 0, creditLimit: Double = 0, dueDay: Int = 1, rate: String = "Monthly",
        apr: Double = 0, autoPay: Bool = false, active: Bool = true, yearlyDate: String? = nil,
        notes: String? = nil, createdAt: String? = nil, updatedAt: String? = nil, notionPageId: String? = nil
    ) {
        self.id = id
        self.name = name
        self.type = type
        self.payment = payment
        self.remaining = remaining
        self.available = available
        self.creditLimit = creditLimit
        self.dueDay = dueDay
        self.rate = rate
        self.apr = apr
        self.autoPay = autoPay
        self.active = active
        self.yearlyDate = yearlyDate
        self.notes = notes
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.notionPageId = notionPageId
    }
}

/// The known values of `bill.rate`. Rates are matched case-sensitively; anything else is treated
/// as `Once` (never assigned to a period).
public enum BillRate: String, Sendable, CaseIterable {
    case monthly = "Monthly"
    case biWeekly = "Bi-Weekly"
    case yearly = "Yearly"
    case once = "Once"
}

/// The built-in bill types, in All Bills group order.
public enum BillType {
    public static let canonicalOrder = [
        "Cards", "Bills", "Loans", "Installments", "Subscriptions", "Business", "People", "Medical", "Other",
    ]
}

/// .NET-style sequential `Double` sum (the reference's `Enumerable.Sum`), in element order.
func sequentialSum<S: Sequence>(_ items: S, _ value: (S.Element) -> Double) -> Double {
    var total = 0.0
    for item in items { total += value(item) }
    return total
}
