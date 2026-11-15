using System.Globalization;
using PayDay.Sync.Json;

namespace PayDay.Sync.Model;

public enum SyncValueKind
{
    Null,
    Bool,
    Number,
    String,
}

/// <summary>
/// A change record's value (<c>spec/sync-protocol.md</c> section 4.1): <c>null</c>, a boolean, a
/// finite number or a string. Objects and arrays are not values. Equality compares numbers by value
/// and never equates a boolean with a number.
/// </summary>
public readonly struct SyncValue : IEquatable<SyncValue>
{
    private readonly bool _bool;
    private readonly double _number;
    private readonly string? _string;

    private SyncValue(SyncValueKind kind, bool b, double n, string? s)
    {
        Kind = kind;
        _bool = b;
        _number = n;
        _string = s;
    }

    public static SyncValue Null => default;

    public static SyncValue True => new(SyncValueKind.Bool, true, 0, null);

    public static SyncValue False => new(SyncValueKind.Bool, false, 0, null);

    public SyncValueKind Kind { get; }

    public bool IsNull => Kind == SyncValueKind.Null;

    public bool IsBool => Kind == SyncValueKind.Bool;

    public bool IsNumber => Kind == SyncValueKind.Number;

    public bool IsString => Kind == SyncValueKind.String;

    public static SyncValue Bool(bool value) => new(SyncValueKind.Bool, value, 0, null);

    public static SyncValue Number(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), value, "Record numbers must be finite.");
        return new(SyncValueKind.Number, false, value, null);
    }

    public static SyncValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SyncValueKind.String, false, 0, value);
    }

    /// <summary>A string value, or null when <paramref name="value"/> is null.</summary>
    public static SyncValue StringOrNull(string? value) => value is null ? Null : String(value);

    public bool BoolValue => IsBool ? _bool : throw new InvalidOperationException($"Value is {Kind}, not Bool.");

    public double NumberValue => IsNumber ? _number : throw new InvalidOperationException($"Value is {Kind}, not Number.");

    public string StringValue => IsString ? _string! : throw new InvalidOperationException($"Value is {Kind}, not String.");

    /// <summary>True only for the boolean <c>false</c> (the liveness test).</summary>
    public bool IsFalse => IsBool && !_bool;

    /// <summary>True only for the boolean <c>true</c>.</summary>
    public bool IsTrue => IsBool && _bool;

    public bool TryGetString(out string value)
    {
        value = _string ?? string.Empty;
        return IsString;
    }

    public bool TryGetNumber(out double value)
    {
        value = _number;
        return IsNumber;
    }

    /// <summary>
    /// The tie-break order of section 4.2: <c>null &lt; false &lt; true &lt; any number &lt; any
    /// string</c>; numbers by value, strings by UTF-8 bytes.
    /// </summary>
    public static int CompareOrder(SyncValue a, SyncValue b)
    {
        var ra = a.Rank;
        var rb = b.Rank;
        if (ra != rb) return ra.CompareTo(rb);
        return a.Kind switch
        {
            SyncValueKind.Number => a._number < b._number ? -1 : a._number > b._number ? 1 : 0,
            SyncValueKind.String => Math.Sign(Utf8Order.Compare(a._string, b._string)),
            _ => 0,
        };
    }

    private int Rank
        => Kind switch
        {
            SyncValueKind.Null => 0,
            SyncValueKind.Bool => _bool ? 2 : 1,
            SyncValueKind.Number => 3,
            _ => 4,
        };

    /// <summary>Converts a JSON value; false for objects, arrays and non-finite numbers.</summary>
    public static bool TryFromJson(JsonData? json, out SyncValue value)
    {
        value = Null;
        if (json is null) return false;
        switch (json.Kind)
        {
            case JsonDataKind.Null:
                return true;
            case JsonDataKind.Bool:
                value = Bool(json.BoolValue);
                return true;
            case JsonDataKind.Number when double.IsFinite(json.NumberValue):
                value = Number(json.NumberValue);
                return true;
            case JsonDataKind.String:
                value = String(json.StringValue);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Converts a JSON value or throws <see cref="ArgumentException"/>.</summary>
    public static SyncValue FromJson(JsonData json)
        => TryFromJson(json, out var v) ? v : throw new ArgumentException("Not a record value (objects, arrays and non-finite numbers are not allowed).", nameof(json));

    public JsonData ToJson()
        => Kind switch
        {
            SyncValueKind.Null => JsonData.Null,
            SyncValueKind.Bool => JsonData.Bool(_bool),
            SyncValueKind.Number => JsonData.Number(_number),
            _ => JsonData.String(_string!),
        };

    public bool Equals(SyncValue other)
        => Kind == other.Kind && Kind switch
        {
            SyncValueKind.Null => true,
            SyncValueKind.Bool => _bool == other._bool,
            SyncValueKind.Number => _number == other._number,
            _ => string.Equals(_string, other._string, StringComparison.Ordinal),
        };

    public override bool Equals(object? obj) => obj is SyncValue other && Equals(other);

    public override int GetHashCode()
        => Kind switch
        {
            SyncValueKind.Null => 0,
            SyncValueKind.Bool => _bool ? 2 : 1,
            SyncValueKind.Number => _number == 0 ? 3 : _number.GetHashCode(),
            _ => StringComparer.Ordinal.GetHashCode(_string!),
        };

    public static bool operator ==(SyncValue left, SyncValue right) => left.Equals(right);

    public static bool operator !=(SyncValue left, SyncValue right) => !left.Equals(right);

    public override string ToString()
        => Kind switch
        {
            SyncValueKind.Null => "null",
            SyncValueKind.Bool => _bool ? "true" : "false",
            SyncValueKind.Number => _number.ToString("R", CultureInfo.InvariantCulture),
            _ => "\"" + _string + "\"",
        };
}
