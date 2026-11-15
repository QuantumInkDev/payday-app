using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.Tests;

public class BackupRestorePromptTests
{
    private static (BackupRestorePrompt prompt, FakeDatabaseService db, InMemoryBackupStore store) Build()
    {
        var db = new FakeDatabaseService();
        var store = new InMemoryBackupStore();
        var backups = new BackupRotationService(db, store);
        return (new BackupRestorePrompt(db, backups), db, store);
    }

    // ------------------------------------------------------------------
    // GetCandidateAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetCandidate_EmptyDbAndNoBackups_ReturnsNull()
    {
        var (prompt, _, _) = Build();
        Assert.Null(await prompt.GetCandidateAsync());
    }

    [Fact]
    public async Task GetCandidate_NonEmptyDb_ReturnsNullEvenIfBackupsExist()
    {
        var (prompt, db, store) = Build();
        db.Bills.Add(new Bill { Id = "1", Name = "Anything", Type = "Bills" });
        var stamp = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        store.Seed(BackupRotationService.FormatFileName(stamp), "{}", stamp);

        Assert.Null(await prompt.GetCandidateAsync());
    }

    [Fact]
    public async Task GetCandidate_EmptyDbAndBackups_ReturnsNewestEntry()
    {
        var (prompt, _, store) = Build();
        var older = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc);
        store.Seed(BackupRotationService.FormatFileName(older), "{}", older);
        store.Seed(BackupRotationService.FormatFileName(newer), "{}", newer);

        var entry = await prompt.GetCandidateAsync();

        Assert.NotNull(entry);
        Assert.Equal(BackupRotationService.FormatFileName(newer), entry!.FileName);
    }

    // ------------------------------------------------------------------
    // ApplyAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task ApplyAsync_RestoresBillsPaymentsSnapshotsSettings()
    {
        var (prompt, db, store) = Build();
        var json = BackupSerializer.ToJson(
            new[] { new Bill { Id = "b1", Name = "Amazon", Type = "Cards", Payment =50 } },
            new[] { new Payment { Id = 1, BillId = "b1", PeriodKey = "2026-05-15", AmountPaid = 50 } },
            new[] { new Snapshot { Id = 1, SnapshotDate = "2026-05-15", TotalRemaining =1000 } },
            new Dictionary<string, string?> { ["PayAnchor"] = "2026-05-15" });
        var stamp = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var name = BackupRotationService.FormatFileName(stamp);
        store.Seed(name, json, stamp);

        await prompt.ApplyAsync(new BackupEntry(name, stamp));

        Assert.Single(db.Bills);
        Assert.Equal("Amazon", db.Bills[0].Name);
        Assert.Single(db.Payments);
        Assert.Single(db.Snapshots);
        Assert.Equal("2026-05-15", db.Settings["PayAnchor"]);
    }

    [Fact]
    public async Task ApplyAsync_RoundTripsThroughRotationServiceCreate()
    {
        // Create a backup via BackupRotationService, then nuke the DB, then restore.
        var db = new FakeDatabaseService();
        var store = new InMemoryBackupStore();
        var backups = new BackupRotationService(db, store);
        db.Bills.Add(new Bill { Id = "b1", Name = "Electric", Type = "Bills", Payment =80 });
        db.Settings["PayAnchor"] = "2026-05-15";
        var fileName = await backups.CreateAsync();

        db.Bills.Clear();
        db.Settings.Clear();
        Assert.Empty(db.Bills);

        var prompt = new BackupRestorePrompt(db, backups);
        var candidate = await prompt.GetCandidateAsync();
        Assert.NotNull(candidate);
        Assert.Equal(fileName, candidate!.FileName);

        await prompt.ApplyAsync(candidate);

        Assert.Single(db.Bills);
        Assert.Equal("Electric", db.Bills[0].Name);
        Assert.Equal("2026-05-15", db.Settings["PayAnchor"]);
    }

    [Fact]
    public async Task ApplyAsync_BadJson_ThrowsBeforeMutatingDb()
    {
        var (prompt, db, store) = Build();
        db.Bills.Add(new Bill { Id = "keep", Name = "Existing", Type = "Bills" });
        var stamp = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var name = BackupRotationService.FormatFileName(stamp);
        store.Seed(name, "{\"formatVersion\": 999}", stamp);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => prompt.ApplyAsync(new BackupEntry(name, stamp)));

        Assert.Single(db.Bills);
        Assert.Equal("Existing", db.Bills[0].Name);
    }
}
