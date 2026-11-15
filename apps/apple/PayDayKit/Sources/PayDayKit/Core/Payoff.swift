import Foundation

/// A months-to-payoff estimate.
public enum PayoffEstimate: Sendable, Hashable {
    /// Paid off after this many months.
    case months(Int)
    /// The payment does not cover the monthly interest.
    case never
    /// No balance or no payment.
    case notApplicable
}

/// Payoff estimates (spec/conformance/core/payoff.json, kind `estimate`), as today's C#
/// `PayoffCalculator.EstimatePayoff`. IEEE double arithmetic throughout, so `120.48 / 10.04`
/// (`12.000000000000002`) gives 13 months (core README quirk 6).
public enum Payoff {
    public static func estimate(remaining: Double, payment: Double, apr: Double) -> PayoffEstimate {
        if remaining <= 0 || payment <= 0 { return .notApplicable }
        if apr <= 0 { return .months(clampedInt((remaining / payment).rounded(.up))) }
        let r = apr / 12.0 / 100.0
        if payment <= remaining * r { return .never }
        let months = -log(1 - (remaining * r) / payment) / log(1 + r)
        return .months(clampedInt(months.rounded(.up)))
    }

    private static func clampedInt(_ x: Double) -> Int {
        guard x.isFinite else { return Int(Int32.max) }
        return Int(min(max(x, Double(Int32.min)), Double(Int32.max)))
    }
}

/// One Payoff Tracker row.
public struct PayoffItem: Sendable, Hashable {
    public let bill: Bill
    public let estimate: PayoffEstimate
    /// The bill has a credit limit (`creditLimit > 0`).
    public let hasLimit: Bool
    /// `remaining / creditLimit` clamped to 0...1, or 0 without a limit.
    public let limitUsedFraction: Double

    public init(_ bill: Bill) {
        self.bill = bill
        estimate = Payoff.estimate(remaining: bill.remaining, payment: bill.payment, apr: bill.apr)
        hasLimit = bill.creditLimit > 0
        limitUsedFraction = hasLimit ? min(max(bill.remaining / bill.creditLimit, 0), 1) : 0
    }
}

/// Payoff Tracker orderings.
public enum PayoffStrategy: Sendable, CaseIterable {
    /// Bills with a month count first by months ascending, then "never", then not applicable;
    /// ties by name.
    case payoffTime
    /// Remaining ascending, then name.
    case snowball
    /// APR descending, then remaining ascending, then name.
    case avalanche
}

/// The Payoff Tracker (payoff.json, kind `tracker`), as today's C# `PayoffTrackerPageViewModel`.
public struct PayoffTracker: Sendable, Hashable {
    /// Active bills with `remaining > 0`, in input order.
    public let items: [PayoffItem]
    public let totalRemaining: Double

    public init(bills: [Bill]) {
        items = bills.filter { $0.active && $0.remaining > 0 }.map(PayoffItem.init)
        totalRemaining = sequentialSum(items) { $0.bill.remaining }
    }

    /// Items in strategy order (stable; names compare ordinal, case-insensitive).
    public func ordered(by strategy: PayoffStrategy) -> [PayoffItem] {
        func byName(_ a: PayoffItem, _ b: PayoffItem) -> Int {
            StringOrder.ordinalIgnoreCaseCompare(a.bill.name, b.bill.name)
        }
        switch strategy {
        case .payoffTime:
            func key(_ i: PayoffItem) -> (Int, Int) {
                switch i.estimate {
                case .months(let n): return (0, n)
                case .never: return (1, 0)
                case .notApplicable: return (2, 0)
                }
            }
            return items.stableSorted { a, b in
                let ka = key(a), kb = key(b)
                if ka != kb { return ka < kb }
                return byName(a, b) < 0
            }
        case .snowball:
            return items.stableSorted { a, b in
                if a.bill.remaining != b.bill.remaining { return a.bill.remaining < b.bill.remaining }
                return byName(a, b) < 0
            }
        case .avalanche:
            return items.stableSorted { a, b in
                if a.bill.apr != b.bill.apr { return a.bill.apr > b.bill.apr }
                if a.bill.remaining != b.bill.remaining { return a.bill.remaining < b.bill.remaining }
                return byName(a, b) < 0
            }
        }
    }
}
