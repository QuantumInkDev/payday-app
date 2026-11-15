using PayDay.Sync.Local;

namespace PayDay.Sync.Coordination;

/// <summary>
/// When to sync (<c>spec/sync-protocol.md</c> 10.1, "Sync triggers"): at launch and on returning to the
/// foreground, 5 seconds after the last local edit (debounced), every 5 minutes while in the
/// foreground, and on "Sync now".
/// <para>
/// The trigger logic is a plain state machine over a <see cref="TimeProvider"/>:
/// <see cref="NextDue"/> says when the next sync is due and <see cref="RunDueAsync"/> runs it if it is,
/// so tests drive it with a manual clock. <see cref="Start"/> runs the same logic on a background loop
/// for the app. A failing sync never stops the loop.
/// </para>
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    public static readonly TimeSpan DefaultEditDebounce = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task> _sync;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTimeOffset? _immediate;
    private DateTimeOffset? _afterEdit;
    private DateTimeOffset? _periodic;
    private bool _foreground = true;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private LocalSync? _attached;

    /// <param name="sync">One sync (normally <see cref="SyncCoordinator.SyncAsync"/>).</param>
    public SyncScheduler(Func<CancellationToken, Task> sync, TimeProvider? time = null, TimeSpan? editDebounce = null, TimeSpan? interval = null)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _time = time ?? TimeProvider.System;
        EditDebounce = editDebounce ?? DefaultEditDebounce;
        Interval = interval ?? DefaultInterval;
    }

    public TimeSpan EditDebounce { get; }

    public TimeSpan Interval { get; }

    /// <summary>How many syncs this scheduler started.</summary>
    public int Runs { get; private set; }

    /// <summary>The last exception a sync threw (the coordinator reports through its status and never throws; this is for anything else).</summary>
    public Exception? LastError { get; private set; }

    public bool InForeground
    {
        get
        {
            lock (_lock) return _foreground;
        }
    }

    /// <summary>Raises a local-edit trigger for every app write that went through sync.</summary>
    public void Attach(LocalSync sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        Detach();
        _attached = sync;
        sync.LocalEdited += OnLocalEdited;
    }

    public void Detach()
    {
        if (_attached is { } s) s.LocalEdited -= OnLocalEdited;
        _attached = null;
    }

    private void OnLocalEdited(object? sender, EventArgs e) => NotifyLocalEdit();

    /// <summary>The app started: sync now.</summary>
    public void NotifyLaunch() => SyncNow();

    /// <summary>The app came to (or left) the foreground. Coming back syncs now; in the background only edits and "Sync now" trigger.</summary>
    public void NotifyForeground(bool foreground)
    {
        lock (_lock) _foreground = foreground;
        if (foreground) SyncNow();
        else Wake();
    }

    /// <summary>A local edit: sync <see cref="EditDebounce"/> after the last one.</summary>
    public void NotifyLocalEdit()
    {
        lock (_lock) _afterEdit = _time.GetUtcNow() + EditDebounce;
        Wake();
    }

    /// <summary>"Sync now".</summary>
    public void SyncNow()
    {
        lock (_lock) _immediate = _time.GetUtcNow();
        Wake();
    }

    /// <summary>When the next sync is due, or null when nothing is scheduled.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_lock) return NextDueLocked();
        }
    }

    /// <summary>Runs one sync if one is due now. Returns true when it ran.</summary>
    public async Task<bool> RunDueAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (NextDueLocked() is not { } due || due > now) return false;
            _immediate = null;
            if (_afterEdit <= now) _afterEdit = null;
        }
        Runs++;
        try
        {
            await _sync(ct).ConfigureAwait(false);
            LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
        lock (_lock) _periodic = _time.GetUtcNow() + Interval;
        return true;
    }

    private DateTimeOffset? NextDueLocked()
    {
        DateTimeOffset? next = null;
        foreach (var t in new[] { _immediate, _afterEdit, _foreground ? _periodic : null })
        {
            if (t is { } v && (next is null || v < next)) next = v;
        }
        return next;
    }

    /// <summary>Starts the background loop (idempotent). It sleeps until <see cref="NextDue"/> or a new trigger.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    /// <summary>Stops the background loop and waits for a running sync to finish.</summary>
    public async Task StopAsync()
    {
        if (_loopCts is not { } cts || _loop is not { } loop) return;
        cts.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        cts.Dispose();
        _loopCts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await RunDueAsync(ct).ConfigureAwait(false)) continue;
            var delay = NextDue is { } due ? due - _time.GetUtcNow() : Timeout.InfiniteTimeSpan;
            if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already awake.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Detach();
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _wake.Dispose();
    }
}
