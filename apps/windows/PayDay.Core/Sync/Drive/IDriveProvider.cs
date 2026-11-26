namespace PayDay.Sync.Drive;

/// <summary>
/// One file in the vault folder as the provider lists it (<c>spec/sync-protocol.md</c> 6.1).
/// </summary>
/// <param name="Id">The provider's own ID for this file. Two files can share a <paramref name="Path"/> on providers that do not enforce unique names (Google Drive), never an ID.</param>
/// <param name="Path">The path relative to the vault root, <c>/</c>-separated, for example <c>devices/0a0a0a0a0a0a0a0a/000001.chg</c>.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="CreatedTime">
/// The creation time the <b>provider</b> assigned (OneDrive <c>createdDateTime</c>, Google Drive
/// <c>createdTime</c>, Dropbox <c>server_modified</c>), never one a client set; null when the provider
/// gives none. Snapshot age and stale pairing files are measured with it (sections 9.1, 11.1).
/// </param>
public sealed record DriveItem(string Id, string Path, long Size, DateTimeOffset? CreatedTime)
{
    /// <summary>The file name: the last path segment.</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>One folder's listing: its files, its sub-folder names, and the provider's clock at the time of the listing.</summary>
/// <param name="ServerTime">The provider's current time when it answered (for example the HTTP <c>Date</c> header), or null.</param>
public sealed record DriveFolderListing(IReadOnlyList<DriveItem> Files, IReadOnlyList<string> Folders, DateTimeOffset? ServerTime);

/// <summary>Why a drive call failed.</summary>
public enum DriveErrorKind
{
    /// <summary>Network trouble, throttling, a 5xx: retry later.</summary>
    Transient,

    /// <summary>The sign-in expired or was revoked: the user signs in again.</summary>
    Unauthorized,

    /// <summary>The file does not exist (any more).</summary>
    NotFound,

    /// <summary>"Upload new" found a file with that name already (providers that enforce unique names).</summary>
    AlreadyExists,

    /// <summary>The drive is full.</summary>
    QuotaExceeded,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>A drive call failed. Sync never loses local data because of one: it reports the failure and retries on a later sync.</summary>
public class DriveException : Exception
{
    public DriveException(DriveErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public DriveErrorKind Kind { get; }
}

/// <summary>
/// The user's cloud drive, seen through the app folder holding the vault (<c>PayDay/</c>): what the
/// sync loop needs and nothing more. Paths are vault-relative (<see cref="DriveItem.Path"/>); the root
/// folder is <c>""</c>. Implementations: OneDrive (Microsoft Graph, app folder), later Google Drive and
/// Dropbox; <see cref="InMemoryDriveProvider"/> for tests.
/// <para>
/// Contract: uploads never set client timestamps (section 11.1); <see cref="UploadNewAsync"/> creates
/// missing parent folders and never overwrites; listings may lag behind uploads (eventual
/// consistency) and, on providers without unique names, may show two files with one path.
/// </para>
/// </summary>
public interface IDriveProvider
{
    /// <summary>A short name for messages ("OneDrive").</summary>
    string Name { get; }

    /// <summary>
    /// A stable key for the provider, recorded with the vault state so a device in a vault always
    /// reconnects to the provider that holds it (<see cref="DriveChoice"/>).
    /// </summary>
    string Key => Name;

    /// <summary>True when the provider refuses a second file with the same path (OneDrive, Dropbox); false for Google Drive.</summary>
    bool EnforcesUniqueNames { get; }

    /// <summary>True when <see cref="DeleteAsync"/> bypasses the recycle bin (Google Drive <c>files.delete</c>).</summary>
    bool DeletesPermanently { get; }

    /// <summary>Lists one folder (<c>""</c> for the vault root). A folder that does not exist lists as empty.</summary>
    Task<DriveFolderListing> ListAsync(string folder, CancellationToken ct = default);

    /// <summary>Downloads one listed file. Throws <see cref="DriveException"/> (<see cref="DriveErrorKind.NotFound"/> when it is gone).</summary>
    Task<byte[]> DownloadAsync(DriveItem item, CancellationToken ct = default);

    /// <summary>
    /// Uploads a new file at <paramref name="path"/>, failing with <see cref="DriveErrorKind.AlreadyExists"/>
    /// when the provider enforces unique names and the path is taken. Returns the new item.
    /// </summary>
    Task<DriveItem> UploadNewAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Deletes one file, permanently where the provider can (<see cref="DeletesPermanently"/>). A file already gone is not an error.</summary>
    Task DeleteAsync(DriveItem item, CancellationToken ct = default);

    /// <summary>The provider's current time (for example the <c>Date</c> header of a cheap request).</summary>
    Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct = default);
}
