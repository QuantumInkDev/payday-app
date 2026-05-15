using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Tests;

/// <summary>
/// Auto-backup wiring tests (chunk 6b). PayDayPageViewModel + InsightsPageViewModel
/// fire fire-and-forget rotation calls after local persistence; tests await the
/// VM's <c>PendingAutoBackup</c> task to make the side effects deterministic.
/// </summary>
public class AutoBackupTests
{
    private static FakeDatabaseService SeedDb()
    {
        var db = new FakeDatabaseService();
        db.Settings["PayAnchor"] = "2026-05-15";
        db.Bills.Add(new Bill { Id = "1", Name = "Electric", Type = "Bills", Cost = 400, DueDay = 15, Rate = "Monthly", Active = true });
        return db;
    }

    // ------------------------------------------------------------------
    // PayDayPageViewModel — payments trigger backup
    // ------------------------------------------------------------------

    [Fact]
    public async Task MarkPaid_NoBackupService_StatusStaysNotConfigured()
    {
        var db = SeedDb();
        var vm = new PayDayPageViewModel(db);
        await vm.LoadAsync(new DateTime(2026, 5, 15));

        var row = vm.UnpaidBills.Single();
        await vm.MarkPaidCommand.ExecuteAsync(row);
        if (vm.PendingAutoBackup is not null) await vm.PendingAutoBackup;

        Assert.Single(db.Payments);
        Assert.Equal(BackupStatus.NotConfigured, vm.LastBackupStatus);
    }

    [Fact]
    public async Task MarkPaid_BackupSucceeds_StatusOkAndFileWritten()
    {
        var db = SeedDb();
        var store = new InMemoryBackupStore();
        var backups = new BackupRotationService(db, store);
        var vm = new PayDayPageViewModel(db, notion: null, backups: backups);
        await vm.LoadAsync(new DateTime(2026, 5, 15));

        var row = vm.UnpaidBills.Single();
        await vm.MarkPaidCommand.ExecuteAsync(row);
        Assert.NotNull(vm.PendingAutoBackup);
        await vm.PendingAutoBackup!;

        Assert.Single(db.Payments);
        Assert.Equal(BackupStatus.Ok, vm.LastBackupStatus);
        Assert.Empty(vm.LastBackupError);
        Assert.Single(store.WriteHistory);
        var snapshot = store.Snapshot().Single();
        Assert.Contains("\"name\": \"Electric\"", snapshot.Value);
    }

    [Fact]
    public async Task MarkPaid_BackupFails_LocalRowStillPersists_StatusFailed()
    {
        var db = SeedDb();
        var backups = new BackupRotationService(db, new ThrowingBackupStore());
        var vm = new PayDayPageViewModel(db, notion: null, backups: backups);
        await vm.LoadAsync(new DateTime(2026, 5, 15));

        var row = vm.UnpaidBills.Single();
        await vm.MarkPaidCommand.ExecuteAsync(row);
        await vm.PendingAutoBackup!;

        Assert.Single(db.Payments);       // local insert was NOT rolled back
        Assert.Single(vm.PaidBills);
        Assert.Equal(BackupStatus.Failed, vm.LastBackupStatus);
        Assert.NotEmpty(vm.LastBackupError);
    }

    [Fact]
    public async Task MarkAllPaid_TriggersExactlyOneBackup()
    {
        var db = SeedDb();
        db.Bills.Add(new Bill { Id = "2", Name = "Phone", Type = "Bills", Cost = 100, DueDay = 15, Rate = "Monthly", Active = true });
        var store = new InMemoryBackupStore();
        var backups = new BackupRotationService(db, store);
        var vm = new PayDayPageViewModel(db, notion: null, backups: backups);
        await vm.LoadAsync(new DateTime(2026, 5, 15));

        await vm.MarkAllPaidCommand.ExecuteAsync(null);
        await vm.PendingAutoBackup!;

        Assert.Equal(2, db.Payments.Count);
        Assert.Single(store.WriteHistory);  // one rotation, not one per row
        Assert.Equal(BackupStatus.Ok, vm.LastBackupStatus);
    }

    // ------------------------------------------------------------------
    // InsightsPageViewModel — snapshots trigger backup
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveSnapshot_NoBackupService_StatusStaysNotConfigured()
    {
        var db = SeedDb();
        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        await vm.SaveSnapshotAsync(new DateTime(2026, 5, 15));
        if (vm.PendingAutoBackup is not null) await vm.PendingAutoBackup;

        Assert.Single(db.Snapshots);
        Assert.Equal(BackupStatus.NotConfigured, vm.LastBackupStatus);
    }

    [Fact]
    public async Task SaveSnapshot_BackupSucceeds_StatusOk()
    {
        var db = SeedDb();
        var store = new InMemoryBackupStore();
        var backups = new BackupRotationService(db, store);
        var vm = new InsightsPageViewModel(db, notion: null, backups: backups);
        await vm.LoadAsync();

        await vm.SaveSnapshotAsync(new DateTime(2026, 5, 15));
        await vm.PendingAutoBackup!;

        Assert.Single(db.Snapshots);
        Assert.Equal(BackupStatus.Ok, vm.LastBackupStatus);
        Assert.Single(store.WriteHistory);
    }

    [Fact]
    public async Task SaveSnapshot_BackupFails_LocalSnapshotPersists_StatusFailed()
    {
        var db = SeedDb();
        var backups = new BackupRotationService(db, new ThrowingBackupStore());
        var vm = new InsightsPageViewModel(db, notion: null, backups: backups);
        await vm.LoadAsync();

        await vm.SaveSnapshotAsync(new DateTime(2026, 5, 15));
        await vm.PendingAutoBackup!;

        Assert.Single(db.Snapshots);
        Assert.Equal(BackupStatus.Failed, vm.LastBackupStatus);
        Assert.NotEmpty(vm.LastBackupError);
    }

    // ------------------------------------------------------------------
    // Test fakes
    // ------------------------------------------------------------------

    private sealed class ThrowingBackupStore : IBackupStore
    {
        public Task WriteAsync(string fileName, string content, CancellationToken ct = default)
            => throw new InvalidOperationException("Disk on fire.");
        public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BackupEntry>>(Array.Empty<BackupEntry>());
        public Task<string> ReadAsync(string fileName, CancellationToken ct = default)
            => throw new InvalidOperationException("No reads here.");
        public Task DeleteAsync(string fileName, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
