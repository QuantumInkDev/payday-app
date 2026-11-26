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
        var when = new DateTime(2025, 5, 15, 12, 0, 0, DateTimeKind.Utc);
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
    public void ToJson_LeavesOutThisDevicesCloudSignInSettings()
    {
        var json = BackupSerializer.ToJson(
            Array.Empty<Bill>(),
            Array.Empty<Payment>(),
            Array.Empty<Snapshot>(),
            new Dictionary<string, string?>
            {
                ["PayAnchor"] = "2025-03-20",
                ["GoogleDriveClientId"] = "000000000000-fictional.apps.googleusercontent.com",
                ["GoogleDriveClientSecret"] = "fictional-secret",
                ["OneDriveClientId"] = "00000000-0000-0000-0000-00000000c1d0",
            });

        var settings = BackupSerializer.FromJson(json).Settings;
        Assert.Equal(new[] { "PayAnchor" }, settings.Keys);
    }

    [Fact]
    public async System.Threading.Tasks.Task Restoring_KeepsThisDevicesCloudSignInSettings()
    {
        using var env = new Sync.Local.SyncTestEnvironment();
        await env.InitializeAsync();
        await env.Db.SetSettingAsync("GoogleDriveClientSecret", "fictional-secret");
        await env.Db.SetSettingAsync("Theme", "Dark");

        await env.Db.ReplaceAllDataAsync(
            Array.Empty<Bill>(),
            Array.Empty<Payment>(),
            Array.Empty<Snapshot>(),
            new Dictionary<string, string?> { ["PayAnchor"] = "2025-03-20", ["GoogleDriveClientSecret"] = "from-an-old-backup" });

        Assert.Equal("fictional-secret", await env.Db.GetSettingAsync("GoogleDriveClientSecret"));
        Assert.Null(await env.Db.GetSettingAsync("Theme"));
        Assert.Equal("2025-03-20", await env.Db.GetSettingAsync("PayAnchor"));
    }

    [Fact]
    public void RoundTrip_PreservesAllTables()
    {
        var bills = new[]
        {
            new Bill
            {
                Id = "b1", Name = "Amber", Type = "Cards",
                Payment =55, Remaining =1200.00, Available = 1800, CreditLimit = 3000,
                DueDay = 1, Rate = "Monthly", APR = 24.99,
                AutoPay = false, Active = true, Notes = "test note",
            },
        };
        var payments = new[]
        {
            new Payment { Id = 5, BillId = "b1", PeriodKey = "2025-05-15", AmountPaid = 55, PaidAt = "2025-05-16T10:00:00" },
        };
        var snapshots = new[]
        {
            new Snapshot { Id = 1, SnapshotDate = "2025-05-15", TotalRemaining =1200.00, Details = "{\"b1\":1200.00}" },
        };
        var settings = new Dictionary<string, string?>
        {
            ["PayAnchor"] = "2025-03-20",
            ["EarlyStart"] = "false",
            ["NullKey"] = null,
        };

        var json = BackupSerializer.ToJson(bills, payments, snapshots, settings);
        var parsed = BackupSerializer.FromJson(json);

        Assert.Equal(1, parsed.FormatVersion);
        Assert.Single(parsed.Bills);
        var b = parsed.Bills[0];
        Assert.Equal("Amber", b.Name);
        Assert.Equal(1200.00, b.Remaining);
        Assert.Equal(24.99, b.APR);
        Assert.Equal("test note", b.Notes);
        Assert.True(b.Active);
        Assert.False(b.AutoPay);

        Assert.Single(parsed.Payments);
        Assert.Equal("b1", parsed.Payments[0].BillId);
        Assert.Equal(55, parsed.Payments[0].AmountPaid);

        Assert.Single(parsed.Snapshots);
        Assert.Equal(1200.00, parsed.Snapshots[0].TotalRemaining);

        Assert.Equal("2025-03-20", parsed.Settings["PayAnchor"]);
        Assert.Equal("false", parsed.Settings["EarlyStart"]);
        Assert.Null(parsed.Settings["NullKey"]);
    }

    [Fact]
    public void FromJson_FutureVersion_Throws()
    {
        var json = """{"formatVersion": 999, "exportedAt": "2025-01-01"}""";
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
        var json = """{"formatVersion": 1, "exportedAt": "2025-01-01"}""";
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
          "exportedAt": "2025-05-15T00:00:00",
          "bills": [
            { "id": "1", "name": "Amber", "type": "Cards", "cost": 55.0, "owed": 1200.00 }
          ]
        }
        """;
        var parsed = BackupSerializer.FromJson(legacyJson);
        var bill = Assert.Single(parsed.Bills);
        Assert.Equal(55.0, bill.Payment);
        Assert.Equal(1200.00, bill.Remaining);
    }
}
