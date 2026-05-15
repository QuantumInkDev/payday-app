using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PayDay.Services;

/// <summary>
/// Abstraction over the on-disk auto-backup folder (plan §6.2). The Windows-backed
/// implementation (<c>WindowsBackupStore</c>) lives in the app project because it
/// uses <c>Windows.Storage</c>; <see cref="BackupRotationService"/> here in
/// <c>PayDay.Core</c> drives it without touching the filesystem directly so it
/// can be unit-tested with an in-memory fake.
/// </summary>
public interface IBackupStore
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="fileName"/>, overwriting any existing entry.</summary>
    Task WriteAsync(string fileName, string content, CancellationToken ct = default);

    /// <summary>Returns every entry the store currently holds. Filtering is up to the caller.</summary>
    Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>Reads the content of <paramref name="fileName"/>. Throws if absent.</summary>
    Task<string> ReadAsync(string fileName, CancellationToken ct = default);

    /// <summary>Removes <paramref name="fileName"/>. No-ops if the entry is absent.</summary>
    Task DeleteAsync(string fileName, CancellationToken ct = default);
}

/// <summary>One auto-backup file as seen by <see cref="IBackupStore"/>.</summary>
public sealed record BackupEntry(string FileName, DateTime LastWriteUtc);
