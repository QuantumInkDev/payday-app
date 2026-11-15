import CryptoKit
import Foundation
import Testing
@testable import PayDayKit

/// Every case of spec/conformance/sync/*.json, one parameterized test case per fixture case,
/// mirroring tools/spec/check_fixtures.py.
@Suite("Sync conformance")
struct SyncConformanceTests {
    @Test("hlc.json", arguments: Fixtures.cases("sync/hlc.json"))
    func hlc(_ c: FixtureCase) throws { try guarded(c) { try SyncRunner.hlc(c) } }

    @Test("ids.json", arguments: Fixtures.cases("sync/ids.json"))
    func ids(_ c: FixtureCase) throws { try guarded(c) { try SyncRunner.ids(c) } }

    @Test("crypto.json", arguments: Fixtures.cases("sync/crypto.json"))
    func crypto(_ c: FixtureCase) throws { try guarded(c) { try SyncRunner.crypto(c) } }

    @Test("recovery-phrase.json", arguments: Fixtures.cases("sync/recovery-phrase.json"))
    func recoveryPhrase(_ c: FixtureCase) throws { try guarded(c) { try SyncRunner.recoveryPhrase(c) } }

    @Test("files.json", arguments: Fixtures.cases("sync/files.json"))
    func files(_ c: FixtureCase) throws { try guarded(c) { try SyncRunner.files(c) } }

    @Test("merge.json", arguments: Fixtures.cases("sync/merge.json"))
    func merge(_ c: FixtureCase) throws { try guarded(c) { try MergeRunner.check(c) } }

    @Test("compaction.json", arguments: Fixtures.cases("sync/compaction.json"))
    func compaction(_ c: FixtureCase) throws { try guarded(c) { try SyncRunner.compaction(c) } }

    private func guarded(_ c: FixtureCase, _ body: () throws -> Void) throws {
        if let e = c.loadError { Issue.record("fixture file failed to load: \(e)"); return }
        try body()
    }
}

enum SyncRunner {
    // MARK: hlc.json

    static func hlc(_ c: FixtureCase) throws {
        let i = c.input, e = c.expected
        switch try i.string("fn") {
        case "encode":
            let wall = try i.int64("wall"), counter = try i.int64("counter"), deviceId = try i.string("deviceId")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let h = try HLC(wall: wall, counter: counter, deviceId: deviceId)
                #expect(HLC.isValid(h.string) && (try? HLC(h.string)) == h, "round trip")
                return .object(JSONObject(["hlc": .string(h.string)]))
            }
        case "decode":
            let text = try i.string("hlc")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let h = try HLC(text)
                return .object(JSONObject(["wall": .number(Double(h.wall)), "counter": .number(Double(h.counter)),
                                           "deviceId": .string(h.deviceId.rawValue)]))
            }
        case "compare":
            let a = try HLC(try i.string("a")), b = try HLC(try i.string("b"))
            let want = try e.int("result")
            #expect(HLC.compare(a, b) == want)
            #expect(StringOrder.utf8Compare(a.string, b.string) == want, "byte-wise string compare")
            #expect(HLC.compare(b, a) == -want, "antisymmetry")
        case "tick":
            let last = try i.optionalString("last").map { try HLC($0) }
            let device = try device(i, "deviceId"), now = try i.int64("now")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let h = try HLC.tick(last: last, now: now, device: device)
                if let last { #expect(h > last, "tick must move the clock forward") }
                return .object(JSONObject(["hlc": .string(h.string)]))
            }
        case "observe":
            let last = try i.optionalString("last").map { try HLC($0) }
            let h = HLC.observe(last: last, remote: try HLC(try i.string("remote")), device: try device(i, "deviceId"))
            expectSame(.object(JSONObject(["hlc": .string(h.string)])), e, "observe")
        case "sequence":
            var clock = HLCClock(device: try device(i, "deviceId"))
            var states: [JSONValue] = []
            var seen: [HLC] = []
            for step in try i.array("steps") {
                if try step.string("op") == "tick" {
                    let before = clock.state
                    let h = try clock.tick(now: try step.int64("now"))
                    if let before { #expect(h > before, "tick must increase") }
                    for r in seen { #expect(h > r, "tick after observe must exceed the remote") }
                } else {
                    let r = try HLC(try step.string("remote"))
                    clock.observe(r)
                    seen.append(r)
                }
                states.append(.string(clock.state!.string))
            }
            expectSame(.object(JSONObject(["states": .array(states)])), e, "sequence")
        case "skewWarning":
            let w = HLC.skewWarning(remote: try HLC(try i.string("remote")), now: try i.int64("now"))
            expectSame(.object(JSONObject(["warning": .bool(w)])), e, "skew warning")
        case let fn:
            throw FixtureFailure("unknown fn \(fn)")
        }
    }

