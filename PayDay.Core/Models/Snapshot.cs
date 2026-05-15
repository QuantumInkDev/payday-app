namespace PayDay.Models;

public sealed class Snapshot
{
    public long Id { get; set; }
    public string SnapshotDate { get; set; } = string.Empty;
    public double TotalOwed { get; set; }
    public string? Details { get; set; }
    public string? NotionPageId { get; set; }
}
