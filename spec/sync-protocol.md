# FutureCheck sync protocol

**Format:** sync format 1 · **Date:** 2026-11-14 · **Status:** draft for review

How FutureCheck devices share data through the user's own cloud drive, encrypted end to end. This
document is precise enough that the Windows, Apple, Android and web apps, written separately,
read and write each other's files byte for byte. The data being synced is defined in
`data-model.md`. Test vectors are in `conformance/sync/`.

"(decided here)" marks a choice the design (`planning/2026-11-14-multiplatform-design.md`, sections
6 and 10) left to the builder. MUST, MUST NOT, SHOULD and MAY are used in their RFC 2119 sense.

**Contents:** 1 Devices · 2 Hybrid logical clock · 3 IDs · 4 Change records and merge ·
5 Balance checkpoints and payments · 6 Files and encryption · 7 vault.json and key generations ·
8 Recovery phrase · 9 QR pairing · 10 Writing and reading · 11 Snapshots and compaction ·
12 Flows · 13 Security notes · 14 Error codes · 15 Decisions made here

**String matching (applies everywhere in this spec and `data-model.md`).** Every format that a
string must have (HLC, device ID, UUID, date, timestamp, hex key or key check, `#RRGGBB` colour,
decimal or digit text, file and path names, the pairing QR text) is matched against the **whole
string**, using **ASCII only**: a digit is `0`–`9`, hex is `0`–`9` and `a`–`f` (plus `A`–`F` only
where a format says so). Nothing is trimmed, and nothing is case-folded or digit-converted, unless a
section says so explicitly (only the recovery phrase, section 8, and the pairing QR, section 9.1).
So `"<hlc>\n"` is not an HLC, and `٢٠٢٦-١١-١٢` (Arabic-Indic digits) is not a date. Beware regex
`$` (in Python, Java and .NET it also matches before a final newline; use `\z`, `\Z` in Python,
or a full-match function) and `\d` (matches non-ASCII digits in .NET and in Python); likewise,
parse dates and numbers only after the ASCII pattern has matched. Fixtures: trailing-newline and
non-ASCII-digit cases in `hlc.json`, `ids.json`, `files.json`, `crypto.json` and `merge.json`
(decided here).

---

## 1. Devices

- Each device in a vault has a **device ID**: 8 bytes from a cryptographically secure random
  generator, written as **16 lowercase hex characters**, for example `9c2e44f0a1b3d5e7`
  (decided here). Regex: `^[0-9a-f]{16}$`.
- A device ID is created every time a device creates or joins a vault, and is never reused. A
  device that leaves and rejoins, or whose local data was wiped (a browser clearing storage), gets
  a new ID. This guarantees that a device ID's sequence numbers (section 10.1) never restart.
- A device writes only inside `devices/<its ID>/` (plus snapshots and pairing files, sections 9
  and 11).
- Each device writes a `device` row about itself (`data-model.md` section 4.6) in its first change
  file.

Fixtures: `ids.json` (`isDeviceId`).

## 2. Hybrid logical clock

Every change record carries an HLC: wall-clock milliseconds plus a counter plus the device ID. The
three parts give a total order that every device computes the same way, even when device clocks
disagree.

### 2.1 Encoding (decided here)

```
<wall>-<counter>-<deviceId>
wall      48-bit unsigned Unix time in milliseconds (UTC), 12 lowercase hex digits, zero-padded
counter   16-bit unsigned, 4 lowercase hex digits, zero-padded
deviceId  16 lowercase hex digits (section 1)
```

Always exactly 34 ASCII characters. Regex: `^[0-9a-f]{12}-[0-9a-f]{4}-[0-9a-f]{16}$`. Anything
else, including uppercase, is malformed.

Example: `01a1d9edc200-0000-0a0a0a0a0a0a0a0a` is 2026-11-14T12:00:00.000Z (1794657600000 ms),
counter 0, device `0a0a0a0a0a0a0a0a`.

### 2.2 Comparison

Compare `(wall, counter, deviceId)` lexicographically: wall first, then counter, then device ID
(as a 64-bit unsigned number, or equivalently as a hex string). Because every part is fixed-width
lowercase hex, **this is the same as comparing the two strings byte by byte**, and apps MAY do
exactly that. Two HLCs are equal only if all three parts are equal.

### 2.3 Clock state and updates

Each device stores one HLC string as its clock state (initially none). The device-ID part of the
state is always the device's own ID.

**tick** (a local edit; returns the HLC to stamp it with, and becomes the new state):

```
tick(last, now):            # now = physical clock, Unix ms
    if last is none: return (now, 0, me)
    w = max(now, last.wall)
    if w == last.wall:
        c = last.counter + 1
        if c > 0xFFFF: w = w + 1; c = 0        # carry into the wall time
    else:
        c = 0
    if w > 2^48 - 1: fail ("wallOutOfRange")
    return (w, c, me)
```

**observe** (after merging a record stamped `remote`; never increments):

```
observe(last, remote):
    if last is none or (remote.wall, remote.counter) > (last.wall, last.counter):
        return (remote.wall, remote.counter, me)
    return last
```

One user action (save a bill, mark paid, restore a backup) takes **one** tick, and every record it
writes carries that HLC. Different actions on one device always get different HLCs.

**Persistence (MUST).** The new clock state is saved in the **same local transaction** as the records
stamped with it (section 10.1), and the state after `observe` is saved in the same transaction as
the merge. Otherwise a crash could leave records stamped with an HLC the saved clock never reached;
after a restart the device would issue that HLC again for a different edit, and two different values
of one field would tie on HLC (resolved only by value order, not by which edit came last). As a
safety net, on startup a device MAY set its state to the maximum of the saved state and every HLC in
its local store that carries its own device ID.

Because `tick` takes the maximum of the physical clock and everything observed, an edit made after
seeing another device's edit always sorts after it, whatever either clock says.

Fixtures: `hlc.json` (`encode`, `decode`, `compare`, `tick`, `observe`, `sequence`).

### 2.4 Clock skew (decided here)

- Merging is never conditional on the local clock. Every device accepts every valid record and
  observes its HLC, so all devices converge on the same state. (Rejecting records that look "too
  far in the future" would make the result depend on which device's clock is right, and devices
  would disagree.)
- A device whose clock runs ahead wins concurrent edits for the size of its skew. That is the
  price of convergence and is acceptable for one person's devices.
- When a merged record's wall time is more than **10 minutes** ahead of the local physical clock
  (`remote.wall - now > 600000`), the app SHOULD show a non-blocking warning naming the device
  ("<device name>'s clock is ahead; check its date and time"). Fixture: `hlc.json`
  (`skewWarning`).
- `paidAt` and `createdAt` are wall-clock timestamps for display. They are never used to order
  anything.

## 3. IDs

### 3.1 UUIDv7 (random IDs)

New bills, extra payments, balance snapshots, income sources, paychecks of `Irregular` income
sources, and the vault ID are UUIDv7
(RFC 9562), written canonically: lowercase hex, `8-4-4-4-12`, 36 characters.

Layout, from a 48-bit Unix-ms timestamp `t` and 10 random bytes `r[0..9]` (decided here, so the
fixtures can pin it):

```
bytes 0-5   t, big-endian
byte  6     0x70 | (r[0] & 0x0F)     # version 7
byte  7     r[1]
byte  8     0x80 | (r[2] & 0x3F)     # RFC 9562 variant
bytes 9-15  r[3..9]
```

`r` comes from a cryptographically secure generator. Apps MAY use a platform UUIDv7 generator
instead if it produces this layout. Nothing reads the timestamp back except for sorting in UIs.

### 3.2 UUIDv5 (deterministic IDs)

**Namespace UUID (decided here, generated once on 2026-11-14):**

```
def9d7a0-dc52-41e0-906a-f9767c7f894d
```

