import CryptoKit
import Foundation
import Testing
@testable import PayDayKit

/// Checks around the conformance suite itself and the embedded third-party file.
@Suite("Package checks")
struct PackageChecks {
    /// A new fixture file must get a runner, or this fails (spec/README.md: new cases are picked
    /// up without code changes; new files need one line here).
    @Test func everyFixtureFileHasARunner() {
        #expect(Fixtures.conformanceDirectory != nil, "spec/conformance not found")
        #expect(Fixtures.fileNames(in: "core") == Fixtures.coreFiles.sorted())
        #expect(Fixtures.fileNames(in: "sync") == Fixtures.syncFiles.sorted())
    }

    /// spec/README.md: apps embed bip39-english.txt unchanged and test its hash.
    @Test func embeddedWordlistIsTheSpecFile() throws {
        let embedded = try #require(RecoveryPhrase.embeddedWordlistBytes)
        #expect(Hex.encode(SHA256.hash(data: embedded)) == RecoveryPhrase.wordlistSHA256)
        let specCopy = try Data(contentsOf: try #require(Fixtures.specDirectory).appendingPathComponent("bip39-english.txt"))
        #expect(Array(specCopy) == embedded, "the package copy must equal spec/bip39-english.txt byte for byte")
        #expect(try RecoveryPhrase.words().count == 2048)
    }

    /// The rounding table in spec/data-model.md section 3.1.
    static let roundingTable: [(Double, Int64)] = [
        (2.675, 268), (1.005, 100), (1.115, 112), (0.285, 28), (33.335, 3334), (-2.675, -268), (0.1 + 0.2, 30), (19.999, 2000),
    ]

    @Test(arguments: roundingTable)
    func moneyRoundingTable(_ x: Double, _ cents: Int64) throws {
        #expect(try Money.cents(x) == cents)
        #expect(try Money.round2(x) == Double(cents) / 100)
    }

    /// Lone-surrogate shapes the fixtures do not reach: a lead surrogate followed by an escape
    /// that is not a trail surrogate, and a trail surrogate on its own. All are `malformed`.
    @Test(arguments: [#""\ud800A""#, #""\ud800\ud800""#, #""\udc00""#, #""x\udfffy""#])
    func loneSurrogateEscapesAreMalformed(_ json: String) {
        let payload = #"{"type":"changes","formatVersion":1,"vaultId":"019a0000-0000-7000-8000-00000000fa01","deviceId":"0a0a0a0a0a0a0a0a","seq":3,"records":[],"note":"# + json + "}"
        #expect(throws: SyncError.malformed) { try SyncFiles.parsePayload(Array(payload.utf8)) }
        #expect(throws: JSONParseError.loneSurrogate) { try JSONParser.parseLenient(json) }
    }

    /// A backup whose bills, payments or snapshots list holds a `null` entry is rejected (today's
    /// C# reader would crash on it later instead).
    @Test(arguments: ["bills", "payments", "snapshots"])
    func backupWithNullEntryIsRejected(_ key: String) {
        #expect(throws: BackupError.wrongType("\(key)[0]")) {
            try BackupFormat.parse(text: #"{"formatVersion":1,"exportedAt":"","# + "\"\(key)\":[null]}")
        }
    }

    @Test func moneyRangeIsEnforced() {
        #expect(throws: MoneyError.outOfRange) { try Money.cents(1e13) }
        #expect(throws: MoneyError.outOfRange) { try Money.cents(.nan) }
    }

    /// A store's snapshot records merge back into the same state, and a sealed change file opens
    /// and passes its envelope checks.
    @Test func changeFileRoundTrip() throws {
        let device = try #require(DeviceID("0a0a0a0a0a0a0a0a"))
        var clock = HLCClock(device: device)
        let hlc = try clock.tick(now: 1_794_657_600_000)
        let billId = "019a0000-0000-7000-8000-0000000000b1"
        var bill = BillEntity(id: billId)
        bill.name = "Woodgrove Rewards"
        bill.remainingCheckpoint = 1200
        let records = try WriterRules.create(entity: Entity.bill, id: billId, fields: try bill.fieldValues(), hlc: hlc)
        var store = InMemoryRecordStore(merging: records)
        #expect(store.bills().first?.name == "Woodgrove Rewards")
        #expect(store.remainingCents(bill: billId) == 120_000)

        let vaultId = "019a0000-0000-7000-8000-00000000fa01"
        let key = [UInt8](repeating: 7, count: 32)
        let path = SyncPath.changes(deviceId: device, seq: 1).path
        let plaintext = try SyncFiles.changesPayload(vaultId: vaultId, deviceId: device, seq: 1, records: store.allRecords())
        let file = try FileCrypto.seal(secret: key, kind: .changes, generation: 1, path: path, plaintext: plaintext)
        let opened = try FileCrypto.open(file, path: path, vaultKeys: [1: key])
        let payload = try SyncFiles.parsePayload(opened)
        let checked = try SyncFiles.checkEnvelope(payload, path: path, vaultId: vaultId)
        var other = InMemoryRecordStore()
        other.merge(SyncFiles.mergeableRecords(payload, checked: checked))
        #expect(other == store)

        let paid = try WriterRules.markPaid(store, billId: billId, periodKey: "2026-11-12", amount: 55,
                                            paidAt: .string("2026-11-14T12:01:40.000Z"), hlc: try clock.tick(now: 1_794_657_600_000))
        store.merge(paid)
        #expect(store.remainingCents(bill: billId) == 114_500)
    }
}
