# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 4 — UI / Views.** Wire `PayPeriodService.GetCurrentPeriodsAsync()` into the main page. NavigationView currently has Home/About/Settings (from the scaffold) — replace `Pages/HomePage.xaml` with a `PayDay` page per plan §4.2 (hero section, auto-pay summary, unpaid/paid lists, "Mark All Paid"). The `All Bills` page (§4.4) is the second priority because it round-trips the DB and exercises the existing `Bill` CRUD.

Open `planning/current-sprint.md` (archive Phase 3 → `sprint-03-business-logic.md` first) and start with `Pages/PayDayPage.xaml` + `Pages/PayDayPage.xaml.cs` + a `ViewModels/PayDayPageViewModel.cs` using `CommunityToolkit.Mvvm`.

---

## Session — 2026-05-15 (Phase 3 closed)

Phase 3 (business logic) — closed in this session. **All 21 unit tests pass; WinUI app still builds 0 warn / 0 err and launches.**

### What landed
- **Extracted `PayDay.Core` class library** (net9.0, no WinUI deps) — holds all models + pure business logic. Reasoning: a WinUI-flavored test project fails at module init with `COMException 0x80040154 REGDB_E_CLASSNOTREG` because `Microsoft.WindowsAppRuntime.Foundation` injects auto-initializers that demand package identity. Putting pure logic in a plain net9.0 library lets tests run in a normal .NET process.
- `PayDay.Core/Services/IDatabaseService.cs` — interface DI seam. `DatabaseService` in PayDay now implements it; tests get nothing.
- `PayDay.Core/Models/PayPeriod.cs` — record with `Start`, `End`, `Payday`, `IsCurrent`, `Label?`, computed `Key` (yyyy-MM-dd). Also `AssignedPayPeriod(Period, Bills, Total)` in the same file.
- `PayDay.Core/Models/PeriodBill.cs` — record wrapping a `Bill` with its concrete `DueDate?` for the assigned period.
- `PayDay.Core/Services/PayPeriodService.cs` — port of the JS `getPayPeriods` / `getBillDueDate` / `assignBillsToPeriods`. Pure static methods are testable without DB; instance methods (`GetPayAnchorAsync`, `GetEarlyStartAsync`, `GetCurrentPeriodsAsync`) take `IDatabaseService`.
- `PayDay.Core/Services/PaymentService.cs` — `MarkPaidAsync`, `UnmarkPaidAsync`, `GetPeriodPaymentsAsync`, `IsAllPaidAsync(periodKey, bills)`. Last one only counts non-AutoPay bills (auto-pay items don't need manual confirmation).
- `PayDay.Core/Services/PayoffCalculator.cs` — static `EstimatePayoff(owed, payment, apr)` matching JS exactly: `null` for invalid input, `int.MaxValue` when payment can't beat monthly interest, else `Math.Ceiling(months)`.
- `PayDay/Services/DatabaseService.cs` — added `DeletePaymentsForBillInPeriodAsync(periodKey, billId)` to satisfy `IDatabaseService` (also fills a gap; we only had `DeletePaymentAsync(long id)` before).
- `PayDay.Tests/` — xunit on net9.0, references PayDay.Core only. 13 PayPeriod tests + 8 PayoffCalculator tests = 21 passing.

### Notable port details (worth remembering)
- **Date-31-overflow:** JS `new Date(2026, 1, 31)` rolls to March 3, 2026. C# `new DateTime(year, month, day)` throws on invalid days. To mirror JS behavior the port uses `new DateTime(year, month, 1).AddDays(day - 1)` — explicit overflow into the next month. There's a passing test (`GetBillDueDate_Day31InFebWindow_OverflowsToMarch3`) that documents this.
- **Default pay anchor:** `2026-03-20` (per plan §2.1, also seeded via `SeedData.DefaultSettings`).
- **3-label cap:** `GetPayPeriods` walks 8 candidate 14-day windows but only returns the 3 labeled "This / Next / Following". Matches JS.
- **Inactive filter:** `AssignBillsToPeriods` filters `bill.Active == false` up-front; callers do not need to.

### How to re-run the tests / build
```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet test  P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Tests\PayDay.Tests.csproj --nologo
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --nologo
```

---

## Known issues / workarounds

- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available. Install Visual Studio with the WinUI workload for the best signal: `winget install Microsoft.VisualStudio.Community --override "--add Microsoft.VisualStudio.Workload.Universal"`.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0` (or the version line that matches the SDK NuGet in `PayDay.csproj`).
- **dotnet is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **Tests cannot reference `PayDay.csproj` directly.** The WindowsAppSDK auto-initializer module init throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. Use the `PayDay.Core` project for anything you need to unit-test.
- **`dotnet run --launch-profile PayDay`** warns that the profile doesn't exist but still launches via the WinAppSDK build hook. The warning is cosmetic.