`id = UUIDv5(namespace, UTF-8 bytes of name)`, as in RFC 9562 section 5.5: SHA-1 of the 16
namespace bytes followed by the name bytes, first 16 bytes, version nibble 5, variant `10`.

| What | Name string | Example name | Example ID |
|---|---|---|---|
| Regular payment | `payment/regular/<billId>/<periodKey>` | `payment/regular/019a0000-0000-7000-8000-0000000000b1/2026-11-12` | `92c8e9ed-5f91-50a0-b203-103ace1a06d1` |
| Set-aside | `payment/setaside/<billId>/<periodKey>` | `payment/setaside/019a0000-0000-7000-8000-0000000000b1/2026-11-12` | `8e36e8cb-d746-53f7-a422-0c29259e73b0` |
| Paycheck (scheduled cadences only) | `paycheck/<incomeSourceId>/<payDate>` | `paycheck/019a0000-0000-7000-8000-0000000000c1/2026-11-20` | `c5e2bf05-c3ee-50e9-b38e-3c77c6e4d073` |
| Legacy bill | `legacy-bill/<oldId>` | `legacy-bill/13` | `ac253b5d-8ef2-5489-a207-2d390f5a7d44` |
| Migrated extra payment | `legacy-payment/<billId>/<periodKey>/<amountCents>/<paidAt>/<n>` | `legacy-payment/019a0000-0000-7000-8000-0000000000b1/2026-10-29/5500/2026-10-30T15:04:05.000Z/2` | `c47e884c-31ab-57d2-bac6-f13041ee53ca` |
| Migrated balance snapshot | `legacy-snapshot/<snapshotDate>/<totalCents>/<n>` | `legacy-snapshot/2026-10-29/812345/1` | `5315fa02-281f-5445-8d09-a0a76cf87e1f` |

Rules:

- `<billId>` and `<incomeSourceId>` MUST be canonical lowercase UUIDs. Derivation fails
  (`badInput`) on anything else; canonicalize or migrate first.
- `<periodKey>` and `<payDate>` MUST be valid zero-padded `YYYY-MM-DD` dates; otherwise
  `badInput`.
- `<oldId>` is the legacy bill ID exactly as stored (`"1"` to `"27"` for seeded bills). An ID that
  is already UUID-shaped is not a legacy ID; it is lowercased instead (`data-model.md`
  section 8.2).
- **Paychecks:** the deterministic ID is used only when the income source's `cadence` is `Weekly`,
  `Bi-Weekly`, `Semi-Monthly` or `Monthly` when the paycheck is recorded (decided here). For
  `Irregular` (or an unknown cadence) every recorded payout gets a random UUIDv7, so two payouts on
  one day stay two rows. Fixture: `ids.json` (`paycheckIdKind`).
- **Migration names** (`data-model.md` sections 8.3, 8.4) are built from the row's content so two
  devices migrating or restoring the same legacy data produce the same IDs: `<amountCents>` and
  `<totalCents>` are `cents()` of the amount as a decimal integer (`5500`, `-120`); `<paidAt>` is the
  normalized timestamp, or empty when the legacy row had none; `<n>` is 1 for the first row with that
  exact content (by old integer `Id`), 2 for the second, and so on.
- No other separators, padding, case changes or Unicode normalization.

Fixtures: `ids.json`.

## 4. Change records and merge

### 4.1 The change record

Every synced edit is a set of change records, one per field:

```json
{ "entity": "bill",
  "id": "019a0000-0000-7000-8000-0000000000b1",
  "field": "name",
  "value": "Woodgrove Rewards",
  "hlc": "01a1d9edc200-0000-0a0a0a0a0a0a0a0a" }
```

| Key | Type | Meaning |
|---|---|---|
| `entity` | string, non-empty | Entity name from `data-model.md` (`bill`, `payment`, `balanceSnapshot`, `incomeSource`, `paycheck`, `device`, `setting`), or one an older app does not know. |
| `id` | string, non-empty | Row ID. |
| `field` | string, non-empty | Field name, including `deleted`. |
| `value` | null, boolean, number or string | The new value. MUST be present; `null` is explicit. Objects and arrays are not allowed. Numbers MUST be finite. |
| `hlc` | string | A valid HLC (section 2.1). Its device part is the device that made the edit; there is no separate device field. |

A record that breaks any rule in the table is **invalid** and is ignored (not stored, not
observed). Extra keys in a record are ignored, so later versions may add some. Fixture:
`merge.json`, "invalid records are ignored".

### 4.2 Last write wins, per field

The merged state holds, for every (entity, id, field), one winning (value, hlc). To merge a valid
record `r`:

```
cur = state[r.entity, r.id, r.field]
if cur is none
   or r.hlc > cur.hlc
   or (r.hlc == cur.hlc and valueOrder(r.value) > valueOrder(cur.value)):
    state[r.entity, r.id, r.field] = (r.value, r.hlc)
```

`valueOrder` only matters when two records have the identical HLC, which a correct writer never
produces (one action never writes one field twice). It exists so even a buggy writer cannot make
devices diverge (decided here):

```
null < false < true < any number < any string
numbers: by numeric value
strings: by their UTF-8 bytes, lexicographically
```

This merge is commutative, associative and idempotent: the same set of records gives the same
state in any order, and merging a record twice changes nothing. That is the property every
`merge.json` case tests in every delivery order.

### 4.3 Tombstones

Deleting writes `deleted: true` as an ordinary field. Undeleting writes `deleted: false`. Other
fields of a deleted row keep merging (an edit on a device that has not seen the delete is kept),
but they do not bring the row back. Liveness is defined in `data-model.md` section 1.4: live means
the winning `deleted` value is `false`; a row with no `deleted` record is pending and not shown.

Tombstones are never purged in format 1 (decided here). The data is small, and purging safely
needs every device to have seen the tombstone.

### 4.4 What a writer emits

- **Create:** one tick; a record for every field of the entity, with defaults for fields the user
  left empty, plus `deleted: false`.
- **Edit:** one tick; a record only for each field whose value changed. Money compares in cents.
  For `remainingCheckpoint` this is a MUST (section 5.3); for other fields it is a SHOULD.
- **Delete / undelete:** one tick; one `deleted` record.
- A writer MUST apply its own records to its local state with the same merge function, so its
  view equals what other devices will compute.

### 4.5 Unknown entities and fields

Records for entities or fields an app does not know (written by a newer version) MUST be merged and
stored like any other, kept through restarts, and included in snapshots (section 11). They are not
shown. Dropping them would make an older device erase newer data when it writes a snapshot.
Fixture: `merge.json`, "unknown entities and fields...".

## 5. Balance checkpoints and payments

Design section 6.2 replaces "Remaining stored and decremented in place" with a checkpoint plus
payments, so offline payments on two devices are never lost to last-write-wins.

### 5.1 The rule

- Each bill stores `remainingCheckpoint` (`data-model.md` section 4.1). The **checkpoint HLC** is
  the HLC of that field's winning record.
- `Remaining = max(0, checkpoint − sum of Regular and Extra payments whose mark HLC is after the
  checkpoint HLC)`, in integer cents, as defined exactly in `data-model.md` section 5.1.
- The **mark HLC** of a payment is the HLC of its winning `deleted` record. While the payment is
  live that record says `false`.
- "After" is **strictly greater**. A payment whose mark HLC is at or before the checkpoint HLC is
  **absorbed**: the checkpoint already includes it. A payment with the same HLC as the checkpoint
  (as after a migration or a restore, section 12) is absorbed.
- Wall-clock `paidAt` is never used.
- Set-aside payments never lower Remaining.

**Refinement of design section 6.2.** The design says "a payment that was unmarked and marked again
orders by the HLC of its latest mark". On its own that subtracts a payment twice when it was
absorbed by a checkpoint, unmarked, and marked again (Remaining 1000, mark 100, type statement 880,
unmark, mark again gave 780). The derivation above is unchanged; section 5.3 adds two writer
rules so that it holds: unmarking an absorbed payment gives its amount back to the checkpoint, and
"mark paid" on a payment that is already live never rewrites `deleted`, so it never moves the mark
HLC.

