using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PayDay.Sync;
using PayDay.Sync.Clock;
using PayDay.Sync.Crypto;
using PayDay.Sync.Files;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Tests.Sync;

/// <summary>Unit tests for keys, framing, the recovery phrase, pairing, paths, envelopes and vault.json.</summary>
public class SyncFilesTests
{
    private const string VaultId = "019a0000-0000-7000-8000-00000000fa01";
    private const string DeviceA = "0a0a0a0a0a0a0a0a";
    private const string DeviceB = "0b0b0b0b0b0b0b0b";
    private const string Bill = "019a0000-0000-7000-8000-0000000000b1";

    private static readonly byte[] Key1 = Convert.FromHexString("f37bab15f8502440f593435afe8aefac43ad24eb5f5a77c903c93c2e8ab8cdc5");

    private static string Code(Action action) => Assert.Throws<SyncException>(action).Code;

    private static ChangeRecord Rec(string field, SyncValue value, string hlc) => new("bill", Bill, field, value, Hlc.Parse(hlc));

    [Fact]
    public void Keys_RejectWrongLengths()
    {
        Assert.Equal(SyncErrorCodes.BadKeyLength, Code(() => SyncKeys.FileKey(new byte[31])));
        Assert.Equal(SyncErrorCodes.BadKeyLength, Code(() => SyncKeys.PairingKey(new byte[33])));
        Assert.Equal(32, SyncKeys.NewKey().Length);
        Assert.Equal("54c8c2aa80ce76b080b28501f7d8a1b9", SyncKeys.KeyCheckHex(Key1));
    }

    [Fact]
    public void FileFrame_SealWithFreshNonce_RoundTripsAndNeverRepeatsCiphertext()
    {
        const string path = "devices/0a0a0a0a0a0a0a0a/000007.chg";
        var plaintext = Encoding.UTF8.GetBytes("{\"type\":\"changes\"}");
        var a = FileFrame.Seal(Key1, SyncFileKind.Changes, 1, path, plaintext);
        var b = FileFrame.Seal(Key1, SyncFileKind.Changes, 1, path, plaintext);
        Assert.NotEqual(a, b);
        var keys = new Dictionary<uint, byte[]> { [1] = Key1 };
        Assert.Equal(plaintext, FileFrame.Open(a, path, keys));
        Assert.Equal(plaintext, FileFrame.Open(b, path, keys));
        Assert.Equal(1u, FileFrame.ReadGeneration(a));
        Assert.Equal(SyncErrorCodes.AuthFailed, Code(() => FileFrame.Open(a, "devices/0a0a0a0a0a0a0a0a/000008.chg", keys)));
        Assert.Equal(SyncErrorCodes.UnknownKeyGeneration, Code(() => FileFrame.Open(a, path, null)));
    }

