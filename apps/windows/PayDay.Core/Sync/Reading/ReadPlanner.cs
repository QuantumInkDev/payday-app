using PayDay.Sync.Json;

namespace PayDay.Sync.Reading;

/// <summary>A listed snapshot as compaction sees it: its HLC (the file name), its <c>covers</c>, and whether it is old enough (section 11.1).</summary>
public sealed record SnapshotInfo(string Hlc, IReadOnlyDictionary<string, long> Covers, bool AgeOk = false)
{
    /// <summary><c>covers[device]</c>, with a missing device counting as 0.</summary>
    public long CoversFor(string deviceId) => Covers.TryGetValue(deviceId, out var v) ? v : 0;
}

/// <summary>
/// What one sync reads (section 10.2): the snapshots it must merge, the change files it reads in
/// order per device, the read cursors afterwards, and for each device whose next file is missing
/// while a higher one is listed, the first missing sequence number.
/// </summary>
public sealed record ReadPlan(
    IReadOnlyList<string> MergeSnapshots,
    IReadOnlyDictionary<string, IReadOnlyList<long>> ReadFiles,
    IReadOnlyDictionary<string, long> CursorsAfter,
    IReadOnlyDictionary<string, long> Gaps);

/// <summary>Read planning (<c>spec/sync-protocol.md</c> section 10.2 step 3 and section 11.3).</summary>
public static class ReadPlanner
{
    /// <summary>
    /// Plans a read. Each device's files are read from <c>cursor + 1</c> while listed; whenever a
    /// device's next file is not listed (or failed to open in this sync), every snapshot not yet
    /// merged that covers past that device's cursor is merged, raising cursors to its
    /// <c>covers</c>. Repeats until nothing changes.
    /// </summary>
    /// <param name="cursors">Read cursor per device (devices never heard of count as 0).</param>
    /// <param name="files">Listed change-file sequence numbers per device.</param>
    /// <param name="snapshots">Every listed snapshot.</param>
    /// <param name="unreadable">Listed files that failed to open in this sync; they count as not listed.</param>
    public static ReadPlan Plan(
        IReadOnlyDictionary<string, long> cursors,
        IReadOnlyDictionary<string, IReadOnlyCollection<long>> files,
        IReadOnlyCollection<SnapshotInfo> snapshots,
        IReadOnlyDictionary<string, IReadOnlyCollection<long>>? unreadable = null)
    {
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(snapshots);

        var present = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var (device, seqs) in files)
        {
            var set = new HashSet<long>(seqs);
            if (unreadable is not null && unreadable.TryGetValue(device, out var bad)) set.ExceptWith(bad);
            present[device] = set;
        }

        var cur = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (d, c) in cursors) cur[d] = c;
        var devices = new SortedSet<string>(Utf8Order.Instance);
        devices.UnionWith(cur.Keys);
        devices.UnionWith(present.Keys);
        foreach (var s in snapshots) devices.UnionWith(s.Covers.Keys);
        foreach (var d in devices) cur.TryAdd(d, 0);

        bool Listed(string d, long seq) => present.TryGetValue(d, out var set) && set.Contains(seq);

        var merged = new HashSet<string>(StringComparer.Ordinal);
        var read = devices.ToDictionary(d => d, _ => new List<long>(), StringComparer.Ordinal);
        while (true)
        {
            foreach (var d in devices)
            {
                while (Listed(d, cur[d] + 1))
                {
                    cur[d]++;
                    read[d].Add(cur[d]);
                }
            }
            var need = snapshots
                .Where(s => !merged.Contains(s.Hlc)
                    && s.Covers.Any(c => c.Value > cur[c.Key] && !Listed(c.Key, cur[c.Key] + 1)))
                .ToList();
            if (need.Count == 0) break;
            foreach (var s in need)
            {
                merged.Add(s.Hlc);
                foreach (var (d, v) in s.Covers) cur[d] = Math.Max(cur[d], v);
            }
        }

        var gaps = new SortedDictionary<string, long>(Utf8Order.Instance);
        foreach (var d in devices)
        {
            if (present.TryGetValue(d, out var set) && set.Any(q => q > cur[d])) gaps[d] = cur[d] + 1;
        }
        var readFiles = new SortedDictionary<string, IReadOnlyList<long>>(Utf8Order.Instance);
        foreach (var d in devices)
        {
            if (read[d].Count > 0) readFiles[d] = read[d];
        }
        var after = new SortedDictionary<string, long>(Utf8Order.Instance);
        foreach (var d in devices) after[d] = cur[d];
        var mergeList = merged.ToList();
        mergeList.Sort(string.CompareOrdinal);
        return new ReadPlan(mergeList, readFiles, after, gaps);
    }
}