Fixtures: `merge.json` (checkpoint cases).

### 5.2 Payments merge as a set

- **Regular** ("mark paid"): ID from (billId, periodKey) (section 3.2). Two devices marking the same
  bill paid in the same period write the same row; its fields resolve per field.
- **SetAside**: ID from (billId, periodKey) with the `setaside` name, so marking a set-aside done on
  two devices gives one row.
- **Extra**: random UUIDv7. Every extra payment is its own row; they add up.

### 5.3 Writer rules (the `edit` ops in `merge.json`)

`hlc` below is the result of one `tick` on the editing device, and "live", "absorbed" and
"Remaining" are evaluated on that device's state at that moment.

**markPaid** (kind `Regular` or `SetAside`, bill, period, amount, paidAt), for the deterministic ID:

- If the payment is **already live** on this device (a second "mark paid", or an amount change):
  emit only `amountPaid = round2(amount)` and `paidAt`, at `hlc`. Do not write `deleted`, `billId`,
  `kind` or `periodKey`.
- Otherwise (new, or re-marking after an unmark): emit all six, at `hlc`:

  ```
  amountPaid = round2(amount)   billId = bill   deleted = false
  kind = kind                   paidAt = paidAt periodKey = period
  ```

`confirmationNumber`, `note` and `notionPageId` are not touched. An `Extra` payment is a create
(section 4.4) with a new UUIDv7; editing it later is an ordinary edit (no `deleted` write).

**unmarkPaid** (kind, bill, period) and **deletePayment** (payment ID, for any kind):

```
if the payment is not live: emit nothing
emit payment.deleted = true at hlc
if the payment's kind counts against Remaining (Regular or Extra, data-model.md 4.2)
   and its bill is live
   and the payment is absorbed (mark HLC <= checkpoint HLC):
    u = cents(checkpoint) − (counted payments after the checkpoint)   # Remaining before the clamp
    emit bill.remainingCheckpoint = max(0, u + cents(amountPaid)) / 100 at the same hlc
```

The new checkpoint uses the unclamped value, so a Remaining that was showing 0 because of an
overpayment comes back correctly (fixture: "unmark while Remaining is clamped at zero..."). Writing
the checkpoint at `hlc` absorbs every payment this device currently counts, which is correct because
`u` already subtracted them.

**setRemaining** (bill, value, source, lastPushed): the user, or the Notion connector, sets the
bill's Remaining to `value`.

```
if the bill is not live: emit nothing
v = cents(value)                     # value must be >= 0
if source == "notion" and lastPushed is set and cents(lastPushed) == v:
    emit nothing                     # echo: Notion is showing what the connector wrote
if v == cents(Remaining(bill)) as computed on this device now:
    emit nothing                     # unchanged value
otherwise emit remainingCheckpoint = v / 100 at hlc
```

- `source` is `"app"` for the bill editor and `"notion"` for the connector's pull.
- `lastPushed` is the per-device, per-bill Remaining the connector last wrote to Notion
  (`data-model.md` section 6.2). The connector records it every time it pushes a bill.
- Both "emit nothing" rules exist because a spurious checkpoint silently absorbs any offline
  payment from another device that this device has not merged yet. Fixtures: `merge.json`, "echo",
  "app edit to the current computed value", "Notion value equal to the current computed value".

**restore**: section 12.3.

### 5.4 Known limitations

A checkpoint means "the balance is this, as of now, on this device". It cannot know about payments
or unmarks this device has not merged yet. These cases are wrong after merging and are fixed by the
user typing the correct Remaining. They need two devices acting on the same bill while out of touch,
around a checkpoint. Every row has a `merge.json` case; its name is in the last column.

| Case | Result | `merge.json` case |
|---|---|---|
| A real Remaining edit (app or Notion statement figure) while another device has an unmerged payment | The payment is absorbed: Remaining is high by its amount. This is the design's rule (design section 6.2) and the reason for the echo and unchanged-value rules. | "known limitation: a checkpoint set after an offline payment absorbs it" |
| Device A marks a payment and then sets a checkpoint; device B, which never saw A's mark, marks the same bill and period | B's write is the latest mark and is after the checkpoint, so the payment is subtracted again: Remaining is low by its amount. | "known limitation: a device that missed the mark re-marks after a checkpoint" |
| Device A marks a payment and sets a checkpoint; device B, which saw the mark but not the checkpoint, unmarks it | B does not see the payment as absorbed, so it writes no checkpoint: Remaining is low by its amount. | "known limitation: a device that missed the checkpoint unmarks an absorbed payment" |
| Unmarking an absorbed payment raises the checkpoint at the unmark's HLC | Like any checkpoint, it also absorbs payments from other devices this device has not merged yet: Remaining is high by their amount. | "known limitation: unmarking an absorbed payment also absorbs another device's unmerged payment" |
| A checkpoint (a restore, or any real edit) absorbed a payment its writer never saw; later a device unmarks that payment | The payment is at or before the checkpoint HLC, so it counts as absorbed and unmark raises the checkpoint by its amount, although it had never lowered Remaining: Remaining is high by its amount. | "known limitation: unmarking a payment the restore never saw raises Remaining" |

A rule that fixed the second, third and fifth cases would need each payment to remember every mark
and unmark, and each checkpoint which payments it actually saw; per-field last-write-wins keeps only
the winner. That is not worth
the format cost for a one-person app where the fix is to type the balance.

## 6. Files and encryption

### 6.1 Drive layout

```
<provider app folder>/PayDay/
├── vault.json                              plaintext; section 7
├── devices/
│   └── <deviceId>/
│       ├── 000001.chg                      encrypted change file
│       └── 000002.chg
├── snapshots/
│   └── <hlc>.snap                          encrypted snapshot; section 11
└── pairing/
    └── <pairingId>.pair                    encrypted, short-lived; section 9
```

- The vault root folder is named `PayDay` as in the design. Paths in this spec are relative to it,
  with `/` separators and no leading slash.
- Provider app folders: OneDrive `approot`, Google Drive `appDataFolder`, Dropbox App folder
  (design section 6.3).
- A snapshot name `<hlc>.snap` is the design's `<hlc>-<deviceId>.snap`: the HLC string already
  ends with the writer's device ID.

### 6.2 File names

| File | Name | Valid when |
|---|---|---|
| Change file | `<seq>.chg` | `seq` is an integer ≥ 1 written in decimal, zero-padded to at least 6 digits (`000001.chg`, `999999.chg`, `1000000.chg`). The name MUST equal that formatting of its own number: `00001.chg`, `0000001.chg`, `000000.chg` and `000001.CHG` are not change files. |
| Snapshot | `<hlc>.snap` | `<hlc>` is a valid HLC (section 2.1). |
| Pairing | `<pairingId>.pair` | `<pairingId>` is 32 lowercase hex characters. |

Readers ignore any other file (temporary uploads, provider metadata). Fixtures: `files.json`
(`changeFileName`, `parsePath`).

### 6.3 Plaintext payload

The plaintext inside every encrypted file is one JSON object (RFC 8259), UTF-8. Writers SHOULD
write it compactly. Key order is free: **nothing in this protocol hashes or byte-compares JSON**
(decided here); readers parse it. Money values follow `data-model.md` section 3 (rounded to cents
before writing).

**Strict parsing.** A reader MUST treat the payload as `malformed` (and not merge any of it) if it:

- starts with a UTF-8 byte-order mark, or is not valid UTF-8;
- is not exactly one JSON value (whitespace around it is fine; anything else after it is not);
- contains an object with the same key twice, at any depth (many parsers, including `JSON.parse`,
  keep the last one silently; use a parser or a pass that detects duplicates);
- contains a string escape that leaves an unpaired UTF-16 surrogate (`"\ud800"` alone; a valid pair
  such as `"\ud83d\ude00"` is fine);
