using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using PayDay.Sync.Clock;
using PayDay.Sync.Engine;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Storage;
using PayDay.Tests.Conformance;
using PayDay.Tests.Sync;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// The SQLite record store and sync metadata: exact value round trips, the value index, transactions
/// (rollback of records and metadata, no reentrancy), persistence across reopening the file, and that
/// opening the storage creates nothing.
/// </summary>
public sealed class SqliteSyncStorageTests : IDisposable
{
    private static readonly Hlc H1 = Hlc.Parse("01a1d9edc200-0000-0a0a0a0a0a0a0a0a");
    private static readonly Hlc H2 = Hlc.Parse("01a1d9edc200-0001-0a0a0a0a0a0a0a0a");

    private readonly SqliteTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    public static IEnumerable<object[]> Kinds() => new[] { new object[] { "file" }, new object[] { "memory" } };

    private SqliteSyncStorage New(string kind) => kind == "file" ? _files.NewFileStorage() : _files.NewMemoryStorage();

    private static readonly SyncValue[] SampleValues =
    {
        SyncValue.Null,
        SyncValue.True,
        SyncValue.False,
        SyncValue.Number(0),
        SyncValue.Number(-0.0),
        SyncValue.Number(0.1 + 0.2),
        SyncValue.Number(-2.675),
        SyncValue.Number(double.Epsilon),
        SyncValue.Number(9007199254740993.0),
        SyncValue.Number(1e300),
        SyncValue.String(string.Empty),
        SyncValue.String("Woodgrove Rewards"),
        SyncValue.String("d3.5"),
        SyncValue.String("z"),
        SyncValue.String("Northwind – Platinum, 日本, é́, 🙂"),
    };

    [Fact]
    public void Values_RoundTripExactly_ThroughTheCodecAndTheTable()
    {
        using var storage = _files.NewFileStorage();
        for (var i = 0; i < SampleValues.Length; i++)
        {
            var value = SampleValues[i];
            var decoded = SqliteValueCodec.Decode(SqliteValueCodec.Encode(value));
            storage.Records.Set("x", "row" + i, "v", new SyncCell(value, H1));
            Assert.True(storage.Records.TryGet("x", "row" + i, "v", out var stored));
            foreach (var got in new[] { decoded, stored.Value })
            {
                Assert.Equal(value.Kind, got.Kind);
                Assert.Equal(value, got);
                if (value.IsNumber)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(value.NumberValue), BitConverter.DoubleToInt64Bits(got.NumberValue));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void RecordStore_SetsGetsAndEnumerates(string kind)
    {
        using var storage = New(kind);
        var store = storage.Records;
        Assert.Equal(0, store.Count);
        Assert.False(store.TryGet("bill", "b1", "name", out _));

        store.Set("bill", "b1", "name", new SyncCell(SyncValue.String("Woodgrove Rewards"), H1));
        store.Set("bill", "b1", "deleted", new SyncCell(SyncValue.False, H1));
        store.Set("bill", "b2", "name", new SyncCell(SyncValue.String("Contoso Card"), H1));
        store.Set("futureEntity", "x", "y", new SyncCell(SyncValue.Number(1), H1));
        store.Set("bill", "b1", "name", new SyncCell(SyncValue.String("Woodgrove Travel"), H2));

        Assert.Equal(4, store.Count);
        Assert.True(store.TryGet("bill", "b1", "name", out var cell));
        Assert.Equal(new SyncCell(SyncValue.String("Woodgrove Travel"), H2), cell);
        Assert.Equal(new[] { "bill", "futureEntity" }, store.Entities().Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "b1", "b2" }, store.Ids("bill").Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "deleted", "name" }, store.Fields("bill", "b1").Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Empty(store.Ids("payment"));
        Assert.Empty(store.Fields("bill", "nope"));

        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Empty(store.Entities());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void IdsWhere_MatchesValuesLikeSyncValueEquality(string kind)
    {
        using var storage = New(kind);
        var store = storage.Records;
        store.Set("payment", "p1", "billId", new SyncCell(SyncValue.String("b1"), H1));
        store.Set("payment", "p2", "billId", new SyncCell(SyncValue.String("b1"), H1));
        store.Set("payment", "p3", "billId", new SyncCell(SyncValue.String("B1"), H1));
        store.Set("payment", "p4", "amountPaid", new SyncCell(SyncValue.Number(3.0), H1));
        store.Set("payment", "p5", "amountPaid", new SyncCell(SyncValue.Number(-0.0), H1));
        store.Set("payment", "p6", "amountPaid", new SyncCell(SyncValue.Number(0), H1));
        store.Set("payment", "p7", "amountPaid", new SyncCell(SyncValue.True, H1));
        store.Set("payment", "p8", "amountPaid", new SyncCell(SyncValue.String("3"), H1));

        Assert.Equal(new[] { "p1", "p2" }, store.IdsWhere("payment", "billId", SyncValue.String("b1")).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "p4" }, store.IdsWhere("payment", "amountPaid", SyncValue.Number(3)));
        Assert.Equal(new[] { "p5", "p6" }, store.IdsWhere("payment", "amountPaid", SyncValue.Number(0)).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "p5", "p6" }, store.IdsWhere("payment", "amountPaid", SyncValue.Number(-0.0)).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "p7" }, store.IdsWhere("payment", "amountPaid", SyncValue.True));
        Assert.Empty(store.IdsWhere("payment", "amountPaid", SyncValue.Number(1)));

