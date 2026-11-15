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
        new() { Id = "13", Name = "Electric",       Type = "Bills", Payment =120,    DueDay = 8,  Rate = "Monthly", AutoPay = false, Notes = "Variable - estimate" },
        new() { Id = "12", Name = "Phone",          Type = "Bills", Payment =85,    DueDay = 24, Rate = "Monthly", AutoPay = false },
        new() { Id = "10", Name = "Storage",        Type = "Bills", Payment =140, DueDay = 5,  Rate = "Monthly", AutoPay = false },
        new() { Id = "11", Name = "Storage Unit 2",  Type = "Bills", Payment =65,  DueDay = 20, Rate = "Monthly", AutoPay = false },

        // Cards (15)
        new() { Id = "1",  Name = "Woodgrove Rewards",                     Type = "Cards", Payment =55,    Remaining =1200.00, Available = 1800,    CreditLimit = 3000, DueDay = 1,  Rate = "Monthly", AutoPay = false },
        new() { Id = "19", Name = "Contoso Card",                      Type = "Cards", Payment =40,    Remaining =900.00, Available = 1100, CreditLimit = 2000, DueDay = 31, Rate = "Monthly", AutoPay = false },
        new() { Id = "21", Name = "Fabrikam Store Card",                 Type = "Cards", Payment =35,    Remaining =0,       Available = 1500,   CreditLimit = 1500, DueDay = 7,  Rate = "Monthly", AutoPay = false },
        new() { Id = "16", Name = "Northwind – Platinum",     Type = "Cards", Payment =25,    Remaining =0,       Available = 500,    CreditLimit = 500,  DueDay = 15, Rate = "Monthly", AutoPay = false },
        new() { Id = "7",  Name = "Northwind – Cashback",  Type = "Cards", Payment =30,    Remaining =0,       Available = 1000,   CreditLimit = 1000, DueDay = 10, Rate = "Monthly", AutoPay = false },
        new() { Id = "6",  Name = "Northwind – Dining",        Type = "Cards", Payment =25,    Remaining =0,       Available = 1200,   CreditLimit = 1200, DueDay = 8,  Rate = "Monthly", AutoPay = false },
        new() { Id = "4",  Name = "Tailspin – Electronics",         Type = "Cards", Payment =110,   Remaining =3200,    Available = 800,    CreditLimit = 4000, DueDay = 8,  Rate = "Monthly", AutoPay = false },
        new() { Id = "5",  Name = "Tailspin – Home",      Type = "Cards", Payment =25, Remaining =0,       Available = 1000,    CreditLimit = 1000,  DueDay = 16, Rate = "Monthly", AutoPay = false },
        new() { Id = "3",  Name = "Tailspin – Classic",      Type = "Cards", Payment =35,    Remaining =950.00, Available = 1550,    CreditLimit = 2500, DueDay = 23, Rate = "Monthly", AutoPay = false },
        new() { Id = "17", Name = "Litware – Blue",          Type = "Cards", Payment =30,    Remaining =0,       Available = 1000,   CreditLimit = 1000, DueDay = 9,  Rate = "Monthly", AutoPay = false },
        new() { Id = "18", Name = "Litware – Blue 2",        Type = "Cards", Payment =30,    Remaining =0,       Available = 700,    CreditLimit = 700,  DueDay = 20, Rate = "Monthly", AutoPay = false },
        new() { Id = "20", Name = "Proseware Card",             Type = "Cards", Payment =40,    Remaining =0,       Available = 1500,   CreditLimit = 1500, DueDay = 6,  Rate = "Monthly", AutoPay = false },
        new() { Id = "9",  Name = "Wingtip Credit",                     Type = "Cards", Payment =50,    Remaining =1800.00, Available = 700,    CreditLimit = 2500, DueDay = 14, Rate = "Monthly", AutoPay = false },
        new() { Id = "8",  Name = "Adventure Works Furniture",                    Type = "Cards", Payment =75,    Remaining =600,     Available = 2400,   CreditLimit = 3000, DueDay = 8,  Rate = "Monthly", AutoPay = false, Notes = "Furniture financing" },
        new() { Id = "2",  Name = "Coho Store Card",                     Type = "Cards", Payment =25,    Remaining =0,       Available = 500,    CreditLimit = 500,  DueDay = 2,  Rate = "Monthly", AutoPay = false },

        // Loans (3)
        new() { Id = "14", Name = "Car Loan", Type = "Loans", Payment =95, DueDay = 15, Rate = "Bi-Weekly", AutoPay = true,  Notes = "Auto-deducted from paycheck" },
        new() { Id = "26", Name = "Student Loan", Type = "Loans", Payment =0,      DueDay = 15, Rate = "Bi-Weekly", AutoPay = true,  Notes = "Auto-deducted from paycheck" },
        new() { Id = "27", Name = "Personal Loan",      Type = "Loans", Payment =0,      DueDay = 1,  Rate = "Monthly",   AutoPay = false },

        // People (1)
        new() { Id = "25", Name = "Roommate", Type = "People", Payment =0, DueDay = 1, Rate = "Bi-Weekly", AutoPay = false },

        // Subscriptions (4)
        new() { Id = "23", Name = "Cloud Storage",      Type = "Subscriptions", Payment =2.99,  DueDay = 12, Rate = "Monthly", AutoPay = true },
        new() { Id = "15", Name = "Music Streaming",         Type = "Subscriptions", Payment =11.99, DueDay = 8,  Rate = "Monthly", AutoPay = true },
        new() { Id = "24", Name = "Ride Pass",            Type = "Subscriptions", Payment =9.99,    DueDay = 22, Rate = "Monthly", AutoPay = true },
        new() { Id = "22", Name = "Video Streaming", Type = "Subscriptions", Payment =13.99, DueDay = 27, Rate = "Monthly", AutoPay = true },
    };

    // Source: planning/PAYDAY_WINUI3_PLAN.md §2.1 + §5.1. Notion IDs are data-source IDs.
    public static readonly IReadOnlyList<(string Key, string? Value)> DefaultSettings = new (string, string?)[]
    {
        ("PayAnchor",         "2026-03-20"),
        ("EarlyStart",        "false"),
        ("LastNotionSync",    null),
        ("NotionBillsDb",     "00000000-0000-0000-0000-000000000102"),
        ("NotionPaymentsDb",  "00000000-0000-0000-0000-000000000202"),
        ("NotionSnapshotsDb", "00000000-0000-0000-0000-000000000302"),
    };
}
