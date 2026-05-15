using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PayDay.Services;

/// <summary>
/// Auto-rotating backup engine (plan §6.2). On every mark-paid or snapshot save,
/// the view-model fires <see cref="CreateAsync"/>; this snapshots the full DB
/// via <see cref="BackupSerializer"/>, writes it to the supplied
/// <see cref="IBackupStore"/> under a timestamped filename, and trims the
/// folder back to the most recent <see cref="MaxBackups"/> entries.
///
/// <para>Pure logic — all I/O goes through <see cref="IBackupStore"/> so this
/// runs on plain net9.0 in tests.</para>
/// </summary>
public sealed class BackupRotationService
{
    public const int MaxBackups = 10;
    public const string FileNamePrefix = "payday-backup-";
    public const string FileNameExtension = ".json";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    private readonly IDatabaseService _db;
    private readonly IBackupStore _store;
    private readonly Func<DateTime> _utcNow;

    public BackupRotationService(IDatabaseService db, IBackupStore store, Func<DateTime>? utcNow = null)
    {
        _db = db;
        _store = store;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Formats a backup filename for the given UTC timestamp:
    /// <c>payday-backup-yyyyMMdd-HHmmss.json</c>.
    /// </summary>
    public static string FormatFileName(DateTime utc) =>
        $"{FileNamePrefix}{utc.ToString(TimestampFormat, CultureInfo.InvariantCulture)}{FileNameExtension}";

    /// <summary>
    /// Snapshots the full DB, writes the JSON to the store under a timestamped
    /// filename, and trims the folder to <see cref="MaxBackups"/>. Returns the
    /// filename that was written.
    /// </summary>
    public async Task<string> CreateAsync(CancellationToken ct = default)
    {
        var bills = await _db.GetAllBillsAsync().ConfigureAwait(false);
        var payments = await _db.GetAllPaymentsAsync().ConfigureAwait(false);
        var snapshots = await _db.GetAllSnapshotsAsync().ConfigureAwait(false);
        var settings = await _db.GetAllSettingsAsync().ConfigureAwait(false);

        var now = _utcNow();
        var json = BackupSerializer.ToJson(bills, payments, snapshots, settings, exportedAt: now);
        var fileName = FormatFileName(now);

        await _store.WriteAsync(fileName, json, ct).ConfigureAwait(false);
        await TrimAsync(ct).ConfigureAwait(false);
        return fileName;
    }

    /// <summary>
    /// Returns the auto-backup entries known to the store, newest first.
    /// Filters out anything that doesn't match the
    /// <c>payday-backup-*.json</c> pattern so a stray file in the folder
    /// can't poison rotation.
    /// </summary>
    public async Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        var entries = await _store.ListAsync(ct).ConfigureAwait(false);
        return entries
            .Where(IsBackupFile)
            .OrderByDescending(e => e.LastWriteUtc)
            .ThenByDescending(e => e.FileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Returns the newest backup, or <c>null</c> if the folder is empty.</summary>
    public async Task<BackupEntry?> LatestAsync(CancellationToken ct = default)
    {
        var entries = await ListAsync(ct).ConfigureAwait(false);
        return entries.Count == 0 ? null : entries[0];
    }

    /// <summary>Reads the raw JSON of a specific backup file.</summary>
    public Task<string> ReadAsync(string fileName, CancellationToken ct = default)
        => _store.ReadAsync(fileName, ct);

    /// <summary>
    /// Deletes everything beyond the <see cref="MaxBackups"/> most recent
    /// entries. Returns the number of files deleted.
    /// </summary>
    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        var entries = await ListAsync(ct).ConfigureAwait(false);
        if (entries.Count <= MaxBackups) return 0;

        var stale = entries.Skip(MaxBackups).ToList();
        foreach (var entry in stale)
        {
            await _store.DeleteAsync(entry.FileName, ct).ConfigureAwait(false);
        }
        return stale.Count;
    }

    private static bool IsBackupFile(BackupEntry entry) =>
        entry.FileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase)
        && entry.FileName.EndsWith(FileNameExtension, StringComparison.OrdinalIgnoreCase);
}
