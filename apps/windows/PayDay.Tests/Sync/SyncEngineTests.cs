using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Sync;
using PayDay.Sync.Clock;
using PayDay.Sync.Crypto;
using PayDay.Sync.Engine;
using PayDay.Sync.Files;
using PayDay.Sync.Json;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Reading;

namespace PayDay.Tests.Sync;

/// <summary>
/// The engine contracts the SQLite storage, OneDrive provider and setup screens will build on:
/// payment tombstones only through the payment rules, transactions (atomic, not reentrant),
/// serialized replica access, materializer hooks, gap-safe cursors, clearing on join, re-stamping,
/// batch splitting, pairing acceptance and key handling.
/// </summary>
public class SyncEngineTests
{
    private const string VaultId = "019a0000-0000-7000-8000-00000000fa01";
    private const long T0 = 1747915200000;
    private static readonly byte[] VaultKey = Convert.FromHexString("f37bab15f8502440f593435afe8aefac43ad24eb5f5a77c903c93c2e8ab8cdc5");

    private readonly ManualTimeProvider _time = new(T0);

    /// <summary>The storage every device in these tests uses; the SQLite variant overrides it.</summary>
    protected virtual ISyncStorage NewStorage() => new InMemorySyncStorage();

    private SyncReplica NewDevice(ISyncMaterializer? materializer = null)
        => SyncReplica.Start(NewStorage(), _time, VaultId, materializer: materializer);

    private static VaultCodec NewCodec() => new(VaultId, new Dictionary<uint, byte[]> { [1] = VaultKey }, 1);

    private string CreateBill(SyncReplica device, double remaining)
    {
        _time.Advance(1000);
        var id = device.NewRowId();
        device.CreateRow(SyncEntities.Bill, id, new Dictionary<string, SyncValue>
        {
            ["name"] = SyncValue.String("Woodgrove Rewards"),
            ["remainingCheckpoint"] = SyncValue.Number(remaining),
        });
        return id;
    }

    // ---- B1: payments are tombstoned only through the section 5.3 rules ----------------------------

    [Fact]
    public void DeleteRow_RefusesPayments_SoUnmarkingAnAbsorbedPaymentStillRaisesTheCheckpoint()
    {
        var a = NewDevice();
        var bill = CreateBill(a, 1000);
        _time.Advance(1000);
        var mark = a.MarkPaid(PaymentKinds.Regular, bill, "2025-05-15", 100, null);
        var paymentId = mark[0].Id;
        Assert.Equal(90000, a.State.RemainingCents(bill));
        _time.Advance(1000);
        a.SetRemaining(bill, 880, RemainingSource.App);
        Assert.Equal(88000, a.State.RemainingCents(bill));

        var ex = Assert.Throws<ArgumentException>(() => a.DeleteRow(SyncEntities.Payment, paymentId));
        Assert.Contains("PaymentWriter.DeletePayment", ex.Message, StringComparison.Ordinal);
        Assert.True(a.State.IsLive(SyncEntities.Payment, paymentId), "the refused delete wrote nothing");
        Assert.Throws<ArgumentException>(() => RowWriter.Undelete(a.State, SyncEntities.Payment, paymentId, Hlc.Encode(T0 + 99_000, 0, a.DeviceId)));

        _time.Advance(1000);
        a.DeletePayment(paymentId);
        Assert.Equal(98000, a.State.RemainingCents(bill));
    }

    // ---- C2: transactions and threading ------------------------------------------------------------

    [Fact]
    public void Commit_RollsBackEverything_WhenTheActionThrows()
    {
        var a = NewDevice();
        var bill = CreateBill(a, 100);
        var clock = a.Metadata.ClockState;
        var pending = a.Metadata.PendingRecords.Count;
        var cells = a.State.Store.Count;

        Assert.Throws<InvalidOperationException>(() => a.Commit((s, h) =>
        {
            s.Merge(new ChangeRecord(SyncEntities.Bill, bill, "name", SyncValue.String("half-written"), h));
            throw new InvalidOperationException("simulated failure");
        }));

        Assert.Equal(clock, a.Metadata.ClockState);
        Assert.Equal(pending, a.Metadata.PendingRecords.Count);
        Assert.Equal(cells, a.State.Store.Count);
        Assert.Equal(SyncValue.String("Woodgrove Rewards"), a.State.Value(SyncEntities.Bill, bill, "name"));
    }

