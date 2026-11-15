using System.Globalization;
using Microsoft.Data.Sqlite;
using PayDay.Sync.Clock;
using PayDay.Sync.Engine;
using PayDay.Sync.Model;

namespace PayDay.Sync.Storage;

/// <summary>
/// <see cref="ISyncMetadata"/> over the <c>sync_meta</c>, <c>sync_outbox</c>, <c>sync_batches</c>,
/// <c>sync_batch_records</c>, <c>sync_cursors</c>, <c>sync_merged_ahead</c> and
/// <c>sync_snapshot_seen</c> tables. Nothing is cached in memory, so a rolled-back transaction
/// leaves no stale value behind.
/// </summary>
internal sealed class SqliteSyncMetadata : ISyncMetadata
{
    private const string DeviceIdKey = "deviceId";
    private const string VaultIdKey = "vaultId";
    private const string ClockStateKey = "clockState";
    private const string NextSeqKey = "nextSeq";

    private readonly SqliteSyncStorage _storage;

    public SqliteSyncMetadata(SqliteSyncStorage storage) => _storage = storage;

    public string? DeviceId
    {
        get => GetMeta(DeviceIdKey);
        set => SetMeta(DeviceIdKey, value);
    }

    public string? VaultId
    {
        get => GetMeta(VaultIdKey);
        set => SetMeta(VaultIdKey, value);
    }

    public Hlc? ClockState
    {
        get => GetMeta(ClockStateKey) is { } text ? Hlc.Parse(text) : null;
        set => SetMeta(ClockStateKey, value?.Text);
    }

