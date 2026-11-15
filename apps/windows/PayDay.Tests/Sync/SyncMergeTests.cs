using System;
using System.Collections.Generic;
using System.Linq;
using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Reading;

namespace PayDay.Tests.Sync;

/// <summary>Unit tests for the merged state, the generic writers, backup settings and compaction decisions.</summary>
public class SyncMergeTests
{
    private const string DeviceA = "0a0a0a0a0a0a0a0a";
    private const string Bill = "019a0000-0000-7000-8000-0000000000b1";
    private static readonly Hlc H1 = Hlc.Parse("01a1d9edc200-0000-0a0a0a0a0a0a0a0a");
    private static readonly Hlc H2 = Hlc.Parse("01a1d9edc5e8-0000-0a0a0a0a0a0a0a0a");
    private static readonly Hlc H3 = Hlc.Parse("01a1d9edc9d0-0000-0a0a0a0a0a0a0a0a");

    private static SyncState BillState()
    {
        var s = new SyncState();
        s.MergeAll(RowWriter.Create(SyncEntities.Bill, Bill, new Dictionary<string, SyncValue>
        {
            ["name"] = SyncValue.String("Woodgrove Rewards"),
            ["remainingCheckpoint"] = SyncValue.Number(1200.004),
            ["payment"] = SyncValue.Number(55),
        }, H1));
        return s;
    }

    [Fact]
    public void Create_WritesEveryKnownFieldWithDefaults_AndRoundsMoney()
    {
        var records = RowWriter.Create(SyncEntities.Bill, Bill, new Dictionary<string, SyncValue>
        {
            ["name"] = SyncValue.String("Woodgrove Rewards"),
            ["remainingCheckpoint"] = SyncValue.Number(1200.004),
            ["futureField"] = SyncValue.String("kept"),
        }, H1);
        Assert.Equal(SyncSchema.FieldsOf(SyncEntities.Bill).Count + 1, records.Count);
        Assert.All(records, r => Assert.Equal(H1, r.Hlc));
        Assert.Equal(records.OrderBy(r => r.Field, StringComparer.Ordinal).Select(r => r.Field), records.Select(r => r.Field));
        Assert.Equal(SyncValue.Number(1200), records.Single(r => r.Field == "remainingCheckpoint").Value);
        Assert.Equal(SyncValue.False, records.Single(r => r.Field == "deleted").Value);
        Assert.Equal(SyncValue.True, records.Single(r => r.Field == "active").Value);
        Assert.Equal(SyncValue.Number(1), records.Single(r => r.Field == "dueDay").Value);
        Assert.Equal(SyncValue.String("kept"), records.Single(r => r.Field == "futureField").Value);
        Assert.Throws<ArgumentException>(() => RowWriter.Create(SyncEntities.Bill, Bill, new Dictionary<string, SyncValue> { ["deleted"] = SyncValue.True }, H1));
    }

    [Fact]
    public void Edit_WritesOnlyChangedFields_ComparingMoneyInCents()
    {
        var s = BillState();
        var records = RowWriter.Edit(s, SyncEntities.Bill, Bill, new Dictionary<string, SyncValue>
        {
            ["name"] = SyncValue.String("Woodgrove Rewards"),
            ["payment"] = SyncValue.Number(55.001),
            ["notes"] = SyncValue.String("Statement on the 3rd"),
            ["apr"] = SyncValue.Number(24.99),
        }, H2);
        Assert.Equal(new[] { "apr", "notes" }, records.Select(r => r.Field));
        Assert.Throws<ArgumentException>(() => RowWriter.Edit(s, SyncEntities.Bill, Bill, new Dictionary<string, SyncValue> { ["remainingCheckpoint"] = SyncValue.Number(1) }, H2));
    }

    [Fact]
    public void DeleteAndUndelete_WriteOneTombstoneRecord_OnlyWhenItChanges()
    {
        var s = BillState();
        Assert.Empty(RowWriter.Undelete(s, SyncEntities.Bill, Bill, H2));
        var del = RowWriter.Delete(s, SyncEntities.Bill, Bill, H2);
        Assert.Equal(SyncValue.True, Assert.Single(del).Value);
        s.MergeAll(del);
        Assert.False(s.IsLive(SyncEntities.Bill, Bill));
        Assert.Empty(RowWriter.Delete(s, SyncEntities.Bill, Bill, H3));
        Assert.Single(RowWriter.Undelete(s, SyncEntities.Bill, Bill, H3));
    }

    [Fact]
    public void SetSetting_RoundsTheBufferAndSkipsUnchangedValues()
    {
        var s = new SyncState();
        var set = RowWriter.SetSetting(s, SyncSettingKeys.SafeToSpendBuffer, SyncValue.Number(75.505), H1);
        Assert.Equal(SyncValue.Number(75.51), Assert.Single(set).Value);
        s.MergeAll(set);
        Assert.Empty(RowWriter.SetSetting(s, SyncSettingKeys.SafeToSpendBuffer, SyncValue.Number(75.51), H2));
        var reset = RowWriter.SetSetting(s, SyncSettingKeys.SafeToSpendBuffer, SyncValue.Null, H2);
        s.MergeAll(reset);
        Assert.True(s.IsLive(SyncEntities.Setting, SyncSettingKeys.SafeToSpendBuffer), "a null value still makes the setting row live");
    }