    [Fact]
    public void NestedTransactions_AreRejectedWithAClearException_AndLeaveNoTrace()
    {
        var a = NewDevice();
        var clock = a.Metadata.ClockState;
        var ex = Assert.Throws<InvalidOperationException>(() => a.Commit((s, h) =>
        {
            a.SetSetting(SyncSettingKeys.PayAnchor, SyncValue.String("2025-03-20"));
            return Array.Empty<ChangeRecord>();
        }));
        Assert.Contains("not reentrant", ex.Message, StringComparison.Ordinal);
        Assert.Equal(clock, a.Metadata.ClockState);
        Assert.Empty(a.Metadata.PendingRecords);
    }

    [Fact]
    public void ConcurrentCallers_AreSerialized_EachActionGettingItsOwnHlc()
    {
        var a = NewDevice();
        Parallel.For(0, 200, i => a.SetSetting(SyncSettingKeys.TypeColor("Type" + i), SyncValue.String("#123456")));
        var hlcs = a.Read((_, m) => m.PendingRecords.Select(r => r.Hlc).ToList());
        Assert.Equal(200, hlcs.Count);
        Assert.Equal(200, hlcs.Distinct().Count());
        Assert.Equal(hlcs.Max(), a.Metadata.ClockState);
    }

    // ---- C3: hooks ---------------------------------------------------------------------------------

    private sealed class RecordingMaterializer : ISyncMaterializer
    {
        public List<RowKey> Rows { get; } = new();

        public bool ThrowNext { get; set; }

        public void RowsChanged(SyncState state, IReadOnlyCollection<RowKey> rows)
        {
            Rows.AddRange(rows);
            if (ThrowNext) throw new InvalidOperationException("materialized table write failed");
        }
    }

    [Fact]
    public void Materializer_SeesChangedRows_InsideTheSameTransaction()
    {
        var materializer = new RecordingMaterializer();
        var a = NewDevice(materializer);
        var bill = CreateBill(a, 100);
        Assert.Equal(new[] { new RowKey(SyncEntities.Bill, bill) }, materializer.Rows);

        // A failing materialized write rolls the merge back too.
        materializer.ThrowNext = true;
        Assert.Throws<InvalidOperationException>(() => a.SetRemaining(bill, 50, RemainingSource.App));
        Assert.Equal(10000, a.State.RemainingCents(bill));
        materializer.ThrowNext = false;

        var b = NewDevice();
        var codec = NewCodec();
        var batch = Assert.Single(a.SealPending());
        var (path, data) = codec.SealChangeFile(a.DeviceId, batch.Seq, batch.Records);
        var outcome = b.MergeFile(codec.Open(path, data));
        Assert.Equal(new[] { new RowKey(SyncEntities.Bill, bill) }, outcome.ChangedRows);
        Assert.Empty(b.MergeFile(codec.Open(path, data)).ChangedRows);
    }

    [Fact]
    public void Start_CanClearSyncedData_ForTheJoinFlow()
    {
        var storage = NewStorage();
        var first = SyncReplica.Start(storage, _time, VaultId);
        CreateBill(first, 100);
        Assert.NotEqual(0, storage.Records.Count);

        var kept = SyncReplica.Start(storage, _time, VaultId);
        Assert.NotEqual(0, kept.State.Store.Count);
        var joined = SyncReplica.Start(storage, _time, VaultId, clearSyncedData: true);
        Assert.Equal(0, joined.State.Store.Count);
        Assert.Empty(joined.State.Entities());
        Assert.Throws<ArgumentException>(() => SyncReplica.Start(storage, _time, "not-a-uuid"));
    }

    [Fact]
    public void RecordStore_ValueIndex_FollowsChanges_AndRollback()
    {
        var store = new InMemoryRecordStore();
        var h1 = Hlc.Parse("0196f7dd5600-0000-0a0a0a0a0a0a0a0a");
        store.Set("payment", "p1", "billId", new SyncCell(SyncValue.String("b1"), h1));
        store.Set("payment", "p2", "billId", new SyncCell(SyncValue.String("b1"), h1));
        store.Set("payment", "p3", "amountPaid", new SyncCell(SyncValue.Number(3.0), h1));
        Assert.Equal(new[] { "p1", "p2" }, store.IdsWhere("payment", "billId", SyncValue.String("b1")).Order());
        Assert.Equal(new[] { "p3" }, store.IdsWhere("payment", "amountPaid", SyncValue.Number(3)));
        Assert.Empty(store.IdsWhere("payment", "amountPaid", SyncValue.True));

        store.BeginJournal();
        store.Set("payment", "p2", "billId", new SyncCell(SyncValue.String("b2"), h1));
        store.Clear();
        store.Set("payment", "p9", "billId", new SyncCell(SyncValue.String("b1"), h1));
        store.RollbackJournal();

        Assert.Equal(3, store.Count);
        Assert.Equal(new[] { "p1", "p2" }, store.IdsWhere("payment", "billId", SyncValue.String("b1")).Order());
        Assert.Empty(store.IdsWhere("payment", "billId", SyncValue.String("b2")));
    }

