/// A bill placed in a period, with the date it is due there.
public struct PeriodBill: Sendable, Hashable {
    public let bill: Bill
    public let dueDate: CivilDate?
}

/// A period with the bills due in it.
public struct AssignedPeriod: Sendable, Hashable {
    public let period: PayPeriod
    /// Ordered by due date ascending; equal dates keep input order.
    public let bills: [PeriodBill]
    /// Sum of the bills' `payment`.
    public let total: Double
}

/// Bill-to-period assignment (spec/conformance/core/bill-assignment.json), as today's C#
/// `PayPeriodService.AssignBillsToPeriods`, quirks included.
public enum BillAssignment {
    /// The Monthly rule for any window: the first of (previous, same, next) month relative to
    /// `periodStart`'s month whose due date lies in `[periodStart, periodEnd]`. A due date is the
    /// month's 1st plus `dueDay - 1` days, so it overflows instead of clamping (core README quirk 2).
    public static func monthlyDueDate(dueDay: Int, periodStart: CivilDate, periodEnd: CivilDate) -> CivilDate? {
        for offset in [-1, 0, 1] {
            guard let monthStart = periodStart.firstOfMonth(addingMonths: offset),
                  let candidate = monthStart.adding(days: dueDay - 1) else { continue }
            if candidate >= periodStart && candidate <= periodEnd { return candidate }
        }
        return nil
    }

    /// The date a bill is due in `period`, or `nil` if it does not land there.
    public static func dueDate(of bill: Bill, in period: PayPeriod) -> CivilDate? {
        switch bill.rate {
        case BillRate.biWeekly.rawValue:
            return period.start
        case BillRate.monthly.rawValue:
            return monthlyDueDate(dueDay: bill.dueDay, periodStart: period.start, periodEnd: period.end)
        case BillRate.yearly.rawValue:
            return yearlyDueDate(bill.yearlyDate, in: period)
        default:
            return nil // "Once" or unknown: never auto-assigned.
        }
    }

    /// Assigns active bills to each period. Inactive bills never land.
    public static func assign(_ bills: [Bill], to periods: [PayPeriod]) -> [AssignedPeriod] {
        let active = bills.filter(\.active)
        return periods.map { period in
            var assigned: [PeriodBill] = []
            for bill in active {
                let due = dueDate(of: bill, in: period)
                if due == nil && bill.rate != BillRate.biWeekly.rawValue { continue }
                assigned.append(PeriodBill(bill: bill, dueDate: due))
            }
            // Stable sort by due date (no date sorts last). The C# List.Sort is not stable in
            // general but is for up to 16 items; the rule every app follows is to keep input order.
            let sorted = assigned.stableSorted { a, b in
                switch (a.dueDate, b.dueDate) {
                case let (x?, y?): return x < y
                case (_?, nil): return true
                default: return false
                }
            }
            return AssignedPeriod(period: period, bills: sorted, total: sequentialSum(sorted) { $0.bill.payment })
        }
    }

    /// Yearly: `MM-DD` tried in the period start's year, then the next year, with the same
    /// overflow (`02-29` in 2025 is March 1). Mirrors C#: `string.IsNullOrWhiteSpace`, `Split('-')`
    /// into exactly two `int.TryParse` parts, `new DateTime(year, mm, 1).AddDays(dd - 1)` where an
    /// out-of-range month or date skips that year.
    static func yearlyDueDate(_ yearlyDate: String?, in period: PayPeriod) -> CivilDate? {
        guard let text = yearlyDate, !text.unicodeScalars.allSatisfy(\.properties.isWhitespace) else { return nil }
        let parts = text.split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 2, let mm = dotNetIntParse(parts[0]), let dd = dotNetIntParse(parts[1]) else {
            return nil
        }
        for year in [period.start.year, period.start.year + 1] {
            guard let first = CivilDate(year: year, month: mm, day: 1),
                  let candidate = first.adding(days: dd - 1) else { continue }
            if period.contains(candidate) { return candidate }
        }
        return nil
    }

    /// .NET `int.TryParse(string)` with `NumberStyles.Integer`: optional surrounding white space,
    /// an optional leading sign, ASCII digits, within `Int32` range.
    static func dotNetIntParse(_ text: Substring) -> Int? {
        var scalars = text.unicodeScalars
        while let f = scalars.first, f.properties.isWhitespace { scalars.removeFirst() }
        while let l = scalars.last, l.properties.isWhitespace { scalars.removeLast() }
        var negative = false
        if let f = scalars.first, f == "-" || f == "+" {
            negative = f == "-"
            scalars.removeFirst()
        }
        guard !scalars.isEmpty else { return nil }
        var value: Int64 = 0
        for s in scalars {
            guard s.value >= 0x30, s.value <= 0x39 else { return nil }
            value = value * 10 + Int64(s.value - 0x30)
            if value > Int64(Int32.max) + 1 { return nil }
        }
        if negative { value = -value }
        guard value >= Int64(Int32.min), value <= Int64(Int32.max) else { return nil }
        return Int(value)
    }
}
