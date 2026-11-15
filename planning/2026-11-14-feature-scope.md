# FutureCheck: v1 feature scope

**Date:** 2026-11-14 · **Status:** decided (Claude's call, at the user's request to keep building) ·
**Parent:** `2026-11-14-multiplatform-design.md` §9 step 3

This settles which shortlist features ship in v1, so the synced data format can be frozen once in
`spec/`. Anything below that adds synced data is in the format from day one, even if a platform's
screen for it comes later.

## In v1

| # | Feature | What the user gets | Synced data it adds |
|---|---|---|---|
| 1 | **Income** | One or more income sources (yours, a partner's, a side job). Each has a cadence: weekly, biweekly, semimonthly, monthly or irregular. Expected amounts fill the forecast; recording what actually arrived overrides the estimate for that paycheck. | `IncomeSource` rows; `Paycheck` rows (actuals only) |
| 2 | **Forecast and safe-to-spend** | For This / Next / Following period: income minus bills due minus set-asides, and one "safe to spend" number for the current period. | Synced setting `SafeToSpendBuffer` (amount kept back) |
| 3 | **Set-asides** | Yearly bills spread across the paychecks before they're due ("set aside $40 this check for insurance"). Marking a set-aside done is like marking a bill paid. v1 applies set-asides to every bill with rate `Yearly`; a per-bill opt-in field and other rates can be added later without breaking the format. | `Payment.Kind = SetAside` rows; they never lower Remaining |
| 4 | **Extra payments and target-date payoff** | Pay extra on a card or loan at any time; set a payoff date and see the payment per period needed to hit it. | `Payment.Kind = Extra` rows (random IDs); `Bill.TargetPayoffDate` |
| 5 | **Confirmation numbers and CSV export** | Store a confirmation number and a note on any payment; export bills and payments to CSV. | `Payment.ConfirmationNumber`, `Payment.Note` |
| 6 | **Due-date reminders** | A notification a set number of days before a manual bill is due, with "Mark paid" on the notification where the platform allows it. | Synced setting `ReminderDaysBefore` (default 2); on/off is per device |

## Not in v1

- **Sharing with a partner.** Two people need two keys to one vault; the key-generation format
  (design §6.4) leaves room for it. Income sources (#1) already cover two incomes in one plan.
- **PDF export and home-screen widgets.** Pure presentation, no format impact; add any time.

## Rules the spec must encode

- Payment kinds: `Regular` and `SetAside` get deterministic IDs from (bill, period), as in design
  §6.2; `Extra` gets a random UUIDv7. Only `Regular` and `Extra` count against Remaining.
- Because a set-aside's ID comes from (bill, period), two devices marking it done produce one row.
- The set-aside balance for a bill is the sum of its `SetAside` payments since its last `Regular`
  payment.
- `Paycheck` IDs are deterministic from (income source, pay date), so recording the same paycheck
  on two devices merges into one row.
- Safe-to-spend for the current period = expected or actual income in the period − unpaid bills
  due in the period − set-asides due in the period − `SafeToSpendBuffer`, floored at zero.

## Where it gets built

Windows first, during or after design step 5 (sync), then the Mac and web apps as part of feature
parity. The data model and its conformance fixtures land in step 4.
