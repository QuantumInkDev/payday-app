using System;
using System.Collections.Generic;
using System.Linq;
using PayDay.Sync.Clock;
using PayDay.Sync.Crypto;
using PayDay.Sync.Engine;
using PayDay.Sync.Files;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Tests.Sync;

/// <summary>
/// Two devices syncing through encrypted files in memory: local actions on <see cref="SyncReplica"/>,
/// sealing and uploading batches in order, opening them with <see cref="VaultCodec"/> on the other
/// device, cursors, snapshots and the clock rules.
/// </summary>
public class SyncReplicaTests
{
    private const string VaultId = "019a0000-0000-7000-8000-00000000fa01";
    private const long T0 = 1794657600000;

    private static readonly byte[] VaultKey = Convert.FromHexString("f37bab15f8502440f593435afe8aefac43ad24eb5f5a77c903c93c2e8ab8cdc5");

    private readonly ManualTimeProvider _timeA = new(T0);
    private readonly ManualTimeProvider _timeB = new(T0);
    private readonly VaultCodec _codec = new(VaultId, new Dictionary<uint, byte[]> { [1] = VaultKey }, 1);

    /// <summary>The drive: vault-relative path to file bytes.</summary>
    private readonly Dictionary<string, byte[]> _drive = new(StringComparer.Ordinal);

    /// <summary>The storage every device in these tests uses; the SQLite variants override it.</summary>
    protected virtual ISyncStorage NewStorage() => new InMemorySyncStorage();

    private SyncReplica NewDevice(ManualTimeProvider time) => SyncReplica.Start(NewStorage(), time, VaultId);

    private void Upload(SyncReplica device)
    {
        device.SealPending();
        while (device.NextBatchToUpload is { } batch)
        {
            var (path, data) = _codec.SealChangeFile(device.DeviceId, batch.Seq, batch.Records);
            _drive[path] = data;
            device.MarkUploaded(batch.Seq);
        }
    }

    private void Download(SyncReplica device)
    {
        // Change files of other devices, in sequence order (section 10.2).
        var changes = _drive.Keys
            .Select(SyncPaths.Parse)
            .Where(p => p.Kind == SyncPathKind.Changes && p.DeviceId != device.DeviceId)
            .OrderBy(p => p.DeviceId, StringComparer.Ordinal).ThenBy(p => p.Seq);
        foreach (var path in changes)
        {
            if (path.Seq <= device.Cursor(path.DeviceId!)) continue;
            device.MergeFile(_codec.Open(path.Path, _drive[path.Path]));
        }
    }

    private static string CreateBill(SyncReplica device, string name, double remaining)
    {
        var id = device.NewRowId();
        device.CreateRow(SyncEntities.Bill, id, new Dictionary<string, SyncValue>
        {
            ["name"] = SyncValue.String(name),
            ["type"] = SyncValue.String("Cards"),
            ["payment"] = SyncValue.Number(55),
            ["remainingCheckpoint"] = SyncValue.Number(remaining),
        });
        return id;
    }

    [Fact]
    public void Start_GivesANewDeviceIdEveryTime_AndCursorsStartAtZero()
    {
        var storage = NewStorage();
        var first = SyncReplica.Start(storage, _timeA, VaultId);
        first.Metadata.SetCursor("0b0b0b0b0b0b0b0b", 9);
        var second = SyncReplica.Start(storage, _timeA, VaultId);
        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.Equal(0, second.Cursor("0b0b0b0b0b0b0b0b"));
        Assert.Equal(VaultId, second.Metadata.VaultId);
        Assert.Throws<ArgumentException>(() => new SyncReplica(NewStorage(), _timeA));
    }

    [Fact]
    public void TwoDevices_ConvergeThroughEncryptedChangeFiles()
    {
        var a = NewDevice(_timeA);
        var b = NewDevice(_timeB);
        var bill = CreateBill(a, "Woodgrove Rewards", 1200);
        Upload(a);
        Download(b);
        Assert.True(b.State.IsLive(SyncEntities.Bill, bill));
        Assert.Equal(120000, b.State.RemainingCents(bill));
        Assert.Equal(1, b.Cursor(a.DeviceId));

        // Both pay the same bill for the same period while offline: one payment row (section 5.2).
        _timeA.Advance(1000);
        _timeB.Advance(2000);
        a.MarkPaid(PaymentKinds.Regular, bill, "2026-11-12", 55, "2026-11-14T12:00:01.000Z");
        b.MarkPaid(PaymentKinds.Regular, bill, "2026-11-12", 60, "2026-11-14T12:00:02.000Z");
        Upload(a);
        Upload(b);
        Download(a);
        Download(b);

        Assert.Equal(a.State.AllRecords(), b.State.AllRecords());
        Assert.Single(a.State.LivePaymentsFor(bill));
        Assert.Equal(120000 - 6000, a.State.RemainingCents(bill));
        Assert.Equal(2, a.Cursor(b.DeviceId) + b.Cursor(a.DeviceId) - 1);
    }