    [Fact]
    public void Read_FallsBackToTheDefaultForMissingOrWrongTypeValues()
    {
        var s = BillState();
        s.Merge(new ChangeRecord(SyncEntities.Bill, Bill, "dueDay", SyncValue.String("15"), H2));
        s.Merge(new ChangeRecord(SyncEntities.Bill, Bill, "autoPay", SyncValue.Number(1), H2));
        Assert.Equal(SyncValue.Number(1), s.Read(SyncEntities.Bill, Bill, "dueDay"));
        Assert.Equal(SyncValue.False, s.Read(SyncEntities.Bill, Bill, "autoPay"));
        Assert.Equal(SyncValue.String("15"), s.Value(SyncEntities.Bill, Bill, "dueDay"));
        Assert.Equal(SyncValue.Null, s.Read("futureEntity", "x", "y"));
    }

    [Fact]
    public void PendingRows_AreNeitherLiveNorDeleted()
    {
        var s = new SyncState();
        s.Merge(new ChangeRecord(SyncEntities.Bill, Bill, "name", SyncValue.String("Contoso Card"), H2));
        Assert.True(s.IsPending(SyncEntities.Bill, Bill));
        Assert.False(s.IsLive(SyncEntities.Bill, Bill));
        Assert.Empty(PaymentWriter.SetRemaining(s, Bill, 10, RemainingSource.App, null, H3));
        s.Merge(new ChangeRecord(SyncEntities.Bill, Bill, "deleted", SyncValue.False, H1));
        Assert.False(s.IsPending(SyncEntities.Bill, Bill));
        Assert.True(s.IsLive(SyncEntities.Bill, Bill));
    }

    [Fact]
    public void AllRecords_IsTheSnapshotContent_SortedByKey()
    {
        var s = BillState();
        s.Merge(new ChangeRecord("futureEntity", "z", "f", SyncValue.Number(1), H2));
        var all = s.AllRecords();
        Assert.Equal(s.Store.Count, all.Count);
        Assert.Equal(all, ChangeRecord.SortByKey(all.Reverse()));
        var copy = new SyncState();
        copy.MergeAll(all);
        Assert.Equal(all, copy.AllRecords());
        Assert.Equal(H2, s.LatestHlcOf(DeviceA));
        Assert.Null(s.LatestHlcOf("0b0b0b0b0b0b0b0b"));
    }

    [Fact]
    public void ExtraPayments_AreCreatesWithRandomIds_AndAddUp()
    {
        var s = BillState();
        var time = new ManualTimeProvider(1794657600000);
        s.MergeAll(PaymentWriter.AddExtraPayment(Uuid.NewV7(time), Bill, "2026-11-12", 100.005, "2026-11-14T12:00:00.000Z", H2, confirmationNumber: "CONF-1"));
        s.MergeAll(PaymentWriter.AddExtraPayment(Uuid.NewV7(time), Bill, "2026-11-12", 50, null, H3));
        Assert.Equal(2, s.LivePaymentsFor(Bill).Count);
        Assert.Equal(120000 - 10001 - 5000, s.RemainingCents(Bill));
        Assert.Equal(PaymentKinds.Extra, s.PaymentKind(s.LivePaymentsFor(Bill)[0]));
        Assert.Throws<PayDay.Sync.SyncException>(() => PaymentWriter.AddExtraPayment("13", Bill, "2026-11-12", 1, null, H3));
    }