    [Fact]
    public void RestampLocalState_MakesEveryRecordThisDevicesOwn_ForANewVault()
    {
        var storage = NewStorage();
        var other = NewDevice();
        var bill = CreateBill(other, 1200);
        other.CreateRow(SyncEntities.Device, other.DeviceId, new Dictionary<string, SyncValue> { ["name"] = SyncValue.String("Old PC") });
        var me = SyncReplica.Start(storage, _time, VaultId);
        me.MergeRecords(other.State.AllRecords());

        // Create a new vault from that already-synced state.
        var fresh = SyncReplica.Start(storage, _time, "019a0000-0000-7000-8000-00000000fa02");
        var restamped = fresh.RestampLocalState();
        Assert.NotEmpty(restamped);
        Assert.All(restamped, r => Assert.Equal(fresh.DeviceId, r.Hlc.DeviceId));
        Assert.DoesNotContain(restamped, r => r.Entity == SyncEntities.Device);
        Assert.Equal(120000, fresh.State.RemainingCents(bill));

        using var codec = new VaultCodec("019a0000-0000-7000-8000-00000000fa02", new Dictionary<uint, byte[]> { [1] = VaultKey }, 1);
        var batch = Assert.Single(fresh.SealPending());
        var (path, _) = codec.SealChangeFile(fresh.DeviceId, batch.Seq, batch.Records);
        Assert.EndsWith("/000001.chg", path, StringComparison.Ordinal);
    }

    // ---- C4: batch splitting ---------------------------------------------------------------------

    [Fact]
    public void SealPending_SplitsAbove5000Records()
    {
        var a = NewDevice();
        var rows = Enumerable.Range(0, 12_000).Select(i => $"019a0000-0000-7000-8000-{i:x12}").ToList();
        a.Commit((_, h) => rows.Select(id => new ChangeRecord("futureEntity", id, "x", SyncValue.Number(1), h)).ToArray());
        var batches = a.SealPending();
        Assert.Equal(new[] { 1L, 2, 3 }, batches.Select(b => b.Seq));
        Assert.Equal(new[] { 5000, 5000, 2000 }, batches.Select(b => b.Records.Count));
        Assert.Equal(12_000, batches.SelectMany(b => b.Records).Select(r => r.Id).Distinct().Count());
        Assert.Equal(4, a.Metadata.NextSeq);
    }

    // ---- C1: order-safe reading ----------------------------------------------------------------------

    [Fact]
    public void ReadPlan_Steps_AndAnyOrderOfMerging_ReachTheSameCursors()
    {
        var writer = NewDevice();
        var codec = NewCodec();
        var files = new Dictionary<long, (string Path, byte[] Data)>();
        for (var i = 0; i < 4; i++)
        {
            CreateBill(writer, 100 + i);
            var batch = Assert.Single(writer.SealPending());
            files[batch.Seq] = codec.SealChangeFile(writer.DeviceId, batch.Seq, batch.Records);
            writer.MarkUploaded(batch.Seq);
        }
        var snap = writer.PrepareSnapshot();
        var covers = new Dictionary<string, long> { [writer.DeviceId] = 2 };
        var (snapPath, snapData) = codec.SealSnapshot(snap.Hlc, covers, snap.Records);

        // Files 1 and 2 were compacted away; 3 and 4 are listed.
        var plan = ReadPlanner.Plan(
            new Dictionary<string, long>(),
            new Dictionary<string, IReadOnlyCollection<long>> { [writer.DeviceId] = new long[] { 3, 4 } },
            new[] { new SnapshotInfo(snap.Hlc.Text, covers) });
        Assert.Equal(ReadStepKind.Snapshot, plan.Steps[0].Kind);
        Assert.Equal(new long[] { 3, 4 }, plan.Steps.Skip(1).Select(s => s.Seq));

        EnvelopeResult OpenStep(ReadStep s) => s.Kind == ReadStepKind.Snapshot
            ? codec.Open(snapPath, snapData)
            : codec.Open(files[s.Seq].Path, files[s.Seq].Data);

        var inOrder = NewDevice();
        foreach (var step in plan.Steps) inOrder.MergeFile(OpenStep(step));
        Assert.Equal(plan.CursorsAfter[writer.DeviceId], inOrder.Cursor(writer.DeviceId));

        var reversed = NewDevice();
        foreach (var step in plan.Steps.Reverse()) reversed.MergeFile(OpenStep(step));
        Assert.Equal(4, reversed.Cursor(writer.DeviceId));
        Assert.Empty(reversed.Metadata.MergedAhead(writer.DeviceId));
        Assert.Equal(inOrder.State.AllRecords(), reversed.State.AllRecords());
    }