    [Fact]
    public void LocalActions_TakeOneTickEach_AndSaveTheClockWithTheRecords()
    {
        var a = NewDevice(_timeA);
        var bill = CreateBill(a, "Contoso Card", 900);
        var afterCreate = a.Metadata.ClockState!.Value;
        Assert.All(a.Metadata.PendingRecords, r => Assert.Equal(afterCreate, r.Hlc));

        // An action that changes nothing writes nothing and does not move the saved clock.
        Assert.Empty(a.SetRemaining(bill, 900, RemainingSource.App));
        Assert.Equal(afterCreate, a.Metadata.ClockState);

        var set = a.SetRemaining(bill, 880, RemainingSource.App);
        var single = Assert.Single(set);
        Assert.True(single.Hlc > afterCreate);
        Assert.Equal(single.Hlc, a.Metadata.ClockState);
        Assert.Equal(88000, a.State.RemainingCents(bill));

        Assert.Throws<InvalidOperationException>(() => a.Commit((_, h) => new[]
        {
            new ChangeRecord(SyncEntities.Bill, bill, "name", SyncValue.String("x"), Hlc.Encode(h.Wall, h.Counter + 1, a.DeviceId)),
        }));
    }

    [Fact]
    public void Batches_AreSealedImmutably_AndUploadedStrictlyInOrder()
    {
        var a = NewDevice(_timeA);
        CreateBill(a, "Woodgrove Rewards", 100);
        var first = Assert.Single(a.SealPending());
        CreateBill(a, "Contoso Card", 200);
        var second = Assert.Single(a.SealPending());
        Assert.Empty(a.SealPending());
        Assert.Equal((1, 2), (first.Seq, second.Seq));
        Assert.Equal(first, a.NextBatchToUpload);
        Assert.Throws<InvalidOperationException>(() => a.MarkUploaded(2));
        a.MarkUploaded(1);
        Assert.Equal(second, a.NextBatchToUpload);
        a.MarkUploaded(2);
        Assert.Null(a.NextBatchToUpload);
        Assert.Equal(2, a.Metadata.LastUploadedSeq);
        Assert.Equal(3, a.Metadata.NextSeq);
    }

    [Fact]
    public void MergeFile_ObservesHlcs_AndReportsClockSkew()
    {
        var a = NewDevice(_timeA);
        var b = NewDevice(_timeB);
        _timeB.Advance(11 * 60 * 1000); // B's clock runs 11 minutes ahead
        CreateBill(b, "Fabrikam Loan", 5000);
        Upload(b);

        var path = _drive.Keys.Single();
        var outcome = a.MergeFile(_codec.Open(path, _drive[path]));
        Assert.Equal(new[] { b.DeviceId }, outcome.SkewedDevices);
        Assert.True(outcome.Changed > 0);
        var observed = a.Metadata.ClockState!.Value;
        Assert.Equal(T0 + 11 * 60 * 1000, observed.Wall);
        Assert.Equal(a.DeviceId, observed.DeviceId);

        // A's next edit sorts after everything it has seen, whatever its own clock says.
        var bill = CreateBill(a, "Northwind Utility", 75);
        Assert.True(a.State.TryGet(SyncEntities.Bill, bill, "name", out var cell) && cell.Hlc > observed);
    }

    [Fact]
    public void Files_AboveAGap_AreRemembered_AndTheCursorPassesThemOnceTheGapIsFilled()
    {
        var a = NewDevice(_timeA);
        var b = NewDevice(_timeB);
        CreateBill(a, "Woodgrove Rewards", 100);
        Upload(a);
        var bill2 = CreateBill(a, "Contoso Card", 200);
        Upload(a);
        var second = SyncPaths.ChangeFilePath(a.DeviceId, 2);
        b.MergeFile(_codec.Open(second, _drive[second]));
        Assert.True(b.State.IsLive(SyncEntities.Bill, bill2));
        Assert.Equal(0, b.Cursor(a.DeviceId));
        var first = SyncPaths.ChangeFilePath(a.DeviceId, 1);
        b.MergeFile(_codec.Open(first, _drive[first]));
        Assert.Equal(2, b.Cursor(a.DeviceId));
        Assert.Empty(b.Metadata.MergedAhead(a.DeviceId));
    }

