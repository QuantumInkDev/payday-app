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
        new() { Id = "13", Name = "Electric",       Type = "Bills", Cost = 120,    DueDay = 3,  Rate = "Monthly", AutoPay = false, Notes = "Variable - estimate" },
        new() { Id = "12", Name = "Phone",          Type = "Bills", Cost = 85,    DueDay = 11, Rate = "Monthly", AutoPay = false },
        new() { Id = "10", Name = "Storage",        Type = "Bills", Cost = 140, DueDay = 18,  Rate = "Monthly", AutoPay = false },
        new() { Id = "11", Name = "Storage Unit 2",  Type = "Bills", Cost = 65,  DueDay = 26, Rate = "Monthly", AutoPay = false },

        // Cards (15)
        new() { Id = "1",  Name = "Woodgrove Rewards",                     Type = "Cards", Cost = 55,    Owed = 1200.00, Available = 1800,    CreditLimit = 3000, DueDay = 4,  Rate = "Monthly", AutoPay = false },
        new() { Id = "19", Name = "Contoso Card",                      Type = "Cards", Cost = 40,    Owed = 900.00, Available = 1100, CreditLimit = 2000, DueDay = 13, Rate = "Monthly", AutoPay = false },
        new() { Id = "21", Name = "Fabrikam Store Card",                 Type = "Cards", Cost = 35,    Owed = 0,       Available = 1500,   CreditLimit = 1500, DueDay = 21,  Rate = "Monthly", AutoPay = false },
        new() { Id = "16", Name = "Northwind – Platinum",     Type = "Cards", Cost = 25,    Owed = 0,       Available = 500,    CreditLimit = 500,  DueDay = 28, Rate = "Monthly", AutoPay = false },
        new() { Id = "7",  Name = "Northwind – Cashback",  Type = "Cards", Cost = 30,    Owed = 0,       Available = 1000,   CreditLimit = 1000, DueDay = 2, Rate = "Monthly", AutoPay = false },
        new() { Id = "6",  Name = "Northwind – Dining",        Type = "Cards", Cost = 25,    Owed = 0,       Available = 1200,   CreditLimit = 1200, DueDay = 17,  Rate = "Monthly", AutoPay = false },
        new() { Id = "4",  Name = "Tailspin – Electronics",         Type = "Cards", Cost = 110,   Owed = 3200,    Available = 800,    CreditLimit = 4000, DueDay = 25,  Rate = "Monthly", AutoPay = false },
        new() { Id = "5",  Name = "Tailspin – Home",      Type = "Cards", Cost = 25, Owed = 0,       Available = 1000,    CreditLimit = 1000,  DueDay = 9, Rate = "Monthly", AutoPay = false },
        new() { Id = "3",  Name = "Tailspin – Classic",      Type = "Cards", Cost = 35,    Owed = 950.00, Available = 1550,    CreditLimit = 2500, DueDay = 6, Rate = "Monthly", AutoPay = false },
        new() { Id = "17", Name = "Litware – Blue",          Type = "Cards", Cost = 30,    Owed = 0,       Available = 1000,   CreditLimit = 1000, DueDay = 19,  Rate = "Monthly", AutoPay = false },
        new() { Id = "18", Name = "Litware – Blue 2",        Type = "Cards", Cost = 30,    Owed = 0,       Available = 700,    CreditLimit = 700,  DueDay = 12, Rate = "Monthly", AutoPay = false },
        new() { Id = "20", Name = "Proseware Card",             Type = "Cards", Cost = 40,    Owed = 0,       Available = 1500,   CreditLimit = 1500, DueDay = 24,  Rate = "Monthly", AutoPay = false },
        new() { Id = "9",  Name = "Wingtip Credit",                     Type = "Cards", Cost = 50,    Owed = 1800.00, Available = 700,    CreditLimit = 2500, DueDay = 27, Rate = "Monthly", AutoPay = false },
        new() { Id = "8",  Name = "Adventure Works Furniture",                    Type = "Cards", Cost = 75,    Owed = 600,     Available = 2400,   CreditLimit = 3000, DueDay = 10,  Rate = "Monthly", AutoPay = false, Notes = "Furniture financing" },
        new() { Id = "2",  Name = "Coho Store Card",                     Type = "Cards", Cost = 25,    Owed = 0,       Available = 500,    CreditLimit = 500,  DueDay = 15,  Rate = "Monthly", AutoPay = false },

        // Loans (3)
        new() { Id = "14", Name = "Car Loan", Type = "Loans", Cost = 95, DueDay = 5, Rate = "Bi-Weekly", AutoPay = true,  Notes = "Auto-deducted from paycheck" },
        new() { Id = "26", Name = "Student Loan", Type = "Loans", Cost = 0,      DueDay = 5, Rate = "Bi-Weekly", AutoPay = true,  Notes = "Auto-deducted from paycheck" },
        new() { Id = "27", Name = "Personal Loan",      Type = "Loans", Cost = 0,      DueDay = 20,  Rate = "Monthly",   AutoPay = false },

        // People (1)
        new() { Id = "25", Name = "Roommate", Type = "People", Cost = 0, DueDay = 7, Rate = "Bi-Weekly", AutoPay = false },

        // Subscriptions (4)
        new() { Id = "23", Name = "Cloud Storage",      Type = "Subscriptions", Cost = 2.99,  DueDay = 3, Rate = "Monthly", AutoPay = true },
        new() { Id = "15", Name = "Music Streaming",         Type = "Subscriptions", Cost = 11.99, DueDay = 14,  Rate = "Monthly", AutoPay = true },
        new() { Id = "24", Name = "Ride Pass",            Type = "Subscriptions", Cost = 9.99,    DueDay = 16, Rate = "Monthly", AutoPay = true },
        new() { Id = "22", Name = "Video Streaming", Type = "Subscriptions", Cost = 13.99, DueDay = 30, Rate = "Monthly", AutoPay = true },
    };

    // Source: planning/PAYDAY_WINUI3_PLAN.md §2.1 + §5.1. Notion IDs are data-source IDs.
    public static readonly IReadOnlyList<(string Key, string? Value)> DefaultSettings = new (string, string?)[]
    {
        ("PayAnchor",         "2025-03-20"),
        ("EarlyStart",        "false"),
        ("LastNotionSync",    null),
        ("NotionBillsDb",     "00000000-0000-0000-0000-000000000102"),
        ("NotionPaymentsDb",  "00000000-0000-0000-0000-000000000202"),
        ("NotionSnapshotsDb", "00000000-0000-0000-0000-000000000302"),
    };
}
