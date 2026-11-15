"""Reading with snapshots, and compaction. Spec: spec/sync-protocol.md sections 10.2 and 11.

A snapshot in these functions is {"hlc": str, "covers": {deviceId: int}, "ageOk": bool}.
HLC strings order snapshots (byte-wise string order equals HLC order).
"""

DAY_MS = 24 * 60 * 60 * 1000
MIN_AGE_MS = 7 * DAY_MS


def _cov(s, d):
    return s["covers"].get(d, 0)


def read_plan(cursors, files, snapshots, unreadable=None):
    """Which snapshots a reader must merge and which change files it reads, in one sync.

    cursors: {deviceId: highest contiguous seq merged}; files: {deviceId: [listed seqs]};
    unreadable: {deviceId: [listed seqs that failed to open in this sync]}, treated as not listed.
    Returns mergeSnapshots (sorted HLCs), readFiles ({device: [seqs]}), cursorsAfter, gaps.
    """
    cur = {d: c for d, c in cursors.items()}
    bad = {d: set(v) for d, v in (unreadable or {}).items()}
    present = {d: set(v) - bad.get(d, set()) for d, v in files.items()}
    devices = set(cur) | set(present) | {d for s in snapshots for d in s["covers"]}
    for d in devices:
        cur.setdefault(d, 0)
    merged, read = set(), {d: [] for d in devices}
    while True:
        for d in sorted(devices):
            while cur[d] + 1 in present.get(d, ()):
                cur[d] += 1
                read[d].append(cur[d])
        need = [
            s for s in snapshots
            if s["hlc"] not in merged
            and any(v > cur[d] and cur[d] + 1 not in present.get(d, ()) for d, v in s["covers"].items())
        ]
        if not need:
            break
        for s in need:
            merged.add(s["hlc"])
            for d, v in s["covers"].items():
                cur[d] = max(cur[d], v)
    gaps = {d: cur[d] + 1 for d in sorted(devices) if any(q > cur[d] for q in present.get(d, ()))}
    return {
        "mergeSnapshots": sorted(merged),
        "readFiles": {d: read[d] for d in sorted(devices) if read[d]},
        "cursorsAfter": {d: cur[d] for d in sorted(devices)},
        "gaps": gaps,
    }


def snapshot_age_ok(first_seen_local_ms, local_now_ms, server_created_ms, server_now_ms):
    """Old enough to compact against: 7 days since this device first listed the snapshot (local
    clock only) and 7 days since the provider created the file (provider clock only). The snapshot's
    own HLC wall time is not used, so a writer with a skewed clock cannot stall or rush compaction."""
    if None in (first_seen_local_ms, server_created_ms, server_now_ms):
        return False
    return (local_now_ms - first_seen_local_ms >= MIN_AGE_MS
            and server_now_ms - server_created_ms >= MIN_AGE_MS)


def deletable_change_files(me, seqs, snapshots):
    """Own change files that device `me` may delete."""
    out = []
    for n in sorted(seqs):
        covering = [s for s in snapshots if _cov(s, me) >= n]
        if not covering or not any(s["ageOk"] for s in covering):
            continue
        oldest = min(s["hlc"] for s in covering)
        if all(_cov(s, me) >= n for s in snapshots if s["hlc"] > oldest):
            out.append(n)
    return out


def _dominates(k, s):
    return all(_cov(k, d) >= v for d, v in s["covers"].items())


def deletable_snapshots(snapshots):
    """Snapshots any device may delete: never the 2 newest; an older one only when the newest is
    old enough and one of the 2 newest covers at least as much for every device."""
    ordered = sorted(snapshots, key=lambda s: s["hlc"], reverse=True)
    kept, older = ordered[:2], ordered[2:]
    if not ordered or not ordered[0]["ageOk"]:
        return []
    return sorted(s["hlc"] for s in older if any(_dominates(k, s) for k in kept))