    [Fact]
    public void Snapshot_RoundTripsThroughTheDrive_AndRaisesCursors()
    {
        var a = NewDevice(_timeA);
        var b = NewDevice(_timeB);
        var bill = CreateBill(b, "Woodgrove Rewards", 1200);
        Upload(b);
        Download(a);
        a.MarkPaid(PaymentKinds.Regular, bill, "2026-11-12", 55, null);
        Upload(a);

        var snap = a.PrepareSnapshot();
        Assert.Equal(1, snap.Covers[a.DeviceId]);
        Assert.Equal(1, snap.Covers[b.DeviceId]);
        Assert.Equal(snap.Hlc, a.Metadata.ClockState);
        var (path, data) = _codec.SealSnapshot(snap.Hlc, snap.Covers, snap.Records);

        // A new device joins and merges only the snapshot (a compacted vault).
        var c = NewDevice(new ManualTimeProvider(T0 + 5000));
        c.MergeFile(_codec.Open(path, data));
        Assert.Equal(a.State.AllRecords(), c.State.AllRecords());
        Assert.Equal(1, c.Cursor(a.DeviceId));
        Assert.Equal(1, c.Cursor(b.DeviceId));
        Assert.Equal(120000 - 5500, c.State.RemainingCents(bill));
        Assert.True(c.Metadata.ClockState >= Hlc.Encode(snap.Hlc.Wall, 0, c.DeviceId));
    }

    [Fact]
    public void RecoverClock_RaisesTheStateToEveryOwnHlcInTheStore()
    {
        var storage = NewStorage();
        var a = SyncReplica.Start(storage, _timeA, VaultId);
        _timeA.Advance(60_000);
        var bill = CreateBill(a, "Woodgrove Rewards", 100);
        var written = a.Metadata.ClockState!.Value;

        // Simulate a crash that lost the clock state but kept the records.
        storage.Metadata.ClockState = null;
        _timeA.UnixMs = T0;
        var restarted = new SyncReplica(storage, _timeA);
        restarted.RecoverClock();
        Assert.Equal(written, restarted.Metadata.ClockState);
        var next = Assert.Single(restarted.SetRemaining(bill, 50, RemainingSource.App));
        Assert.True(next.Hlc > written);
    }

    [Fact]
    public void Restore_ThroughTheReplica_UsesOneHlcAndLeavesDevicesAlone()
    {
        var a = NewDevice(_timeA);
        a.CreateRow(SyncEntities.Device, a.DeviceId, new Dictionary<string, SyncValue>
        {
            ["name"] = SyncValue.String("Windows PC"),
            ["platform"] = SyncValue.String("windows"),
        });
        var old = CreateBill(a, "Old Card", 10);
        var restoredBill = "019a0000-0000-7000-8000-0000000000b1";
        var backup = new RestoreInput(
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SyncValue>>>
            {
                [SyncEntities.Bill] = new Dictionary<string, IReadOnlyDictionary<string, SyncValue>>
                {
                    [restoredBill] = new Dictionary<string, SyncValue>
                    {
                        ["name"] = SyncValue.String("Woodgrove Rewards"),
                        ["remainingCheckpoint"] = SyncValue.Number(1145),
                    },
                },
            },
            PayDay.Sync.Json.StrictJson.Parse("{\"PayAnchor\":\"2025-03-20\",\"Theme\":\"Dark\"}"));
        var records = a.Restore(backup);
        Assert.Single(records.Select(r => r.Hlc).Distinct());
        Assert.DoesNotContain(records, r => r.Entity == SyncEntities.Device);
        Assert.False(a.State.IsLive(SyncEntities.Bill, old));
        Assert.True(a.State.IsLive(SyncEntities.Bill, restoredBill));
        Assert.True(a.State.IsLive(SyncEntities.Device, a.DeviceId));
        Assert.Equal(SyncValue.String("2025-03-20"), a.State.Value(SyncEntities.Setting, SyncSettingKeys.PayAnchor, "value"));
        Assert.Equal(114500, a.State.RemainingCents(restoredBill));
    }
}
