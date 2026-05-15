using System.Collections.Generic;

namespace PayDay.Services;

/// <summary>
/// Status of the most recent fire-and-forget push (payment or snapshot).
/// Surfaced on the page VMs so the UI can show a green/red indicator next to
/// "last synced" without blocking the mark-paid flow.
/// </summary>
public enum NotionPushStatus
{
    NotConfigured = 0,
    Ok = 1,
    Failed = 2,
}

/// <summary>
/// Summary returned by <see cref="NotionSyncService.SyncBillsAsync"/>.
/// Counts are tallied across the whole bidirectional pass; <see cref="Errors"/>
/// holds per-page failures (the sync continues past individual failures).
/// </summary>
public sealed record NotionSyncResult(
    int Created,
    int Updated,
    int Pulled,
    int Archived,
    IReadOnlyList<string> Errors)
{
    public static readonly NotionSyncResult Empty =
        new(0, 0, 0, 0, System.Array.Empty<string>());

    public bool HasErrors => Errors.Count > 0;
}