    public long NextSeq
    {
        get => GetMeta(NextSeqKey) is { } text ? long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) : 1;
        set => SetMeta(NextSeqKey, value.ToString(CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<ChangeRecord> PendingRecords
        => ReadRecords(_storage.Cached("SELECT entity, row_id, field, value, hlc FROM sync_outbox ORDER BY pos;"));

    public void EnqueuePending(IEnumerable<ChangeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var cmd = _storage.Cached(
            "INSERT INTO sync_outbox (entity, row_id, field, value, hlc) VALUES ($e, $i, $f, $v, $h);",
            "$e", "$i", "$f", "$v", "$h");
        foreach (var r in records)
        {
            BindRecord(cmd, r);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearPending() => Execute("DELETE FROM sync_outbox;");

    public IReadOnlyList<SealedBatch> SealedBatches
    {
        get
        {
            var heads = new List<(long Seq, bool Uploaded)>();
            using (var reader = _storage.Cached("SELECT seq, uploaded FROM sync_batches ORDER BY seq;").ExecuteReader())
            {
                while (reader.Read()) heads.Add((reader.GetInt64(0), reader.GetInt64(1) != 0));
            }
            var records = new Dictionary<long, List<ChangeRecord>>();
            using (var reader = _storage.Cached("SELECT seq, entity, row_id, field, value, hlc FROM sync_batch_records ORDER BY seq, pos;").ExecuteReader())
            {
                while (reader.Read())
                {
                    var seq = reader.GetInt64(0);
                    if (!records.TryGetValue(seq, out var list)) records[seq] = list = new List<ChangeRecord>();
                    list.Add(RecordAt(reader, 1));
                }
            }
            return heads
                .Select(h => new SealedBatch(h.Seq, records.TryGetValue(h.Seq, out var list) ? list : new List<ChangeRecord>(), h.Uploaded))
                .ToList();
        }
    }

    public void AddSealedBatch(SealedBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var head = _storage.Cached("INSERT INTO sync_batches (seq, uploaded) VALUES ($s, $u);", "$s", "$u");
        head.Parameters["$s"].Value = batch.Seq;
        head.Parameters["$u"].Value = batch.Uploaded ? 1 : 0;
        head.ExecuteNonQuery();
        var cmd = _storage.Cached(
            "INSERT INTO sync_batch_records (seq, pos, entity, row_id, field, value, hlc) VALUES ($s, $p, $e, $i, $f, $v, $h);",
            "$s", "$p", "$e", "$i", "$f", "$v", "$h");
        for (var pos = 0; pos < batch.Records.Count; pos++)
        {
            cmd.Parameters["$s"].Value = batch.Seq;
            cmd.Parameters["$p"].Value = pos;
            BindRecord(cmd, batch.Records[pos]);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkBatchUploaded(long seq)
    {
        var cmd = _storage.Cached("UPDATE sync_batches SET uploaded = 1 WHERE seq = $s;", "$s");
        cmd.Parameters["$s"].Value = seq;
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException($"No sealed batch {seq}.");
    }

    public long LastUploadedSeq
        => Convert.ToInt64(_storage.Cached("SELECT COALESCE(MAX(seq), 0) FROM sync_batches WHERE uploaded = 1;").ExecuteScalar(), CultureInfo.InvariantCulture);

    public IReadOnlyDictionary<string, long> Cursors
    {
        get
        {
            var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
            using var reader = _storage.Cached("SELECT device_id, seq FROM sync_cursors;").ExecuteReader();
            while (reader.Read()) cursors[reader.GetString(0)] = reader.GetInt64(1);
            return cursors;
        }
    }

    public void SetCursor(string deviceId, long seq)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var cmd = _storage.Cached(
            "INSERT INTO sync_cursors (device_id, seq) VALUES ($d, $s) ON CONFLICT (device_id) DO UPDATE SET seq = excluded.seq;",
            "$d", "$s");
        cmd.Parameters["$d"].Value = deviceId;
        cmd.Parameters["$s"].Value = seq;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyCollection<long> MergedAhead(string deviceId)
    {
        var cmd = _storage.Cached("SELECT seq FROM sync_merged_ahead WHERE device_id = $d ORDER BY seq;", "$d");
        cmd.Parameters["$d"].Value = deviceId;
        var list = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetInt64(0));
        return list;
    }

    public void SetMergedAhead(string deviceId, IEnumerable<long> seqs)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(seqs);
        var wanted = new SortedSet<long>(seqs);
        var delete = _storage.Cached("DELETE FROM sync_merged_ahead WHERE device_id = $d;", "$d");
        delete.Parameters["$d"].Value = deviceId;
        delete.ExecuteNonQuery();
        var insert = _storage.Cached("INSERT INTO sync_merged_ahead (device_id, seq) VALUES ($d, $s);", "$d", "$s");
        foreach (var seq in wanted)
        {
            insert.Parameters["$d"].Value = deviceId;
            insert.Parameters["$s"].Value = seq;
            insert.ExecuteNonQuery();
        }
    }

    public long? SnapshotFirstListed(string snapshotName)
    {
        var cmd = _storage.Cached("SELECT first_listed FROM sync_snapshot_seen WHERE name = $n;", "$n");
        cmd.Parameters["$n"].Value = snapshotName;
        return cmd.ExecuteScalar() is long t ? t : null;
    }

    public void SetSnapshotFirstListed(string snapshotName, long unixMs)
    {
        ArgumentNullException.ThrowIfNull(snapshotName);
        var cmd = _storage.Cached(
            "INSERT INTO sync_snapshot_seen (name, first_listed) VALUES ($n, $t) ON CONFLICT (name) DO UPDATE SET first_listed = excluded.first_listed;",
            "$n", "$t");
        cmd.Parameters["$n"].Value = snapshotName;
        cmd.Parameters["$t"].Value = unixMs;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Forgets all sync state. The device's sync settings (<c>sync_device</c>) and the materializer's local IDs are kept.</summary>
    public void Reset()
    {
        Execute("DELETE FROM sync_meta;");
        Execute("DELETE FROM sync_outbox;");
        Execute("DELETE FROM sync_batch_records;");
        Execute("DELETE FROM sync_batches;");
        Execute("DELETE FROM sync_cursors;");
        Execute("DELETE FROM sync_merged_ahead;");
        Execute("DELETE FROM sync_snapshot_seen;");
    }

    private string? GetMeta(string key)
    {
        var cmd = _storage.Cached("SELECT value FROM sync_meta WHERE key = $k;", "$k");
        cmd.Parameters["$k"].Value = key;
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string? value)
    {
        if (value is null)
        {
            var delete = _storage.Cached("DELETE FROM sync_meta WHERE key = $k;", "$k");
            delete.Parameters["$k"].Value = key;
            delete.ExecuteNonQuery();
            return;
        }
        var cmd = _storage.Cached(
            "INSERT INTO sync_meta (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            "$k", "$v");
        cmd.Parameters["$k"].Value = key;
        cmd.Parameters["$v"].Value = value;
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var cmd = _storage.CreateCommand(sql);
        cmd.ExecuteNonQuery();
    }

    private static void BindRecord(SqliteCommand cmd, ChangeRecord r)
    {
        cmd.Parameters["$e"].Value = r.Entity;
        cmd.Parameters["$i"].Value = r.Id;
        cmd.Parameters["$f"].Value = r.Field;
        cmd.Parameters["$v"].Value = SqliteValueCodec.Encode(r.Value);
        cmd.Parameters["$h"].Value = r.Hlc.Text;
    }

    private static List<ChangeRecord> ReadRecords(SqliteCommand cmd)
    {
        var list = new List<ChangeRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(RecordAt(reader, 0));
        return list;
    }

    private static ChangeRecord RecordAt(SqliteDataReader reader, int first)
        => new(
            reader.GetString(first),
            reader.GetString(first + 1),
            reader.GetString(first + 2),
            SqliteValueCodec.Decode(reader.GetString(first + 3)),
            Hlc.Parse(reader.GetString(first + 4)));
}
