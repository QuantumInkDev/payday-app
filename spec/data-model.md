# FutureCheck data model

**Format:** sync format 1 · **Date:** 2026-11-14 · **Status:** draft for review

The canonical entities every FutureCheck app stores and syncs, with every field's type, units and
meaning. `sync-protocol.md` says how these values travel between devices and merge; this document
says what they are.

"(decided here)" marks a choice the design (`planning/2026-11-14-multiplatform-design.md`) left
open.

---

## 1. Conventions

### 1.1 Types

A field value travels as a JSON value inside a change record (`sync-protocol.md` section 4). The
types used below:

| Type | JSON | Rules |
|---|---|---|
| `string` | string | UTF-8. Any length. Apps do not trim or case-fold on write. |
| `string?` | string or `null` | `null` means "not set". An empty string is a value, not "not set". |
| `bool` | `true` / `false` | Never `0`/`1`. |
| `int` | number with an integer value | `3` and `3.0` are the same value. |
| `number` | number | Finite. Used for non-money quantities (APR). Not rounded. |
| `money` | number | See section 3. |
| `date` | string `YYYY-MM-DD` | A calendar date with no time or zone, for example `2026-11-12`. Always zero-padded. |
| `date?` | date or `null` | |
| `timestamp` | string `YYYY-MM-DDTHH:MM:SS.sssZ` | UTC, exactly three fraction digits, literal `Z`. Example `2026-11-14T18:30:05.120Z`. Display only; never used to order edits. |
| `id` | string | A canonical UUID: lowercase hex, `8-4-4-4-12` (section 2). |
| `enum` | string | One of the listed values, spelled exactly. |

### 1.2 Defaults, missing and invalid values

- Every field has a default. A field with no record (written by an older app, or not delivered
  yet) reads as its default.
- A record whose value has the wrong JSON type for a known field (for example a string in a
  `money` field) is still stored and merged; merge never rejects on schema. When the app reads
  that field, it uses the default instead (decided here). This keeps merge identical on every
  platform even when one app has a bug.
- An `enum` value an app does not know (written by a newer version) is kept as-is. Each enum below
  says how an older app treats unknown values.

### 1.3 Field names

Field names are camelCase and are part of the format. They match the backup JSON names where a
backup name exists (`amountPaid`, `snapshotDate`, ...). Two names differ from today's columns on
purpose: `remainingCheckpoint` (was `Remaining`/`Owed`) and `apr` (was `APR`).

### 1.4 Rows, tombstones and liveness

- A row is identified by (entity, id). Its fields are merged one by one.
- Every entity except `setting` has a `deleted` field (`bool`). Deleting a row writes
  `deleted: true`; nothing is ever removed. Undeleting writes `deleted: false`.
- **A row is live when its `deleted` field has a record and that value is `false`.** A row with
  field records but no `deleted` record is *pending*: some of its edits arrived before its create.
  Pending rows are kept but not shown (decided here). Creating a row always writes
  `deleted: false`.
- A `setting` row is live when its `value` field has a record (even if the value is `null`).

---

## 2. IDs

| Entity | ID | How it is made |
|---|---|---|
| `bill` | `id` | UUIDv7 (random). Legacy IDs are migrated (section 8.2). |
| `payment`, kind `Regular` | `id` | UUIDv5 from (billId, periodKey) |
| `payment`, kind `SetAside` | `id` | UUIDv5 from (billId, periodKey) |
| `payment`, kind `Extra` | `id` | UUIDv7 (random) |
| `balanceSnapshot` | `id` | UUIDv7 (random) |
| `incomeSource` | `id` | UUIDv7 (random) |
| `paycheck` | `id` | UUIDv5 from (incomeSourceId, payDate) |
| `device` | device ID | 16 lowercase hex characters, random (not a UUID) |
| `setting` | key | The setting's name, for example `PayAnchor` |

