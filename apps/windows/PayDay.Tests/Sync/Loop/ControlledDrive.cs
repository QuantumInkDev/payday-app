using System;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Sync.Drive;

namespace PayDay.Tests.Sync.Loop;

/// <summary>
/// A drive wrapper a test can steer: <see cref="SignedOut"/> makes every call fail as unauthorized
/// (an expired or revoked sign-in), and <see cref="Hold"/> makes listings wait until released or
/// cancelled (a slow, throttled drive).
/// </summary>
internal sealed class ControlledDrive : IDriveProvider
{
    private readonly IDriveProvider _inner;
    private TaskCompletionSource? _hold;

    public ControlledDrive(IDriveProvider inner) => _inner = inner;

    public bool SignedOut { get; set; }

    public int ListCalls { get; private set; }

    /// <summary>Completes when a listing is waiting on <see cref="Hold"/>.</summary>
    public TaskCompletionSource Held { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>From now on listings wait until <see cref="Release"/> or their cancellation.</summary>
    public void Hold()
    {
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release() => _hold?.TrySetResult();

    public string Name => "OneDrive";

    public bool EnforcesUniqueNames => _inner.EnforcesUniqueNames;

    public bool DeletesPermanently => _inner.DeletesPermanently;

    private void Check()
    {
        if (SignedOut) throw new DriveException(DriveErrorKind.Unauthorized, "The OneDrive sign-in expired.");
    }

    public async Task<DriveFolderListing> ListAsync(string folder, CancellationToken ct = default)
    {
        ListCalls++;
        Check();
        if (_hold is { } hold)
        {
            Held.TrySetResult();
            await hold.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        return await _inner.ListAsync(folder, ct).ConfigureAwait(false);
    }

    public Task<byte[]> DownloadAsync(DriveItem item, CancellationToken ct = default)
    {
        Check();
        return _inner.DownloadAsync(item, ct);
    }

    public Task<DriveItem> UploadNewAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Check();
        return _inner.UploadNewAsync(path, data, ct);
    }

    public Task DeleteAsync(DriveItem item, CancellationToken ct = default)
    {
        Check();
        return _inner.DeleteAsync(item, ct);
    }

    public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct = default)
    {
        Check();
        return _inner.GetServerTimeAsync(ct);
    }
}
