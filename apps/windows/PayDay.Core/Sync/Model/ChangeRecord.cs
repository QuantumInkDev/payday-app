using PayDay.Sync.Clock;
using PayDay.Sync.Json;

namespace PayDay.Sync.Model;

/// <summary>
/// One synced field edit (<c>spec/sync-protocol.md</c> section 4.1). Only valid records exist as
/// instances: <see cref="TryFromJson"/> is how a reader drops invalid ones.
/// </summary>
public sealed record ChangeRecord
{
    public ChangeRecord(string entity, string id, string field, SyncValue value, Hlc hlc)
    {
        if (string.IsNullOrEmpty(entity)) throw new ArgumentException("Entity must be a non-empty string.", nameof(entity));
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Row ID must be a non-empty string.", nameof(id));
        if (string.IsNullOrEmpty(field)) throw new ArgumentException("Field must be a non-empty string.", nameof(field));
        if (hlc == default) throw new ArgumentException("A record needs an HLC.", nameof(hlc));
        Entity = entity;
        Id = id;
        Field = field;
        Value = value;
        Hlc = hlc;
    }

    public string Entity { get; }

    public string Id { get; }

    public string Field { get; }

    public SyncValue Value { get; }

    public Hlc Hlc { get; }

    /// <summary>
    /// Reads a record from a payload, applying every validity rule of section 4.1: <c>entity</c>,
    /// <c>id</c> and <c>field</c> non-empty strings; <c>value</c> present and null, boolean, finite
    /// number or string; <c>hlc</c> a canonical HLC. Extra keys are ignored. Invalid records give false.
    /// </summary>
    public static bool TryFromJson(JsonData? json, out ChangeRecord? record)
    {
        record = null;
        if (json is null || json.Kind != JsonDataKind.Object) return false;
        var entity = NonEmptyString(json, "entity");
        var id = NonEmptyString(json, "id");
        var field = NonEmptyString(json, "field");
        var hlcText = NonEmptyString(json, "hlc");
        if (entity is null || id is null || field is null || hlcText is null) return false;
        if (!json.TryGetProperty("value", out var rawValue) || !SyncValue.TryFromJson(rawValue, out var value)) return false;
        if (!Hlc.TryParse(hlcText, out var hlc)) return false;
        record = new ChangeRecord(entity, id, field, value, hlc);
        return true;
    }

    public JsonData ToJson()
        => JsonData.Object(
            ("entity", JsonData.String(Entity)),
            ("id", JsonData.String(Id)),
            ("field", JsonData.String(Field)),
            ("value", Value.ToJson()),
            ("hlc", JsonData.String(Hlc.Text)));

    /// <summary>The order emitted records are listed in: entity, then id, then field, by UTF-8 bytes.</summary>
    public static int CompareByKey(ChangeRecord? a, ChangeRecord? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var c = Utf8Order.Compare(a.Entity, b.Entity);
        if (c != 0) return c;
        c = Utf8Order.Compare(a.Id, b.Id);
        return c != 0 ? c : Utf8Order.Compare(a.Field, b.Field);
    }

    /// <summary>Sorts records by (entity, id, field).</summary>
    public static IReadOnlyList<ChangeRecord> SortByKey(IEnumerable<ChangeRecord> records)
    {
        var list = records.ToList();
        list.Sort(CompareByKey);
        return list;
    }

    public override string ToString() => $"{Entity}/{Id}/{Field} = {Value} @ {Hlc}";

    private static string? NonEmptyString(JsonData json, string key)
        => json.TryGetProperty(key, out var v) && v.Kind == JsonDataKind.String && v.StringValue.Length > 0 ? v.StringValue : null;
}