- contains `NaN`, `Infinity` or other non-JSON tokens;
- contains a number that overflows binary64 (`1e400`, or an integer with hundreds of digits). Every
  JSON number in this protocol is the IEEE-754 binary64 value it parses to (round to nearest, as
  `JSON.parse` does); a value that cannot be represented cannot be merged the same way everywhere.

The root of a payload and of `vault.json` MUST be a JSON object; an array, string, number, `true`,
`false` or `null` root is `malformed`.

The same rules apply to `vault.json`. Fixtures: `files.json` (`payloadText`, `payloadHex`,
`vaultText` cases).

**Integer fields** (`formatVersion`, `seq`, `covers` values, `generation`, vault.json generation
`id` and `currentGeneration`): writers MUST write plain digits (`3`, never `3.0` or `3e0`). Readers
MUST accept any JSON number that is an integer within ±(2^53 − 1) **after it is parsed to an IEEE-754
binary64 value** (round-to-nearest, as `JSON.parse` does), as that integer: `3`, `3.0`, `3e0`,
`30e-1`, `3.0000000000000001` and `2.9999999999999999` all parse to exactly 3; `3.000000000001` does
not, and `1e400` overflows and is rejected. This is because JavaScript cannot tell `3` from `3.0`,
so the rule has to be one every platform can apply to a parsed double. Readers MUST
reject booleans, strings, `null` and non-integral numbers in these fields as `malformed` (Python and
some other languages treat `true` as 1; that is not allowed here). A `formatVersion` that is a valid
integer other than 1 is `unsupportedFormat`. The same reading rule applies to `int` fields inside
records (`data-model.md` section 1.1).

**Change file** (`devices/<deviceId>/<seq>.chg`):

```json
{ "type": "changes",
  "formatVersion": 1,
  "vaultId": "019a0000-0000-7000-8000-00000000fa01",
  "deviceId": "0a0a0a0a0a0a0a0a",
  "seq": 1,
  "records": [ { "entity": "bill", "id": "...", "field": "name", "value": "...", "hlc": "..." } ] }
```

**Snapshot** (`snapshots/<hlc>.snap`):

```json
{ "type": "snapshot",
  "formatVersion": 1,
  "vaultId": "...",
  "deviceId": "<the writer; equals the device part of hlc>",
  "hlc": "<equals the file name without .snap>",
  "covers": { "<deviceId>": <seq>, "...": 0 },
  "records": [ ... ] }
```

**Pairing file**: section 9.

Envelope checks, after decryption and before merging, run in this order; the first failure is
reported (fixtures: `files.json`, `checkEnvelope`):

| # | Check | Error |
|---|---|---|
| 1 | The payload is a JSON object | `malformed` |
| 2 | `type` is `"changes"` for `.chg`, `"snapshot"` for `.snap`, `"pairing"` for `.pair` | `typeMismatch` |
| 3 | `formatVersion` is an integer (see above) equal to `1` | `malformed` if not an integer, else `unsupportedFormat` |
| 4 | `vaultId` equals vault.json's `vaultId` | `vaultMismatch` |
| 4p | pairing file only: `generation` is an integer ≥ 1, `vaultKey` is 64 lowercase hex digits, `deviceId` is a device ID, `expiresAt` is a timestamp; then stop | `malformed` |
| 5 | `deviceId` equals the folder name (change file) or the HLC's device part (snapshot) | `deviceMismatch` |
| 6 | change file: `seq` is an integer equal to the file's number | `malformed` if not an integer, else `seqMismatch` |
| 7 | snapshot: `hlc` equals the file name without `.snap` | `hlcMismatch` |
| 8 | snapshot: `covers` is an object mapping device IDs to integers ≥ 0 | `malformed` |
| 9 | `records` is an array | `malformed` |

A file failing an envelope check is not merged at all. Unknown extra keys are ignored.

In a **change file**, a record whose HLC device part is not the file's device is ignored (the
rest of the file is merged). A change file carries only its own device's edits. Snapshots carry
records from every device.

`formatVersion` is the **sync format version**. It changes only for breaking changes
(`data-model.md` section 9). An app that meets a newer version stops reading that file, does not
advance past it, and asks the user to update.

### 6.4 Compression

None in format 1 (decided here). The data is tens of kilobytes; compression would add a dependency
on identical deflate behaviour for no real gain. The header's compression byte is reserved for a
later format; readers reject any value but `0`.

### 6.5 Key derivation

The vault key is 32 random bytes per key generation (section 7). It is never used directly as an
encryption key. Sub-keys are derived with **HKDF-SHA256 (RFC 5869)**, salt = 32 zero bytes (the
same as an empty salt), with these `info` strings (ASCII, no terminator) (decided here):

| Key | Input key | info | Length |
|---|---|---|---|
| file key | vault key | `futurecheck/v1/file-key` | 32 |
| key check value | vault key | `futurecheck/v1/key-check` | 16 |
| pairing key | pairing secret | `futurecheck/v1/pairing-key` | 32 |

Platforms: .NET `HKDF.DeriveKey`, CryptoKit `HKDF<SHA256>.deriveKey`, WebCrypto
`deriveBits({name: "HKDF", hash: "SHA-256", salt: new Uint8Array(32), info})`. Android has no HKDF
in the JCA; for lengths up to 32 it is two HMAC calls:

```
prk = HMAC-SHA256(key = 32 zero bytes, message = inputKey)
okm = HMAC-SHA256(key = prk, message = info || 0x01), truncated to the length
```

Example: the all-zero vault key gives file key
`d0dedc15d8ccdbcfda015ae941e282b3a46e4d2672a46a1c0c4baf753757d5c3` and key check
`fdecadbe5583e55c611722372319c350`. Fixtures: `crypto.json` (`deriveKeys`, `derivePairingKey`).

### 6.6 Encrypted file framing

AES-256-GCM (design section 6.4), 12-byte nonce, 16-byte tag. Every `.chg`, `.snap` and `.pair`
file is:

| Offset | Length | Content |
|---|---|---|
| 0 | 4 | Magic `FCSY` (`46 43 53 59`) |
| 4 | 1 | Format version, `0x01` |
| 5 | 1 | Kind: `0x01` change file, `0x02` snapshot, `0x03` pairing file |
| 6 | 1 | Compression, `0x00` (section 6.4) |
| 7 | 1 | Reserved, `0x00` |
| 8 | 4 | Key generation, unsigned 32-bit big-endian. `≥ 1` for change files and snapshots; `0` for pairing files. |
| 12 | 12 | Nonce |
| 24 | n | Ciphertext (same length as the plaintext) |
| 24 + n | 16 | GCM authentication tag |

- **Key:** the file key (section 6.5) of the named generation; for a pairing file, the pairing key.
- **Nonce:** 12 bytes from a cryptographically secure generator, fresh for every file written,
  including every retry of an upload. Never derived from a counter or the sequence number.
- **Associated data (AAD):** header bytes 0 to 11 (magic through key generation, 12 bytes),
  followed by the UTF-8 bytes of the file's path relative to the vault root, for example
  `devices/0a0a0a0a0a0a0a0a/000001.chg`. No separator, no length prefix. The nonce is not part of
  the AAD (GCM already authenticates it).

Binding the path means a file cannot be moved to another device's folder, renamed to another
sequence number, or turned into a snapshot without failing authentication.

Example (from `crypto.json`, "seal a change file"): header
`4643535901010000000000019681ebb04083ec2cbc739c78`, AAD
`464353590101000000000001` + hex of `devices/0a0a0a0a0a0a0a0a/000001.chg`.

**Opening a file**, checks in this order (each failure stops; codes are in the fixtures):

