/// A calendar date with no time or zone (`YYYY-MM-DD`), proleptic Gregorian, years 1 to 9999
/// (the range of .NET's `DateTime` and Python's `date`).
///
/// Date arithmetic is done on a day count, never through `Calendar`, so it does not depend on the
/// device's locale, time zone or calendar settings.
public struct CivilDate: Sendable, Hashable, Comparable, CustomStringConvertible {
    public let year: Int
    public let month: Int
    public let day: Int

    /// A valid date, or `nil`.
    public init?(year: Int, month: Int, day: Int) {
        guard (1...9999).contains(year), (1...12).contains(month),
              day >= 1, day <= CivilDate.daysInMonth(year: year, month: month) else { return nil }
        self.year = year
        self.month = month
        self.day = day
    }

    /// Parses exactly `YYYY-MM-DD` (ASCII digits, zero-padded) holding a real date.
    public init?(iso text: String) {
        let b = Array(text.utf8)
        guard b.count == 10, b[4] == UInt8(ascii: "-"), b[7] == UInt8(ascii: "-"),
              let y = CivilDate.digits(b[0..<4]), let m = CivilDate.digits(b[5..<7]),
              let d = CivilDate.digits(b[8..<10]) else { return nil }
        self.init(year: y, month: m, day: d)
    }

    /// True when `text` is a valid zero-padded `YYYY-MM-DD` date.
    public static func isValid(_ text: String) -> Bool { CivilDate(iso: text) != nil }

    public static func isLeapYear(_ y: Int) -> Bool { (y % 4 == 0 && y % 100 != 0) || y % 400 == 0 }

    public static func daysInMonth(year: Int, month: Int) -> Int {
        switch month {
        case 1, 3, 5, 7, 8, 10, 12: return 31
        case 4, 6, 9, 11: return 30
        case 2: return isLeapYear(year) ? 29 : 28
        default: return 0
        }
    }

    /// Days since 1970-01-01 (negative before it).
    public var dayNumber: Int {
        // Howard Hinnant's days_from_civil.
        let y = month <= 2 ? year - 1 : year
        let era = (y >= 0 ? y : y - 399) / 400
        let yoe = y - era * 400
        let mp = (month + 9) % 12
        let doy = (153 * mp + 2) / 5 + day - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146_097 + doe - 719_468
    }

    /// The date `dayNumber` days after 1970-01-01, or `nil` outside years 1 to 9999.
    public init?(dayNumber z0: Int) {
        let z = z0 + 719_468
        let era = (z >= 0 ? z : z - 146_096) / 146_097
        let doe = z - era * 146_097
        let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100)
        let mp = (5 * doy + 2) / 153
        let d = doy - (153 * mp + 2) / 5 + 1
        let m = mp < 10 ? mp + 3 : mp - 9
        let y = yoe + era * 400 + (m <= 2 ? 1 : 0)
        self.init(year: y, month: m, day: d)
    }

    /// This date plus `days` (which may be negative), or `nil` if the result leaves years 1 to 9999.
    public func adding(days: Int) -> CivilDate? {
        let (sum, overflow) = dayNumber.addingReportingOverflow(days)
        return overflow ? nil : CivilDate(dayNumber: sum)
    }

    /// The first day of the month `months` after this date's month.
    public func firstOfMonth(addingMonths months: Int) -> CivilDate? {
        let total = (year * 12 + (month - 1)).addingReportingOverflow(months)
        guard !total.overflow else { return nil }
        let y = total.partialValue >= 0 ? total.partialValue / 12 : (total.partialValue - 11) / 12
        let m = total.partialValue - y * 12 + 1
        return CivilDate(year: y, month: m, day: 1)
    }

    /// Days from `self` to `other` (`other - self`).
    public func days(until other: CivilDate) -> Int { other.dayNumber - dayNumber }

    /// `YYYY-MM-DD`.
    public var iso: String {
        CivilDate.pad(year, 4) + "-" + CivilDate.pad(month, 2) + "-" + CivilDate.pad(day, 2)
    }

    public var description: String { iso }

    public static func < (a: CivilDate, b: CivilDate) -> Bool {
        (a.year, a.month, a.day) < (b.year, b.month, b.day)
    }

    static func pad(_ n: Int, _ width: Int) -> String {
        let s = String(n)
        return s.count >= width ? s : String(repeating: "0", count: width - s.count) + s
    }

    static func digits(_ bytes: ArraySlice<UInt8>) -> Int? {
        var n = 0
        for b in bytes {
            guard b >= UInt8(ascii: "0"), b <= UInt8(ascii: "9") else { return nil }
            n = n * 10 + Int(b - UInt8(ascii: "0"))
        }
        return n
    }
}

/// A wall-clock date and time with no zone, to the second (`YYYY-MM-DDTHH:MM:SS`), as the core
/// fixtures write "now". Pay-period rules use the device's local wall-clock time.
public struct LocalDateTime: Sendable, Hashable, Comparable, CustomStringConvertible {
    public let date: CivilDate
    /// Seconds since midnight, 0 to 86,399.
    public let secondsOfDay: Int

    public init?(date: CivilDate, hour: Int, minute: Int, second: Int) {
        guard (0..<24).contains(hour), (0..<60).contains(minute), (0..<60).contains(second) else { return nil }
        self.date = date
        self.secondsOfDay = hour * 3600 + minute * 60 + second
    }

    /// Parses exactly `YYYY-MM-DDTHH:MM:SS`.
    public init?(iso text: String) {
        let b = Array(text.utf8)
        guard b.count == 19, b[10] == UInt8(ascii: "T"), b[13] == UInt8(ascii: ":"), b[16] == UInt8(ascii: ":"),
              let date = CivilDate(iso: String(decoding: b[0..<10], as: UTF8.self)),
              let h = CivilDate.digits(b[11..<13]), let m = CivilDate.digits(b[14..<16]),
              let s = CivilDate.digits(b[17..<19]) else { return nil }
        self.init(date: date, hour: h, minute: m, second: s)
    }

    public var hour: Int { secondsOfDay / 3600 }
    public var minute: Int { secondsOfDay / 60 % 60 }
    public var second: Int { secondsOfDay % 60 }

    /// This time plus `seconds` (which may be negative), or `nil` outside the date range.
    public func adding(seconds: Int) -> LocalDateTime? {
        let total = secondsOfDay + seconds
        let dayShift = total >= 0 ? total / 86_400 : (total - 86_399) / 86_400
        guard let d = date.adding(days: dayShift) else { return nil }
        let rest = total - dayShift * 86_400
        return LocalDateTime(date: d, hour: rest / 3600, minute: rest / 60 % 60, second: rest % 60)
    }

    public var iso: String {
        date.iso + "T" + CivilDate.pad(hour, 2) + ":" + CivilDate.pad(minute, 2) + ":" + CivilDate.pad(second, 2)
    }

    public var description: String { iso }

    public static func < (a: LocalDateTime, b: LocalDateTime) -> Bool {
        (a.date, a.secondsOfDay) < (b.date, b.secondsOfDay)
    }
}