    [Fact]
    public void SetRemaining_RejectsNegativeValues()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PaymentWriter.SetRemaining(BillState(), Bill, -0.01, RemainingSource.App, null, H2));

    [Fact]
    public void Remaining_ReadsAnOutOfRangeCheckpointAsZero()
    {
        var s = BillState();
        s.Merge(new ChangeRecord(SyncEntities.Bill, Bill, "remainingCheckpoint", SyncValue.Number(2e13), H2));
        Assert.Equal(0, s.RemainingCents(Bill));
    }

    // ---- Backup settings (data-model 8.5) -------------------------------------------------------

    private static SyncValue? Map(string key, string json) => BackupSettings.MapOne(key, StrictJson.Parse(json));

    [Theory]
    [InlineData("EarlyStart", "\"TRUE\"", "true")]
    [InlineData("EarlyStart", "\"yes\"", "false")]
    [InlineData("EarlyStart", "\" true\"", "false")]
    [InlineData("EarlyStart", "true", "true")]
    [InlineData("EarlyStart", "1", null)]
    [InlineData("SafeToSpendBuffer", "\"-12.345\"", "-12.35")]
    [InlineData("SafeToSpendBuffer", "\"12.\"", null)]
    [InlineData("SafeToSpendBuffer", "\"1e3\"", null)]
    [InlineData("SafeToSpendBuffer", "\"99999999999999\"", null)]
    [InlineData("SafeToSpendBuffer", "40.004", "40")]
    [InlineData("SafeToSpendBuffer", "true", null)]
    [InlineData("ReminderDaysBefore", "\"07\"", "7")]
    [InlineData("ReminderDaysBefore", "\"-1\"", null)]
    [InlineData("ReminderDaysBefore", "3.0", "3")]
    [InlineData("ReminderDaysBefore", "-1", null)]
    [InlineData("ReminderDaysBefore", "true", null)]
    [InlineData("PayAnchor", "\"2025-03-20\"", "\"2025-03-20\"")]
    [InlineData("PayAnchor", "\"2025-02-30\"", null)]
    [InlineData("TypeColor/Cards", "\"#a1b2c3\"", "\"#A1B2C3\"")]
    [InlineData("TypeColor/Cards", "\"#a1b2c\"", null)]
    [InlineData("Theme", "\"Dark\"", null)]
    [InlineData("PayAnchor", "null", null)]
    public void BackupSettings_MapOne(string key, string raw, string? expected)
    {
        var got = Map(key, raw);
        if (expected is null)
        {
            Assert.Null(got);
        }
        else
        {
            Assert.True(SyncValue.TryFromJson(StrictJson.Parse(expected), out var want));
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void BackupSettings_LegacyTypeColors_ExplicitKeysOverride_NotionNeverRestored()
    {
        var settings = StrictJson.Parse(
            "{\"TypeColors\":\"{\\\"Cards\\\":\\\"#fd79a8\\\",\\\"Gym\\\":\\\"#00b894\\\",\\\"\\\":\\\"#000000\\\",\\\"Bad\\\":5}\","
            + "\"TypeColor/Gym\":\"#111111\",\"NotionOwnerDeviceId\":\"0d0d0d0d0d0d0d0d\",\"NotionBillsDb\":\"db-1\",\"Unknown\":\"x\"}");
        var restore = BackupSettings.MapForRestore(settings);
        Assert.Equal(new[] { "TypeColor/Cards", "TypeColor/Gym" }, restore.Keys);
        Assert.Equal(SyncValue.String("#FD79A8"), restore["TypeColor/Cards"]);
        Assert.Equal(SyncValue.String("#111111"), restore["TypeColor/Gym"]);

        var migration = BackupSettings.MapForMigration(settings);
        Assert.Equal(SyncValue.String("db-1"), migration[SyncSettingKeys.NotionBillsDb]);
        Assert.False(migration.ContainsKey(SyncSettingKeys.NotionOwnerDeviceId));

        Assert.Empty(BackupSettings.MapForRestore(StrictJson.Parse("{\"TypeColors\":\"not json\"}")));
        Assert.Empty(BackupSettings.MapForRestore(StrictJson.Parse("{\"TypeColors\":\"[\\\"#123456\\\"]\"}")));
    }

    [Fact]
    public void Restore_RejectsRowsOfEntitiesItMustNotWrite()
    {
        var rows = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SyncValue>>>
        {
            [SyncEntities.Device] = new Dictionary<string, IReadOnlyDictionary<string, SyncValue>>(),
        };
        Assert.Throws<ArgumentException>(() => RestoreWriter.Restore(new SyncState(), new RestoreInput(rows, StrictJson.Parse("{}")), H1));
    }

    // ---- Compaction decisions (section 11.2) ------------------------------------------------------

    [Theory]
    [InlineData(true, true, true, 0, 20, 20, false, true)]
    [InlineData(true, true, true, 0, 19, 19, false, false)]
    [InlineData(true, true, true, 1, 300, 100, false, true)]
    [InlineData(true, true, true, 1, 300, 99, false, false)]
    [InlineData(true, true, true, 1, 300, 1, true, true)]
    [InlineData(true, true, true, 1, 300, 0, true, false)]
    [InlineData(false, true, true, 0, 50, 50, false, false)]
    [InlineData(true, false, true, 0, 50, 50, false, false)]
    [InlineData(true, true, false, 1, 500, 200, true, false)]
    public void ShouldWriteSnapshot(bool fg, bool uploaded, bool mergedAll, int snaps, int total, int uncovered, bool newestOld, bool expected)
        => Assert.Equal(expected, Compaction.ShouldWriteSnapshot(new SnapshotWriteInputs(fg, uploaded, mergedAll, snaps, total, uncovered, newestOld)));

    [Fact]
    public void SnapshotCovers_AddsTheWritersOwnLastUploadedSeq()
    {
        var covers = Compaction.SnapshotCovers(new Dictionary<string, long> { ["0b0b0b0b0b0b0b0b"] = 7 }, DeviceA, 4);
        Assert.Equal(7, covers["0b0b0b0b0b0b0b0b"]);
        Assert.Equal(4, covers[DeviceA]);
    }
}
