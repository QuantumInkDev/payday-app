namespace PayDay.Models;

public sealed class Payment
{
    public long Id { get; set; }
    public string BillId { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public double AmountPaid { get; set; }
    public string? PaidAt { get; set; }
    public string? NotionPageId { get; set; }
}
