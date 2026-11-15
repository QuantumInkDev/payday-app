namespace PayDay.Sync.Reading;

/// <summary>Inputs to the snapshot-writing decision of section 11.2.</summary>
public sealed record SnapshotWriteInputs(
    bool InForeground,
    bool AllSealedBatchesUploaded,
    bool MergedEveryListedSnapshot,
    int ListedSnapshotCount,
    int TotalChangeFiles,
    int ChangeFilesNotCoveredByNewest,
    bool NewestSnapshotAgeOk);

/// <summary>Snapshots and compaction (<c>spec/sync-protocol.md</c> section 11).</summary>
public static class Compaction
{
    public const long DayMs = 24L * 60 * 60 * 1000;
    public const long MinSnapshotAgeMs = 7 * DayMs;
    public const int FirstSnapshotFileThreshold = 20;
    public const int UncoveredFileThreshold = 100;

    /// <summary>
    /// Section 11.1: old enough when this device first listed the snapshot at least 7 days ago by
    /// its own clock, and the provider created the file at least 7 days ago by the provider's clock.
    /// Any missing time means not old enough. The snapshot's HLC is never used.
    /// </summary>
    public static bool SnapshotAgeOk(long? firstSeenLocalMs, long localNowMs, long? serverCreatedMs, long? serverNowMs)
    {
        if (firstSeenLocalMs is not { } first || serverCreatedMs is not { } created || serverNowMs is not { } serverNow) return false;
        return localNowMs - first >= MinSnapshotAgeMs && serverNow - created >= MinSnapshotAgeMs;
    }

    /// <summary>
    /// Section 11.5: the own change files device <paramref name="me"/> may delete. File N may go when
    /// some listed snapshot covers it and one of those is old enough, and every snapshot newer than
    /// the oldest one covering N also covers N.
    /// </summary>
    public static IReadOnlyList<long> DeletableChangeFiles(string me, IEnumerable<long> seqs, IReadOnlyCollection<SnapshotInfo> snapshots)
    {
        ArgumentNullException.ThrowIfNull(seqs);
        ArgumentNullException.ThrowIfNull(snapshots);
        var output = new List<long>();
        foreach (var n in seqs.Distinct().Order())
        {
            var covering = snapshots.Where(s => s.CoversFor(me) >= n).ToList();
            if (covering.Count == 0 || !covering.Any(s => s.AgeOk)) continue;
            var oldest = covering.Select(s => s.Hlc).Min(StringComparer.Ordinal)!;
            if (snapshots.Where(s => string.CompareOrdinal(s.Hlc, oldest) > 0).All(s => s.CoversFor(me) >= n))
            {
                output.Add(n);
            }
        }
        return output;
    }

    /// <summary><paramref name="kept"/> dominates <paramref name="other"/>: it covers at least as much for every device in <paramref name="other"/>.</summary>
    public static bool Dominates(SnapshotInfo kept, SnapshotInfo other)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(other);
        return other.Covers.All(c => kept.CoversFor(c.Key) >= c.Value);
    }

    /// <summary>
    /// Section 11.4: the snapshots any device may delete. Never the 2 newest (by HLC); an older one
    /// only when the newest is old enough and one of the 2 newest dominates it.
    /// </summary>
    public static IReadOnlyList<string> DeletableSnapshots(IReadOnlyCollection<SnapshotInfo> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var ordered = snapshots.OrderByDescending(s => s.Hlc, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0 || !ordered[0].AgeOk) return Array.Empty<string>();
        var kept = ordered.Take(2).ToList();
        return ordered.Skip(2)
            .Where(s => kept.Any(k => Dominates(k, s)))
            .Select(s => s.Hlc)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Section 11.2: whether this device should write a snapshot now.</summary>
    public static bool ShouldWriteSnapshot(SnapshotWriteInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!inputs.InForeground || !inputs.AllSealedBatchesUploaded || !inputs.MergedEveryListedSnapshot) return false;
        if (inputs.ListedSnapshotCount == 0) return inputs.TotalChangeFiles >= FirstSnapshotFileThreshold;
        return inputs.ChangeFilesNotCoveredByNewest >= UncoveredFileThreshold
            || (inputs.NewestSnapshotAgeOk && inputs.ChangeFilesNotCoveredByNewest >= 1);
    }

    /// <summary>
    /// A new snapshot's <c>covers</c> (section 11.2): the writer's read cursors for every device it
    /// knows, plus its own last uploaded sequence number.
    /// </summary>
    public static IReadOnlyDictionary<string, long> SnapshotCovers(IReadOnlyDictionary<string, long> cursors, string me, long lastUploadedSeq)
    {
        ArgumentNullException.ThrowIfNull(cursors);
        var covers = new Dictionary<string, long>(cursors, StringComparer.Ordinal);
        covers[me] = Math.Max(covers.TryGetValue(me, out var c) ? c : 0, lastUploadedSeq);
        return covers;
    }
}
