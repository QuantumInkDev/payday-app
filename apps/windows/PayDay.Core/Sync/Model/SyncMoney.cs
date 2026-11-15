namespace PayDay.Sync.Model;

/// <summary>
/// Money rounding, exactly as <c>spec/data-model.md</c> section 3.1 writes it: round half away
/// from zero to cents, computed literally in binary64. Sums are taken in integer cents.
/// </summary>
public static class SyncMoney
{
    /// <summary>Money values must satisfy |x| &lt; 1e13.</summary>
    public const double MaxAbs = 1e13;

    /// <summary><c>cents(x)</c>. Throws <see cref="ArgumentOutOfRangeException"/> for non-finite or out-of-range input.</summary>
    public static long Cents(double x)
        => TryCents(x, out var c) ? c : throw new ArgumentOutOfRangeException(nameof(x), x, "Money must be finite with |x| < 1e13.");

    public static bool TryCents(double x, out long cents)
    {
        cents = 0;
        if (!double.IsFinite(x) || Math.Abs(x) >= MaxAbs) return false;
        var s = Math.Abs(x) * 100.0;     // one IEEE-754 multiplication
        var i = Math.Truncate(s);
        if (s - i >= 0.5) i += 1;        // s - i is exact
        cents = (long)(x < 0 ? -i : i);
        return true;
    }

    /// <summary><c>round2(x) = cents(x) / 100</c>: what a writer stores in a money field.</summary>
    public static double Round2(double x) => Cents(x) / 100.0;

    /// <summary>Converts integer cents back to a money value (one IEEE-754 division).</summary>
    public static double FromCents(long cents) => cents / 100.0;

    /// <summary>Two money values are equal when their cents are equal (section 3.2).</summary>
    public static bool AreEqual(double a, double b) => Cents(a) == Cents(b);
}
