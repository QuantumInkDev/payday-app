using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace PayDay.Services;

/// <summary>
/// <see cref="IBackupStore"/> backed by <c>ApplicationData.Current.LocalFolder/backups/</c>.
/// The <c>backups</c> subfolder is created on demand on every operation, so a
/// fresh install (which has no such folder) just gets a transparent first
/// <c>CreateAsync</c>.
/// </summary>
public sealed class WindowsBackupStore : IBackupStore
{
    public const string FolderName = "backups";

    private static Task<StorageFolder> GetFolderAsync()
        => ApplicationData.Current.LocalFolder
            .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists)
            .AsTask();

    public async Task WriteAsync(string fileName, string content, CancellationToken ct = default)
    {
        var folder = await GetFolderAsync().ConfigureAwait(false);
        var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);
        await FileIO.WriteTextAsync(file, content).AsTask(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        var folder = await GetFolderAsync().ConfigureAwait(false);
        var files = await folder.GetFilesAsync().AsTask(ct).ConfigureAwait(false);
        var entries = new List<BackupEntry>(files.Count);
        foreach (var file in files)
        {
            var props = await file.GetBasicPropertiesAsync().AsTask(ct).ConfigureAwait(false);
            entries.Add(new BackupEntry(file.Name, props.DateModified.UtcDateTime));
        }
        return entries;
    }

    public async Task<string> ReadAsync(string fileName, CancellationToken ct = default)
    {
        var folder = await GetFolderAsync().ConfigureAwait(false);
        var file = await folder.GetFileAsync(fileName).AsTask(ct).ConfigureAwait(false);
        return await FileIO.ReadTextAsync(file).AsTask(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        var folder = await GetFolderAsync().ConfigureAwait(false);
        try
        {
            var file = await folder.GetFileAsync(fileName).AsTask(ct).ConfigureAwait(false);
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete).AsTask(ct).ConfigureAwait(false);
        }
        catch (System.IO.FileNotFoundException)
        {
            // Silent — IBackupStore.DeleteAsync is no-op on missing files.
        }
    }
}
