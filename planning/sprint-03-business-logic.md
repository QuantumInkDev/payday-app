# Sprint: Phase 3 — Business Logic

**Started:** 2026-05-15
**Closed:** 2026-05-15
**Scope:** Port the JS pay-period engine, payment-tracking helpers, and payoff calculator to C#. No UI yet — that's Phase 4.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §3 (§3.1 Pay Period Engine, §3.2 Payment Tracking, §3.3 Payoff Calculator).
**Prior sprint:** `sprint-02-data-layer.md` (closed 2026-05-15, commit `0ee335f`).

## Tasks

### 3.0 — Models (new)
- [x] `PayDay.Core/Models/PayPeriod.cs` — `Start`, `End`, `Payday`, `IsCurrent`, `Label`, `Key` (record); also `AssignedPayPeriod` in the same file.
- [x] `PayDay.Core/Models/PeriodBill.cs` — record projecting `Bill` with the concrete `DueDate` that landed it in this period.

### 3.0a — PayDay.Core extraction (added mid-sprint)
- [x] **New `PayDay.Core` class library (net9.0)** — holds models + pure business logic. Created because the WinUI tests project pulls in `Microsoft.WindowsAppRuntime.Bootstrap` module initializers that crash without package identity (COMException 0x80040154). Splitting out a plain net9.0 library lets tests run without the WinUI runtime.
- [x] `PayDay.Core/Services/IDatabaseService.cs` — interface with the slice of DB methods that `PayPeriodService` / `PaymentService` need (`GetSettingAsync`, `SetSettingAsync`, `GetAllBillsAsync`, `InsertPaymentAsync`, `GetPaymentsByPeriodAsync`, `DeletePaymentsForBillInPeriodAsync`).
- [x] `PayDay/Services/DatabaseService.cs` now implements `IDatabaseService` and gained `DeletePaymentsForBillInPeriodAsync`.
- [x] `PayDay/PayDay.csproj` adds `<ProjectReference Include="..\PayDay.Core\PayDay.Core.csproj"/>`.

### 3.1 — Pay Period Engine (plan §3.1)
- [x] `PayDay.Core/Services/PayPeriodService.cs`
  - [x] `GetPayPeriods(DateTime anchor, DateTime today, int count = 8)` — pure function, ports the JS `getPayPeriods`. Walks 14-day windows from anchor, picks the period containing `today`, labels it "This / Next / Following", returns the 3 labeled periods.
  - [x] `GetBillDueDate(int dueDay, DateTime periodStart, DateTime periodEnd)` — checks prev/current/next month for the day-of-month that lands inside the window. Honors months that don't have the day (e.g. day 31 in Feb → falls through, returns null).
  - [x] `AssignBillsToPeriods(IEnumerable<Bill> bills, IReadOnlyList<PayPeriod> periods)` — for each period, filters bills by Rate (Bi-Weekly: always, Monthly: by due-day-in-window, Yearly: by MM-DD in window, Once: never). Inactive bills are excluded upstream by the caller (or here — decide). Sorted by `DueDate`.
  - [x] `GetCurrentPeriodsAsync(DatabaseService db)` — convenience: reads `PayAnchor` from Settings, gets active bills, returns assigned periods.
  - [x] `GetEarlyStartAsync` / `SetEarlyStartAsync` — persists the "Early Start" toggle in Settings.

### 3.2 — Payment Tracking (plan §3.2)
- [x] `PayDay.Core/Services/PaymentService.cs`
  - [x] `MarkPaidAsync(string periodKey, string billId, double amount)` — inserts Payments row, returns the new id.
  - [x] `UnmarkPaidAsync(string periodKey, string billId)` — deletes all matching rows for the period+bill combo.
  - [x] `GetPeriodPaymentsAsync(string periodKey)` — passes through to DatabaseService.
  - [x] `IsAllPaidAsync(string periodKey, IEnumerable<PeriodBill> manualBills)` — returns true if every non-AutoPay bill in the period has a payment.

### 3.3 — Payoff Calculator (plan §3.3)
- [x] `PayDay.Core/Services/PayoffCalculator.cs`
  - [x] Static `EstimatePayoff(double owed, double payment, double apr = 0)` — returns months (int) or `null` (no balance / no payment) or `int.MaxValue` (payment can't beat interest). Matches JS formula exactly.

### 3.4 — Tests
- [x] `PayDay.Tests/PayDay.Tests.csproj` — xunit on net9.0, references `PayDay.Core` only (NOT the WinUI project). **21 tests pass.**
- [x] `PayDay.Tests/PayPeriodServiceTests.cs`
  - [x] Anchor in past → current period contains "today"; labels are exactly This/Next/Following in order.
  - [x] Anchor in future → walks backwards by 14 days until it lands at-or-before today.
  - [x] Today exactly on anchor → anchor is the current period.
  - [x] `GetBillDueDate`: day-15 in a normal mid-month window → returns mid-month date.
  - [x] `GetBillDueDate`: day-31 in February window → returns null (no Feb 31).
  - [x] `AssignBillsToPeriods`: Bi-Weekly bill appears in every period.
  - [x] `AssignBillsToPeriods`: "Once" bills never appear.
  - [x] `AssignBillsToPeriods`: Yearly bill with `YearlyDate = "12-25"` lands in the period containing Dec 25.
- [x] `PayDay.Tests/PayoffCalculatorTests.cs`
  - [x] APR=0 → ceil(owed/payment).
  - [x] APR>0, payment > interest → matches amortization formula.
  - [x] payment <= interest → returns int.MaxValue (never).
  - [x] owed=0 or payment=0 → returns null.

### Sprint exit criteria
- [x] `dotnet test PayDay.Tests` exits 0 with all tests passing.
- [x] `dotnet build PayDay/PayDay.csproj` exits 0 (no warnings in our code).
- [x] No UI changes — app launches identically to Phase 2.
- [x] Commit + push.

## Next sprint

**Phase 4 — UI / Views.** Wire `PayPeriodService` into the `PayDay` page (the main view). NavigationView already has Home/About/Settings — replace Home with the PayDay page per plan §4.1.
