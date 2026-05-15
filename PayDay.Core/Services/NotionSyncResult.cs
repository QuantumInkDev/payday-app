using System.Collections.Generic;

namespace PayDay.Services;

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
