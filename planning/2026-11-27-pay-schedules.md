# Flexible pay schedules

**Date:** 2026-11-27 · **Status:** requirement and the decisions below are the developer's; the setting layout is a
proposal (Claude's call) to be finalised in `spec/` before building.

## Requirement

Most people are paid every two weeks, but not everyone. FutureCheck's pay periods must follow the
user's actual schedule:

- **Weekly**
- **Every two weeks** (today's only option)
- **Twice a month** (semi-monthly), for example the 1st and 15th
- **Monthly**

Today, pay periods are fixed at 14 days, stepping from the synced setting `PayAnchor`
(`spec/data-model.md`, settings; `PayPeriodService.GetPayPeriods`). Income sources already carry a
cadence (feature scope #1), but the periods themselves don't.

## Proposal

### The setting

Add a synced `PaySchedule` setting that defines the periods. Its fields mirror `incomeSource`:

| Field | Meaning |
|---|---|
| `cadence` | `Weekly`, `Bi-Weekly`, `Semi-Monthly` or `Monthly`. |
| `anchorDate` | `Weekly` and `Bi-Weekly`: any one real pay date. Periods step 7 or 14 days from it. |
| `payDay1` | `Semi-Monthly` and `Monthly`: day of month, 1 to 31. |
| `payDay2` | `Semi-Monthly`: the second day of month, greater than `payDay1`. |

- **Migration:** a missing or invalid `PaySchedule` falls back to `{ cadence: Bi-Weekly,
  anchorDate: PayAnchor }`. Existing installs and synced vaults keep exactly today's periods, with no
  migration step.
- **Old readers:** they ignore the new setting and keep using `PayAnchor`. Older apps on a
  non-biweekly vault therefore show biweekly periods until they're updated. The spec must say this.

### Period boundaries

- **The `periodKey`** is the period's start date, as today. Weekly, biweekly and monthly need nothing
  else.
- **Semi-monthly:** with `payDay1 = 1` and `payDay2 = 15`, the periods run 1st to 14th and 15th to
  the end of the month.
- **Days past the end of a month** (29, 30, 31) clamp to its last day. For example, day 31 in
  February becomes the 28th or 29th. The fixtures must cover leap years and a `payDay2` that clamps
  onto `payDay1`'s month end.
- **Monthly:** one period from `payDay1` to the day before the next `payDay1`.
- **The 3 PM early start** applies the same way to every cadence: the next period starts at 3 PM the
  day before. (`EarlyStart` is still unread; see the open conformance quirks.)

### Bills in periods

The rules don't change. A monthly bill goes in the period that contains its due date; a bill with
rate `Bi-Weekly` keeps its own 14-day cycle. With weekly periods, some periods get no monthly bills,
and that's expected.

## Decisions (developer, 2026-11-27)

1. **Changing schedule mid-stream: nothing is inferred.** A bill counts as paid in a period only if
   the user explicitly marked it paid. After a schedule change, a bill's new current period counts
   as paid only if there is an explicit `Regular` payment for that bill whose `paidAt` falls inside
   the new period. A bill that wasn't explicitly paid stays unpaid. Old payments are never rewritten.
   This needs a fixture.
2. **Weekend paydays: offered as a choice.** Setting: "If payday falls on a weekend" → *Use the
   Friday before* / *Keep the date*. It shifts the period start the same way for every cadence.
   Holidays are not in v1.
3. **New: "Start next pay period" button.** This is the manual version of today's automatic 3 PM
   early start, for when a check lands early.
   - If every bill in the current period is paid, start the next period right away.
   - If any are unpaid, warn first and name how many are left ("2 bills in this period aren't
     marked paid. Start the next period anyway?"). Unpaid bills keep showing as overdue in the
     period they belong to, as they do today.
   - It's synced, so every device agrees which period is current. Proposed: a synced setting holding
     the `periodKey` of the period started early. The early start ends at that period's normal start
     time.
4. **Several income sources on different cadences.** Periods follow one schedule, `PaySchedule`.
   Income from other sources lands in whichever period contains its pay date (feature scope #1
   already works this way).

## Where it fits in the build order

After the Windows sync exit test (design step 5) and before the macOS app (step 7). The Mac, web and
Android apps then reach parity through the conformance fixtures. Work:

1. `spec/data-model.md`: the `PaySchedule` setting, the boundary rules and the fallback.
2. `spec/conformance/core`: period fixtures for each cadence, month ends, leap years, and the
   schedule change.
3. Windows:
   - `PayPeriodService` builds periods from the schedule.
   - Settings and onboarding get a schedule picker. Today the Settings page only has an anchor date.
   - The weekend option and the "Start next pay period" button (with its warning) go on the PayDay
     page.
   - Every "biweekly" string in the app UI becomes cadence-neutral.
4. Website: the pay-schedule element (`data-roadmap="pay-schedules"`) goes live once Windows ships
   it.