1. Length < 40 bytes: `tooShort`.
2. Bytes 0–3 are not `FCSY`: `badMagic`.
3. Byte 4 is not `1`: `unsupportedVersion`.
4. Byte 5 is not 1, 2 or 3: `unknownKind`.
5. Byte 6 is not `0`: `unsupportedCompression`.
6. Byte 7 is not `0`: `reservedNonZero`.
7. Kind does not match the file name's extension (`.chg` 1, `.snap` 2, `.pair` 3): `kindMismatch`.
8. Key generation is `0` for a change file or snapshot, or not `0` for a pairing file:
   `badKeyGeneration`.
9. This device has no key for that generation: `unknownKeyGeneration`.
10. GCM decryption fails: `authFailed`.

Then parse the JSON and run the envelope checks (section 6.3).

Fixtures: `crypto.json` (`seal`, `open`), including tamper cases that must fail.

## 7. vault.json and key generations

### 7.1 Contents

`vault.json` is plaintext UTF-8 JSON at the vault root. It holds no secrets.

```json
{
  "type": "futurecheck-vault",
  "formatVersion": 1,
  "vaultId": "019a0000-0000-7000-8000-00000000fa01",
  "createdAt": "2026-11-14T12:00:00.000Z",
  "createdBy": "0a0a0a0a0a0a0a0a",
  "currentGeneration": 1,
  "generations": [
    { "id": 1, "createdAt": "2026-11-14T12:00:00.000Z", "keyCheck": "<32 lowercase hex>" }
  ]
}
```

| Field | Meaning |
|---|---|
| `type` | Always `"futurecheck-vault"`. |
| `formatVersion` | Sync format version (section 6.3). A reader that does not support it stops syncing and asks for an update. |
| `vaultId` | UUIDv7, made when the vault is created. Every payload names it, so files from a different vault (for example one deleted and recreated) are rejected. A reader MUST check that it is a canonical lowercase UUID (any version: `8-4-4-4-12` lowercase hex, the whole string); anything else, including a missing value, is `malformed` (decided here). |
| `createdAt`, `createdBy` | When and by which device the vault was created. Informational. |
| `currentGeneration` | The generation new files are encrypted with. MUST be listed in `generations`. |
| `generations[].id` | Key generation number, integer ≥ 1, unique. |
| `generations[].createdAt` | When it was created. |
| `generations[].keyCheck` | Key check value: HKDF output (section 6.5), 16 bytes, lowercase hex. |

Validation, in this order, each failure `malformed` unless noted: the root is an object; `type` is
exactly `"futurecheck-vault"`; `formatVersion` (integer rules of section 6.3; a valid integer other
than 1 is `unsupportedFormat`); `vaultId`; `generations` is a non-empty array whose items each have
an integer `id` ≥ 1 and a `keyCheck` of exactly 32 lowercase hex digits; ids are unique; and
`currentGeneration` is an integer listed among them. `createdAt`, `createdBy` and
`generations[].createdAt` are informational and not validated (except for the duplicate rule in
section 12.1). Readers ignore unknown fields. Fixtures: `files.json` (`checkVault`).

### 7.2 Key check value

The key check value lets a device confirm that a typed recovery phrase or a scanned QR code is the
key of this vault, and which generation it is, before trying to decrypt anything. It is an HKDF
output of a 256-bit random key, so it reveals nothing useful about the key. Fixtures: `crypto.json`
(`matchGeneration`).

### 7.3 Key generations

- Format 1 creates exactly one generation (`id: 1`). Every encrypted file's header names the
  generation it used (section 6.6), and a reader keeps a map from generation to key.
- **Rotation** (cutting off a lost device, design section 6.4) is not implemented in v1. What the
  format already fixes: a rotation adds a generation with a new random vault key, sets
  `currentGeneration` to it, writes a full snapshot under the new key, and issues a new recovery
  phrase. How a device that only knows the new key reads files written under older generations
  (for example the previous key wrapped by the new one) will be specified together with rotation;
  until then a device meeting `unknownKeyGeneration` tells the user the vault was re-keyed and asks
  for the current recovery phrase or a QR pairing.

## 8. Recovery phrase

The vault key is shown once at vault creation as a **24-word BIP39 English phrase** (decided here).

- **Wordlist:** `spec/bip39-english.txt`, the BIP39 English list (MIT License), 2048 words,
  SHA-256 `2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda`. Apps embed this exact
  file and test its hash.
- **Encode:** entropy = the 32-byte vault key of the current generation. Checksum = the first byte
  of SHA-256(entropy). Take the 264 bits `entropy || checksum`, split them into 24 groups of 11 bits
  from the most significant end, and map each group (0–2047) to the word at that line of the
  list (0-based). Join with single spaces.
- **Decode:** lowercase `A`–`Z` only (other characters are left as they are), then split on runs of
  exactly these separators: space U+0020, tab U+0009, CR U+000D, LF U+000A and no-break space U+00A0
  (decided here; pasted text often carries U+00A0). Any other character, including other Unicode
  spaces, is part of a word. Leading and trailing separators are ignored. Require exactly 24 words
  (`wrongWordCount`), map each word to its index (`unknownWord`), rebuild the 264
  bits, and check that the last 8 bits equal the first byte of SHA-256 of the first 256
  (`badChecksum`). Then confirm the key against vault.json's key check values (section 7.2).
- Only full words decode. Apps MAY autocomplete from the first four letters (unique in the BIP39
  English list), but the decoded phrase is always the full words.
- **The BIP39 seed derivation (PBKDF2, passphrase) is not used.** The 24 words encode the key
  itself, so any standard BIP39 library's "mnemonic to entropy" function works.
- After rotation (section 7.3) the phrase changes. Apps MAY offer "Show recovery phrase" again in
  Settings on any unlocked device, behind the platform's device authentication (decided here).

Fixtures: `recovery-phrase.json`, including the published BIP39 256-bit test vectors.

## 9. QR pairing

Adds a device without typing 24 words (design section 6.5). The new device has already signed in
to the same drive.

### 9.1 Flow (decided here)

On the existing device ("Add a device"):

1. Make a **pairing ID** (16 random bytes) and a **pairing secret** (32 random bytes).
2. Write `pairing/<pairingId hex>.pair`: kind 3, key generation 0, encrypted with the pairing key
   (HKDF of the secret, section 6.5). Payload:

   ```json
   { "type": "pairing", "formatVersion": 1, "vaultId": "...", "generation": 1,
     "vaultKey": "<64 lowercase hex>", "deviceId": "<existing device>",
     "expiresAt": "<now + 10 minutes, timestamp>" }
   ```

3. Show a QR code of the text
   `FCPAIR:1:<pairingId, 32 uppercase hex>:<pairing secret, 64 uppercase hex>`
   (106 characters; uppercase hex and `:` fit QR alphanumeric mode).
4. Delete the pairing file when the new device's `device` row arrives, when the user closes the
   screen, or after 10 minutes, whichever is first.

