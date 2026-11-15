using PayDay.Sync.Clock;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>
/// A JSON backup ready to restore: rows by entity and ID (IDs already migrated, each bill's
/// Remaining already mapped to <c>remainingCheckpoint</c>) and the backup's settings object exactly
/// as stored in the file.
/// </summary>
public sealed record RestoreInput(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SyncValue>>> Rows,
    JsonData Settings);

/// <summary>Restore a JSON backup with sync on (<c>spec/sync-protocol.md</c> section 12.3).</summary>
public static class RestoreWriter
{
    /// <summary>
    /// The records of one restore, all at <paramref name="hlc"/>, sorted by entity, id and field:
    /// <list type="bullet">
    ///   <item>every field of every backup row of the five plan entities, plus <c>deleted: false</c>;</item>
    ///   <item><c>deleted: true</c> for every plan-entity row this device knows that is not in the
    ///     backup and not already tombstoned (live or pending rows);</item>
    ///   <item>every plan setting the mapping produced, and <c>null</c> for every other plan setting
    ///     this device has a non-null value for.</item>
    /// </list>
    /// Device rows, unknown entities, unknown setting keys and the Notion connector keys are never written.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> Restore(SyncState state, RestoreInput backup, Hlc hlc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(backup);
        foreach (var entity in backup.Rows.Keys)
        {
            if (!SyncEntities.PlanEntities.Contains(entity, StringComparer.Ordinal))
            {
                throw new ArgumentException($"A restore does not write {entity} rows.", nameof(backup));
            }
        }
        var settings = BackupSettings.MapForRestore(backup.Settings);
        var output = new List<ChangeRecord>();
        foreach (var entity in SyncEntities.PlanEntities)
        {
            var rows = backup.Rows.TryGetValue(entity, out var r)
                ? r
                : new Dictionary<string, IReadOnlyDictionary<string, SyncValue>>(StringComparer.Ordinal);
            foreach (var (id, fields) in rows)
            {
                foreach (var (field, value) in fields)
                {
                    if (field != SyncEntities.DeletedField) output.Add(new ChangeRecord(entity, id, field, value, hlc));
                }
                output.Add(new ChangeRecord(entity, id, SyncEntities.DeletedField, SyncValue.False, hlc));
            }
            foreach (var id in state.Ids(entity))
            {
                if (!rows.ContainsKey(id) && state.Value(entity, id, SyncEntities.DeletedField) is not { IsTrue: true })
                {
                    output.Add(new ChangeRecord(entity, id, SyncEntities.DeletedField, SyncValue.True, hlc));
                }
            }
        }
        foreach (var (key, value) in settings)
        {
            output.Add(new ChangeRecord(SyncEntities.Setting, key, SyncEntities.ValueField, value, hlc));
        }
        foreach (var key in state.Ids(SyncEntities.Setting))
        {
            if (!settings.ContainsKey(key)
                && SyncSettingKeys.IsPlanSetting(key)
                && state.Value(SyncEntities.Setting, key, SyncEntities.ValueField) is { IsNull: false })
            {
                output.Add(new ChangeRecord(SyncEntities.Setting, key, SyncEntities.ValueField, SyncValue.Null, hlc));
            }
        }
        return ChangeRecord.SortByKey(output);
    }
}
