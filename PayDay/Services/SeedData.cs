using System.Collections.Generic;
using PayDay.Models;

namespace PayDay.Services;

internal static class SeedData
{
    // Source: planning/PAYDAY_WINUI3_PLAN.md §2.3. IDs match the original HTML app.
    // The HTML app uses integer IDs; we persist them as TEXT to keep room for sync IDs later.
    public static readonly IReadOnlyList<Bill> Bills = new Bill[]
    {
        // Bills (4)
        new() { Id = "13", Name = "Electric",       Type = "Bills", Payment =400,    DueDay = 8,  Rate = "Monthly", AutoPay = false, Notes = "Variable - estimate" },
        new() { Id = "12", Name = "Phone",          Type = "Bills", Payment =151,    DueDay = 24, Rate = "Monthly", AutoPay = false },
        new() { Id = "10", Name = "Storage",        Type = "Bills", Payment =368.23, DueDay = 5,  Rate = "Monthly", AutoPay = false },
        new() { Id = "11", Name = "Storage Clari",  Type = "Bills", Payment =90.63,  DueDay = 20, Rate = "Monthly", AutoPay = false },

        // Cards (15)
        new() { Id = "1",  Name = "Amazon",                     Type = "Cards", Payment =87,    Remaining =1545.06, Available = 954,    CreditLimit = 2500, DueDay = 1,  Rate = "Monthly", AutoPay = false },
        new() { Id = "19", Name = "Apple",                      Type = "Cards", Payment =61,    Remaining =1647.50, Available = 352.50, CreditLimit = 2000, DueDay = 31, Rate = "Monthly", AutoPay = false },
        new() { Id = "21", Name = "Burlington",                 Type = "Cards", Payment =49,    Remaining =0,       Available = 1030,   CreditLimit = 1030, DueDay = 7,  Rate = "Monthly", AutoPay = false },
        new() { Id = "16", Name = "Capital One – Platinum",     Type = "Cards", Payment =25,    Remaining =0,       Available = 400,    CreditLimit = 400,  DueDay = 15, Rate = "Monthly", AutoPay = false },
        new() { Id = "7",  Name = "Capital One – QuickSilver",  Type = "Cards", Payment =45,    Remaining =0,       Available = 1300,   CreditLimit = 1300, DueDay = 10, Rate = "Monthly", AutoPay = false },
        new() { Id = "6",  Name = "Capital One – Savor",        Type = "Cards", Payment =25,    Remaining =0,       Available = 1300,   CreditLimit = 1300, DueDay = 8,  Rate = "Monthly", AutoPay = false },
        new() { Id = "4",  Name = "Citibank – BestBuy",         Type = "Cards", Payment =180,   Remaining =6000,    Available = 100,    CreditLimit = 6100, DueDay = 8,  Rate = "Monthly", AutoPay = false },
        new() { Id = "5",  Name = "Citibank – Home Depot",      Type = "Cards", Payment =30.66, Remaining =0,       Available = 750,    CreditLimit = 750,  DueDay = 16, Rate = "Monthly", AutoPay = false },
        new() { Id = "3",  Name = "Citibank – Simplicity",      Type = "Cards", Payment =20,    Remaining =1767.27, Available = 750,    CreditLimit = 2500, DueDay = 23, Rate = "Monthly", AutoPay = false },
        new() { Id = "17", Name = "Credit One – Amex",          Type = "Cards", Payment =42,    Remaining =0,       Available = 1250,   CreditLimit = 1250, DueDay = 9,  Rate = "Monthly", AutoPay = false },
        new() { Id = "18", Name = "Credit One – Amex 2",        Type = "Cards", Payment =61,    Remaining =0,       Available = 800,    CreditLimit = 800,  DueDay = 20, Rate = "Monthly", AutoPay = false },
        new() { Id = "20", Name = "Discovery Card",             Type = "Cards", Payment =63,    Remaining =0,       Available = 2000,   CreditLimit = 2000, DueDay = 6,  Rate = "Monthly", AutoPay = false },
        new() { Id = "9",  Name = "PayPal",                     Type = "Cards", Payment =64,    Remaining =2752.06, Available = 415,    CreditLimit = 3200, DueDay = 14, Rate = "Monthly", AutoPay = false },
        new() { Id = "8",  Name = "Raymour",                    Type = "Cards", Payment =92,    Remaining =500,     Available = 3500,   CreditLimit = 4000, DueDay = 8,  Rate = "Monthly", AutoPay = false, Notes = "Furniture financing" },
        new() { Id = "2",  Name = "Target",                     Type = "Cards", Payment =30,    Remaining =0,       Available = 400,    CreditLimit = 400,  DueDay = 2,  Rate = "Monthly", AutoPay = false },

        // Loans (3)
        new() { Id = "14", Name = "401K Loan #2", Type = "Loans", Payment =112.93, DueDay = 15, Rate = "Bi-Weekly", AutoPay = true,  Notes = "Auto-deducted from paycheck" },
        new() { Id = "26", Name = "401K Loan #1", Type = "Loans", Payment =0,      DueDay = 15, Rate = "Bi-Weekly", AutoPay = true,  Notes = "Auto-deducted from paycheck" },
        new() { Id = "27", Name = "Prosper",      Type = "Loans", Payment =0,      DueDay = 1,  Rate = "Monthly",   AutoPay = false },

        // People (1)
        new() { Id = "25", Name = "Mom", Type = "People", Payment =0, DueDay = 1, Rate = "Bi-Weekly", AutoPay = false },

        // Subscriptions (4)
        new() { Id = "23", Name = "Google One",      Type = "Subscriptions", Payment =9.99,  DueDay = 12, Rate = "Monthly", AutoPay = true },
        new() { Id = "15", Name = "Spotify",         Type = "Subscriptions", Payment =10.65, DueDay = 8,  Rate = "Monthly", AutoPay = true },
        new() { Id = "24", Name = "Uber",            Type = "Subscriptions", Payment =10,    DueDay = 22, Rate = "Monthly", AutoPay = true },
        new() { Id = "22", Name = "YouTube Premium", Type = "Subscriptions", Payment =10.67, DueDay = 27, Rate = "Monthly", AutoPay = true },
    };

    // Source: planning/PAYDAY_WINUI3_PLAN.md §2.1 + §5.1. Notion IDs are data-source IDs.
    public static readonly IReadOnlyList<(string Key, string? Value)> DefaultSettings = new (string, string?)[]
    {
        ("PayAnchor",         "2026-03-20"),
        ("EarlyStart",        "false"),
        ("LastNotionSync",    null),
        ("NotionBillsDb",     "f5fe82ee-9224-4566-9ff9-22d7ce4510e8"),
        ("NotionPaymentsDb",  "a953d84c-5b80-4c6c-baf4-ac5cd40b70ec"),
        ("NotionSnapshotsDb", "612d5c43-fb3c-428a-8e85-a16234ee28b7"),
    };
}