**Every device** also deletes stale pairing files on every sync (section 10.2, step 5): any file in
`pairing/` whose provider-assigned creation time is more than 10 minutes before the provider's
current time (section 11.1 says how to read both; writers never set these timestamps). This removes files left behind when the pairing screen was
killed or the device lost power. Deletion uses the provider's permanent delete where it has one
(Google Drive's `files.delete` bypasses the trash); otherwise an ordinary delete, which on OneDrive
and Dropbox leaves the file recoverable from the recycle bin or deleted-file history for weeks
(decided here).

On the new device:

1. Scan the QR code inside the app (never through a URL scheme, which could log it). Parse it
   exactly like this (decided here): remove leading and trailing characters from the recovery
   phrase's separator set (space, tab, CR, LF, U+00A0; section 8) and nothing else; change ASCII
   `a`–`z` to `A`–`Z` and no other character; then the whole string must match
   `FCPAIR:1:<32 hex>:<64 hex>` with uppercase hex. Anything else is `badQr`, including non-ASCII
   letters that some case-mapping functions turn into ASCII (dotless `ı` U+0131 becomes `I` under
   Unicode uppercasing), other Unicode spaces, and spaces inside the text.
2. Download `pairing/<pairingId>.pair`, open it with the pairing key, run the envelope checks,
   reject it if `expiresAt` has passed (by the local clock).
3. Check `vaultKey` against vault.json's key check value for `generation` (section 7.2), store it
   in the keystore, delete the pairing file, and continue with "join" (section 12.2).

Fixtures: `crypto.json` (`pairingQr`, `parsePairingQr`, and the pairing `seal`/`open` cases).

### 9.2 What it protects against, and what not

The pairing file plus the QR code together are the vault key. Neither alone is.

- **Covered:** a photo, screenshot or screen recording of the QR code alone does not reveal the vault
  key: it is useless without the pairing file, which lives only in the user's drive. The drive
  provider alone sees only ciphertext and never the QR code.
- **Not covered, stated plainly:**
  - The 10-minute expiry is enforced only by an honest new device checking `expiresAt`. An attacker
    holding the QR code and the pairing file can decrypt it at any time.
  - The pairing file can outlive the 10 minutes: until some device syncs and deletes it (a killed app
    leaves it behind), and after deletion for as long as the provider keeps deleted files (OneDrive's
    recycle bin, Dropbox's deleted-file history: weeks). So a photo of the QR code plus access to the
    user's drive account, including its deleted items, within that time reveals the vault key.
  - Malware on either device, or anyone who has the recovery phrase (which is the key itself).
  - There is no confirmation code: the user is assumed to be holding both devices.
- **Advice the app gives:** show the QR code only while pairing, never screenshot or photograph it,
  and pair only on a drive account that is yours alone.

## 10. Writing and reading

### 10.1 Writing change files

1. Local edits produce records (section 4.4). The records, the new clock state (section 2.3) and the
   outgoing-queue entries MUST be written in **one local transaction**, together with applying the
   records to the local state.
2. When a sync runs, all queued records are **sealed** into one batch: assign `seq` = the device's
   next sequence number (starting at 1), and persist the batch's plaintext and `seq` locally. A
   sealed batch is immutable: its records never change and its number is never reused.
3. Upload it as `devices/<me>/<seq>.chg`. A device MUST NOT upload `seq + 1` before `seq` has
   uploaded successfully, so a device's files appear in order.
4. On success, mark the batch uploaded and increment the next sequence number. On failure, retry
   later with the same plaintext and `seq` (re-encrypting with a fresh nonce is fine).
5. If one or more files with that name already exist when uploading (for example a previous upload
   succeeded but the app did not hear back), download and open each. If every one holds the same
   records as the sealed batch, treat the upload as done. If any differs, or any fails to open (this
   device's own upload always opens), two installs share a device ID (a cloned disk or restored app
   data): stop syncing, tell the user, and rejoin with a new device ID (section 12.2).
6. A batch MUST contain at least one record. There is no size limit; writers SHOULD split batches
   above about 5,000 records.

"The same records" means equal `records` arrays: same length, and each record equal as parsed JSON
values, in order.

**Sync triggers (decided here):** at launch and on returning to the foreground; 5 seconds after
the last local edit (debounced); every 5 minutes while in the foreground; and on "Sync now". The
web app also syncs when the tab becomes visible.

### 10.2 Reading

Each device keeps, locally, a **read cursor** per device: the highest `seq` such that files 1 to
`seq` of that device have all been merged, or are covered by a merged snapshot. Devices it has never
heard of have cursor 0. A sync:

1. Read vault.json. Stop if `formatVersion` is unsupported. If `vaultId` differs from the one this
   device joined, the vault was deleted and recreated: stop and offer to join the new one
   (section 12.2).
2. List `devices/*/`, `snapshots/` and `pairing/`. The set of devices is every folder under
   `devices/` plus every device ID that appears in any snapshot's `covers`.
3. Repeat until nothing changes:
   - for each device `D`, open and merge files `cursor[D] + 1`, `+ 2`, ... while they are listed.
     After each file, `observe` every valid record's HLC and advance `cursor[D]`;
   - if, for some device `D`, file `cursor[D] + 1` is **not listed** (whether or not any higher file
     is), merge **every** listed snapshot not yet merged whose `covers[D] > cursor[D]` (section 11.3).
     A listed file that failed to open in this sync counts as **not listed** here (decided here: no
     24-hour wait, since merging a snapshot is always safe), so a corrupt or undecryptable file that a
     snapshot covers never blocks reading (`compaction.json`, `unreadable`).
4. Update the materialized tables and the UI in the same transaction as each merge.
5. Delete stale pairing files (section 9.1).

The snapshots to merge in step 3 are exactly those in `compaction.json` (`readPlan`,
`mergeSnapshots`). A reader MAY merge other snapshots too (for example every snapshot when joining,
to avoid downloading many small files); the resulting cursors are the same.

Properties the reader relies on, and MUST keep:

- **Idempotent.** Re-reading a file, or merging a snapshot whose records are already merged,
  changes nothing (section 4.2). A crash between merging and saving the cursor is harmless.
- **Order-free.** Files from different devices, or files beyond a gap, may be merged in any order;
  the result is the same. A reader MAY merge files above a gap; `cursor[D]` stays below the gap, and
  the reader MAY remember which higher files it already merged to avoid downloading them again.
- **Every snapshot, not only the newest.** Two devices can write snapshots at the same time, and the
  newer one by HLC can cover less for some device than the older one. A reader that only looked at
  the newest snapshot could stall forever on a compacted gap (`compaction.json`, "concurrent
  snapshots...").
- **Gaps.** If file `cursor[D] + 1` is missing, no snapshot covers it, and a higher file of `D` is
  listed, it is probably the provider's listing lag: retry next sync. If it persists for 24 hours,
  show "Some changes from <device name> could not be found". If no higher file is listed and no
  snapshot covers it, `D` simply has nothing new.
- **Duplicate names.** Google Drive does not enforce unique file names, so a listing can show two
  files with the same name. A reader opens every one. If their `records` are the same (as defined in
  10.1), merge one. If they differ, treat it as a device-ID clash: merge neither, do not advance the
  cursor, and show "Two devices are using the same identity; <device name> needs to rejoin". If any
  copy fails to open, merge none of them and treat the name as an unreadable file (the rule above:
  it counts as not listed for the snapshot step, and is reported after 24 hours); a readable copy
  alone cannot be trusted while another copy might hold different records. The same applies to
  snapshots with the same name. (Two `vault.json` files: section 12.1.)
- **Unreadable files** (`authFailed`, `badMagic`, envelope errors) are not merged and do not advance
  the cursor. Retry on later syncs; if still failing after 24 hours, show "A file from
  <device name> could not be read". `unknownKeyGeneration` and `unsupportedVersion` /
  `unsupportedFormat` show their specific messages immediately (section 14).
- A device never reads its own change files back (its records are already merged locally), except to
  resolve an upload conflict (section 10.1, step 5).

## 11. Snapshots and compaction

A snapshot is a full copy of the merged state, so a new device reads a few files instead of every
change file ever written (design section 6.3). Compaction then deletes what snapshots have made
redundant. Every rule here is pinned by `conformance/sync/compaction.json`.

The design (section 6.3) says files can be removed once "all known devices have passed" them. Devices
do not publish their read positions, so format 1 meets the same goal a different way (decided here):
**a deleted file is always covered by a snapshot that is never deleted while it is the only one
covering it**, and readers always find that snapshot (section 10.2). A device that was offline for
months then merges a snapshot instead of the deleted files, and ends in the same state.

### 11.1 Snapshot age

Compaction waits until a snapshot is at least **7 days** old, so devices whose listing lags see it
before the files it replaces disappear. Any one device's clock can be wrong, so age is measured on
two clocks that a skewed writer cannot influence, and both must agree (decided here):

- **local:** this device's physical clock now, minus the time this device **first listed** the
  snapshot (recorded locally, per snapshot name) ≥ 7 days. The snapshot's own HLC wall time is never
  used: a device whose clock runs a year ahead would otherwise make its snapshots look new to every
  other device for a year and stop compaction vault-wide;
- **provider:** the provider's current time minus the file's **provider-assigned** creation time
  ≥ 7 days. "Current time" is the provider's response time (for example the HTTP `Date` header of
  the listing), never the local clock. Use OneDrive's item `createdDateTime` (not
  `fileSystemInfo`), Google Drive `createdTime` and Dropbox `server_modified` (snapshots are written
  once).

**Writers MUST NOT set provider timestamps** that clients are allowed to set (Google Drive
`createdTime` and `modifiedTime`, OneDrive `fileSystemInfo`, Dropbox `client_modified`); the age
checks and stale-pairing cleanup (section 9.1) rely on the provider assigning them. A buggy writer
that sets them anyway can only move the provider check, never the local first-listed check.

If the provider gives no creation time, or this device has no first-listed time, the snapshot is not
old enough. The "7 days old" snapshot-writing trigger (11.2) uses the same two measurements.
Fixtures: `compaction.json` (`snapshotAgeOk`).

### 11.2 Writing a snapshot (thresholds decided here)

A device writes a snapshot when all of these hold:

- it is in the foreground, has uploaded all its sealed batches, and has merged **every** listed
  snapshot (so its cursors are at least every snapshot's `covers`, and its snapshot dominates them);
- and one of:
  - no snapshot exists and the vault holds 20 or more change files;
  - at least 100 change files are not covered by the newest snapshot;
  - the newest snapshot is at least 7 days old (section 11.1) and at least one change file is not
    covered by it.

Contents: `hlc` = one `tick`; `covers` = the writer's read cursors for every device it knows, plus its
own last uploaded `seq`; `records` = the winning record of every (entity, id, field) in its state,
including tombstoned, pending and unknown rows. Name: `snapshots/<hlc>.snap`, kind 2, current
generation.

Two devices may write snapshots at about the same time. That is harmless; both are valid, and the
deletion rules below keep whatever either one alone covers.

### 11.3 Merging a snapshot

Merge its records like any other records (section 4.2), observe their HLCs, then set
`cursor[D] = max(cursor[D], covers[D])` for every `D` in `covers`. Because snapshots are merged state,
merging one is idempotent and never undoes anything newer.

### 11.4 Deleting snapshots

Snapshot `S` **dominates** snapshot `T` when `S.covers[D] ≥ T.covers[D]` for every device `D` in
`T.covers` (a device missing from `covers` counts as 0).

- The 2 newest snapshots (by HLC order of their names) are never deleted.
- Any device MAY delete an older snapshot `T` when the newest snapshot is old enough (11.1) **and**
  one of the 2 newest dominates `T`.

So a snapshot is only deleted when a kept snapshot covers at least as much for every device.
Fixtures: `compaction.json` (`deletableSnapshots`).

### 11.5 Deleting change files

Only the owning device deletes its change files; nobody deletes another device's files in format 1
(a retired device's files stay, and readers skip them through `covers`). Device `me` MAY delete its
own `devices/<me>/<N>.chg` when, among the listed snapshots:

1. at least one has `covers[me] ≥ N`, and at least one of those is old enough (11.1); and
2. every snapshot newer (by HLC) than the oldest snapshot with `covers[me] ≥ N` also has
   `covers[me] ≥ N`.

Rule 2 stops a device from deleting files that a newer, concurrent snapshot does not cover.
Fixtures: `compaction.json` (`deletableChangeFiles`).

`vault.json` is never deleted by the app.

## 12. Flows

### 12.1 First device: create a vault

1. User picks a provider and signs in (design section 6.5).
2. If `PayDay/vault.json` already exists, stop and offer "join" instead.
3. Generate a device ID (section 1) and start the clock state (section 2.3). Generate the vault key
   (32 bytes) and `vaultId` (UUIDv7). Store the key in the keystore.
4. If the local database is from today's Windows app, run the migration (`data-model.md`
   section 8). It takes one HLC (one `tick` with the new device ID) for all migrated records.
5. Upload `devices/<me>/000001.chg` with every record of the local state plus this device's
   `device` row.
6. Upload `vault.json` **last**, so a vault.json always means the first data is already there.
   Use the provider's "fail if the file exists" option where it has one (OneDrive does; Google
   Drive does not enforce unique names). If two vault.json files are found, every device uses the
   one with the earliest `createdAt`, ties broken by the smaller `vaultId` (decided here); the
   other creator is told to join. For this comparison a `createdAt` that is not a valid timestamp
   sorts after every valid one.
7. Show the recovery phrase and say plainly that losing every device and the phrase loses the
   synced data; offer a JSON export (design section 6.4).

### 12.2 Next device: join

1. "I already use FutureCheck", sign in to the same provider, read vault.json.
2. Unlock with a QR code (section 9) or the recovery phrase (section 8). Confirm the key against a
   key check value.
3. **If this device already has data** (any live bill, payment, income source or paycheck), say
   that its current data will be saved to a backup file and replaced, and do it only after the user
   agrees (design section 6.5: v1 does not merge independent histories). Save the JSON backup, then
   clear the local synced data and sync state. Per-device settings stay.
4. Generate a new device ID. Read as in section 10.2 with every cursor at 0: that merges the
   snapshots covering compacted files and every change file they do not cover. (Merging every listed
   snapshot first is allowed and saves downloads.)
5. Write the first change file: this device's `device` row.

### 12.3 Restore a JSON backup with sync on

Design section 6.5: restore means "make every device look like this backup". Fixture: `merge.json`,
"restore re-stamps the backup..." (op `restore`).

1. Confirm with the user and save the current state as a backup first.
2. Map the backup as in `data-model.md` section 8: IDs (legacy bill IDs, string or integer payment
   IDs), each bill's Remaining to `remainingCheckpoint`, and the backup's `settings` object to synced
   setting values (section 8.5: for example a legacy `TypeColors` JSON value becomes one
   `TypeColor/<type>` key per colour, and `"true"`/`"false"` text becomes a boolean).
3. Take **one** HLC for the whole restore. At that HLC, write:
   - for the **plan entities** `bill`, `payment`, `balanceSnapshot`, `incomeSource` and `paycheck`:
     every field of every row in the backup (including fields equal to the current value: a restore
     re-stamps everything) plus `deleted: false`;
   - for the same five entities: `deleted: true` for every row this device knows that is not in the
     backup and is not already tombstoned (live or pending rows);
   - every **plan setting** the mapping produced: `PayAnchor`, `EarlyStart`, `SafeToSpendBuffer`,
     `ReminderDaysBefore` and `TypeColor/<type>` keys;
   - `value: null` for every plan setting this device knows that the mapping did not produce and that
     is not already null (a colour missing from the backup resets to its default).

   The **Notion connector keys** (`NotionOwnerDeviceId`, `NotionBillsDb`, `NotionPaymentsDb`,
   `NotionSnapshotsDb`) are neither written nor cleared, whatever the backup holds (decided here).
   Backups export the whole settings table, so a backup can hold a dead device's ID as owner, `null`,
   or database IDs from an older connection; writing any of them at the restore HLC would silently
   break the connector. Per-device keys in the backup (`Theme`, `LastNotionSync`, `SchemaVersion`, ...)
   are not synced (step 5), and unknown keys are ignored. Fixtures: `merge.json`, "restore never
   writes or clears the Notion connector settings", "restore maps legacy backup settings".
4. Never write `device` rows, rows of entities this app does not know, or setting keys it does not
   know. They are not part of a backup, and tombstoning them would erase another device's identity or
   a newer app's data.
5. Apply per-device settings from the backup to this device only.

Consequences: restored payments carry the same HLC as the restored checkpoints, so they are absorbed
(section 5.1) and not subtracted again; unmarking one later gives its amount back (section 5.3). An
offline payment on another device made before the restore HLC survives as a row but does not lower
Remaining (section 5.4); edits made after the restore HLC apply on top as usual.

### 12.4 Leaving a vault

"Turn off sync" stops syncing and removes the vault key and drive tokens from this device. Local data
stays. The device's files stay in the vault. Turning sync on again is a "join" (12.2) with a new
device ID.

### 12.5 Notion connector ownership (decided here)

- The synced setting `NotionOwnerDeviceId` names the one device that runs the Notion connector.
  Connecting Notion on a device sets it to that device's ID. Other devices show "Notion: connected
  on <device name>" and do not push or pull.
- A device that sees `NotionOwnerDeviceId` change to another device stops running the connector.
- Notion page and database IDs are synced (`bill.notionPageId`, `payment.notionPageId`,
  `balanceSnapshot.notionPageId`, `Notion*Db` settings), so a new owner updates the same pages
  instead of creating duplicates.
- The echo state (`lastPushed`, section 5.3) is per device. A new owner MUST push every bill before
  its first pull, so its `lastPushed` values are known. (A Notion edit made in the minutes while
  ownership moves can be overwritten by that push.)
- Notion's Remaining column is always written as computed Remaining. Pulling a Remaining value goes
  through `setRemaining` with `source: "notion"`.

## 13. Security notes

- The drive provider sees: that a vault exists, file names (device IDs, sequence numbers, snapshot
  HLCs, so roughly when each device edits), file sizes, and vault.json. It cannot read or change
  any record: every payload is AES-256-GCM encrypted with the file's path and header as associated
  data.
- The provider (or anyone with drive access) can delete files, withhold new ones, or replay old
  ones. Replays are harmless (merge is idempotent; old records lose to newer ones). Deleting or
  withholding loses or delays data; gaps are reported (section 10.2), but a missing newest file
  cannot be detected. Backups (JSON export) are the defence.
- vault.json is not authenticated. Editing it can only stop sync (for example a changed key check
  makes a correct phrase look wrong), not expose data.
- Nonces are 96-bit random per file. At this data volume (thousands of files per key, not billions)
  the collision risk is negligible.
- A device that has the vault key can read everything. Cutting off a lost device needs rotation
  (section 7.3), which v1 does not ship, plus signing that device out of the drive provider.

## 14. Error codes

Error codes appear in fixtures as `{"error": "<code>"}`. Apps map them to messages; the codes are
the contract.

| Code | Where | Meaning / what the user sees |
|---|---|---|
| `malformed` | HLC decode, envelope, vault.json | Bad shape. |
| `wallOutOfRange`, `counterOutOfRange`, `badDeviceId` | HLC encode/tick | Programming error. |
| `badInput` | Deterministic IDs | Non-canonical ID or bad date. |
| `notAUuid`, `notLegacy` | ID helpers (reference only) | |
| `tooShort`, `badMagic`, `unknownKind`, `kindMismatch`, `reservedNonZero`, `unsupportedCompression`, `badKeyGeneration`, `authFailed` | Opening a file | "A file from <device> could not be read" after 24 hours (section 10.2). |
| `unsupportedVersion`, `unsupportedFormat` | File header, payload, vault.json | "Update FutureCheck to keep syncing." |
| `unknownKeyGeneration` | Opening a file | "This vault was re-keyed. Enter the current recovery phrase or pair with a QR code." |
| `typeMismatch`, `vaultMismatch`, `deviceMismatch`, `seqMismatch`, `hlcMismatch` | Envelope | Treated like an unreadable file. |
| `badPath`, `badSeq` | File names | Not a sync file; ignored. |
| `wrongWordCount`, `unknownWord`, `badChecksum` | Recovery phrase | "Check the words and try again." |
| `badQr` | Pairing QR | "That isn't a FutureCheck pairing code." |
| `badKeyLength`, `badNonceLength` | Crypto helpers (reference only) | |

## 15. Decisions made here

| # | Decision | Section |
|---|---|---|
| 1 | Device ID: 64 random bits as 16 lowercase hex; new on every create/join | 1 |
| 2 | HLC string `wall12-counter4-device16`, compared byte-wise; tick/observe rules; counter carry | 2.1–2.3 |
| 3 | No rejection for clock skew; warn when a remote is more than 10 minutes ahead | 2.4 |
| 4 | UUIDv7 byte layout from 10 random bytes | 3.1 |
| 5 | UUIDv5 namespace `def9d7a0-dc52-41e0-906a-f9767c7f894d` and the four name formats | 3.2 |
| 6 | Value order (`null < false < true < number < string`) breaks identical-HLC ties | 4.2 |
| 7 | Rows without a `deleted` record are pending, not live | 4.3 |
| 8 | Tombstones are never purged in format 1 | 4.3 |
| 9 | Unknown entities and fields are stored and forwarded | 4.5 |
| 10 | Checkpoint HLC = HLC of the winning `remainingCheckpoint` record; mark HLC = HLC of the winning `deleted` record; "after" is strict | 5.1 |
| 11 | Echo detection by comparing the Notion value with the connector's last pushed value | 5.3 |
| 11a | Unmarking an absorbed payment raises the checkpoint by its amount (unclamped); "mark paid" on a live payment writes only `amountPaid` and `paidAt` | 5.3 |
| 11b | Known limitations of checkpoints with concurrent offline edits are accepted, not fixed | 5.4 |
| 12 | Plaintext JSON is never hashed or byte-compared, so no canonical JSON; strict parsing (no BOM, no duplicate keys, no lone surrogates, no binary64 overflow, object root); integers written as digits, read as any integral number, never booleans | 6.3 |
| 12a | Every string format is matched against the whole string in ASCII; no trimming or folding unless stated | top of document |
| 12b | `vaultId` must be a canonical lowercase UUID | 7.1 |
| 13 | Change files carry only their own device's records | 6.3 |
| 14 | No compression | 6.4 |
| 15 | HKDF-SHA256 sub-keys (file key, key check, pairing key) | 6.5 |
| 16 | Framing: `FCSY`, version, kind, compression, reserved, generation, nonce; AAD = header bytes 0–11 + path | 6.6 |
| 17 | vault.json fields; key check value = 16-byte HKDF output | 7 |
| 18 | Recovery phrase: BIP39 English, 24 words, encoding the key directly; separators are space, tab, CR, LF, U+00A0 only | 8 |
| 19 | QR pairing through a short-lived encrypted pairing file in the drive; every device deletes stale pairing files | 9 |
| 20 | Sealed batches are immutable; a same-name file with different records means a device-ID clash (also for Google Drive duplicate names) | 10.1, 10.2 |
| 21 | Sync triggers: launch/foreground, 5 s debounce, 5 min, manual | 10.1 |
| 22 | Read cursor = highest contiguous seq; a missing file is filled from every snapshot that covers it | 10.2 |
| 23 | Snapshot thresholds 20 / 100 files / 7 days; age measured from this device's first listing (local clock) and from the provider-assigned creation time (provider clock), never from the snapshot's HLC; writers never set provider timestamps; a file that fails to open counts as not listed for the snapshot fallback; a snapshot is deleted only if a kept one dominates it; a change file only if no newer snapshot covers less; owner-only deletion of change files | 11 |
| 24 | vault.json written last; duplicate vault.json resolved by earliest `createdAt` | 12.1 |
| 25 | Restore uses one HLC, maps backup settings per data-model 8.5, tombstones only plan entities, writes and nulls only plan settings (never the Notion connector keys), never touches devices or unknown data | 12.3 |
| 26 | Notion connector owner = synced `NotionOwnerDeviceId`; new owner pushes before pulling | 12.5 |
| 27 | Clock state is persisted in the same transaction as the records it stamped | 2.3 |
| 28 | Paychecks of Irregular sources get UUIDv7 IDs; migrated extra payments and balance snapshots get content-derived UUIDv5 IDs | 3.2 |
