using System.Globalization;

namespace PayDay.Sync.Drive;

/// <summary>The drive operations <see cref="InMemoryDriveProvider"/> can be told to fail.</summary>
public enum DriveOperation
{
    List,
    Download,
    Upload,
    Delete,
    ServerTime,
}

/// <summary>
/// An in-memory <see cref="IDriveProvider"/> shared by every simulated device in a test, with the
/// awkward parts of real providers on demand:
/// <list type="bullet">
///   <item><b>Unique names or not</b>: like OneDrive (a second upload to a path fails) or like Google
///     Drive (it creates a second file with the same name; <see cref="AddCopy"/> makes one directly).</item>
///   <item><b>Eventual listings</b>: a new file appears in listings only <see cref="ListingDelay"/> after
///     it was created (server clock), or after <see cref="HideFromListings"/> listings.</item>
///   <item><b>Failures</b>: <see cref="FailNext"/> makes the next matching calls throw, and
///     <see cref="LoseNextUploadReply"/> stores an upload but reports a failure (the reply was lost).</item>
///   <item><b>Its own clock</b>: creation times and <see cref="DriveFolderListing.ServerTime"/> come from
///     the server clock, which may be offset from any device's clock (<see cref="ServerClockOffset"/>).</item>
/// </list>
/// Thread-safe. Deleted files go to <see cref="RecycleBin"/> unless deletes are permanent.
/// </summary>
public sealed class InMemoryDriveProvider : IDriveProvider
{
    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly List<StoredFile> _files = new();
    private readonly List<StoredFile> _recycled = new();
    private readonly Dictionary<string, int> _hidden = new(StringComparer.Ordinal);
    private readonly List<Failure> _failures = new();
    private long _nextId = 1;
    private int _loseUploadReplies;

