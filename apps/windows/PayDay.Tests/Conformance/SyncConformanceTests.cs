using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PayDay.Sync;
using PayDay.Sync.Clock;
using PayDay.Sync.Crypto;
using PayDay.Sync.Files;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;
using PayDay.Sync.Reading;
using static PayDay.Tests.Conformance.SyncFixtureJson;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Runs every case in <c>spec/conformance/sync/*.json</c> against the C# sync library
/// (<c>PayDay.Core/Sync</c>). Each fixture case is its own xUnit test case, named by the case's
/// <c>name</c>; a case added to a file is picked up without changing this code. Merge cases are run
/// in every delivery order for up to 8 records and in 2,000 seeded orders (plus forward and reverse)
/// for larger cases, some with records delivered twice (<see cref="SyncMergeHarness"/>).
/// </summary>
public class SyncConformanceTests
{
    public static IEnumerable<object[]> HlcCases() => ConformanceFixtures.SyncCaseNames("hlc.json");
    public static IEnumerable<object[]> IdsCases() => ConformanceFixtures.SyncCaseNames("ids.json");
    public static IEnumerable<object[]> CryptoCases() => ConformanceFixtures.SyncCaseNames("crypto.json");
    public static IEnumerable<object[]> RecoveryPhraseCases() => ConformanceFixtures.SyncCaseNames("recovery-phrase.json");
    public static IEnumerable<object[]> FilesCases() => ConformanceFixtures.SyncCaseNames("files.json");
    public static IEnumerable<object[]> MergeCases() => ConformanceFixtures.SyncCaseNames("merge.json");
    public static IEnumerable<object[]> CompactionCases() => ConformanceFixtures.SyncCaseNames("compaction.json");

    [Theory]
    [MemberData(nameof(HlcCases))]
    public void Hlc(string caseName) => Check("hlc.json", caseName, RunHlc);

    [Theory]
    [MemberData(nameof(IdsCases))]
    public void Ids(string caseName) => Check("ids.json", caseName, RunIds);

    [Theory]
    [MemberData(nameof(CryptoCases))]
    public void Crypto(string caseName) => Check("crypto.json", caseName, RunCrypto);

    [Theory]
    [MemberData(nameof(RecoveryPhraseCases))]
    public void RecoveryPhrase(string caseName) => Check("recovery-phrase.json", caseName, RunRecoveryPhrase);

    [Theory]
    [MemberData(nameof(FilesCases))]
    public void Files(string caseName) => Check("files.json", caseName, RunFiles);

    [Theory]
    [MemberData(nameof(MergeCases))]
    public void Merge(string caseName)
    {
        var testCase = ConformanceFixtures.FindSyncCase("merge.json", caseName);
        SyncMergeHarness.Run(caseName, testCase["input"]!.AsObject(), testCase["expected"]!.AsObject());
    }

    [Theory]
    [MemberData(nameof(CompactionCases))]
    public void Compaction(string caseName) => Check("compaction.json", caseName, RunCompaction);

    [Fact]
    public void SyncFolder_HasExactlyTheKnownFiles_WithWellFormedCases()
    {
        var dir = ConformanceFixtures.SyncDirectory;
        var present = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(ConformanceFixtures.SyncFiles.OrderBy(n => n, StringComparer.Ordinal).ToArray(), present);
        foreach (var file in ConformanceFixtures.SyncFiles)
        {
            var root = ConformanceFixtures.LoadFile(ConformanceFixtures.SyncFolder, file);
            Assert.Equal(1, (int?)root["version"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)root["description"]), $"{file}: description is empty");
            var cases = ConformanceFixtures.Cases(root).Select(c => c!.AsObject()).ToList();
            Assert.NotEmpty(cases);
            var names = cases.Select(c => (string?)c["name"]).ToList();
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n), $"{file}: a case has no name"));
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            Assert.All(cases, c => Assert.True(c["input"] is JsonObject && c["expected"] is JsonObject, $"{file} / {c["name"]}: needs input and expected"));
        }
    }

    [Fact]
    public void Bip39Wordlist_IsEmbeddedUnchanged_AndMatchesItsPinnedHash()
    {
        var embedded = PayDay.Sync.Crypto.RecoveryPhrase.WordlistBytes();
        var onDisk = File.ReadAllBytes(Path.Combine(ConformanceFixtures.SpecDirectory, "bip39-english.txt"));
        Assert.Equal(onDisk, embedded);
        Assert.Equal(PayDay.Sync.Crypto.RecoveryPhrase.WordlistSha256, Convert.ToHexStringLower(SHA256.HashData(embedded)));
        Assert.Equal("2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda", PayDay.Sync.Crypto.RecoveryPhrase.WordlistSha256);
        Assert.Equal(2048, PayDay.Sync.Crypto.RecoveryPhrase.Words.Count);
        Assert.Equal("abandon", PayDay.Sync.Crypto.RecoveryPhrase.Words[0]);
        Assert.Equal("zoo", PayDay.Sync.Crypto.RecoveryPhrase.Words[2047]);
    }

    // ------------------------------------------------------------------------------------------

    private static void Check(string file, string caseName, Func<JsonObject, JsonObject, JsonNode?> run)
    {
        var testCase = ConformanceFixtures.FindSyncCase(file, caseName);
        var input = testCase["input"]!.AsObject();
        var expected = testCase["expected"]!.AsObject();
        JsonNode? actual;
        try
        {
            actual = run(input, expected);
        }
        catch (SyncException ex)
        {
            actual = new JsonObject { ["error"] = ex.Code };
        }
        var diffs = Differences(expected, actual);
        Assert.True(diffs.Count == 0, $"sync/{file} / {caseName}:\n  " + string.Join("\n  ", diffs));
    }

    private static JsonObject HlcResult(PayDay.Sync.Clock.Hlc h) => new() { ["hlc"] = h.Text };

    private static JsonNode? RunHlc(JsonObject i, JsonObject e)
    {
        var device = NullableStr(i["deviceId"]);
        switch (Str(i["fn"]))
        {
            case "encode":
            {
                var h = PayDay.Sync.Clock.Hlc.Encode(Long(i["wall"]), Long(i["counter"]), device!);
                var back = PayDay.Sync.Clock.Hlc.Parse(h.Text);
                Assert.Equal((Long(i["wall"]), (int)Long(i["counter"]), device), (back.Wall, back.Counter, back.DeviceId));
                return HlcResult(h);
            }
            case "decode":
            {
                var h = PayDay.Sync.Clock.Hlc.Parse(Str(i["hlc"]));
                return new JsonObject { ["wall"] = h.Wall, ["counter"] = h.Counter, ["deviceId"] = h.DeviceId };
            }
            case "compare":
            {
                var a = Str(i["a"]);
                var b = Str(i["b"]);
                var result = PayDay.Sync.Clock.Hlc.Compare(a, b);
                Assert.Equal(Math.Sign(string.CompareOrdinal(a, b)), result);
                Assert.Equal(-result, PayDay.Sync.Clock.Hlc.Compare(b, a));
                return new JsonObject { ["result"] = result };
            }
            case "tick":
            {
                PayDay.Sync.Clock.Hlc? last = i["last"] is null ? null : PayDay.Sync.Clock.Hlc.Parse(Str(i["last"]));
                var h = HlcClock.Tick(last, Long(i["now"]), device!);
                if (last is { } l) Assert.True(h > l, "tick must move the clock forward");
                return HlcResult(h);
            }
            case "observe":
            {
                PayDay.Sync.Clock.Hlc? last = i["last"] is null ? null : PayDay.Sync.Clock.Hlc.Parse(Str(i["last"]));
                return HlcResult(HlcClock.Observe(last, PayDay.Sync.Clock.Hlc.Parse(Str(i["remote"])), device!));
            }
            case "sequence":
            {
                PayDay.Sync.Clock.Hlc? state = null;
                var states = new List<string>();
                var seen = new List<PayDay.Sync.Clock.Hlc>();
                foreach (var step in i["steps"]!.AsArray().Select(s => s!.AsObject()))
                {
                    if (Str(step["op"]) == "tick")
                    {
                        var next = HlcClock.Tick(state, Long(step["now"]), device!);
                        if (state is { } prev) Assert.True(next > prev, "tick must increase");
                        Assert.All(seen, r => Assert.True(next > r, "a tick after an observe must exceed the remote"));
                        state = next;
                    }
                    else
                    {
                        var remote = PayDay.Sync.Clock.Hlc.Parse(Str(step["remote"]));
                        seen.Add(remote);
                        state = HlcClock.Observe(state, remote, device!);
                    }
                    states.Add(state!.Value.Text);
                }
                return new JsonObject { ["states"] = Strings(states) };
            }
            case "skewWarning":
                return new JsonObject { ["warning"] = HlcClock.SkewWarning(PayDay.Sync.Clock.Hlc.Parse(Str(i["remote"])), Long(i["now"])) };
            default:
                throw new InvalidOperationException($"unknown fn {i["fn"]}");
        }
    }

    private static JsonNode? RunIds(JsonObject i, JsonObject e)
    {
        JsonObject NameAndId(string name, string id)
        {
            Assert.Equal(Uuid.V5(Uuid.Namespace, name), id);
            Assert.Equal(5, Guid.Parse(id).Version);
            return new JsonObject { ["name"] = name, ["id"] = id };
        }

        switch (Str(i["fn"]))
        {
            case "regularPaymentId":
                return NameAndId(
                    SyncIds.RegularPaymentName(Str(i["billId"]), Str(i["periodKey"])),
                    SyncIds.RegularPaymentId(Str(i["billId"]), Str(i["periodKey"])));
            case "setAsideId":
                return NameAndId(
                    SyncIds.SetAsideName(Str(i["billId"]), Str(i["periodKey"])),
                    SyncIds.SetAsideId(Str(i["billId"]), Str(i["periodKey"])));
            case "paycheckId":
                return NameAndId(
                    SyncIds.PaycheckName(Str(i["incomeSourceId"]), Str(i["payDate"])),
                    SyncIds.PaycheckId(Str(i["incomeSourceId"]), Str(i["payDate"])));
            case "paycheckIdKind":
                return new JsonObject
                {
                    ["kind"] = SyncIds.KindForCadence(Str(i["cadence"])) == PaycheckIdKind.UuidV5 ? "uuidv5" : "uuidv7",
                };
            case "legacyPaymentId":
            {
                var args = (Str(i["billId"]), Str(i["periodKey"]), Long(i["amountCents"]), Str(i["paidAt"]), Long(i["occurrence"]));
                return NameAndId(
                    SyncIds.LegacyPaymentName(args.Item1, args.Item2, args.Item3, args.Item4, args.Item5),
                    SyncIds.LegacyPaymentId(args.Item1, args.Item2, args.Item3, args.Item4, args.Item5));
            }
            case "legacySnapshotId":
                return NameAndId(
                    SyncIds.LegacySnapshotName(Str(i["snapshotDate"]), Long(i["totalCents"]), Long(i["occurrence"])),
                    SyncIds.LegacySnapshotId(Str(i["snapshotDate"]), Long(i["totalCents"]), Long(i["occurrence"])));
            case "migrateBillId":
            {
                var oldId = Str(i["id"]);
                var id = SyncIds.MigrateBillId(oldId);
                if (e.ContainsKey("name"))
                {
                    return NameAndId(SyncIds.LegacyBillName(oldId), id);
                }
                return new JsonObject { ["id"] = id };
            }
            case "uuid7":
            {
                var id = Uuid.V7(Long(i["unixMs"]), Hex(i["randomHex"]));
                var guid = Guid.Parse(id);
                Assert.Equal(7, guid.Version);
                Assert.Equal(Long(i["unixMs"]), Uuid.Inspect(id).UnixMs);
                return new JsonObject { ["id"] = id };
            }
            case "inspectUuid":
            {
                var info = Uuid.Inspect(Str(i["id"]));
                var o = new JsonObject { ["canonical"] = info.Canonical };
                if (info.Version is { } v) o["version"] = v;
                if (info.Rfc9562Variant is { } ok) o["rfc9562Variant"] = ok;
                if (info.UnixMs is { } ms) o["unixMs"] = ms;
                return o;
            }
            case "isDeviceId":
                return new JsonObject { ["valid"] = DeviceIds.IsValid(Str(i["id"])) };
            default:
                throw new InvalidOperationException($"unknown fn {i["fn"]}");
        }
    }

    private static JsonNode? RunCrypto(JsonObject i, JsonObject e)
    {
        switch (Str(i["fn"]))
        {
            case "deriveKeys":
            {
                var key = Hex(i["vaultKeyHex"]);
                Assert.Equal(Str(e["fileKeyHex"]), Convert.ToHexStringLower(HmacOnlyHkdf(key, SyncKeys.FileKeyInfo, 32)));
                Assert.Equal(Str(e["keyCheckHex"]), Convert.ToHexStringLower(HmacOnlyHkdf(key, SyncKeys.KeyCheckInfo, 16)));
                return new JsonObject
                {
                    ["fileKeyHex"] = Convert.ToHexStringLower(SyncKeys.FileKey(key)),
                    ["keyCheckHex"] = SyncKeys.KeyCheckHex(key),
                };
            }
            case "derivePairingKey":
            {
                var secret = Hex(i["pairingSecretHex"]);
                Assert.Equal(Str(e["pairingKeyHex"]), Convert.ToHexStringLower(HmacOnlyHkdf(secret, SyncKeys.PairingKeyInfo, 32)));
                return new JsonObject { ["pairingKeyHex"] = Convert.ToHexStringLower(SyncKeys.PairingKey(secret)) };
            }
            case "seal":
            {
                var kind = (SyncFileKind)Long(i["kind"]);
                var secret = Hex(kind == SyncFileKind.Pairing ? i["pairingSecretHex"] : i["vaultKeyHex"]);
                var generation = (uint)Long(i["keyGeneration"]);
                var path = Str(i["path"]);
                var plaintext = Encoding.UTF8.GetBytes(Str(i["plaintextUtf8"]));
                var data = FileFrame.Seal(secret, kind, generation, Hex(i["nonceHex"]), path, plaintext);
                Assert.Equal(FileFrame.HeaderLength + plaintext.Length + FileFrame.TagLength, data.Length);
                var keys = kind == SyncFileKind.Pairing ? null : new Dictionary<uint, byte[]> { [generation] = secret };
                Assert.Equal(plaintext, FileFrame.Open(data, path, keys, kind == SyncFileKind.Pairing ? secret : null));
                return new JsonObject
                {
                    ["headerHex"] = Convert.ToHexStringLower(data.AsSpan(0, FileFrame.HeaderLength)),
                    ["aadHex"] = Convert.ToHexStringLower(FileFrame.Aad(data.AsSpan(0, FileFrame.HeaderLength), path)),
                    ["fileHex"] = Convert.ToHexStringLower(data),
                };
            }
            case "open":
            {
                Dictionary<uint, byte[]>? keys = null;
                if (i["vaultKeysHex"] is JsonObject vk && vk.Count > 0)
                {
                    keys = vk.ToDictionary(p => uint.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => Hex(p.Value));
                }
                var secret = i.ContainsKey("pairingSecretHex") ? Hex(i["pairingSecretHex"]) : null;
                var plaintext = FileFrame.Open(Hex(i["fileHex"]), Str(i["path"]), keys, secret);
                return new JsonObject { ["plaintextUtf8"] = new UTF8Encoding(false, true).GetString(plaintext) };
            }
            case "matchGeneration":
            {
                var vault = VaultFile.Check(ToData(i["vault"]));
                var gen = VaultFile.MatchGeneration(vault, Hex(i["vaultKeyHex"]));
                return new JsonObject { ["generation"] = gen is { } g ? JsonValue.Create(g) : null };
            }
            case "pairingQr":
            {
                var id = Hex(i["pairingIdHex"]);
                var qr = PairingQr.Format(id, Hex(i["pairingSecretHex"]));
                Assert.All(qr, c => Assert.Contains(c, "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:"));
                return new JsonObject { ["qr"] = qr, ["path"] = SyncPaths.PairingPath(Convert.ToHexStringLower(id)) };
            }
            case "parsePairingQr":
            {
                var code = PairingQr.Parse(Str(i["qr"]));
                return new JsonObject
                {
                    ["pairingIdHex"] = Convert.ToHexStringLower(code.PairingId),
                    ["pairingSecretHex"] = Convert.ToHexStringLower(code.PairingSecret),
                };
            }
            default:
                throw new InvalidOperationException($"unknown fn {i["fn"]}");
        }
    }

    private static JsonNode? RunRecoveryPhrase(JsonObject i, JsonObject e)
    {
        switch (Str(i["fn"]))
        {
            case "encode":
            {
                var words = PayDay.Sync.Crypto.RecoveryPhrase.Encode(Hex(i["keyHex"]));
                Assert.Equal(string.Join(' ', words), PayDay.Sync.Crypto.RecoveryPhrase.EncodeToString(Hex(i["keyHex"])));
                return new JsonObject { ["words"] = Strings(words), ["phrase"] = string.Join(' ', words) };
            }
            case "decode":
                return new JsonObject { ["keyHex"] = Convert.ToHexStringLower(PayDay.Sync.Crypto.RecoveryPhrase.Decode(Str(i["phrase"]))) };
            default:
                throw new InvalidOperationException($"unknown fn {i["fn"]}");
        }
    }

    private static JsonNode? RunFiles(JsonObject i, JsonObject e)
    {
        switch (Str(i["fn"]))
        {
            case "changeFileName":
            {
                var name = SyncPaths.ChangeFileName(Long(i["seq"]));
                Assert.True(SyncPaths.TryParseChangeFileName(name, out var back) && back == Long(i["seq"]), "parse back");
                return new JsonObject { ["name"] = name };
            }
            case "parsePath":
            {
                var p = SyncPaths.Parse(Str(i["path"]));
                return p.Kind switch
                {
                    SyncPathKind.Changes => new JsonObject { ["kind"] = "changes", ["deviceId"] = p.DeviceId, ["seq"] = p.Seq },
                    SyncPathKind.Snapshot => new JsonObject { ["kind"] = "snapshot", ["hlc"] = p.Hlc, ["deviceId"] = p.DeviceId },
                    _ => new JsonObject { ["kind"] = "pairing", ["pairingId"] = p.PairingId },
                };
            }
            case "checkEnvelope":
            {
                var forms = new[] { "payload", "payloadText", "payloadHex" }.Where(i.ContainsKey).ToList();
                Assert.Single(forms);
                var path = Str(i["path"]);
                var vaultId = Str(i["vaultId"]);
                var result = forms[0] switch
                {
                    "payload" => Envelope.Check(ToData(i["payload"]), path, vaultId),
                    "payloadText" => Envelope.CheckBytes(Encoding.UTF8.GetBytes(Str(i["payloadText"])), path, vaultId),
                    _ => Envelope.CheckBytes(Hex(i["payloadHex"]), path, vaultId),
                };
                return new JsonObject { ["ignoredRecords"] = Longs(result.IgnoredRecords.Select(x => (long)x)) };
            }
            case "checkVault":
            {
                var vault = i.ContainsKey("vaultText")
                    ? VaultFile.CheckBytes(Encoding.UTF8.GetBytes(Str(i["vaultText"])))
                    : VaultFile.Check(ToData(i["vault"]));
                return new JsonObject { ["currentGeneration"] = vault.CurrentGeneration, ["generations"] = Longs(vault.GenerationIds) };
            }
            default:
                throw new InvalidOperationException($"unknown fn {i["fn"]}");
        }
    }

    private static IReadOnlyCollection<SnapshotInfo> Snapshots(JsonNode? node)
        => node!.AsArray()
            .Select(s => s!.AsObject())
            .Select(s => new SnapshotInfo(Str(s["hlc"]), LongDict(s["covers"]), s["ageOk"]?.GetValue<bool>() ?? false))
            .ToArray();

    private static JsonNode? RunCompaction(JsonObject i, JsonObject e)
    {
        switch (Str(i["fn"]))
        {
            case "readPlan":
            {
                var cursors = LongDict(i["cursors"]);
                var files = LongListDict(i["files"]);
                var snapshots = Snapshots(i["snapshots"]);
                var unreadable = i.ContainsKey("unreadable") ? LongListDict(i["unreadable"]) : null;
                var plan = ReadPlanner.Plan(cursors, files, snapshots, unreadable);

                // Merging every snapshot up front is allowed and must reach the same cursors (section 10.2).
                var raised = cursors.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
                foreach (var s in snapshots)
                {
                    foreach (var (d, v) in s.Covers) raised[d] = Math.Max(raised.TryGetValue(d, out var c) ? c : 0, v);
                }
                var eager = ReadPlanner.Plan(raised, files, snapshots, unreadable);
                foreach (var (d, v) in plan.CursorsAfter)
                {
                    Assert.Equal(v, eager.CursorsAfter.TryGetValue(d, out var c) ? c : 0);
                }

                var readFiles = new JsonObject();
                foreach (var (d, seqs) in plan.ReadFiles) readFiles[d] = Longs(seqs);
                return new JsonObject
                {
                    ["mergeSnapshots"] = Strings(plan.MergeSnapshots),
                    ["readFiles"] = readFiles,
                    ["cursorsAfter"] = LongMap(plan.CursorsAfter),
                    ["gaps"] = LongMap(plan.Gaps),
                };
            }
            case "snapshotAgeOk":
                return new JsonObject
                {
                    ["ok"] = PayDay.Sync.Reading.Compaction.SnapshotAgeOk(
                        NullableLong(i["firstSeenLocalMs"]), Long(i["localNowMs"]), NullableLong(i["serverCreatedMs"]), NullableLong(i["serverNowMs"])),
                };
            case "deletableChangeFiles":
            {
                var me = Str(i["deviceId"]);
                var snapshots = Snapshots(i["snapshots"]);
                var deletable = PayDay.Sync.Reading.Compaction.DeletableChangeFiles(me, i["seqs"]!.AsArray().Select(Long), snapshots);
                // Every deletable file stays covered by a snapshot that is not itself deletable.
                var doomed = PayDay.Sync.Reading.Compaction.DeletableSnapshots(snapshots);
                var survivors = snapshots.Where(s => !doomed.Contains(s.Hlc)).ToList();
                Assert.All(deletable, n => Assert.Contains(survivors, s => s.CoversFor(me) >= n));
                return new JsonObject { ["deletable"] = Longs(deletable) };
            }
            case "deletableSnapshots":
            {
                var snapshots = Snapshots(i["snapshots"]);
                var deletable = PayDay.Sync.Reading.Compaction.DeletableSnapshots(snapshots);
                var kept = snapshots.Where(s => !deletable.Contains(s.Hlc)).ToList();
                Assert.All(snapshots.Where(s => deletable.Contains(s.Hlc)),
                    s => Assert.Contains(kept, k => PayDay.Sync.Reading.Compaction.Dominates(k, s)));
                return new JsonObject { ["deletable"] = Strings(deletable) };
            }
            default:
                throw new InvalidOperationException($"unknown fn {i["fn"]}");
        }
    }

    /// <summary>
    /// HKDF-SHA256 written with HMAC only, as an Android build without HKDF would (section 6.5), to
    /// cross-check the library's use of <see cref="HKDF"/>.
    /// </summary>
    private static byte[] HmacOnlyHkdf(byte[] ikm, byte[] info, int length)
    {
        var prk = HMACSHA256.HashData(new byte[32], ikm);
        var okm = new List<byte>();
        var t = Array.Empty<byte>();
        for (byte counter = 1; okm.Count < length; counter++)
        {
            t = HMACSHA256.HashData(prk, t.Concat(info).Append(counter).ToArray());
            okm.AddRange(t);
        }
        return okm.Take(length).ToArray();
    }
}
