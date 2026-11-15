# Core conformance fixtures

These JSON files pin down FutureCheck's core rules: pay periods, which bills fall in which period,
totals, payoff estimates, and the backup file format. Every app (Windows/C#, macOS/Swift,
Android/Kotlin, web/TypeScript) must load these files in its test suite and pass every case.

The expected values were generated from the current C# code, which is the reference implementation
(design doc §8). Nobody worked them out by hand. Where the C# behaviour looks surprising, the
fixtures still record what it does. Those cases are listed under [Known quirks](#known-quirks).

| File | What it covers | Cases |
|---|---|---|
| `pay-periods.json` | 14-day periods from an anchor, This/Next/Following labels, the 3 PM early start | 26 |
| `bill-assignment.json` | Which bills land in which period, and on what date (Monthly, Bi-Weekly, Yearly, Once) | 33 |
| `totals.json` | PayDay page totals, Dashboard headline figures and per-period sections, All Bills group subtotals | 36 |
| `payoff.json` | Months to pay off, and Payoff Tracker ordering (time to payoff, snowball, avalanche) | 27 |
| `backup.json` | Writing, reading and round-tripping the backup JSON, including legacy keys | 35 |

## File format

```json
{
  "version": 1,
  "description": "what the file covers and the exact input/expected shapes",
  "cases": [
    { "name": "unique, human-readable", "input": { }, "expected": { } }
  ]
}
```

- Each file's `description` field has the full rule text and the shapes for that file. The
  sections below summarise them.
- A file can hold several kinds of case. When it does, `input.kind` says which kind a case is.
- Case names are unique within a file. Run each case as its own test, named after the case, so a
  failure tells you which case broke.
- Dates are written `YYYY-MM-DD`. Local date-times are written `YYYY-MM-DDTHH:MM:SS` with no time
  zone, and mean wall-clock time on the device.
- Money is a JSON number with at most 2 decimals. Ratios have at most 4 decimals.
- Most dates fall in 2025. A few cases run into 2026 (year boundary) or 2028 (leap year).
- All names and amounts are made up.

### Comparing results

Build your result in the same shape as `expected`, then compare the two structurally:

- **Objects:** both must have exactly the same keys. Key order does not matter. A key that is
  present with the value `null` is not the same as a key that is missing.
- **Arrays:** same length, same order.
- **Strings, booleans, null:** must match exactly.
- **Numbers:** if the property name ends in `Fraction`, they must agree within **0.0001**. Every
  other number must agree within **0.005** (half a cent). Integers such as months and counts are
  covered by the same rule, which makes them effectively exact.

The tolerance covers floating-point noise (the C# code uses `double`). It does not cover a
different rule. If your platform only passes when you round differently, open an issue.

### Shared input: Bill

Bills appear in the inputs of `bill-assignment`, `totals`, `payoff` and `backup`. Any field left
out takes its default:

| Field | Type | Default | Notes |
|---|---|---|---|
| `id` | string | required | |
| `name` | string | `""` | |
| `type` | string | `""` | `Cards`, `Bills`, `Loans`, `Installments`, `Subscriptions`, `Business`, `People`, `Medical`, `Other`, or any custom string. Case-sensitive. |
| `payment` | number | `0` | Amount due each time the bill comes up |
| `remaining` | number | `0` | Outstanding balance |
| `available` | number | `0` | Only used by backup |
| `creditLimit` | number | `0` | |
| `dueDay` | integer | `1` | Day of month. Values outside 1..31 are not validated (see quirks). |
| `rate` | string | `"Monthly"` | `Monthly`, `Bi-Weekly`, `Yearly`, `Once`. Case-sensitive. Anything else never lands in a period. |
| `apr` | number | `0` | Annual percentage, so `24.99` means 24.99% |
| `autoPay` | boolean | `false` | |
| `active` | boolean | `true` | |
| `yearlyDate` | string or null | `null` | `MM-DD`, used when `rate` is `Yearly` |
| `notes`, `createdAt`, `updatedAt`, `notionPageId` | string or null | `null` | Only used by backup |

### String ordering

"By name" always means **ordinal, case-insensitive**: upper-case both strings, then compare them
code unit by code unit. Do not use locale collation. Every sort in these files is stable: items
that compare equal stay in input order. The fixtures only use ASCII names in sorted lists.

## pay-periods.json

`input { anchor: date, now: local date-time }` → `expected { periods: [3] }`. Each period is
`{ label, key, start, end, payday, isCurrent }`.

- Periods are 14 days long and sit on a grid of 14-day steps through `anchor`. The grid runs
  forwards and backwards, so the anchor can be in the past or the future.
- **Early start:** add 9 hours to `now` and take the date. From 15:00:00 on the day before a
  period starts, that period is already current. Before 15:00 the shift changes nothing.
- The period containing that shifted date is `This Pay Period`. The two after it are
  `Next Pay Period` and `Following Period`.
- `end` is `start` + 13 days (inclusive). `key` and `payday` both equal `start`.
- `isCurrent` is true only for the first period.

## bill-assignment.json

**kind `periods`:** `input { kind, anchor, now, bills }` → `expected { periods: [ { label, key,
start, end, bills: [ { id, dueDate } ] } ] }`.

- **Monthly:** try the month before the period's start month, then that month, then the month
  after. Take the first whose due date falls in `[start, end]`.
  - The due date is the 1st of the month plus `dueDay − 1` days. It **overflows** rather than
    clamping: day 31 in April is May 1, and days 29/30/31 in February 2025 are March 1/2/3.
- **Bi-Weekly:** in every period, with `dueDate` = period start. `dueDay` is ignored.
- **Yearly:** `yearlyDate` `MM-DD`, tried in the period start's year and then the next year,
  with the same overflow (`02-29` in 2025 is March 1).
  - It never lands if it does not split on `-` into exactly two integers, or if the month is
    outside 1..12.
- **Once, or any other rate:** never lands.
- **Inactive bills:** never land.
- Within a period, bills are ordered by `dueDate` ascending. Ties keep input order.

**kind `monthlyDueDate`:** `input { kind, dueDay, periodStart, periodEnd }` → `expected { dueDate }`
(a date or `null`). This is the Monthly rule on its own, for any window.

## totals.json

**kind `payDayPage`:** the PayDay home page for the current period. Input:
`{ kind, anchor, now, bills, payments: [ { billId, periodKey, amountPaid } ] }`.

- Auto-pay bills go in `autoPayBills`. Payments against them are ignored.
- A manual bill counts as paid if any payment has its `billId` and `periodKey` equal to the
  current period key. If there are several, only the first in input order counts.
- Each list keeps bill-assignment order.

Expected fields:

| Field | Value |
|---|---|
| `periodKey` | The current period key |
| `totalDue` | Sum of manual bills' `payment` |
| `totalPaid` | Sum of the counted `amountPaid` values |
| `remaining` | `max(0, totalDue − totalPaid)` |
| `progressFraction` | `totalDue > 0 ? min(1, totalPaid / totalDue) : 0` |
| `isAllPaid` | No unpaid manual bills, and (at least one paid bill or `totalDue == 0`) |
| `autoPayTotal` | Sum of auto-pay bills' `payment` |
| `autoPayBills`, `unpaidBills` | Lists of bill ids |
| `paidBills` | List of `{ id, amountPaid }` |

**kind `dashboard`:** `input { kind, anchor, now, bills }`. All headline figures use active bills
only.

| Field | Value |
|---|---|
| `totalMonthlyObligations` | Monthly `payment` + Bi-Weekly `payment × 26 / 12` + Yearly `payment / 12`. Every other rate adds 0. |
| `totalRemaining` | Sum of `remaining` |
| `hasCreditCards` | True if some bill has `type` exactly `Cards` and `creditLimit > 0` |
| `creditUtilizationPct` | Those cards' `remaining` ÷ their `creditLimit` × 100, rounded to 1 decimal. 0 when `hasCreditCards` is false. |
| `billsDueThisPeriod` | Bill count of the first section |
| `sections` | One per period: `{ label, key, manualTotal, autoPayTotal, grandTotal, billCount, manualBills, autoPayBills }` |

**kind `billGroups`:** the All Bills page. `input { kind, bills }` → `expected { totalBills,
groups: [ { type, count, totalPayment, totalRemaining, bills } ] }`.

- Bills are grouped by `type`. Grouping is exact and case-sensitive.
- Group order: `Cards, Bills, Loans, Installments, Subscriptions, Business, People, Medical,
  Other`, then any other type, by name.
- Within a group, bills are ordered by name.
- `count` includes inactive bills. `totalPayment` and `totalRemaining` count active bills only.

## payoff.json

**kind `estimate`:** `input { kind, remaining, payment, apr }` → `expected { months }`, where
`months` is an integer, the string `"never"`, or `null`.

- `null` when `remaining <= 0` or `payment <= 0`.
- When `apr <= 0`: `ceil(remaining / payment)`.
- Otherwise, with `r = apr / 12 / 100`:
  - `"never"` when `payment <= remaining × r`;
  - else `ceil(−ln(1 − remaining × r / payment) / ln(1 + r))`.

Use IEEE double arithmetic, as the reference does.

**kind `tracker`:** `input { kind, bills }`. Only active bills with `remaining > 0` are included.
`expected { itemCount, totalRemaining, items, order }`:

- `items` lists `{ id, months, hasLimit, limitUsedFraction }` in input order.
  - `hasLimit` is `creditLimit > 0`.
  - `limitUsedFraction` is `remaining / creditLimit` clamped to 0..1, or 0 without a limit.
- `order` holds three lists of ids:
  - `payoffTime`: bills with a month count first, ascending; then `"never"`; then `null`. Ties
    by name.
  - `snowball`: `remaining` ascending, then name.
  - `avalanche`: `apr` descending, then `remaining` ascending, then name.

## backup.json

- **kind `serialize`:** `input { kind, exportedAtUtc, bills, payments, snapshots, settings }` →
  `expected { document }`. Compare `document` structurally with the JSON you write.
  - Keys are camelCase: `formatVersion`, `exportedAt`, `bills`, `payments`, `snapshots`,
    `settings`. The bill APR key is `apr`.
  - `formatVersion` is `1`.
  - `exportedAt` is the UTC time with 7 fractional digits and a `Z`, for example
    `2025-05-15T12:00:00.0000000Z`.
  - A record property whose value is null is **left out**. A null value inside `settings` is
    **written** as `null`.
  - Payment fields: `id` (integer), `billId`, `periodKey`, `amountPaid`, `paidAt`,
    `notionPageId`. Snapshot fields: `id`, `snapshotDate`, `totalRemaining`, `details`,
    `notionPageId`.
- **kind `parse`:** `input { kind, document }` (a JSON object), or `input { kind, text }` for input
  that cannot be written as a plain object, such as invalid JSON or duplicate keys. The result is
  either `expected { rejected: true }` or `expected { backup }`.
  - `backup` is normalised: every field of every record is present, with defaults filled in.
    Payment defaults are `id 0`, `billId ""`, `periodKey ""`, `amountPaid 0`. Snapshot defaults
    are `id 0`, `snapshotDate ""`, `totalRemaining 0`. Optional strings default to `null`.
  - Missing or null `bills`, `payments`, `snapshots` or `settings` become empty.
  - Missing `exportedAt` becomes `""`.
  - Keys are case-sensitive. Unknown keys are ignored.
  - Inside a bill, the legacy keys `cost` and `owed` are read as `payment` and `remaining`.
  - The file is rejected if it is not valid JSON (comments and trailing commas count as invalid),
    if the root is not an object, if `formatVersion` is missing, below 1 or above 1, or if any
    value has the wrong JSON type.
- **kind `roundTrip`:** input as for `serialize` → `expected { backup }`: the normalised result of
  parsing what `serialize` wrote.

## Known quirks

These are C# behaviours that might be bugs. The fixtures record them as they are, so every app
behaves the same way. Changing one is a rule change: update the fixtures first (see below), then
every app.

1. **The early start can't be turned off.** The `EarlyStart` setting is saved and loaded, but the
   period calculation never reads it, so the 3 PM shift always applies.
2. **Due days overflow instead of clamping.** This was done on purpose to match the original
   `payday.html`.
   - Day 31 in a 30-day month moves to the 1st of the next month. So a "31st" bill is due on
     May 1 and May 31, and never in April.
   - Days 29 to 31 in February 2025 become March 1 to 3.
   - Day 0 and day 32 are not rejected. They become the last day of the previous month and the
     1st or 2nd of the next.
   - Yearly dates work the same way: `02-29` becomes March 1 in non-leap years, and `12-32`
     becomes January 1.
3. **Rate and type are case-sensitive.**
   - `monthly` (lower case) is not `Monthly`, so the bill never lands in a period.
   - A bill with type `cards` doesn't count toward credit utilization, and gets its own All Bills
     group.
4. **Ordering of bills due the same day isn't guaranteed in C#.** C# uses `List.Sort`, which is
   not a stable sort. It behaves stably for up to 16 bills in a period, which is what the
   fixtures capture. The rule all apps follow is to keep input order.
5. **A partial payment counts as paid.** On the PayDay page:
   - Any payment, even a short one or a 0 payment, marks a manual bill paid.
   - Only the first payment per bill per period is counted.
   - A period with no manual bills reports `isAllPaid = true`.
   - A 0-payment bill left unpaid keeps `isAllPaid` false.
6. **Floating-point month counts.** `120.48 / 10.04` is `12.000000000000002` in IEEE doubles, so
   the payoff estimate is 13 months, not 12. Any `double` implementation gets the same result.
   Integer cents would give 12. The design doc says to switch to integer cents if platforms ever
   disagree on rounding.
7. **No payoff dates.** Design doc §8 lists "payoff dates", but the reference only computes month
   counts. There is no calendar payoff date to capture yet. Target-date payoff is a v1 feature
   (`planning/2026-11-14-feature-scope.md`), and its fixtures should come with that rule.
8. **Old snapshot totals are lost on restore.** Snapshots stored `totalOwed` before the
   Owed→Remaining rename (chunk 7c), but only the bill keys `cost`/`owed` are migrated. Restoring
   a pre-7c backup gives every snapshot `totalRemaining = 0`.
9. **Backup parsing edge cases.**
   - A bill holding both `cost` and `payment` keeps whichever comes **later** in the file.
   - Capitalised `Cost`/`Owed` are not migrated.
   - `"exportedAt": null` stays `null`, although the C# property is declared non-nullable.
   - A root that is an array or `null` is rejected, but by an incidental exception rather than an
     explicit check.
10. **Credit utilization rounding.** C# rounds to 1 decimal with `Math.Round`, which rounds exact
    halves to even. With binary doubles, exact halves at the second decimal practically never
    happen, so half-up rounding also passes every case here. Use half-to-even to be safe.

## Regenerating expected values

The C# conformance tests live in `apps/windows/PayDay.Tests/Conformance/`. The same code checks
every case and can rewrite the expected values.

To add a case, add it with `"expected": null`. Then run:

```bash
PAYDAY_REGEN_FIXTURES=1 dotnet test apps/windows/PayDay.Tests --filter RegenerateFixtures
```

Review the diff before committing. Regenerating only rewrites `expected` fields. It rounds money
to 2 decimals and `*Fraction` values to 4, and writes 2-space-indented JSON with `\n` line
endings.

Without the environment variable, `RegenerateFixtures` does nothing. The test adapter rejects any
input key it doesn't recognise, so a typo in a fixture fails the test instead of silently falling
back to a default.
