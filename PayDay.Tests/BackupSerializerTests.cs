using System;
using System.Collections.Generic;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.Tests;

public class BackupSerializerTests
{
    [Fact]
    public void ToJson_IncludesFormatVersionAndExportedAt()
    {
        var when = new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
        var json = BackupSerializer.ToJson(
            new[] { new Bill { Id = "1", Name = "Test", Type = "Cards" } },
            Array.Empty<Payment>(),
            Array.Empty<Snapshot>(),
            new Dictionary<string, string?> { ["Foo"] = "Bar" },
            exportedAt: when);

        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"exportedAt\":", json);
        Assert.Contains("\"name\": \"Test\"", json);
    }

    [Fact]
    public void RoundTrip_PreservesAllTables()
    {
        var bills = new[]
        {
            new Bill
            {
                Id = "b1", Name = "Amazon", Type = "Cards",
                Payment =87, Remaining =1545.06, Available = 954, CreditLimit = 2500,
                DueDay = 1, Rate = "Monthly", APR = 24.99,
                AutoPay = false, Active = true, Notes = "test note",
            },
        };
        var payments = new[]
        {
            new Payment { Id = 5, BillId = "b1", PeriodKey = "2026-05-15", AmountPaid = 87, PaidAt = "2026-05-16T10:00:00" },
        };
        var snapshots = new[]
        {
            new Snapshot { Id = 1, SnapshotDate = "2026-05-15", TotalRemaining =1545.06, Details = "{\"b1\":1545.06}" },
        };
        var settings = new Dictionary<string, string?>
        {
            ["PayAnchor"] = "2026-03-20",
            ["EarlyStart"] = "false",
            ["NullKey"] = null,
        };

        var json = BackupSerializer.ToJson(bills, payments, snapshots, settings);
        var parsed = BackupSerializer.FromJson(json);

        Assert.Equal(1, parsed.FormatVersion);
        Assert.Single(parsed.Bills);
        var b = parsed.Bills[0];
        Assert.Equal("Amazon", b.Name);
        Assert.Equal(1545.06, b.Remaining);
        Assert.Equal(24.99, b.APR);
        Assert.Equal("test note", b.Notes);
        Assert.True(b.Active);
        Assert.False(b.AutoPay);

        Assert.Single(parsed.Payments);
        Assert.Equal("b1", parsed.Payments[0].BillId);
        Assert.Equal(87, parsed.Payments[0].AmountPaid);

        Assert.Single(parsed.Snapshots);
        Assert.Equal(1545.06, parsed.Snapshots[0].TotalRemaining);

        Assert.Equal("2026-03-20", parsed.Settings["PayAnchor"]);
        Assert.Equal("false", parsed.Settings["EarlyStart"]);
        Assert.Null(parsed.Settings["NullKey"]);
    }

    [Fact]
    public void FromJson_FutureVersion_Throws()
    {
        var json = """{"formatVersion": 999, "exportedAt": "2026-01-01"}""";
        var ex = Assert.Throws<InvalidOperationException>(() => BackupSerializer.FromJson(json));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void FromJson_MissingVersion_Throws()
    {
        var json = """{"bills": []}""";
        Assert.Throws<InvalidOperationException>(() => BackupSerializer.FromJson(json));
    }

    [Fact]
    public void FromJson_OnlyHeader_LeavesEmptyCollections()
    {
        var json = """{"formatVersion": 1, "exportedAt": "2026-01-01"}""";
        var parsed = BackupSerializer.FromJson(json);
        Assert.Empty(parsed.Bills);
        Assert.Empty(parsed.Payments);
        Assert.Empty(parsed.Snapshots);
        Assert.Empty(parsed.Settings);
    }

    [Fact]
    public void FromJson_LegacyCostAndOwedKeys_RestoreAsPaymentAndRemaining()
    {
        // Pre-Phase-7c backups stored bills with "cost" and "owed" keys.
        var legacyJson = """
        {
          "formatVersion": 1,
          "exportedAt": "2026-05-15T00:00:00",
          "bills": [
            { "id": "1", "name": "Amazon", "type": "Cards", "cost": 87.0, "owed": 1545.06 }
          ]
        }
        """;
        var parsed = BackupSerializer.FromJson(legacyJson);
        var bill = Assert.Single(parsed.Bills);
        Assert.Equal(87.0, bill.Payment);
        Assert.Equal(1545.06, bill.Remaining);
    }
}