The exact UUIDv5 namespace, name strings and UUIDv7 layout are in `sync-protocol.md` section 3,
with vectors in `conformance/sync/ids.json`.

- IDs are compared as exact strings. Writers always write canonical lowercase. An app importing an
  ID from outside (backup file, Notion) lowercases a UUID-shaped ID before use.
- References (`payment.billId`, `paycheck.incomeSourceId`) hold the referenced row's ID.
- Deterministic IDs are what make "the same thing done on two devices" one row instead of two.

---

## 3. Money

Money stays `double` (IEEE-754 binary64) in all four apps, as today. A money value is an amount in
the user's single currency; there is no currency field (the apps do not convert currencies).

**Rule for writers:** round a money value to cents (below) before writing it to a record. Records
therefore normally hold at most two decimals. Readers accept any finite JSON number.

**Range:** `|x| < 10,000,000,000,000` (1e13). Larger values are invalid input.

### 3.1 Rounding (round half away from zero, to 2 decimals)

Fixtures and apps round with this exact algorithm, in binary64 arithmetic (decided here: the design
named the mode; this pins the algorithm so all four platforms produce the same bits):

```
cents(x):                      # x finite, |x| < 1e13
    s = |x| * 100              # one IEEE-754 multiplication, round-to-nearest-even
    i = trunc(s)               # integer part
    if s - i >= 0.5: i = i + 1 # s - i is exact
    return x < 0 ? -i : i      # an integer; fits in 64 bits

round2(x) = cents(x) / 100     # one IEEE-754 division
```

This equals .NET's `Math.Round(x, 2, MidpointRounding.AwayFromZero)` for this range: checked bit for bit on .NET 9 (arm64) over 18 million values, with no differences (2026-11-14).
Implement it literally on every platform (Swift `rounded(.towardZero)`, Kotlin `truncate`, JS
`Math.trunc`); do not use `BigDecimal(String)`, `Decimal`, `toFixed`, `Math.round` (JS and Java
round -2.5 to -2) or string formatting, which give different answers for some inputs.

| x | x * 100 as binary64 | cents(x) | round2(x) |
|---|---|---|---|
| 2.675 | 267.5 | 268 | 2.68 |
| 1.005 | 100.49999999999999 | 100 | 1.00 |
| 1.115 | 111.5 | 112 | 1.12 |
| 0.285 | 28.499999999999996 | 28 | 0.28 |
| 33.335 | 3333.5 | 3334 | 33.34 |
| -2.675 | 267.5 (of the absolute value) | -268 | -2.68 |
| 0.1 + 0.2 | 30.000000000000004 | 30 | 0.30 |
| 19.999 | 1999.8999999999999 | 2000 | 20.00 |

### 3.2 Where rounding applies

- **At write:** every money field written to a record is `round2` of the entered value.
- **At aggregation:** sums are taken in integer cents. Remaining, set-aside balances, period
  totals and safe-to-spend convert each term with `cents()` and add integers, so the order of
  addition cannot change the result. `round2` is never applied to a running double sum.
