using System.Globalization;

namespace PayDay.Sync.Json;

public enum JsonDataKind
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// An immutable parsed JSON value, as produced by <see cref="StrictJson.Parse(ReadOnlySpan{byte})"/>.
/// Numbers are IEEE-754 binary64 values exactly as a JSON parser rounds them
/// (<c>spec/sync-protocol.md</c> section 6.3), so a number that overflows (<c>1e400</c>) is kept as
/// an infinity and rejected later by whichever rule reads it. Object members keep their order and
/// never repeat a key.
/// </summary>
public sealed class JsonData
{
    public static readonly JsonData Null = new(JsonDataKind.Null);
    public static readonly JsonData True = new(JsonDataKind.Bool) { _bool = true };
    public static readonly JsonData False = new(JsonDataKind.Bool) { _bool = false };

    private static readonly IReadOnlyList<JsonData> NoItems = System.Array.Empty<JsonData>();
    private static readonly IReadOnlyList<KeyValuePair<string, JsonData>> NoMembers = System.Array.Empty<KeyValuePair<string, JsonData>>();

    private bool _bool;
    private double _number;
    private string? _string;
    private IReadOnlyList<JsonData> _items = NoItems;
    private IReadOnlyList<KeyValuePair<string, JsonData>> _members = NoMembers;
    private Dictionary<string, JsonData>? _index;

    private JsonData(JsonDataKind kind)
    {
        Kind = kind;
    }

    public JsonDataKind Kind { get; }

    public bool IsNull => Kind == JsonDataKind.Null;

    public static JsonData Bool(bool value) => value ? True : False;

    public static JsonData Number(double value) => new(JsonDataKind.Number) { _number = value };

    public static JsonData String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(JsonDataKind.String) { _string = value };
    }

    public static JsonData Array(IEnumerable<JsonData> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(JsonDataKind.Array) { _items = items.ToArray() };
    }

    /// <summary>An object. Throws <see cref="ArgumentException"/> when a key repeats.</summary>
    public static JsonData Object(IEnumerable<KeyValuePair<string, JsonData>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var list = members.ToArray();
        var index = new Dictionary<string, JsonData>(list.Length, StringComparer.Ordinal);
        foreach (var (key, value) in list)
        {
            if (!index.TryAdd(key, value))
            {
                throw new ArgumentException($"Duplicate object key \"{key}\".", nameof(members));
            }
        }
        return new(JsonDataKind.Object) { _members = list, _index = index };
    }

    public static JsonData Object(params (string Key, JsonData Value)[] members)
        => Object(members.Select(m => new KeyValuePair<string, JsonData>(m.Key, m.Value)));

    public bool BoolValue => Kind == JsonDataKind.Bool ? _bool : throw WrongKind(JsonDataKind.Bool);

    public double NumberValue => Kind == JsonDataKind.Number ? _number : throw WrongKind(JsonDataKind.Number);

    public string StringValue => Kind == JsonDataKind.String ? _string! : throw WrongKind(JsonDataKind.String);

    public IReadOnlyList<JsonData> Items => Kind == JsonDataKind.Array ? _items : throw WrongKind(JsonDataKind.Array);

    public IReadOnlyList<KeyValuePair<string, JsonData>> Members
        => Kind == JsonDataKind.Object ? _members : throw WrongKind(JsonDataKind.Object);

    /// <summary>The member named <paramref name="key"/>, or false when this is not an object or has no such key.</summary>
    public bool TryGetProperty(string key, out JsonData value)
    {
        if (_index is not null && _index.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }
        value = Null;
        return false;
    }

    /// <summary>The member named <paramref name="key"/>, or <c>null</c> (not JSON null) when missing.</summary>
    public JsonData? this[string key] => TryGetProperty(key, out var v) ? v : null;

    /// <summary>The string value, or <c>null</c> when this is not a string.</summary>
    public string? AsString() => Kind == JsonDataKind.String ? _string : null;

    /// <summary>Structural equality: numbers by value, objects ignoring member order.</summary>
    public static bool DeepEquals(JsonData? a, JsonData? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Kind != b.Kind) return false;
        switch (a.Kind)
        {
            case JsonDataKind.Null:
                return true;
            case JsonDataKind.Bool:
                return a._bool == b._bool;
            case JsonDataKind.Number:
                return a._number.Equals(b._number) || a._number == b._number;
            case JsonDataKind.String:
                return string.Equals(a._string, b._string, StringComparison.Ordinal);
            case JsonDataKind.Array:
                if (a._items.Count != b._items.Count) return false;
                for (var i = 0; i < a._items.Count; i++)
                {
                    if (!DeepEquals(a._items[i], b._items[i])) return false;
                }
                return true;
            default:
                if (a._members.Count != b._members.Count) return false;
                foreach (var (key, value) in a._members)
                {
                    if (!b.TryGetProperty(key, out var other) || !DeepEquals(value, other)) return false;
                }
                return true;
        }
    }

    public override string ToString()
        => Kind switch
        {
            JsonDataKind.Number when !double.IsFinite(_number) => _number.ToString(CultureInfo.InvariantCulture),
            _ => System.Text.Encoding.UTF8.GetString(StrictJson.Serialize(this)),
        };

    private InvalidOperationException WrongKind(JsonDataKind wanted)
        => new($"JSON value is {Kind}, not {wanted}.");
}
