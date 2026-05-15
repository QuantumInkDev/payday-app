namespace PayDay.Models;

public sealed class Bill
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double Cost { get; set; }
    public double Owed { get; set; }
    public double Available { get; set; }
    public double CreditLimit { get; set; }
    public int DueDay { get; set; } = 1;
    public string Rate { get; set; } = "Monthly";
    public double APR { get; set; }
    public bool AutoPay { get; set; }
    public bool Active { get; set; } = true;
    public string? YearlyDate { get; set; }
    public string? Notes { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public string? NotionPageId { get; set; }
}