    /// <param name="clock">The clock the server's time is based on (plus <see cref="ServerClockOffset"/>).</param>
    /// <param name="enforcesUniqueNames">True to behave like OneDrive, false like Google Drive.</param>
    /// <param name="deletesPermanently">False to keep deleted files in <see cref="RecycleBin"/>, like OneDrive and Dropbox.</param>
    public InMemoryDriveProvider(TimeProvider clock, bool enforcesUniqueNames = true, bool deletesPermanently = true)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        EnforcesUniqueNames = enforcesUniqueNames;
        DeletesPermanently = deletesPermanently;
    }

    public string Name => "In-memory drive";

    public bool EnforcesUniqueNames { get; }

    public bool DeletesPermanently { get; }

    /// <summary>How far the server's clock is from <c>clock</c> (positive: the server is ahead).</summary>
    public TimeSpan ServerClockOffset { get; set; }

    /// <summary>A new file is listed only once the server's clock is this far past its creation.</summary>
    public TimeSpan ListingDelay { get; set; }

    public DateTimeOffset ServerNow => _clock.GetUtcNow() + ServerClockOffset;

    public int ListCalls { get; private set; }

    public int DownloadCalls { get; private set; }

    public int UploadCalls { get; private set; }

    /// <summary>Every stored path (one entry per file, so a duplicate name appears twice), sorted.</summary>
    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_lock) return _files.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Paths of deleted files the provider still keeps (when deletes are not permanent).</summary>
    public IReadOnlyList<string> RecycleBin
    {
        get
        {
            lock (_lock) return _recycled.Select(f => f.Path).ToArray();
        }
    }

    public bool Exists(string path)
    {
        lock (_lock) return _files.Any(f => f.Path == path);
    }

    /// <summary>The bytes of the (first) file at <paramref name="path"/>.</summary>
    public byte[] Read(string path)
    {
        lock (_lock) return Find(path).Data.ToArray();
    }

    /// <summary>Replaces the bytes of the (first) file at <paramref name="path"/>: corruption or tampering.</summary>
    public void Overwrite(string path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock) Find(path).Data = data.ToArray();
    }

    /// <summary>Removes a file behind every device's back (for example a provider losing it).</summary>
    public void Remove(string path)
    {
        lock (_lock) _files.RemoveAll(f => f.Path == path);
    }

    /// <summary>Adds a file directly, with the server's current time as creation time (or <paramref name="createdAt"/>).</summary>
    public DriveItem Put(string path, byte[] data, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock) return Store(path, data, createdAt ?? ServerNow).Item;
    }

    /// <summary>Adds a second file with the same path, as Google Drive allows.</summary>
    public DriveItem AddCopy(string path, byte[] data) => Put(path, data);

    /// <summary>Keeps <paramref name="path"/> out of the next <paramref name="listings"/> listings of its folder.</summary>
    public void HideFromListings(string path, int listings)
    {
        lock (_lock) _hidden[path] = listings;
    }

    /// <summary>The next <paramref name="count"/> calls of <paramref name="operation"/> (on a path starting with <paramref name="pathPrefix"/>, if given) throw.</summary>
    public void FailNext(DriveOperation operation, string? pathPrefix = null, int count = 1, DriveErrorKind kind = DriveErrorKind.Transient)
    {
        lock (_lock)
        {
            for (var i = 0; i < count; i++) _failures.Add(new Failure(operation, pathPrefix, kind));
        }
    }

    /// <summary>The next upload is stored but the call fails, as when the reply never arrived.</summary>
    public void LoseNextUploadReply()
    {
        lock (_lock) _loseUploadReplies++;
    }

    public Task<DriveFolderListing> ListAsync(string folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ListCalls++;
            ThrowIfFailing(DriveOperation.List, folder);
            var prefix = folder.Length == 0 ? string.Empty : folder + "/";
            var now = ServerNow;
            var files = new List<DriveItem>();
            var folders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var f in _files)
            {
                if (!f.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (f.Created + ListingDelay > now) continue;
                var rest = f.Path[prefix.Length..];
                var slash = rest.IndexOf('/');
                if (slash >= 0)
                {
                    folders.Add(rest[..slash]);
                    continue;
                }
                if (_hidden.TryGetValue(f.Path, out var left) && left > 0) continue;
                files.Add(f.Item);
            }
            foreach (var path in _hidden.Keys.ToArray())
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal) && path.IndexOf('/', prefix.Length) < 0 && _hidden[path] > 0) _hidden[path]--;
            }
            return Task.FromResult(new DriveFolderListing(files, folders.ToArray(), now));
        }
    }

    public Task<byte[]> DownloadAsync(DriveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            DownloadCalls++;
            ThrowIfFailing(DriveOperation.Download, item.Path);
            var file = _files.FirstOrDefault(f => f.Id == item.Id)
                ?? throw new DriveException(DriveErrorKind.NotFound, $"{item.Path} is gone.");
            return Task.FromResult(file.Data.ToArray());
        }
    }

    public Task<DriveItem> UploadNewAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            UploadCalls++;
            ThrowIfFailing(DriveOperation.Upload, path);
            if (EnforcesUniqueNames && _files.Any(f => f.Path == path))
            {
                throw new DriveException(DriveErrorKind.AlreadyExists, $"{path} already exists.");
            }
            var stored = Store(path, data.ToArray(), ServerNow);
            if (_loseUploadReplies > 0)
            {
                _loseUploadReplies--;
                throw new DriveException(DriveErrorKind.Transient, $"The reply to uploading {path} was lost.");
            }
            return Task.FromResult(stored.Item);
        }
    }

    public Task DeleteAsync(DriveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ThrowIfFailing(DriveOperation.Delete, item.Path);
            var file = _files.FirstOrDefault(f => f.Id == item.Id);
            if (file is null) return Task.CompletedTask;
            _files.Remove(file);
            if (!DeletesPermanently) _recycled.Add(file);
            return Task.CompletedTask;
        }
    }

    public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ThrowIfFailing(DriveOperation.ServerTime, string.Empty);
            return Task.FromResult(ServerNow);
        }
    }

    private StoredFile Store(string path, byte[] data, DateTimeOffset created)
    {
        var id = "item-" + (_nextId++).ToString(CultureInfo.InvariantCulture);
        var file = new StoredFile(id, path, created) { Data = data };
        _files.Add(file);
        return file;
    }

    private StoredFile Find(string path)
        => _files.FirstOrDefault(f => f.Path == path) ?? throw new InvalidOperationException($"No file at {path}.");

    private void ThrowIfFailing(DriveOperation operation, string path)
    {
        var i = _failures.FindIndex(f => f.Operation == operation && (f.PathPrefix is null || path.StartsWith(f.PathPrefix, StringComparison.Ordinal)));
        if (i < 0) return;
        var failure = _failures[i];
        _failures.RemoveAt(i);
        throw new DriveException(failure.Kind, $"Simulated {operation} failure for '{path}'.");
    }

    private sealed class StoredFile
    {
        public StoredFile(string id, string path, DateTimeOffset created)
        {
            Id = id;
            Path = path;
            Created = created;
        }

        public string Id { get; }

        public string Path { get; }

        public DateTimeOffset Created { get; }

        public byte[] Data { get; set; } = Array.Empty<byte>();

        public DriveItem Item => new(Id, Path, Data.Length, Created);
    }

    private sealed record Failure(DriveOperation Operation, string? PathPrefix, DriveErrorKind Kind);
}
