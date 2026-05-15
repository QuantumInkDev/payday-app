using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.Tests;

public class BackupRotationServiceTests
{
    private static (BackupRotationService svc, FakeDatabaseService db, InMemoryBackupStore store) Build(DateTime? now = null)
    {
        var db = new FakeDatabaseService();
        var store = new InMemoryBackupStore();
        var svc = new BackupRotationService(db, store, () => now ?? new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc));
        return (svc, db, store);
    }

    // ------------------------------------------------------------------
    // FormatFileName
    // ------------------------------------------------------------------

    [Fact]
    public void FormatFileName_UsesPrefixTimestampExtension()
    {
        var name = BackupRotationService.FormatFileName(new DateTime(2026, 5, 15, 9, 3, 7, DateTimeKind.Utc));
        Assert.Equal("payday-backup-20260515-090307.json", name);
    }

    [Fact]
    public void FormatFileName_DifferentSeconds_ProduceDifferentNames()
    {
        var a = BackupRotationService.FormatFileName(new DateTime(2026, 5, 15, 9, 3, 7, DateTimeKind.Utc));
        var b = BackupRotationService.FormatFileName(new DateTime(2026, 5, 15, 9, 3, 8, DateTimeKind.Utc));
        Assert.NotEqual(a, b);
    }

    // ------------------------------------------------------------------
    // CreateAsync — happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_WritesTimestampedFile()
    {
        var when = new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
        var (svc, _, store) = Build(when);

        var fileName = await svc.CreateAsync();

        Assert.Equal("payday-backup-20260515-120000.json", fileName);
        Assert.Contains(fileName, store.Snapshot().Keys);
    }

    [Fact]
    public async Task CreateAsync_SnapshotsCurrentDbContents()
    {
        var (svc, db, store) = Build();
        db.Bills.Add(new Bill { Id = "b1", Name = "Amazon", Type = "Cards", Cost = 87 });
        db.Settings["Foo"] = "Bar";

        var fileName = await svc.CreateAsync();
        var json = store.Snapshot()[fileName];

        Assert.Contains("\"name\": \"Amazon\"", json);
        Assert.Contains("\"Foo\": \"Bar\"", json);
        Assert.Contains("\"formatVersion\": 1", json);
    }

    [Fact]
    public async Task CreateAsync_EmbedsExportedAtMatchingClock()
    {
        var when = new DateTime(2026, 5, 15, 4, 30, 0, DateTimeKind.Utc);
        var (svc, _, store) = Build(when);

        var fileName = await svc.CreateAsync();
        var json = store.Snapshot()[fileName];
        using var doc = JsonDocument.Parse(json);
        var exportedAt = doc.RootElement.GetProperty("exportedAt").GetString();

        Assert.NotNull(exportedAt);
        Assert.Contains("2026-05-15", exportedAt);
    }

    // ------------------------------------------------------------------
    // TrimAsync — rotation
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_UnderTen_KeepsAllBackups()
    {
        var store = new InMemoryBackupStore();
        var db = new FakeDatabaseService();
        for (var i = 0; i < 5; i++)
        {
            var stamp = new DateTime(2026, 5, 1, 0, i, 0, DateTimeKind.Utc);
            var svc = new BackupRotationService(db, store, () => stamp);
            store.NextWriteTimestamp = stamp;
            await svc.CreateAsync();
        }

        Assert.Equal(5, store.Snapshot().Count);
        Assert.Empty(store.DeleteHistory);
    }

    [Fact]
    public async Task CreateAsync_AtEleventh_TrimsOldest()
    {
        var store = new InMemoryBackupStore();
        var db = new FakeDatabaseService();

        // Seed 10 historical backups, one per day.
        for (var i = 0; i < 10; i++)
        {
            var stamp = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            store.Seed(BackupRotationService.FormatFileName(stamp), "{}", stamp);
        }
        Assert.Equal(10, store.Snapshot().Count);

        // 11th create should keep 10 and delete the oldest.
        var newest = new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
        var svc = new BackupRotationService(db, store, () => newest);
        store.NextWriteTimestamp = newest;
        var newName = await svc.CreateAsync();

        Assert.Equal(10, store.Snapshot().Count);
        Assert.Contains(newName, store.Snapshot().Keys);
        Assert.Single(store.DeleteHistory);
        var deleted = store.DeleteHistory[0];
        Assert.Equal(
            BackupRotationService.FormatFileName(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            deleted);
    }

    [Fact]
    public async Task TrimAsync_ManyExtra_DeletesAllPastMax()
    {
        var store = new InMemoryBackupStore();
        var db = new FakeDatabaseService();
        for (var i = 0; i < 15; i++)
        {
            var stamp = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            store.Seed(BackupRotationService.FormatFileName(stamp), "{}", stamp);
        }
        var svc = new BackupRotationService(db, store);

        var deleted = await svc.TrimAsync();

        Assert.Equal(5, deleted);
        Assert.Equal(10, store.Snapshot().Count);
        // Newest 10 (Apr-06 .. Apr-15) are the survivors.
        var survivors = store.Snapshot().Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(
            BackupRotationService.FormatFileName(new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc)),
            survivors.First());
    }

    [Fact]
    public async Task TrimAsync_AtOrBelowLimit_DeletesNothing()
    {
        var store = new InMemoryBackupStore();
        for (var i = 0; i < 10; i++)
        {
            var stamp = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            store.Seed(BackupRotationService.FormatFileName(stamp), "{}", stamp);
        }
        var svc = new BackupRotationService(new FakeDatabaseService(), store);

        var deleted = await svc.TrimAsync();

        Assert.Equal(0, deleted);
        Assert.Equal(10, store.Snapshot().Count);
        Assert.Empty(store.DeleteHistory);
    }

    // ------------------------------------------------------------------
    // ListAsync / LatestAsync — ordering + filtering
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_OrdersByLastWriteUtcDescending()
    {
        var store = new InMemoryBackupStore();
        var oldest = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var middle = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var newest = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Seed out of order — service should sort.
        store.Seed(BackupRotationService.FormatFileName(middle), "{}", middle);
        store.Seed(BackupRotationService.FormatFileName(oldest), "{}", oldest);
        store.Seed(BackupRotationService.FormatFileName(newest), "{}", newest);

        var svc = new BackupRotationService(new FakeDatabaseService(), store);
        var entries = await svc.ListAsync();

        Assert.Equal(3, entries.Count);
        Assert.Equal(BackupRotationService.FormatFileName(newest), entries[0].FileName);
        Assert.Equal(BackupRotationService.FormatFileName(oldest), entries[2].FileName);
    }

    [Fact]
    public async Task ListAsync_IgnoresFilesNotMatchingPattern()
    {
        var store = new InMemoryBackupStore();
        store.Seed("readme.txt", "hi", DateTime.UtcNow);
        store.Seed("export-2026.json", "{}", DateTime.UtcNow);
        var valid = BackupRotationService.FormatFileName(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        store.Seed(valid, "{}", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var svc = new BackupRotationService(new FakeDatabaseService(), store);
        var entries = await svc.ListAsync();

        Assert.Single(entries);
        Assert.Equal(valid, entries[0].FileName);
    }

    [Fact]
    public async Task TrimAsync_LeavesNonBackupFilesAlone()
    {
        var store = new InMemoryBackupStore();
        store.Seed("readme.txt", "hi", DateTime.UtcNow);
        for (var i = 0; i < 15; i++)
        {
            var stamp = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            store.Seed(BackupRotationService.FormatFileName(stamp), "{}", stamp);
        }
        var svc = new BackupRotationService(new FakeDatabaseService(), store);

        await svc.TrimAsync();

        Assert.Contains("readme.txt", store.Snapshot().Keys);
    }

    [Fact]
    public async Task LatestAsync_EmptyStore_ReturnsNull()
    {
        var svc = new BackupRotationService(new FakeDatabaseService(), new InMemoryBackupStore());
        Assert.Null(await svc.LatestAsync());
    }

    [Fact]
    public async Task LatestAsync_ReturnsNewest()
    {
        var store = new InMemoryBackupStore();
        var older = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        store.Seed(BackupRotationService.FormatFileName(older), "old", older);
        store.Seed(BackupRotationService.FormatFileName(newer), "new", newer);

        var svc = new BackupRotationService(new FakeDatabaseService(), store);
        var latest = await svc.LatestAsync();

        Assert.NotNull(latest);
        Assert.Equal(BackupRotationService.FormatFileName(newer), latest!.FileName);
    }

    // ------------------------------------------------------------------
    // ReadAsync — restore seam
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_ReturnsContent()
    {
        var (svc, db, store) = Build();
        db.Bills.Add(new Bill { Id = "b1", Name = "Test", Type = "Cards" });
        var name = await svc.CreateAsync();

        var json = await svc.ReadAsync(name);
        Assert.Contains("\"name\": \"Test\"", json);
    }

    // ------------------------------------------------------------------
    // Tail invariant — after many rotations, file count never exceeds 10
    // ------------------------------------------------------------------

    [Fact]
    public async Task ManyCreates_KeepsCountAtTen()
    {
        var store = new InMemoryBackupStore();
        var db = new FakeDatabaseService();

        for (var i = 0; i < 25; i++)
        {
            var stamp = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
            var svc = new BackupRotationService(db, store, () => stamp);
            store.NextWriteTimestamp = stamp;
            await svc.CreateAsync();
        }

        Assert.Equal(10, store.Snapshot().Count);
    }
}