- **At display:** show `cents / 100` with two decimals.
- **Comparison:** two money values are equal when their `cents()` are equal (used by "unchanged
  value" checks, `sync-protocol.md` section 5.3).

### 3.3 When to switch to integer cents

The design (section 6.1) keeps `double` unless the conformance suite shows platforms rounding
differently. With the rule above, the only remaining source of difference is a platform that does
not implement `cents()` literally. **Trigger (decided here):** if any app cannot pass a
conformance case involving money after implementing section 3.1 literally, the format switches
every `money` field to integer cents (JSON integers, field names unchanged, sync format 2), and the
apps store cents. Until then, `double` plus this rule is the format.

---

## 4. Entities

Every table lists all fields. "Required" means a writer must write it when it creates the row; a
missing required field reads as its default like any other.

### 4.1 `bill`

A recurring or one-time obligation: a card, loan, utility, subscription, person, and so on.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | `""` | Display name, for example "Woodgrove Rewards". |
| `type` | string | `""` | Category. Known values: `Cards`, `Bills`, `Loans`, `Installments`, `Subscriptions`, `Business`, `People`, `Medical`, `Other`. Any other string is a custom type. `""` displays in the `Other` group. |
| `payment` | money | `0` | The regular amount due per occurrence. Legacy name `Cost`. |
| `remainingCheckpoint` | money | `0` | The balance checkpoint amount. The checkpoint time is the HLC of this field's winning record. The user never sees this directly; they see computed Remaining (section 5.1). Legacy: the stored `Remaining` (earlier `Owed`). Not negative. |
| `available` | money | `0` | Available credit, as the user entered it. Not derived from `creditLimit` and Remaining, and not changed by payments (same as today). |
| `creditLimit` | money | `0` | Credit limit. |
| `dueDay` | int | `1` | Day of month the bill is due, 1 to 31, for `Monthly` bills. A day past the month's end overflows into the next month (conformance/core fixtures, matching today's C#). |
| `rate` | enum | `Monthly` | `Monthly`, `Bi-Weekly`, `Yearly`, `Once`. How the bill repeats. An unknown value is treated as `Once` (never auto-assigned to a period) (decided here). |
| `apr` | number | `0` | Annual percentage rate in percent: `24.99` means 24.99%. `0` means none. Not rounded. |
| `autoPay` | bool | `false` | Paid automatically; excluded from manual "mark paid" totals. |
| `active` | bool | `true` | Inactive bills are kept but not assigned to periods or counted in totals. |
| `yearlyDate` | string? | `null` | `MM-DD` for `Yearly` bills, for example `03-15`. |
| `notes` | string? | `null` | Free text. |
| `targetPayoffDate` | date? | `null` | v1 feature 4: the date the user wants the balance paid off. The app computes the payment per period needed. |
| `notionPageId` | string? | `null` | The Notion page for this bill, so any device's connector updates the same page. |
| `createdAt` | timestamp? | `null` | When the bill was created. Display only. |
| `deleted` | bool | (required) | Tombstone. |

Today's `UpdatedAt` is not synced: it is derived as the latest HLC wall time among the row's
records (decided here).

### 4.2 `payment`

A record that money went (or was set aside) toward a bill. Payments merge as a set: each payment is
its own row.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `billId` | id | (required) | The bill. Written at create and never changed. |
| `kind` | enum | `Regular` | `Regular`: the "mark paid" payment for this bill in this period. `Extra`: an extra or one-time payment (v1 feature 4). `SetAside`: money put aside toward a future bill (v1 feature 3). Written at create and never changed. An unknown kind does not count against Remaining and is not shown (decided here). |
| `periodKey` | date | (required) | Start date of the pay period the payment belongs to (the `PayPeriod.Key` today). For `Extra`, the period containing the day it was made. Written at create and never changed. |
| `amountPaid` | money | `0` | Amount. Legacy name `AmountPaid`. |
| `paidAt` | timestamp? | `null` | Wall-clock time of the latest mark. Display only: ordering against checkpoints uses HLCs, never `paidAt`. |
| `confirmationNumber` | string? | `null` | v1 feature 5. |
| `note` | string? | `null` | v1 feature 5. |
| `notionPageId` | string? | `null` | The Notion Payments page. |
| `deleted` | bool | (required) | Tombstone. For a `Regular` or `SetAside` payment, `true` means "unmarked". |

Rules:

- Only `Regular` and `Extra` lower Remaining. `SetAside` never does.
- At most one `Regular` and one `SetAside` row exists per (bill, period), because their IDs are
  derived from (billId, periodKey). Unmark writes `deleted: true`; marking again writes
  `deleted: false` (and `amountPaid`, `paidAt`) on the same row.
- `Extra` rows get random IDs, so two extra payments always add up.
- Payments of a deleted bill stay as rows. Apps hide them; they do not cascade-delete (decided
  here).

### 4.3 `balanceSnapshot`

A saved point on the Insights "remaining over time" chart. Called `Snapshot` in today's code; the
spec renames it so it is not confused with sync snapshots (decided here).

| Field | Type | Default | Meaning |
|---|---|---|---|
| `snapshotDate` | date | (required) | The day the snapshot represents. |
| `totalRemaining` | money | `0` | Sum of computed Remaining over active bills that day. Legacy column `TotalOwed`. |
| `details` | string? | `null` | JSON object text mapping bill ID to that bill's Remaining, for example `{"019a...b1":1145.00}`. Informational; not read back today. |
| `notionPageId` | string? | `null` | The Notion Snapshots page. |
| `deleted` | bool | (required) | Tombstone. |

### 4.4 `incomeSource` (v1 feature 1)

One stream of pay: yours, a partner's, a side job.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | `""` | For example "Main job". |
| `cadence` | enum | `Irregular` | `Weekly`, `Bi-Weekly`, `Semi-Monthly`, `Monthly`, `Irregular`. Spelled like `bill.rate` (decided here). An unknown value is treated as `Irregular`. |
| `expectedAmount` | money | `0` | Expected amount of one paycheck. Fills the forecast until an actual is recorded. |
| `anchorDate` | date? | `null` | `Weekly` and `Bi-Weekly`: any one real pay date; the schedule steps 7 or 14 days from it in both directions. |
| `payDay1` | int? | `null` | `Monthly` and `Semi-Monthly`: day of month, 1 to 31. |
| `payDay2` | int? | `null` | `Semi-Monthly`: the second day of month. |
| `active` | bool | `true` | Inactive sources are kept but not forecast. |
| `note` | string? | `null` | Free text. |
| `deleted` | bool | (required) | Tombstone. |

For income, a pay day past the end of a month means the month's last day (`31` in February is the
28th or 29th) (decided here; note this differs from `bill.dueDay`, which overflows like today's
C#). No weekend or holiday adjustment in v1 (decided here). The forecast rules themselves belong
in `conformance/core/` when they are written.

### 4.5 `paycheck` (v1 feature 1)

An actual paycheck that arrived. Only actuals are stored; expected paychecks are computed from
`incomeSource`.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `incomeSourceId` | id | (required) | The source. Written at create and never changed. |
| `payDate` | date | (required) | The scheduled pay date this actual replaces. For an `Irregular` source, the day it arrived. Part of the ID; written at create and never changed. |
| `amount` | money | `0` | Amount that actually arrived. Overrides `expectedAmount` for that pay date. |
| `note` | string? | `null` | Free text. |
| `deleted` | bool | (required) | Tombstone. Deleting an actual brings back the estimate. |

Recording the same paycheck on two devices gives one row because the ID is derived from
(incomeSourceId, payDate).

### 4.6 `device`

One row per device that has joined the vault, so every app can show device names ("Notion:
connected on Windows PC"). Added by this spec (decided here).

| Field | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | `""` | User-visible name, for example "Windows PC". Editable. |
| `platform` | enum | `""` | `windows`, `macos`, `ios`, `android`, `web`. |
| `appVersion` | string? | `null` | The app version that last wrote this row. Written on join and after an app update. |
| `createdAt` | timestamp? | `null` | When the device joined. |
| `deleted` | bool | (required) | Tombstone (a "remove device" screen is not in v1). |

The row's ID is the device ID (`sync-protocol.md` section 1).

### 4.7 `setting`

One row per synced setting. The row ID is the setting key; the only field is `value`. There is no
`deleted` field: `value: null` means "use the default".

The canonical list is in section 6.

---

## 5. Derived values

These are computed by every app from the merged rows; they are never stored as fields. Fixtures:
`conformance/sync/merge.json` (`remaining`, `setAsideBalance`).

### 5.1 Remaining

For a live bill `b`:

```
cp      = the winning record of (bill, b, remainingCheckpoint), or none
cpCents = cents(cp.value) if cp exists and is a number, else 0
paid    = sum of cents(p.amountPaid) over payments p where
            p is live,
            p.billId == b,
            p.kind is "Regular" or "Extra",
            and (cp is none, or markHlc(p) > cp.hlc)        # strictly greater
Remaining(b) = max(0, cpCents - paid) / 100

markHlc(p) = the HLC of p's winning `deleted` record (which is `false`, since p is live)
```

What the user sees does not change from today: marking a bill paid lowers Remaining, unmarking
raises it back, typing a new Remaining sets it. What changes is how it is stored (design section
6.2; writer rules in `sync-protocol.md` section 5).

### 5.2 Set-aside balance

In v1, set-asides apply to every bill whose `rate` is `Yearly` (feature scope #3). A per-bill opt-in
field or other rates can be added later without breaking the format.

For a live bill `b`:

```
last    = the greatest periodKey among live Regular payments of b, or none
balance = sum of cents(p.amountPaid) over live SetAside payments p of b
          with a valid periodKey and (last is none, or p.periodKey > last)   # string order = date order
```

So set-asides made in the same period as the regular payment, or before it, are consumed by it
(decided here: the feature scope says "since its last Regular payment"; this pins "since" to
period order, not HLC order).

### 5.3 Safe to spend

Defined by the feature scope; computed in integer cents per section 3.2 and floored at zero:

```
safeToSpend(current period) = income in the period (actual paycheck amount where recorded,
                              otherwise expectedAmount, per active incomeSource)
                            - unpaid bills due in the period
                            - set-asides due in the period
                            - SafeToSpendBuffer
```

Its exact inputs (which bills, which paychecks fall in a period) are pay-period rules and belong in
`conformance/core/`.

---

## 6. Settings

### 6.1 Synced settings (canonical list)

Stored as `setting` rows. Keys are case-sensitive.

| Key | Value type | Default when absent or `null` | Meaning |
|---|---|---|---|
| `PayAnchor` | date | `2025-03-20` (today's C# fallback; onboarding should set it) | Any one pay-period start date; periods step 14 days from it. |
| `EarlyStart` | bool | `false` | The early-start preference. Note: today's C# applies the 3 PM early start unconditionally and never reads this setting. It syncs so that a later change can honour it. |
| `TypeColor/<type>` | string? `#RRGGBB` (uppercase hex) | The built-in colour for that type | Pill colour override for one bill type, for example key `TypeColor/Cards`. One row per type, so colour edits for different types on two devices both survive (decided here). Reset writes `null`. Apps match `<type>` to bill types case-insensitively; if two keys differ only in case, the one whose value record has the greater HLC wins. |
| `SafeToSpendBuffer` | money | `0` | v1 feature 2: amount kept back from safe-to-spend. |
| `ReminderDaysBefore` | int | `2` | v1 feature 6: days before a manual bill's due date to notify. |
| `NotionOwnerDeviceId` | string? (device ID) | `null` (no connector) | The one device that runs the Notion connector (decided here; `sync-protocol.md` section 12.5). |
| `NotionBillsDb` | string? | `null` | Notion data-source ID of the Bills database. |
| `NotionPaymentsDb` | string? | `null` | Notion data-source ID of the Payments database. |
| `NotionSnapshotsDb` | string? | `null` | Notion data-source ID of the Snapshots database. |

Custom bill types are not a separate setting: they exist as values of `bill.type` and sync with
the bills (decided here; the design listed "custom types" as a synced setting, but today's app has
no custom-type list, it derives them from bills).

### 6.2 Per-device settings and state (never synced)

These never appear in a change record. The list is canonical: anything not in 6.1 is per-device.

| Item | Where it lives | Notes |
|---|---|---|
| `Theme` | local settings | Light/dark/system. |
| Window size and position | local settings | Desktop apps. |
| Notion token | OS keystore (`NotionToken` in Windows Credential Manager today) | Per device; only the owner device needs one. |
| `LastNotionSync` | local settings | Owner device only. |
| Notion last-pushed values | local | Per bill, the Remaining (in cents) the connector last wrote to Notion; used for echo detection (`sync-protocol.md` section 5.3). |
| Reminders on/off | local settings | v1 feature 6: on/off is per device. |
| Drive provider and sign-in tokens | OS keystore | |
| Vault key(s) | OS keystore (web: non-extractable WebCrypto key in IndexedDB) | Never written to the drive. |
| Device ID, HLC state, read cursors, outgoing batches, next sequence number | local sync state | `sync-protocol.md` sections 1, 2, 10. |
| `SchemaVersion` | local settings | The local database's migration version. |
| Auto-backup files and rotation | local files | |

A JSON backup may include per-device settings; restoring it applies them only to the device doing
the restore (design section 6.5).

---

## 7. Storage inside an app (non-normative)

The format does not dictate local storage, but the merge needs, for every (entity, id, field), the
winning value and its HLC. A simple layout that keeps one schema across SQLite platforms:

```sql
CREATE TABLE SyncCells (
  Entity TEXT NOT NULL,
  RowId  TEXT NOT NULL,
  Field  TEXT NOT NULL,
  Value  TEXT NOT NULL,        -- the JSON value, as text
  Hlc    TEXT NOT NULL,        -- canonical HLC string
  PRIMARY KEY (Entity, RowId, Field)
);
```

The existing tables (`Bills`, `Payments`, ...) stay as materialized views of `SyncCells`, updated
in the same transaction as each merge. Unknown entities and fields live only in `SyncCells` and
are forwarded in snapshots (`sync-protocol.md` section 4.5).

---

## 8. Mapping from today's Windows schema

Today's Windows app (schema version 2, `apps/windows/PayDay/Services/DatabaseService.cs`) has four
tables: `Bills`, `Payments`, `Snapshots`, `Settings`. The migration to this model runs once, on the
device, before it creates or joins a vault. It is also how a legacy backup file is imported.

### 8.1 One migration HLC

The migration takes **one** HLC (one `tick`) and stamps every migrated record with it (decided
here). Because "after the checkpoint" is strict (section 5.1), payments that already exist are not
subtracted from the migrated Remaining a second time. Fixture: `merge.json`, "migration stamp".

### 8.2 Bills

| Today | Spec | Rule |
|---|---|---|
| `Id` TEXT, `"1"`–`"27"` (seed) or any non-UUID | `bill` row ID | `UUIDv5(namespace, "legacy-bill/" + oldId)`, using the old ID string exactly as stored (decided here). Deterministic, so the same legacy bill on the VM and the rig, or in an old backup, maps to the same ID. |
| `Id` TEXT, GUID | `bill` row ID | Lowercased. |
| `Name`, `Type`, `DueDay`, `Rate`, `AutoPay`, `Active`, `YearlyDate`, `Notes`, `NotionPageId` | same names, camelCase | `AutoPay`/`Active` INTEGER 0/1 become `bool`. |
| `Payment` (renamed from `Cost` in schema v2) | `payment` | |
| `Remaining` (renamed from `Owed` in schema v2) | `remainingCheckpoint` | Stamped with the migration HLC. |
| `Available`, `CreditLimit` | `available`, `creditLimit` | |
| `APR` | `apr` | |
| `CreatedAt` (SQLite `datetime('now')`, UTC, `YYYY-MM-DD HH:MM:SS`) | `createdAt` | Reformatted to `YYYY-MM-DDTHH:MM:SS.000Z`. |
| `UpdatedAt` | (not synced) | Derived (section 4.1). |
| (none) | `targetPayoffDate` | `null`. |
| (row exists) | `deleted` | `false`. |

Every reference to an old bill ID is rewritten with the same mapping: `Payments.BillId` and the
keys inside `Snapshots.Details`.

### 8.3 Payments

| Today | Spec | Rule |
|---|---|---|
| `Id` INTEGER AUTOINCREMENT | `payment` row ID | Group by (new billId, `PeriodKey`). The row with the lowest old `Id` becomes `kind: Regular` with the deterministic ID. Any further rows for the same pair (possible only through old imports) become `kind: Extra` with a UUIDv7 whose time is their `PaidAt` (decided here). |
| `BillId` | `billId` | Mapped (8.2). |
| `PeriodKey` | `periodKey` | Already `YYYY-MM-DD`. |
| `AmountPaid` | `amountPaid` | `round2`. |
| `PaidAt` (`datetime('now')` UTC, or a C# `"O"` string) | `paidAt` | Parsed as UTC when it has no zone; reformatted to milliseconds, sub-millisecond digits truncated. |
| `NotionPageId` | `notionPageId` | |
| (none) | `kind`, `confirmationNumber`, `note` | `Regular` (or `Extra`, above), `null`, `null`. |
| (row exists) | `deleted` | `false`. Unmark today deletes the row, so no migrated payment is unmarked. |

### 8.4 Snapshots

| Today | Spec | Rule |
|---|---|---|
| `Snapshots` table | `balanceSnapshot` | Renamed entity. |
| `Id` INTEGER | row ID | UUIDv7 whose time is `SnapshotDate` at 00:00 UTC (decided here). |
| `SnapshotDate`, `TotalRemaining` (renamed from `TotalOwed`), `Details`, `NotionPageId` | `snapshotDate`, `totalRemaining`, `details`, `notionPageId` | `details` keys rewritten (8.2). |

### 8.5 Settings table

| Today's key | Becomes |
|---|---|
| `PayAnchor`, `EarlyStart` | Synced settings of the same name (`EarlyStart` `"true"`/`"false"` text becomes `bool`). |
| `TypeColors` (JSON object, type to `#RRGGBB`) | One `TypeColor/<type>` setting per entry. |
| `NotionBillsDb`, `NotionPaymentsDb`, `NotionSnapshotsDb` | Synced settings of the same name. |
| `Theme`, `LastNotionSync`, `SchemaVersion` | Stay per-device. |
| (none) | `SafeToSpendBuffer`, `ReminderDaysBefore`, `NotionOwnerDeviceId`: written only when the user sets them. A device that has a Notion token when it creates the vault writes `NotionOwnerDeviceId` = its own ID. |

### 8.6 Legacy names

| Old | Current | Where the old name can still appear |
|---|---|---|
| `Cost` | `payment` | Backup JSON from before Phase 7c (`cost` key); `BackupSerializer` rewrites it on import. |
| `Owed` | Remaining (`remainingCheckpoint` in the sync format) | Backup JSON (`owed` key); Notion Bills column "Owed" (read fallback); schema v1 databases. |
| `TotalOwed` | `totalRemaining` | Schema v1 databases; Notion Snapshots "Total Owed". |
| `Snapshot` | `balanceSnapshot` | Today's C# class and table name. |

A backup's `remaining` (or legacy `owed`) is the computed Remaining at export time; importing it sets
`remainingCheckpoint` at the import's HLC. The backup file format itself (string payment IDs, new
entities) is specified with the backup fixtures in `conformance/core/`.

---

## 9. Changing this model

- **Adding** a field or an entity is not a breaking change. Older apps store and forward records
  they do not understand (`sync-protocol.md` section 4.5) and read missing fields as defaults. Add
  it here with a default, add fixtures, then ship.
- **Adding an enum value** is not breaking either; each enum says how older apps treat unknown
  values. Make sure that fallback is acceptable before adding one.
- **Renaming or retyping** a field, or changing a rule in section 5, is breaking. It needs a new
  sync format version (`sync-protocol.md` section 6.3) and a migration.
