using Microsoft.Data.Sqlite;
using PayDay.Sync.Clock;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Sync.Storage;

/// <summary>
/// <see cref="IRecordStore"/> over the <c>sync_records</c> table: one row per (entity, id, field) with
/// the encoded winning value and its HLC. <see cref="IdsWhere"/> uses the
/// (<c>entity</c>, <c>field</c>, <c>value</c>) index. Every enumeration is read completely before it
/// is returned, so callers may write while iterating.
/// </summary>
internal sealed class SqliteRecordStore : IRecordStore
{
    private readonly SqliteSyncStorage _storage;

    public SqliteRecordStore(SqliteSyncStorage storage) => _storage = storage;

    public int Count
    {
        get
        {
            var cmd = _storage.Cached("SELECT COUNT(*) FROM sync_records;");
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public bool TryGet(string entity, string id, string field, out SyncCell cell)
    {
        var cmd = _storage.Cached(
            "SELECT value, hlc FROM sync_records WHERE entity = $e AND row_id = $i AND field = $f;",
            "$e", "$i", "$f");
        cmd.Parameters["$e"].Value = entity;
        cmd.Parameters["$i"].Value = id;
        cmd.Parameters["$f"].Value = field;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            cell = default;
            return false;
        }
        cell = new SyncCell(SqliteValueCodec.Decode(reader.GetString(0)), Hlc.Parse(reader.GetString(1)));
        return true;
    }

    public void Set(string entity, string id, string field, SyncCell cell)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(field);
        var cmd = _storage.Cached(
            """
            INSERT INTO sync_records (entity, row_id, field, value, hlc) VALUES ($e, $i, $f, $v, $h)
            ON CONFLICT (entity, row_id, field) DO UPDATE SET value = excluded.value, hlc = excluded.hlc;
            """,
            "$e", "$i", "$f", "$v", "$h");
        cmd.Parameters["$e"].Value = entity;
        cmd.Parameters["$i"].Value = id;
        cmd.Parameters["$f"].Value = field;
        cmd.Parameters["$v"].Value = SqliteValueCodec.Encode(cell.Value);
        cmd.Parameters["$h"].Value = cell.Hlc.Text;
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<string> Entities()
        => Strings(_storage.Cached("SELECT DISTINCT entity FROM sync_records;"));

    public IEnumerable<string> Ids(string entity)
    {
        var cmd = _storage.Cached("SELECT DISTINCT row_id FROM sync_records WHERE entity = $e;", "$e");
        cmd.Parameters["$e"].Value = entity;
        return Strings(cmd);
    }

    public IEnumerable<KeyValuePair<string, SyncCell>> Fields(string entity, string id)
    {
        var cmd = _storage.Cached("SELECT field, value, hlc FROM sync_records WHERE entity = $e AND row_id = $i;", "$e", "$i");
        cmd.Parameters["$e"].Value = entity;
        cmd.Parameters["$i"].Value = id;
        var list = new List<KeyValuePair<string, SyncCell>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new KeyValuePair<string, SyncCell>(
                reader.GetString(0),
                new SyncCell(SqliteValueCodec.Decode(reader.GetString(1)), Hlc.Parse(reader.GetString(2)))));
        }
        return list;
    }

    public IEnumerable<string> IdsWhere(string entity, string field, SyncValue value)
    {
        var keys = SqliteValueCodec.EqualityKeys(value);
        var cmd = _storage.Cached(
            "SELECT row_id FROM sync_records WHERE entity = $e AND field = $f AND value IN ($v1, $v2);",
            "$e", "$f", "$v1", "$v2");
        cmd.Parameters["$e"].Value = entity;
        cmd.Parameters["$f"].Value = field;
        cmd.Parameters["$v1"].Value = keys[0];
        cmd.Parameters["$v2"].Value = keys[^1];
        return Strings(cmd);
    }

    public void Clear()
    {
        using var cmd = _storage.CreateCommand("DELETE FROM sync_records;");
        cmd.ExecuteNonQuery();
    }

    private static List<string> Strings(SqliteCommand cmd)
    {
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }
}
