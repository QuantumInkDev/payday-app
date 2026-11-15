/// What a reader knows about one listed snapshot (spec/sync-protocol.md section 11).
public struct SnapshotInfo: Sendable, Hashable {
    /// The snapshot's HLC (its file name without `.snap`); string order is HLC order.
    public let hlc: String
    /// Device ID to the highest sequence number the snapshot covers.
    public let covers: [String: Int64]
    /// Old enough to compact against (section 11.1, `Compaction.snapshotAgeOk`).
    public let ageOk: Bool

    public init(hlc: String, covers: [String: Int64], ageOk: Bool = false) {
        self.hlc = hlc
        self.covers = covers
        self.ageOk = ageOk
    }

    func covers(_ device: String) -> Int64 { covers[device] ?? 0 }
}

/// Which snapshots and change files one sync reads (section 10.2, step 3).
public struct ReadPlan: Sendable, Hashable {
    /// HLCs of the snapshots to merge, sorted.
    public let mergeSnapshots: [String]
    /// Per device, the change files to read in order (devices with none are left out).
    public let readFiles: [String: [Int64]]
    /// Every known device's read cursor afterwards.
    public let cursorsAfter: [String: Int64]
    /// Devices with a listed file above a gap, mapped to the first missing sequence number.
    public let gaps: [String: Int64]
}

/// Read planning and compaction decisions (sections 10.2 and 11).
public enum Compaction {
    public static let dayMilliseconds: Int64 = 24 * 60 * 60 * 1000
    public static let minimumAgeMilliseconds: Int64 = 7 * dayMilliseconds

    /// Plans one sync's reading. `cursors`: per device, the highest contiguous merged sequence
    /// number. `files`: listed sequence numbers. `unreadable`: listed files that failed to open in
    /// this sync, treated as not listed so a covering snapshot is merged instead.
    public static func readPlan(cursors: [String: Int64], files: [String: [Int64]], snapshots: [SnapshotInfo],
                                unreadable: [String: [Int64]] = [:]) -> ReadPlan {
        var cur = cursors
        var present: [String: Set<Int64>] = [:]
        for (d, seqs) in files { present[d] = Set(seqs).subtracting(unreadable[d] ?? []) }
        var devices = Set(cur.keys).union(present.keys)
        for s in snapshots { devices.formUnion(s.covers.keys) }
        for d in devices where cur[d] == nil { cur[d] = 0 }
        let orderedDevices = devices.sorted(by: StringOrder.utf8Less)
        var merged = Set<String>()
        var read: [String: [Int64]] = [:]
        func listed(_ d: String, _ seq: Int64) -> Bool { present[d]?.contains(seq) ?? false }
        while true {
            for d in orderedDevices {
                while listed(d, cur[d]! + 1) {
                    cur[d]! += 1
                    read[d, default: []].append(cur[d]!)
                }
            }
            let need = snapshots.filter { s in
                !merged.contains(s.hlc) && s.covers.contains { d, v in v > cur[d]! && !listed(d, cur[d]! + 1) }
            }
            if need.isEmpty { break }
            for s in need {
                merged.insert(s.hlc)
                for (d, v) in s.covers { cur[d] = max(cur[d]!, v) }
            }
        }
        var gaps: [String: Int64] = [:]
        for d in orderedDevices where (present[d] ?? []).contains(where: { $0 > cur[d]! }) { gaps[d] = cur[d]! + 1 }
        return ReadPlan(mergeSnapshots: merged.sorted(by: StringOrder.utf8Less), readFiles: read, cursorsAfter: cur, gaps: gaps)
    }

    /// Old enough to compact against: at least 7 days since this device first listed the snapshot
    /// (local clock only) and since the provider created the file (provider clock only). The
    /// snapshot's own HLC is never used. Unknown times mean "not old enough".
    public static func snapshotAgeOk(firstSeenLocalMs: Int64?, localNowMs: Int64?, serverCreatedMs: Int64?, serverNowMs: Int64?) -> Bool {
        guard let firstSeenLocalMs, let localNowMs, let serverCreatedMs, let serverNowMs else { return false }
        return localNowMs - firstSeenLocalMs >= minimumAgeMilliseconds && serverNowMs - serverCreatedMs >= minimumAgeMilliseconds
    }

    /// The device's own change files it may delete (section 11.5): some listed snapshot covering
    /// file `N` is old enough, and every snapshot newer than the oldest one covering `N` covers it.
    public static func deletableChangeFiles(device me: String, seqs: [Int64], snapshots: [SnapshotInfo]) -> [Int64] {
        seqs.sorted().filter { n in
            let covering = snapshots.filter { $0.covers(me) >= n }
            guard !covering.isEmpty, covering.contains(where: \.ageOk),
                  let oldest = covering.map(\.hlc).min(by: StringOrder.utf8Less) else { return false }
            return snapshots.allSatisfy { !StringOrder.utf8Less(oldest, $0.hlc) || $0.covers(me) >= n }
        }
    }

    /// `s` dominates `t`: `s` covers at least as much as `t` for every device in `t.covers`.
    public static func dominates(_ s: SnapshotInfo, _ t: SnapshotInfo) -> Bool {
        t.covers.allSatisfy { s.covers($0.key) >= $0.value }
    }

    /// Snapshots any device may delete (section 11.4): never the 2 newest; an older one only when
    /// the newest is old enough and one of the 2 newest dominates it. Sorted.
    public static func deletableSnapshots(_ snapshots: [SnapshotInfo]) -> [String] {
        let ordered = snapshots.sorted { StringOrder.utf8Less($1.hlc, $0.hlc) }
        guard let newest = ordered.first, newest.ageOk else { return [] }
        let kept = ordered.prefix(2)
        return ordered.dropFirst(2).filter { t in kept.contains { dominates($0, t) } }.map(\.hlc).sorted(by: StringOrder.utf8Less)
    }
}