        // The index follows changes.
        store.Set("payment", "p2", "billId", new SyncCell(SyncValue.String("b2"), H2));
        Assert.Equal(new[] { "p1" }, store.IdsWhere("payment", "billId", SyncValue.String("b1")));
        Assert.Equal(new[] { "p2" }, store.IdsWhere("payment", "billId", SyncValue.String("b2")));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Transaction_RollsBackRecordsAndMetadata_WhenTheActionThrows(string kind)
    {
        using var storage = New(kind);
        storage.RunInTransaction(() =>
        {
            storage.Metadata.DeviceId = "0a0a0a0a0a0a0a0a";
            storage.Records.Set("bill", "b1", "name", new SyncCell(SyncValue.String("Woodgrove Rewards"), H1));
            storage.Metadata.EnqueuePending(new[] { new ChangeRecord("bill", "b1", "name", SyncValue.String("Woodgrove Rewards"), H1) });
            storage.Metadata.ClockState = H1;
        });

        var boom = new InvalidOperationException("boom");
        var thrown = Assert.Throws<InvalidOperationException>(() => storage.RunInTransaction(() =>
        {
            storage.Records.Set("bill", "b1", "name", new SyncCell(SyncValue.String("Changed"), H2));
            storage.Records.Set("bill", "b9", "name", new SyncCell(SyncValue.String("New"), H2));
            storage.Metadata.ClockState = H2;
            storage.Metadata.NextSeq = 7;
            storage.Metadata.SetCursor("0b0b0b0b0b0b0b0b", 3);
            storage.Metadata.ClearPending();
            storage.Records.Clear();
            throw boom;
        }));
        Assert.Same(boom, thrown);

        Assert.Equal(1, storage.Records.Count);
        Assert.True(storage.Records.TryGet("bill", "b1", "name", out var cell));
        Assert.Equal(SyncValue.String("Woodgrove Rewards"), cell.Value);
        Assert.Equal(H1, storage.Metadata.ClockState);
        Assert.Equal(1, storage.Metadata.NextSeq);
        Assert.Empty(storage.Metadata.Cursors);
        Assert.Single(storage.Metadata.PendingRecords);
        Assert.False(storage.InTransaction);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Transaction_IsNotReentrant(string kind)
    {
        using var storage = New(kind);
        var inner = Assert.Throws<InvalidOperationException>(() => storage.RunInTransaction(() => storage.RunInTransaction(() => { })));
        Assert.Contains("not reentrant", inner.Message, StringComparison.Ordinal);
        storage.RunInTransaction(() => storage.Metadata.NextSeq = 2);
        Assert.Equal(2, storage.Metadata.NextSeq);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Metadata_RoundTripsEveryPart_AndResetForgetsThem(string kind)
    {
        using var storage = New(kind);
        var m = storage.Metadata;
        Assert.Null(m.DeviceId);
        Assert.Null(m.VaultId);
        Assert.Null(m.ClockState);
        Assert.Equal(1, m.NextSeq);
        Assert.Equal(0, m.LastUploadedSeq);

        var r1 = new ChangeRecord("bill", "b1", "name", SyncValue.String("Woodgrove Rewards"), H1);
        var r2 = new ChangeRecord("bill", "b1", "payment", SyncValue.Number(55), H1);
        var r3 = new ChangeRecord("bill", "b1", "notes", SyncValue.Null, H2);
        storage.RunInTransaction(() =>
        {
            m.DeviceId = "0a0a0a0a0a0a0a0a";
            m.VaultId = "019a0000-0000-7000-8000-00000000fa01";
            m.ClockState = H2;
            m.NextSeq = 3;
            m.EnqueuePending(new[] { r2, r1 });
            m.AddSealedBatch(new SealedBatch(1, new[] { r1, r2 }));
            m.AddSealedBatch(new SealedBatch(2, new[] { r3 }));
            m.MarkBatchUploaded(1);
            m.SetCursor("0b0b0b0b0b0b0b0b", 4);
            m.SetCursor("0b0b0b0b0b0b0b0b", 5);
            m.SetMergedAhead("0b0b0b0b0b0b0b0b", new long[] { 9, 7, 7 });
            m.SetSnapshotFirstListed("snapshots/x.snap", 1794657600000);
        });

        Assert.Equal("0a0a0a0a0a0a0a0a", m.DeviceId);
        Assert.Equal("019a0000-0000-7000-8000-00000000fa01", m.VaultId);
        Assert.Equal(H2, m.ClockState);
        Assert.Equal(3, m.NextSeq);
        Assert.Equal(new[] { r2, r1 }, m.PendingRecords);
        Assert.Equal(new[] { new SealedBatch(1, new[] { r1, r2 }, true), new SealedBatch(2, new[] { r3 }) }, m.SealedBatches);
        Assert.Equal(1, m.LastUploadedSeq);
        Assert.Equal(5, m.Cursors["0b0b0b0b0b0b0b0b"]);
        Assert.Equal(new long[] { 7, 9 }, m.MergedAhead("0b0b0b0b0b0b0b0b"));
        Assert.Empty(m.MergedAhead("0c0c0c0c0c0c0c0c"));
        Assert.Equal(1794657600000, m.SnapshotFirstListed("snapshots/x.snap"));
        Assert.Null(m.SnapshotFirstListed("snapshots/y.snap"));
        Assert.Throws<InvalidOperationException>(() => storage.RunInTransaction(() => m.MarkBatchUploaded(99)));
        Assert.Throws<SqliteException>(() => storage.RunInTransaction(() => m.AddSealedBatch(new SealedBatch(2, new[] { r1 }))));

        storage.RunInTransaction(() =>
        {
            m.SetMergedAhead("0b0b0b0b0b0b0b0b", Array.Empty<long>());
            m.ClearPending();
        });
        Assert.Empty(m.MergedAhead("0b0b0b0b0b0b0b0b"));
        Assert.Empty(m.PendingRecords);

        storage.RunInTransaction(m.Reset);
        Assert.Null(m.DeviceId);
        Assert.Null(m.ClockState);
        Assert.Equal(1, m.NextSeq);
        Assert.Empty(m.SealedBatches);
        Assert.Empty(m.Cursors);
        Assert.Null(m.SnapshotFirstListed("snapshots/x.snap"));
    }

    [Fact]
    public void FileStorage_KeepsEverythingAcrossReopening()
    {
        var path = _files.NewPath();
        var cs = SqliteTestFiles.ConnectionString(path);
        var time = new ManualTimeProvider(1794657600000);
        string deviceId;
        string bill;
        using (var storage = new SqliteSyncStorage(cs))
        {
            storage.CreateSchema();
            var replica = SyncReplica.Start(storage, time, "019a0000-0000-7000-8000-00000000fa01");
            deviceId = replica.DeviceId;
            bill = replica.NewRowId();
            replica.CreateRow(SyncEntities.Bill, bill, new Dictionary<string, SyncValue> { ["name"] = SyncValue.String("Woodgrove Rewards"), ["remainingCheckpoint"] = SyncValue.Number(1200) });
            time.Advance(1000);
            replica.MarkPaid(PaymentKinds.Regular, bill, "2026-11-12", 55, null);
            replica.SealPending();
        }

        using (var reopened = new SqliteSyncStorage(cs))
        {
            var replica = new SyncReplica(reopened, time);
            Assert.Equal(deviceId, replica.DeviceId);
            Assert.Equal(114500, replica.State.RemainingCents(bill));
            Assert.Single(replica.Metadata.SealedBatches);
            Assert.Equal(2, replica.Metadata.NextSeq);
            Assert.NotNull(replica.Metadata.ClockState);
        }
    }

    [Fact]
    public void OpeningStorage_CreatesNoTable()
    {
        var path = _files.NewPath();
        using (var conn = new SqliteConnection(SqliteTestFiles.ConnectionString(path)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE Bills (Id TEXT PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }
        using (var storage = new SqliteSyncStorage(SqliteTestFiles.ConnectionString(path, SqliteOpenMode.ReadWrite)))
        {
            Assert.False(storage.SchemaExists());
            Assert.False(storage.IsSyncEnabled());
        }
        using var check = new SqliteConnection(SqliteTestFiles.ConnectionString(path));
        check.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT group_concat(name) FROM sqlite_master WHERE type = 'table';";
        Assert.Equal("Bills", count.ExecuteScalar());
    }

    // ---- merge.json on the SQLite record store -------------------------------------------------------

    public static IEnumerable<object[]> MergeCases() => ConformanceFixtures.SyncCaseNames("merge.json");

    [Theory]
    [MemberData(nameof(MergeCases))]
    public void MergeConformance_OnTheSqliteRecordStore(string caseName)
    {
        var testCase = ConformanceFixtures.FindSyncCase("merge.json", caseName);
        SyncMergeHarness.Run(
            caseName,
            testCase["input"]!.AsObject(),
            testCase["expected"]!.AsObject(),
            () => new SyncState(_files.NewMemoryStorage().Records),
            allOrders: false);
    }
}
