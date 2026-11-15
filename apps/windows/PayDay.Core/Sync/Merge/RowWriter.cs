using PayDay.Sync.Clock;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>Generic writer rules for creating, editing and deleting rows (<c>sync-protocol.md</c> section 4.4).</summary>
public static class RowWriter
{
    /// <summary>
    /// A create: a record for every known field of the entity (the given value, or the field's
    /// default) plus <c>deleted: false</c>, all at <paramref name="hlc"/>. Fields the schema does not
    /// list are written as given. <c>remainingCheckpoint</c> values are rounded like any money field.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> Create(string entity, string id, IReadOnlyDictionary<string, SyncValue> fields, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (entity == SyncEntities.Setting) throw new ArgumentException("Settings are written with SetSetting.", nameof(entity));
        if (fields.ContainsKey(SyncEntities.DeletedField)) throw new ArgumentException("A create always writes deleted: false itself.", nameof(fields));
        var output = new Dictionary<string, SyncValue>(StringComparer.Ordinal);
        foreach (var spec in SyncSchema.FieldsOf(entity))
        {
            output[spec.Name] = spec.Default;
        }
        foreach (var (field, value) in fields)
        {
            output[field] = Normalize(entity, field, value);
        }
        output[SyncEntities.DeletedField] = SyncValue.False;
        return ChangeRecord.SortByKey(output.Select(f => new ChangeRecord(entity, id, f.Key, f.Value, hlc)));
    }

    /// <summary>
    /// An edit: a record only for each field whose value changed (money compares in cents). Use
    /// <see cref="PaymentWriter.SetRemaining"/> for a bill's Remaining, never this method.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> Edit(SyncState state, string entity, string id, IReadOnlyDictionary<string, SyncValue> changes, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.ContainsKey(SyncEntities.DeletedField)) throw new ArgumentException("Use Delete or Undelete for the tombstone.", nameof(changes));
        if (entity == SyncEntities.Bill && changes.ContainsKey("remainingCheckpoint"))
        {
            throw new ArgumentException("Set Remaining through PaymentWriter.SetRemaining (section 5.3).", nameof(changes));
        }
        var output = new List<ChangeRecord>();
        foreach (var (field, raw) in changes)
        {
            var value = Normalize(entity, field, raw);
            var current = state.Value(entity, id, field);
            if (current is { } cur)
            {
                var same = SyncSchema.TryGetField(entity, field, out var spec) ? spec.SameValue(cur, value) : cur == value;
                if (same) continue;
            }
            output.Add(new ChangeRecord(entity, id, field, value, hlc));
        }
        return ChangeRecord.SortByKey(output);
    }

    /// <summary>A delete: one <c>deleted: true</c> record, or nothing when the row is already tombstoned.</summary>
    public static IReadOnlyList<ChangeRecord> Delete(SyncState state, string entity, string id, Hlc hlc)
        => SetDeleted(state, entity, id, deleted: true, hlc);

    /// <summary>An undelete: one <c>deleted: false</c> record, or nothing when the row is already live.</summary>
    public static IReadOnlyList<ChangeRecord> Undelete(SyncState state, string entity, string id, Hlc hlc)
        => SetDeleted(state, entity, id, deleted: false, hlc);

    /// <summary>Sets one synced setting. <c>null</c> means "use the default". Nothing is written when the value is unchanged.</summary>
    public static IReadOnlyList<ChangeRecord> SetSetting(SyncState state, string key, SyncValue value, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (key == SyncSettingKeys.SafeToSpendBuffer && value.IsNumber) value = SyncValue.Number(SyncMoney.Round2(value.NumberValue));
        if (state.Value(SyncEntities.Setting, key, SyncEntities.ValueField) is { } cur && cur == value) return Array.Empty<ChangeRecord>();
        return new[] { new ChangeRecord(SyncEntities.Setting, key, SyncEntities.ValueField, value, hlc) };
    }

    private static IReadOnlyList<ChangeRecord> SetDeleted(SyncState state, string entity, string id, bool deleted, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (entity == SyncEntities.Setting) throw new ArgumentException("Settings have no tombstone; write value: null.", nameof(entity));
        if (state.Value(entity, id, SyncEntities.DeletedField) is { IsBool: true } cur && cur.BoolValue == deleted)
        {
            return Array.Empty<ChangeRecord>();
        }
        return new[] { new ChangeRecord(entity, id, SyncEntities.DeletedField, SyncValue.Bool(deleted), hlc) };
    }

    /// <summary>Writers round every money value to cents before writing it (<c>data-model.md</c> 3.2).</summary>
    private static SyncValue Normalize(string entity, string field, SyncValue value)
        => SyncSchema.TryGetField(entity, field, out var spec) && spec.Type == SyncFieldType.Money && value.IsNumber
            ? SyncValue.Number(SyncMoney.Round2(value.NumberValue))
            : value;
}
