# spec/

The contract between the FutureCheck apps (code name PayDay): Windows (C#), macOS and iOS (Swift),
Android (Kotlin) and web (TypeScript). These apps share no code. They agree because they follow
these documents and pass the same test fixtures.

| Path | What it is |
|---|---|
| `data-model.md` | Every synced entity and field, with type, units and meaning; ID rules; which settings sync; money rounding; how today's Windows schema maps onto it. |
| `sync-protocol.md` | Clocks, IDs, change records, merge rules, balance checkpoints, drive layout, file encryption, vault.json, recovery phrase, QR pairing, reading and writing, snapshots and compaction, setup flows. |
| `bip39-english.txt` | The BIP39 English wordlist used by the recovery phrase (see "Third-party files" below). |
| `conformance/core/` | Fixtures for pay periods, bill-to-period assignment, totals, payoff and the backup JSON format. Owned by the core-rules work; see the files there. |
| `conformance/sync/` | Fixtures for the sync layer: `hlc.json`, `ids.json`, `merge.json`, `crypto.json`, `recovery-phrase.json`, `files.json`, `compaction.json`. |

The design these documents implement is `planning/2026-11-14-multiplatform-design.md` (sections 6
to 8) with the v1 features from `planning/2026-11-14-feature-scope.md`. Where the design left a
choice open, the spec says "(decided here)".

## The rule

**A rule or format change lands in `spec/` and its fixtures first, then in each app.**

In practice:

1. Change the document (`data-model.md` or `sync-protocol.md`).
2. Change or add fixtures, and make the reference checker pass.
3. Only then change the apps. Each app's tests load the fixtures, so an app that has not caught up
   fails its own tests instead of silently disagreeing with the others.

Never "fix" a fixture to match an app. If an app and a fixture disagree, either the app is wrong or
the spec is; decide which in the spec first.

## Fixture files

Every fixture file has the same top-level shape:

```json
{
  "version": 1,
  "description": "What this file covers, and which spec section it tests.",
  "cases": [
    {
      "name": "unique, readable name of the case",
      "note": "optional: why the expected value is what it is",
      "input": { "fn": "encode", "...": "..." },
      "expected": { "...": "..." }
    }
  ]
}
```

- `version` is the version of the fixture file's shape, not of the sync format. It changes only if
  the case shape changes.
- `name` is unique within a file. Test runners should report failures by name.
- `note` is optional and is for humans only.
- `input.fn` names the operation under test. Each file lists its operations below.
- `expected` holds either the result fields, or `{ "error": "<code>" }` when the operation must
  fail. Error codes are camelCase strings and are part of the contract: an app must fail with the
  same code, not just fail.

### Value conventions in fixtures

- Byte strings are lowercase hex (`...Hex` keys), with no separators.
- Text is UTF-8. `plaintextUtf8` holds the exact plaintext bytes as a JSON string.
- Numbers compare by value (`1200` equals `1200.0`). Money results (`remaining`,
  `setAsideBalance`) compare after rounding to cents (`data-model.md` section 3).
- Booleans never equal numbers (`true` is not `1`).
- Lists are ordered unless the file says otherwise. Where a list is a set (for example `live`), the
  fixture already lists it sorted by UTF-8 bytes, and apps should sort before comparing.
- Timestamps in fixtures are fictional and start at `2026-11-14T12:00:00.000Z` (Unix ms
  `1794657600000`, HLC wall `01a1d9edc200`).
- The repository is public. Every name and amount in a fixture is fictional (for example
  "Woodgrove Rewards", 1200.00). Keep it that way.

### Operations in `conformance/sync/`

| File | `input.fn` values |
|---|---|
| `hlc.json` | `encode`, `decode`, `compare`, `tick`, `observe`, `sequence`, `skewWarning` |
| `ids.json` | `regularPaymentId`, `setAsideId`, `paycheckId`, `paycheckIdKind`, `legacyPaymentId`, `legacySnapshotId`, `migrateBillId`, `uuid7`, `inspectUuid`, `isDeviceId` |
| `crypto.json` | `deriveKeys`, `derivePairingKey`, `seal`, `open`, `matchGeneration`, `pairingQr`, `parsePairingQr` |
| `recovery-phrase.json` | `encode`, `decode` |
| `files.json` | `changeFileName`, `parsePath`, `checkEnvelope`, `checkVault` |
| `merge.json` | `merge` |
| `compaction.json` | `readPlan`, `snapshotAgeOk`, `deletableChangeFiles`, `deletableSnapshots` |

`checkEnvelope` gives the decrypted payload in exactly one of three forms: `payload` (an already
parsed JSON object), `payloadText` (the exact JSON text, as a string) or `payloadHex` (the exact
bytes, for cases that are not valid UTF-8 or start with a byte-order mark). `payloadText` and
`payloadHex` MUST go through the app's strict JSON parsing (`sync-protocol.md` section 6.3) before the
envelope checks; that is how the duplicate-key, lone-surrogate, BOM and `3e0` cases are tested.
`checkVault` likewise takes `vault` (object) or `vaultText` (string).

`merge.json` cases have `input.records` (change records) and, for writer-side rules, an
`input.edit`:

```json
"edit": {
  "seen": [0, 1, 2],
  "op": { "op": "setRemaining", "billId": "...", "value": 1000.0,
          "source": "notion", "lastPushed": 1000.0, "hlc": "..." }
}
```

`seen` lists the indexes of `input.records` the editing device had merged when the user acted. The
app runs `op` against that state; the records it writes must equal `expected.emitted` exactly
(order: sorted by entity, then id, then field). `op` values are `markPaid`, `unmarkPaid`,
`deletePayment`, `setRemaining` (`sync-protocol.md` section 5.3) and `restore` (section 12.3).
`restore` carries the backup as `rows` (`{entity: {id: {field: value}}}`, IDs already migrated,
each bill's Remaining already mapped to `remainingCheckpoint`) and `settings` (`{key: value}`).

`expected` in a merge case is the state after merging `input.records` plus `expected.emitted`:

| Key | Meaning |
|---|---|
| `rows` | The winning value of every (entity, id, field) that has at least one valid record, as `{entity: {id: {field: value}}}`. Includes tombstoned, pending and unknown rows. |
| `live` | For every entity in `rows`, the sorted IDs of live rows. |
| `remaining` | Computed Remaining for every live bill. |
| `setAsideBalance` | Computed set-aside balance for every live bill. |
| `emitted` | Present only with `input.edit`: the records the edit writes. |

**A merge case must produce `expected` for every delivery order of its records.** Test runners
should at least run the given order, the reverse order and a few dozen seeded shuffles, and apply
some records twice. The reference checker runs every permutation for up to 8 records and 2000
seeded permutations for larger cases.

## Reference implementation (`tools/spec/`)

A small Python implementation exists only to generate and check the fixtures in
`conformance/sync/`. It is not shipped and is not a fifth app.

```bash
# Python 3.9+; keep the venv outside the repo (it is not git-ignored)
python3 -m venv ~/.venvs/futurecheck-spec
~/.venvs/futurecheck-spec/bin/pip install -r tools/spec/requirements.txt
~/.venvs/futurecheck-spec/bin/python tools/spec/gen_fixtures.py    # writes spec/conformance/sync/*.json
~/.venvs/futurecheck-spec/bin/python tools/spec/check_fixtures.py  # re-derives every expected value; exit 0 = pass
```

- `gen_fixtures.py` is the source of the sync fixtures: change cases there, regenerate, and commit
  the JSON. Do not hand-edit the generated JSON. Merge scenarios carry a hand-written expectation
  that the reference merge must reproduce before the file is written.
- `check_fixtures.py` reads only the JSON files and re-derives every expected value. It also
  cross-checks against independent code: Python's `uuid` module for UUIDv5 and UUIDv7, an
  HMAC-only HKDF, the published BIP39 test vectors, and byte-wise string order for HLCs.
- Modules: `fc_hlc.py`, `fc_ids.py`, `fc_money.py`, `fc_frame.py`, `fc_bip39.py`, `fc_merge.py`,
  `fc_files.py`. Each names the spec section it implements.

## Loading fixtures from an app's tests

Read the files from the repository at test time: find `spec/conformance/` by walking up from the
test binary's directory, as `apps/windows/PayDay.Tests/Conformance/ConformanceFixtures.cs` does.
Do not copy them into an app folder or the build output. A copy goes stale: MSBuild's
`CopyToOutputDirectory="PreserveNewest"`, for example, leaves a deleted or renamed fixture in
`bin/` until a clean build, so the tests keep passing on a file that no longer exists. Iterate over
`cases`, dispatch on `input.fn`, and report each case by `name`. A new case added to a file should
be picked up without changing the test code.

## Third-party files

`bip39-english.txt` is the BIP39 English wordlist from the Bitcoin BIPs repository
(`https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt`). BIP-0039 is under the MIT
License (its header says `License: MIT`, and its Copyright section says "This BIP falls under the
MIT License"; authors Marek Palatinus, Pavol Rusnak, Aaron Voisine, Sean Bowe). Store listings
and an app's open-source notices should credit it. It is included unchanged: 2048 lines, LF line
endings, SHA-256
`2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda`. Apps should embed this
exact file and check its hash in a test.
