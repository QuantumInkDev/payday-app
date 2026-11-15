/// Why a money value could not be rounded.
public enum MoneyError: Error, Sendable, Equatable {
    /// NaN, an infinity, or `|x| >= 1e13` (spec/data-model.md section 3).
    case outOfRange
}

/// Money rounding, exactly as spec/data-model.md section 3.1 pins it.
///
/// Money stays `Double` (IEEE-754 binary64). `cents` is computed literally in binary64 so every
/// platform produces the same bits: scale the absolute value by 100 in one multiplication,
/// truncate, add one if the remainder is at least 0.5, restore the sign. This equals .NET's
/// `Math.Round(x, 2, MidpointRounding.AwayFromZero)` for the valid range. Do not replace it with
/// `Decimal`, string formatting or `rounded(.toNearestOrAwayFromZero)` on `x * 100`, which differ
/// for some inputs.
public enum Money {
    /// Valid money values satisfy `|x| < 1e13`.
    public static let maxMagnitude: Double = 1e13

    /// `cents(x)`: round half away from zero to an integer number of cents.
    public static func cents(_ x: Double) throws(MoneyError) -> Int64 {
        guard x.isFinite, abs(x) < maxMagnitude else { throw .outOfRange }
        return scaled(x, by: 100)
    }

    /// `round2(x) = cents(x) / 100`, one binary64 division.
    public static func round2(_ x: Double) throws(MoneyError) -> Double {
        Double(try cents(x)) / 100
    }

    /// Converts cents back to a money value (`cents / 100`).
    public static func value(cents: Int64) -> Double { Double(cents) / 100 }

    /// Two money values are equal when their cents are equal (section 3.2).
    public static func equal(_ a: Double, _ b: Double) -> Bool {
        guard let ca = try? cents(a), let cb = try? cents(b) else { return false }
        return ca == cb
    }

    /// The same rounding at any scale: `cents(x) == roundScaled(x, scale: 100)`. Used to compare
    /// fixture values (scale 10,000 for `...Fraction` values, spec/conformance/core/README.md).
    /// `nil` for non-finite values or results that do not fit 64 bits.
    public static func roundScaled(_ x: Double, scale: Double) -> Int64? {
        guard x.isFinite else { return nil }
        let s = abs(x) * scale
        guard s < 9.2e18 else { return nil }
        return scaled(x, by: scale)
    }

    private static func scaled(_ x: Double, by scale: Double) -> Int64 {
        let s = abs(x) * scale                  // one IEEE-754 multiplication
        var i = s.rounded(.towardZero)          // integer part
        if s - i >= 0.5 { i += 1 }              // s - i is exact
        let n = Int64(i)
        return x < 0 ? -n : n
    }
}