    [Fact]
    public void FileFrame_SealRefusesInconsistentArguments()
    {
        var nonce = new byte[12];
        Assert.Throws<ArgumentException>(() => FileFrame.Seal(Key1, SyncFileKind.Changes, 0, nonce, "devices/0a0a0a0a0a0a0a0a/000001.chg", Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => FileFrame.Seal(Key1, SyncFileKind.Pairing, 1, nonce, "pairing/abababababababababababababababab.pair", Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => FileFrame.Seal(Key1, SyncFileKind.Snapshot, 1, nonce, "devices/0a0a0a0a0a0a0a0a/000001.chg", Array.Empty<byte>()));
        Assert.Equal(SyncErrorCodes.BadNonceLength, Code(() => FileFrame.Seal(Key1, SyncFileKind.Changes, 1, new byte[11], "devices/0a0a0a0a0a0a0a0a/000001.chg", Array.Empty<byte>())));
    }

    [Fact]
    public void RecoveryPhrase_RoundTripsRandomKeys()
    {
        for (var i = 0; i < 100; i++)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var words = RecoveryPhrase.Encode(key);
            Assert.Equal(24, words.Count);
            Assert.Equal(key, RecoveryPhrase.Decode(string.Join(" \n", words).ToUpperInvariant()));
        }
        Assert.Equal(SyncErrorCodes.BadKeyLength, Code(() => RecoveryPhrase.Encode(new byte[16])));
        Assert.Equal(new[] { "abc", "def" }, RecoveryPhrase.Normalize(" \tABC\r\n def "));
        Assert.Equal(new[] { "a b" }, RecoveryPhrase.Normalize("A B"));
    }

    [Fact]
    public void PairingQr_RoundTripsANewCode()
    {
        var code = PairingQr.NewCode();
        var qr = PairingQr.Format(code);
        Assert.Equal(106, qr.Length);
        var back = PairingQr.Parse("\t" + qr.ToLowerInvariant() + " \r\n");
        Assert.Equal(code.PairingId, back.PairingId);
        Assert.Equal(code.PairingSecret, back.PairingSecret);
        Assert.StartsWith("pairing/", back.PairingPath, StringComparison.Ordinal);
        Assert.Equal(SyncErrorCodes.BadQr, Code(() => PairingQr.Parse(qr + "0")));
        Assert.Equal(SyncErrorCodes.BadQr, Code(() => PairingQr.Parse(qr.Replace(':', ';'))));
        Assert.Equal(SyncErrorCodes.BadQr, Code(() => PairingQr.Parse(qr[..^1] + "G")));
        Assert.Throws<ArgumentException>(() => PairingQr.Format(new byte[15], new byte[32]));
    }

    [Fact]
    public void SyncPaths_BuildAndParse()
    {
        Assert.Equal("devices/0a0a0a0a0a0a0a0a/000042.chg", SyncPaths.ChangeFilePath(DeviceA, 42));
        var hlc = Hlc.Parse("01a1d9f6e9c0-0000-0a0a0a0a0a0a0a0a");
        Assert.Equal("snapshots/01a1d9f6e9c0-0000-0a0a0a0a0a0a0a0a.snap", SyncPaths.SnapshotPath(hlc));
        Assert.Equal(SyncPathKind.Snapshot, SyncPaths.Parse(SyncPaths.SnapshotPath(hlc)).Kind);
        Assert.False(SyncPaths.TryParse(null, out _));
        Assert.False(SyncPaths.TryParse("devices/0a0a0a0a0a0a0a0a/99999999999999999999.chg", out _));
        Assert.False(SyncPaths.TryParse("snapshots/01a1d9f6e9c0-0000-0a0a0a0a0a0a0a0a.snap\n", out _));
        Assert.False(SyncPaths.TryParseChangeFileName("000001.chg\n", out _));
        Assert.Throws<ArgumentException>(() => SyncPaths.PairingPath("ABABABABABABABABABABABABABABABAB"));
        Assert.Equal(SyncErrorCodes.BadDeviceId, Code(() => SyncPaths.ChangeFilePath("x", 1)));
    }

    [Fact]
    public void Envelope_ReturnsOnlyValidOwnRecords_AndCountsTheRest()
    {
        var payload = StrictJson.Parse(
            "{\"type\":\"changes\",\"formatVersion\":1,\"vaultId\":\"" + VaultId + "\",\"deviceId\":\"" + DeviceA + "\",\"seq\":3,\"records\":["
            + "{\"entity\":\"bill\",\"id\":\"" + Bill + "\",\"field\":\"name\",\"value\":\"Woodgrove Rewards\",\"hlc\":\"01a1d9edc200-0000-0a0a0a0a0a0a0a0a\"},"
            + "{\"entity\":\"bill\",\"id\":\"" + Bill + "\",\"field\":\"name\",\"value\":\"other device\",\"hlc\":\"01a1d9edc200-0000-0b0b0b0b0b0b0b0b\"},"
            + "{\"entity\":\"bill\",\"id\":\"" + Bill + "\",\"field\":\"name\",\"value\":[1],\"hlc\":\"01a1d9edc200-0001-0a0a0a0a0a0a0a0a\"},"
            + "7],\"futureKey\":true}");
        var result = Envelope.Check(payload, "devices/0a0a0a0a0a0a0a0a/000003.chg", VaultId);
        Assert.Equal(new[] { 1, 3 }, result.IgnoredRecords);
        Assert.Equal(1, result.InvalidRecordCount);
        Assert.Single(result.Records);
        Assert.Equal("Woodgrove Rewards", result.Records[0].Value.StringValue);
        Assert.Equal(3, result.Seq);
    }

    [Fact]
    public void Envelope_ReadsSnapshotCoversAndPairingPayloads()
    {
        var snap = StrictJson.Parse(
            "{\"type\":\"snapshot\",\"formatVersion\":1,\"vaultId\":\"" + VaultId + "\",\"deviceId\":\"" + DeviceA + "\","
            + "\"hlc\":\"01a1d9f6e9c0-0000-0a0a0a0a0a0a0a0a\",\"covers\":{\"" + DeviceA + "\":3,\"" + DeviceB + "\":7.0},\"records\":["
            + "{\"entity\":\"bill\",\"id\":\"" + Bill + "\",\"field\":\"deleted\",\"value\":false,\"hlc\":\"01a1d9edc200-0000-0b0b0b0b0b0b0b0b\"}]}");
        var s = Envelope.Check(snap, "snapshots/01a1d9f6e9c0-0000-0a0a0a0a0a0a0a0a.snap", VaultId);
        Assert.Equal(7, s.Covers[DeviceB]);
        Assert.Single(s.Records);
        Assert.Equal("01a1d9f6e9c0-0000-0a0a0a0a0a0a0a0a", s.SnapshotHlc!.Value.Text);

        var pairing = Payloads.Pairing(VaultId, 1, Key1, DeviceA, "2026-11-14T12:10:00.000Z");
        var p = Envelope.Check(pairing, "pairing/abababababababababababababababab.pair", VaultId);
        Assert.Equal(Key1, p.Pairing!.VaultKey);
        Assert.Equal(DeviceA, p.Pairing.DeviceId);
        Assert.Equal(SyncErrorCodes.BadPath, Code(() => Envelope.Check(pairing, "vault.json", VaultId)));
    }

    [Fact]
    public void VaultFile_CreateCheckMatchAndChooseAmongDuplicates()
    {
        var json = VaultFile.Create(VaultId, "2026-11-14T12:00:00.000Z", DeviceA, Key1);
        var info = VaultFile.CheckBytes(StrictJson.Serialize(json));
        Assert.Equal(VaultId, info.VaultId);
        Assert.Equal(1, info.CurrentGeneration);
        Assert.Equal(1, VaultFile.MatchGeneration(info, Key1));
        Assert.Null(VaultFile.MatchGeneration(info, new byte[32]));

        var later = info with { CreatedAt = "2026-11-14T12:00:01.000Z", VaultId = "019a0000-0000-7000-8000-00000000fa00" };
        var tie = info with { VaultId = "019a0000-0000-7000-8000-00000000fa00" };
        Assert.Same(info, VaultFile.ChooseAmongDuplicates(new[] { later, info }));
        Assert.Same(tie, VaultFile.ChooseAmongDuplicates(new[] { info, tie }));
        Assert.Equal(SyncErrorCodes.Malformed, Code(() => VaultFile.CheckBytes(Encoding.UTF8.GetBytes("[]"))));
    }

    [Fact]
    public void VaultCodec_SealsAndOpensEveryFileKind()
    {
        var codec = new VaultCodec(VaultId, new Dictionary<uint, byte[]> { [1] = Key1 }, 1);
        var records = new[]
        {
            Rec("deleted", SyncValue.False, "01a1d9edc200-0000-0a0a0a0a0a0a0a0a"),
            Rec("name", SyncValue.String("Woodgrove Rewards"), "01a1d9edc200-0000-0a0a0a0a0a0a0a0a"),
            Rec("payment", SyncValue.Number(55), "01a1d9edc200-0000-0a0a0a0a0a0a0a0a"),
        };
        var (path, data) = codec.SealChangeFile(DeviceA, 1, records);
        Assert.Equal("devices/0a0a0a0a0a0a0a0a/000001.chg", path);
        var opened = codec.Open(path, data);
        Assert.Equal(records, opened.Records);

        var hlc = Hlc.Parse("01a1d9f6e9c0-0000-0b0b0b0b0b0b0b0b");
        var (snapPath, snap) = codec.SealSnapshot(hlc, new Dictionary<string, long> { [DeviceA] = 1, [DeviceB] = 0 }, records);
        var openedSnap = codec.Open(snapPath, snap);
        Assert.Equal(records, openedSnap.Records);
        Assert.Equal(1, openedSnap.Covers[DeviceA]);

        var code = PairingQr.NewCode();
        var (pairPath, pair) = codec.SealPairing(code, DeviceA, "2026-11-14T12:10:00.000Z");
        Assert.Equal(code.PairingPath, pairPath);
        var payload = VaultCodec.OpenPairing(VaultId, PairingQr.Parse(PairingQr.Format(code)), pair);
        Assert.Equal(Key1, payload.VaultKey);
        Assert.Equal(1, VaultFile.MatchGeneration(VaultFile.Check(VaultFile.Create(VaultId, "2026-11-14T12:00:00.000Z", DeviceA, Key1)), payload.VaultKey));

        var other = new VaultCodec("019a0000-0000-7000-8000-00000000fa02", new Dictionary<uint, byte[]> { [1] = Key1 }, 1);
        Assert.Equal(SyncErrorCodes.VaultMismatch, Code(() => other.Open(path, data)));
        Assert.Throws<ArgumentException>(() => codec.SealChangeFile(DeviceB, 1, records));
        Assert.Throws<ArgumentException>(() => codec.SealChangeFile(DeviceA, 2, Array.Empty<ChangeRecord>()));
    }

    [Fact]
    public void Payloads_SameRecordsComparesParsedValuesInOrder()
    {
        var a = StrictJson.Parse("{\"records\":[{\"value\":1.0,\"id\":\"x\"},{\"value\":true}]}");
        var b = StrictJson.Parse("{\"records\":[{\"id\":\"x\",\"value\":1},{\"value\":true}],\"seq\":9}");
        var c = StrictJson.Parse("{\"records\":[{\"value\":true},{\"value\":1,\"id\":\"x\"}]}");
        var d = StrictJson.Parse("{\"records\":[{\"value\":1,\"id\":\"x\"},{\"value\":1}]}");
        Assert.True(Payloads.SameRecords(a, b));
        Assert.False(Payloads.SameRecords(a, c));
        Assert.False(Payloads.SameRecords(a, d));
        Assert.False(Payloads.SameRecords(a, StrictJson.Parse("{}")));
    }
}
