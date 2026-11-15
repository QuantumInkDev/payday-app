namespace PayDay.Sync.Clock;

/// <summary>The clock update rules of <c>spec/sync-protocol.md</c> section 2.3 and 2.4, as pure functions.</summary>
public static class HlcClock
{
    /// <summary>A merged record more than this far ahead of the local clock triggers the skew warning (section 2.4).</summary>
    public const long SkewWarningMs = 10 * 60 * 1000;

    /// <summary>
    /// The HLC for a local edit, which also becomes the new clock state. <paramref name="last"/> is the
    /// stored clock state (or none); <paramref name="nowMs"/> is the physical clock in Unix ms.
    /// </summary>
    public static Hlc Tick(Hlc? last, long nowMs, string deviceId)
    {
        if (last is not { } l)
        {
            return Hlc.Encode(nowMs, 0, deviceId);
        }
        var w = Math.Max(nowMs, l.Wall);
        long c;
        if (w == l.Wall)
        {
            c = l.Counter + 1L;
            if (c > Hlc.MaxCounter)
            {
                w += 1;
                c = 0;
            }
        }
        else
        {
            c = 0;
        }
        if (w > Hlc.MaxWall) throw new SyncException(SyncErrorCodes.WallOutOfRange);
        return Hlc.Encode(w, c, deviceId);
    }

    /// <summary>The clock state after merging a record stamped <paramref name="remote"/>. Never increments.</summary>
    public static Hlc Observe(Hlc? last, Hlc remote, string deviceId)
    {
        if (last is not { } l || remote.Wall > l.Wall || (remote.Wall == l.Wall && remote.Counter > l.Counter))
        {
            return Hlc.Encode(remote.Wall, remote.Counter, deviceId);
        }
        return Hlc.Encode(l.Wall, l.Counter, deviceId);
    }

    /// <summary>True when <paramref name="remote"/> is more than 10 minutes ahead of <paramref name="nowMs"/>.</summary>
    public static bool SkewWarning(Hlc remote, long nowMs) => remote.Wall - nowMs > SkewWarningMs;
}

/// <summary>
/// A device's clock state (section 2.3). Callers persist <see cref="State"/> in the same local
/// transaction as the records stamped with it; <see cref="Engine.SyncReplica"/> does that.
/// </summary>
public sealed class HybridLogicalClock
{
    private readonly TimeProvider _time;

    public HybridLogicalClock(string deviceId, Hlc? state, TimeProvider time)
    {
        if (!Ids.DeviceIds.IsValid(deviceId)) throw new SyncException(SyncErrorCodes.BadDeviceId);
        ArgumentNullException.ThrowIfNull(time);
        DeviceId = deviceId;
        State = state;
        _time = time;
    }

    public string DeviceId { get; }

    public Hlc? State { get; private set; }

    public long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public Hlc Tick()
    {
        var next = HlcClock.Tick(State, NowMs, DeviceId);
        State = next;
        return next;
    }

    public void Observe(Hlc remote) => State = HlcClock.Observe(State, remote, DeviceId);

    /// <summary>
    /// The startup safety net of section 2.3: raise the state to at least <paramref name="seen"/>
    /// (an HLC this device stamped that might be newer than the saved state).
    /// </summary>
    public void RaiseTo(Hlc seen)
    {
        if (State is not { } s || seen.CompareTo(s) > 0)
        {
            State = Hlc.Encode(seen.Wall, seen.Counter, DeviceId);
        }
    }
}
