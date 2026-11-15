/// One 14-day pay period.
public struct PayPeriod: Sendable, Hashable {
    public let start: CivilDate
    /// `start + 13` days, inclusive.
    public let end: CivilDate
    /// Equals `start`.
    public let payday: CivilDate
    public let isCurrent: Bool
    /// `This Pay Period`, `Next Pay Period` or `Following Period`.
    public let label: String?

    /// The period key (`YYYY-MM-DD` of `start`), used as `payment.periodKey`.
    public var key: String { start.iso }

    public func contains(_ date: CivilDate) -> Bool { date >= start && date <= end }
}

/// Pay-period rules (spec/conformance/core/pay-periods.json), as today's C# `PayPeriodService`.
public enum PayPeriods {
    public static let lengthDays = 14
    /// A period unlocks at 3 PM the day before it starts: `now + 9 hours`, then take the date.
    public static let earlyStartShiftSeconds = 9 * 3600
    /// Today's C# fallback when no `PayAnchor` is stored (spec/data-model.md section 6.1).
    public static let defaultAnchor = CivilDate(year: 2025, month: 3, day: 20)!
    public static let labels = ["This Pay Period", "Next Pay Period", "Following Period"]

    /// The three labeled periods around `now` (local wall-clock time), on the 14-day grid through
    /// `anchor`. The 3 PM early start always applies: the reference never reads the `EarlyStart`
    /// setting (core README, quirk 1).
    public static func current(anchor: CivilDate, now: LocalDateTime) -> [PayPeriod] {
        guard let shifted = now.adding(seconds: earlyStartShiftSeconds) else { return [] }
        let today = shifted.date
        let diff = anchor.days(until: today)
        // Floor division: the grid runs both ways from the anchor.
        let steps = diff >= 0 ? diff / lengthDays : -((-diff + lengthDays - 1) / lengthDays)
        guard let currentStart = anchor.adding(days: steps * lengthDays) else { return [] }
        var periods: [PayPeriod] = []
        for (i, label) in labels.enumerated() {
            guard let start = currentStart.adding(days: i * lengthDays),
                  let end = start.adding(days: lengthDays - 1) else { break }
            periods.append(PayPeriod(start: start, end: end, payday: start, isCurrent: i == 0, label: label))
        }
        return periods
    }
}