    static func device(_ v: JSONValue, _ key: String) throws -> DeviceID {
        guard let d = DeviceID(try v.string(key)) else { throw FixtureFailure("\(key) is not a device ID") }
        return d
    }

    // MARK: ids.json

    static func ids(_ c: FixtureCase) throws {
        let i = c.input, e = c.expected
        func named(_ name: () throws(SyncError) -> String, _ id: () throws(SyncError) -> String) {
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let n = try name(), v = try id()
                #expect(v == SyncIDs.uuid5(name: n))
                #expect(SyncIDs.inspect(v).version == 5 && SyncIDs.inspect(v).rfc9562Variant == true, "version 5, RFC variant")
                return .object(JSONObject(["name": .string(n), "id": .string(v)]))
            }
        }
        switch try i.string("fn") {
        case "regularPaymentId":
            let b = try i.string("billId"), p = try i.string("periodKey")
            named({ () throws(SyncError) in try SyncIDs.regularPaymentName(billId: b, periodKey: p) },
                  { () throws(SyncError) in try SyncIDs.regularPaymentId(billId: b, periodKey: p) })
        case "setAsideId":
            let b = try i.string("billId"), p = try i.string("periodKey")
            named({ () throws(SyncError) in try SyncIDs.setAsideName(billId: b, periodKey: p) },
                  { () throws(SyncError) in try SyncIDs.setAsideId(billId: b, periodKey: p) })
        case "paycheckId":
            let s = try i.string("incomeSourceId"), d = try i.string("payDate")
            named({ () throws(SyncError) in try SyncIDs.paycheckName(incomeSourceId: s, payDate: d) },
                  { () throws(SyncError) in try SyncIDs.paycheckId(incomeSourceId: s, payDate: d) })
        case "paycheckIdKind":
            let k = SyncIDs.paycheckIdKind(cadence: try i.string("cadence"))
            expectSame(.object(JSONObject(["kind": .string(k.rawValue)])), e, "paycheck id kind")
        case "legacyPaymentId":
            let b = try i.string("billId"), p = try i.string("periodKey"), cents = try i.int64("amountCents")
            let at = try i.string("paidAt"), n = try i.int64("occurrence")
            named({ () throws(SyncError) in try SyncIDs.legacyPaymentName(billId: b, periodKey: p, amountCents: cents, paidAt: at, occurrence: n) },
                  { () throws(SyncError) in try SyncIDs.legacyPaymentId(billId: b, periodKey: p, amountCents: cents, paidAt: at, occurrence: n) })
        case "legacySnapshotId":
            let d = try i.string("snapshotDate"), cents = try i.int64("totalCents"), n = try i.int64("occurrence")
            named({ () throws(SyncError) in try SyncIDs.legacySnapshotName(snapshotDate: d, totalCents: cents, occurrence: n) },
                  { () throws(SyncError) in try SyncIDs.legacySnapshotId(snapshotDate: d, totalCents: cents, occurrence: n) })
        case "migrateBillId":
            let old = try i.string("id")
            let id = try SyncIDs.migrateBillId(old)
            expectSame(.string(id), try e.req("id"), "migrated id")
            if let name = e["name"] {
                expectSame(.string(try SyncIDs.legacyBillName(oldId: old)), name, "legacy name")
            } else {
                #expect(SyncIDs.isUUIDShaped(old), "a GUID is lowercased, not mapped")
            }
        case "uuid7":
            let ms = try i.int64("unixMs")
            let id = try SyncIDs.uuid7(unixMs: ms, random: try i.hexBytes("randomHex"))
            expectSame(.string(id), try e.req("id"), "uuid7")
            let info = SyncIDs.inspect(id)
            #expect(info.version == 7 && info.rfc9562Variant == true && info.unixMs == ms, "layout")
        case "inspectUuid":
            let info = SyncIDs.inspect(try i.string("id"))
            var o = JSONObject(["canonical": .bool(info.canonical)])
            if let v = info.version { o["version"] = .number(v) }
            if let v = info.rfc9562Variant { o["rfc9562Variant"] = .bool(v) }
            if let v = info.unixMs { o["unixMs"] = .number(v) }
            expectSame(.object(o), e, "inspect")
        case "isDeviceId":
            expectSame(.object(JSONObject(["valid": .bool(DeviceID.isValid(try i.string("id")))])), e, "device id")
        case let fn:
            throw FixtureFailure("unknown fn \(fn)")
        }
    }

    // MARK: crypto.json

    static func crypto(_ c: FixtureCase) throws {
        let i = c.input, e = c.expected
        switch try i.string("fn") {
        case "deriveKeys":
            let k = try i.hexBytes("vaultKeyHex")
            let got = JSONObject(["fileKeyHex": .string(Hex.encode(try FileCrypto.fileKey(vaultKey: k))),
                                  "keyCheckHex": .string(Hex.encode(try FileCrypto.keyCheck(vaultKey: k)))])
            expectSame(.object(got), e, "derived keys")
            #expect(Hex.encode(hmacHKDF(k, FileCrypto.fileKeyInfo, 32)) == e["fileKeyHex"]?.stringValue, "HMAC-only HKDF")
        case "derivePairingKey":
            let s = try i.hexBytes("pairingSecretHex")
            expectSame(.object(JSONObject(["pairingKeyHex": .string(Hex.encode(try FileCrypto.pairingKey(pairingSecret: s)))])), e, "pairing key")
            #expect(Hex.encode(hmacHKDF(s, FileCrypto.pairingKeyInfo, 32)) == e["pairingKeyHex"]?.stringValue, "HMAC-only HKDF")
        case "seal":
            let secret = i["vaultKeyHex"] != nil ? try i.hexBytes("vaultKeyHex") : try i.hexBytes("pairingSecretHex")
            guard let kind = SyncFileKind(rawValue: UInt8(try i.int("kind"))) else { throw FixtureFailure("kind") }
            let gen = UInt32(try i.int64("keyGeneration")), path = try i.string("path")
            let pt = Array(try i.string("plaintextUtf8").utf8)
            let data = try FileCrypto.seal(secret: secret, kind: kind, generation: gen, nonce: try i.hexBytes("nonceHex"), path: path, plaintext: pt)
            let header = Array(data.prefix(24))
            expectSame(.object(JSONObject(["headerHex": .string(Hex.encode(header)),
                                           "aadHex": .string(Hex.encode(FileCrypto.aad(header: header, path: path))),
                                           "fileHex": .string(Hex.encode(data))])), e, "sealed file")
            #expect(data.count == 24 + pt.count + 16, "length")
            let back = kind == .pairing
                ? try FileCrypto.open(data, path: path, vaultKeys: nil, pairingSecret: secret)
                : try FileCrypto.open(data, path: path, vaultKeys: [gen: secret])
            #expect(back == pt, "round trip")
        case "open":
            var keys: [UInt32: [UInt8]]? = nil
            if case .object(let o)? = i["vaultKeysHex"] {
                keys = [:]
                for m in o {
                    guard let g = UInt32(m.key), let s = m.value.stringValue, let k = Hex.decode(s) else { throw FixtureFailure("vaultKeysHex") }
                    keys?[g] = k
                }
            }
            let ps = i["pairingSecretHex"] != nil ? try i.hexBytes("pairingSecretHex") : nil
            let data = try i.hexBytes("fileHex"), path = try i.string("path")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let pt = try FileCrypto.open(data, path: path, vaultKeys: keys, pairingSecret: ps)
                guard let s = String(validating: pt, as: UTF8.self) else { return .null }
                return .object(JSONObject(["plaintextUtf8": .string(s)]))
            }
        case "matchGeneration":
            let vault = try SyncFiles.checkVault(try i.req("vault"))
            let g = try SyncFiles.matchGeneration(vault, vaultKey: try i.hexBytes("vaultKeyHex"))
            expectSame(.object(JSONObject(["generation": g.map { .number($0) } ?? .null])), e, "generation")
        case "pairingQr":
            let pid = try i.hexBytes("pairingIdHex"), sec = try i.hexBytes("pairingSecretHex")
            let qr = SyncFiles.pairingQr(pairingId: pid, secret: sec)
            expectSame(.object(JSONObject(["qr": .string(qr), "path": .string(SyncPath.pairing(pairingId: Hex.encode(pid)).path)])), e, "qr")
            let alphanumeric = Set("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:")
            #expect(qr.allSatisfy { alphanumeric.contains($0) }, "QR alphanumeric mode")
        case "parsePairingQr":
            let text = try i.string("qr")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let (pid, sec) = try SyncFiles.parsePairingQr(text)
                return .object(JSONObject(["pairingIdHex": .string(Hex.encode(pid)), "pairingSecretHex": .string(Hex.encode(sec))]))
            }
        case let fn:
            throw FixtureFailure("unknown fn \(fn)")
        }
    }

    /// HKDF written with HMAC only (as an Android app would), as an independent cross-check.
    static func hmacHKDF(_ ikm: [UInt8], _ info: String, _ length: Int) -> [UInt8] {
        let prk = HMACSHA256.mac(key: [UInt8](repeating: 0, count: 32), message: ikm)
        var okm: [UInt8] = [], t: [UInt8] = []
        var n: UInt8 = 1
        while okm.count < length {
            t = HMACSHA256.mac(key: prk, message: t + Array(info.utf8) + [n])
            okm += t
            n += 1
        }
        return Array(okm.prefix(length))
    }

    // MARK: recovery-phrase.json

    static let bip39Reference: [String: String] = [
        String(repeating: "00", count: 32): Array(repeating: "abandon", count: 23).joined(separator: " ") + " art",
        String(repeating: "ff", count: 32): Array(repeating: "zoo", count: 23).joined(separator: " ") + " vote",
        "68a79eaca2324873eacc50cb9c6eca8cc68ea5d936f98787c60c7ebc74e6ce7c":
            "hamster diagram private dutch cause delay private meat slide toddler razor book happy fancy gospel tennis maple dilemma loan word shrug inflict delay length",
    ]

    static func recoveryPhrase(_ c: FixtureCase) throws {
        let i = c.input, e = c.expected
        switch try i.string("fn") {
        case "encode":
            let keyHex = try i.string("keyHex")
            guard let key = Hex.decode(keyHex) else { throw FixtureFailure("keyHex") }
            let words = try RecoveryPhrase.encode(key: key)
            expectSame(.object(JSONObject(["words": .array(words.map(JSONValue.string)),
                                           "phrase": .string(words.joined(separator: " "))])), e, "phrase")
            if let ref = bip39Reference[keyHex] { #expect(words.joined(separator: " ") == ref, "published BIP39 vector") }
        case "decode":
            let phrase = try i.string("phrase")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                .object(JSONObject(["keyHex": .string(Hex.encode(try RecoveryPhrase.decode(phrase)))]))
            }
        case let fn:
            throw FixtureFailure("unknown fn \(fn)")
        }
    }

    // MARK: files.json

    static func files(_ c: FixtureCase) throws {
        let i = c.input, e = c.expected
        switch try i.string("fn") {
        case "changeFileName":
            let seq = try i.int64("seq")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let name = try SyncFiles.changeFileName(seq: seq)
                #expect(SyncFiles.parseChangeFileName(name) == seq, "parse back")
                return .object(JSONObject(["name": .string(name)]))
            }
        case "parsePath":
            let path = try i.string("path")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                switch try SyncFiles.parsePath(path) {
                case let .changes(d, seq):
                    return .object(JSONObject(["kind": .string("changes"), "deviceId": .string(d.rawValue), "seq": .number(seq)]))
                case let .snapshot(h):
                    return .object(JSONObject(["kind": .string("snapshot"), "hlc": .string(h.string), "deviceId": .string(h.deviceId.rawValue)]))
                case let .pairing(p):
                    return .object(JSONObject(["kind": .string("pairing"), "pairingId": .string(p)]))
                }
            }
        case "checkEnvelope":
            let given = ["payload", "payloadText", "payloadHex"].filter { i[$0] != nil }
            guard given.count == 1 else { throw FixtureFailure("exactly one of payload, payloadText, payloadHex") }
            let path = try i.string("path"), vaultId = try i.string("vaultId")
            let payload: JSONValue? = given[0] == "payload" ? try i.req("payload") : nil
            let bytes: [UInt8]? = given[0] == "payloadText" ? Array(try i.string("payloadText").utf8)
                : given[0] == "payloadHex" ? try i.hexBytes("payloadHex") : nil
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let p: JSONValue
                if let payload { p = payload } else { p = try SyncFiles.parsePayload(bytes ?? []) }
                let r = try SyncFiles.checkEnvelope(p, path: path, vaultId: vaultId)
                return .object(JSONObject(["ignoredRecords": .array(r.ignoredRecords.map { .number($0) })]))
            }
        case "checkVault":
            let vault: JSONValue? = i["vault"]
            let text: String? = try i.optionalString("vaultText")
            expectResultOrError(e) { () throws(SyncError) -> JSONValue in
                let v: SyncFiles.Vault
                if let vault { v = try SyncFiles.checkVault(vault) } else { v = try SyncFiles.checkVault(bytes: Array((text ?? "").utf8)) }
                return .object(JSONObject(["currentGeneration": .number(v.currentGeneration),
                                           "generations": .array(v.generations.map { .number($0) })]))
            }
        case let fn:
            throw FixtureFailure("unknown fn \(fn)")
        }
    }

    // MARK: compaction.json

    static func snapshots(_ v: JSONValue) throws -> [SnapshotInfo] {
        try v.array("snapshots").map { s in
            var covers: [String: Int64] = [:]
            for m in try s.object("covers") {
                guard let n = SyncFiles.asInt(m.value) else { throw FixtureFailure("covers") }
                covers[m.key] = n
            }
            return SnapshotInfo(hlc: try s.string("hlc"), covers: covers, ageOk: s["ageOk"]?.boolValue ?? false)
        }
    }

    static func seqMap(_ v: JSONValue?) throws -> [String: [Int64]] {
        guard case .object(let o)? = v else { return [:] }
        var out: [String: [Int64]] = [:]
        for m in o {
            out[m.key] = try (m.value.arrayValue ?? []).map { x in
                guard let n = SyncFiles.asInt(x) else { throw FixtureFailure("seq list") }
                return n
            }
        }
        return out
    }

    static func intMap(_ d: [String: Int64]) -> JSONValue {
        .object(JSONObject(d.keys.sorted(by: StringOrder.utf8Less).map { JSONMember($0, .number(d[$0]!)) }))
    }

    static func compaction(_ c: FixtureCase) throws {
        let i = c.input, e = c.expected
        switch try i.string("fn") {
        case "readPlan":
            var cursors: [String: Int64] = [:]
            for m in try i.object("cursors") { cursors[m.key] = SyncFiles.asInt(m.value) ?? 0 }
            let files = try seqMap(i["files"]), unreadable = try seqMap(i["unreadable"]), snaps = try snapshots(i)
            let plan = Compaction.readPlan(cursors: cursors, files: files, snapshots: snaps, unreadable: unreadable)
            let got = JSONObject([
                "mergeSnapshots": .array(plan.mergeSnapshots.map(JSONValue.string)),
                "readFiles": .object(JSONObject(plan.readFiles.keys.sorted(by: StringOrder.utf8Less).map {
                    JSONMember($0, .array(plan.readFiles[$0]!.map { .number($0) }))
                })),
                "cursorsAfter": intMap(plan.cursorsAfter),
                "gaps": intMap(plan.gaps),
            ])
            expectSame(.object(got), e, "read plan")
            // Merging every snapshot up front must reach the same cursors.
            var up = cursors
            for s in snaps { for (d, v) in s.covers { up[d] = max(up[d] ?? 0, v) } }
            let extra = Compaction.readPlan(cursors: up, files: files, snapshots: snaps, unreadable: unreadable)
            #expect(plan.cursorsAfter.allSatisfy { extra.cursorsAfter[$0.key] == $0.value }, "optional snapshot merges")
        case "snapshotAgeOk":
            func opt(_ k: String) throws -> Int64? { i[k] == nil || i[k] == .null ? nil : try i.int64(k) }
            let ok = Compaction.snapshotAgeOk(firstSeenLocalMs: try opt("firstSeenLocalMs"), localNowMs: try opt("localNowMs"),
                                              serverCreatedMs: try opt("serverCreatedMs"), serverNowMs: try opt("serverNowMs"))
            expectSame(.object(JSONObject(["ok": .bool(ok)])), e, "age")
        case "deletableChangeFiles":
            let me = try i.string("deviceId"), snaps = try snapshots(i)
            let seqs = try i.array("seqs").map { x in
                guard let n = SyncFiles.asInt(x) else { throw FixtureFailure("seqs") }
                return n
            }
            let got = Compaction.deletableChangeFiles(device: me, seqs: seqs, snapshots: snaps)
            expectSame(.object(JSONObject(["deletable": .array(got.map { .number($0) })])), e, "deletable files")
            let doomed = Set(Compaction.deletableSnapshots(snaps))
            let survivors = snaps.filter { !doomed.contains($0.hlc) }
            for n in got { #expect(survivors.contains { $0.covers(me) >= n }, "file \(n) would be lost") }
        case "deletableSnapshots":
            let snaps = try snapshots(i)
            let got = Compaction.deletableSnapshots(snaps)
            expectSame(.object(JSONObject(["deletable": .array(got.map(JSONValue.string))])), e, "deletable snapshots")
            let kept = snaps.filter { !got.contains($0.hlc) }
            for s in snaps where got.contains(s.hlc) {
                #expect(kept.contains { Compaction.dominates($0, s) }, "deleting \(s.hlc) loses coverage")
            }
        case let fn:
            throw FixtureFailure("unknown fn \(fn)")
        }
    }
}

/// HMAC-SHA256 via CryptoKit, for the independent HKDF cross-check.
enum HMACSHA256 {
    static func mac(key: [UInt8], message: [UInt8]) -> [UInt8] {
        Array(HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: key)))
    }
}