    // ---- C5 and C6: pairing acceptance and key handling ---------------------------------------------

    [Fact]
    public void AcceptPairing_ChecksExpiryAndTheKeyCheckValue()
    {
        using var codec = NewCodec();
        var vault = VaultFile.Check(VaultFile.Create(VaultId, "2025-05-22T12:00:00.000Z", "0a0a0a0a0a0a0a0a", VaultKey));
        var code = PairingQr.NewCode();
        var (_, data) = codec.SealPairing(code, "0a0a0a0a0a0a0a0a", "2025-05-22T12:10:00.000Z");
        var scanned = PairingQr.Parse(PairingQr.Format(code));

        var ok = VaultCodec.AcceptPairing(vault, scanned, data, DateTimeOffset.Parse("2025-05-22T12:10:00.000Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PairingStatus.Accepted, ok.Status);
        Assert.Equal(VaultKey, ok.VaultKey);
        Assert.Equal(1, ok.Generation);
        Assert.Equal("0a0a0a0a0a0a0a0a", ok.SenderDeviceId);

        var late = VaultCodec.AcceptPairing(vault, scanned, data, DateTimeOffset.Parse("2025-05-22T12:10:00.001Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PairingStatus.Expired, late.Status);
        Assert.Null(late.VaultKey);

        // A pairing file whose key does not match this vault.json (for example a recreated vault).
        using var stranger = new VaultCodec(VaultId, new Dictionary<uint, byte[]> { [1] = new byte[32] }, 1);
        var (_, wrong) = stranger.SealPairing(code, "0a0a0a0a0a0a0a0a", "2025-05-22T12:10:00.000Z");
        var mismatch = VaultCodec.AcceptPairing(vault, scanned, wrong, DateTimeOffset.Parse("2025-05-22T12:05:00.000Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PairingStatus.KeyCheckMismatch, mismatch.Status);
        Assert.Null(mismatch.VaultKey);

        var otherVault = VaultFile.Check(VaultFile.Create("019a0000-0000-7000-8000-00000000fa02", "2025-05-22T12:00:00.000Z", "0a0a0a0a0a0a0a0a", VaultKey));
        Assert.Equal(SyncErrorCodes.VaultMismatch, Assert.Throws<SyncException>(() => VaultCodec.AcceptPairing(otherVault, scanned, data, DateTimeOffset.UnixEpoch)).Code);
        var otherCode = PairingQr.NewCode();
        Assert.Equal(SyncErrorCodes.AuthFailed, Assert.Throws<SyncException>(() =>
            VaultCodec.AcceptPairing(vault, new PairingCode(code.PairingId, otherCode.PairingSecret), data, DateTimeOffset.UnixEpoch)).Code);
    }

    [Fact]
    public void VaultCodec_CopiesKeysIn_NeverHandsThemOut_AndZeroesThemOnDispose()
    {
        var keys = new Dictionary<uint, byte[]> { [1] = VaultKey.ToArray() };
        var codec = new VaultCodec(VaultId, keys, 1);
        Array.Clear(keys[1]);
        var record = new ChangeRecord(SyncEntities.Bill, "019a0000-0000-7000-8000-0000000000b1", "name", SyncValue.String("x"), Hlc.Parse("0196f7dd5600-0000-0a0a0a0a0a0a0a0a"));
        var (path, data) = codec.SealChangeFile("0a0a0a0a0a0a0a0a", 1, new[] { record });
        using (var check = NewCodec())
        {
            Assert.Equal(record, Assert.Single(check.Open(path, data).Records));
        }
        Assert.Equal(new uint[] { 1 }, codec.Generations);
        Assert.True(codec.HasKey(1));
        codec.Dispose();
        Assert.Throws<ObjectDisposedException>(() => codec.Open(path, data));
        Assert.Throws<SyncException>(() => new VaultCodec(VaultId, new Dictionary<uint, byte[]> { [1] = new byte[16] }, 1));
    }

    [Fact]
    public void SyncKeys_InfoStringsAreReadOnly()
    {
        Assert.Equal("futurecheck/v1/file-key", System.Text.Encoding.ASCII.GetString(SyncKeys.FileKeyInfo));
        Assert.Equal("futurecheck/v1/key-check", System.Text.Encoding.ASCII.GetString(SyncKeys.KeyCheckInfo));
        Assert.Equal("futurecheck/v1/pairing-key", System.Text.Encoding.ASCII.GetString(SyncKeys.PairingKeyInfo));
    }
}
