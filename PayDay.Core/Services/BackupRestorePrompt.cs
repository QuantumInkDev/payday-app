using System.Threading;
using System.Threading.Tasks;

namespace PayDay.Services;

/// <summary>
/// First-launch restore helper (plan §6.2). Decides whether the app should
/// offer to restore from an auto-backup — true only when the Bills table is
/// empty and the backup folder has at least one file — and applies the
/// restore by atomically replacing local data with the backup contents.
///
/// <para>Pure logic so it can be unit-tested without WinUI. The view layer
/// owns the dialog itself.</para>
/// </summary>
public sealed class BackupRestorePrompt
{
    private readonly IDatabaseService _db;
    private readonly BackupRotationService _backups;

    public BackupRestorePrompt(IDatabaseService db, BackupRotationService backups)
    {
        _db = db;
        _backups = backups;
    }

    /// <summary>
    /// Returns the newest backup the app should offer to restore from, or
    /// <c>null</c> when no prompt is warranted (the DB already has bills, or
    /// the backup folder is empty).
    /// </summary>
    public async Task<BackupEntry?> GetCandidateAsync(CancellationToken ct = default)
    {
        var bills = await _db.GetAllBillsAsync().ConfigureAwait(false);
        if (bills.Count > 0) return null;
        return await _backups.LatestAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <paramref name="entry"/>, parses it via <see cref="BackupSerializer.FromJson"/>,
    /// and atomically replaces the local DB. Throws on a malformed file (and
    /// leaves the DB untouched because <see cref="BackupSerializer.FromJson"/>
    /// validates before the call to <c>ReplaceAllDataAsync</c>).
    /// </summary>
    public async Task ApplyAsync(BackupEntry entry, CancellationToken ct = default)
    {
        var json = await _backups.ReadAsync(entry.FileName, ct).ConfigureAwait(false);
        var file = BackupSerializer.FromJson(json);
        await _db.ReplaceAllDataAsync(file.Bills, file.Payments, file.Snapshots, file.Settings).ConfigureAwait(false);
    }
}
