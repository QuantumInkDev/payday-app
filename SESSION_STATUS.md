# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 4 — chunk 3.** Three things, in order:

1. **Manual smoke test of both shipped pages.** They build 0/0 but have never been launched. Confirm:
   - NavigationView shows "PayDay / All Bills / About / Settings".
   - **PayDay page**: hero shows "This Pay Period" + real date range (e.g. `May 1 – May 14, 2026` given the 2026-05-15 system date and `2026-03-20` anchor). Auto-pay Expander lists Google One / Spotify / Uber / YouTube Premium (~$41.31). Click "Mark Paid" → row moves to Paid list, totals update. Restart app → still in Paid list. Undo moves it back.
   - **All Bills page**: 27 seeded bills appear in groups (Cards → Bills → Loans → Subscriptions → Business → People → Medical → Other). Each row shows auto-pay dot (green for auto, amber for manual), name, cost, owed, due day, rate, active toggle. Flip a toggle → restart app → toggle state persisted.
   - Launch: `$env:PATH = "C:\Program Files\dotnet;$env:PATH"; dotnet run --project P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj`

2. **VM tests.** Move `PayDayPageViewModel` / `PeriodBillRow` / `AllBillsPageViewModel` / `BillGroup` from `PayDay/ViewModels/` to `PayDay.Core/ViewModels/` so xunit can reference them (the WinUI test-host workaround documented in [[payday-core-split]] is infeasible). Add `CommunityToolkit.Mvvm` to `PayDay.Core.csproj`. Write `PayDay.Tests/PayDayPageViewModelTests.cs` and `AllBillsPageViewModelTests.cs` with a fake `IDatabaseService`. Cases listed in `planning/current-sprint.md` §4.5.

3. **Bill editor dialog + "Add New Bill" (plan §4.4).** Currently the button is disabled with a tooltip. Add a ContentDialog with fields for Name / Type / Cost / Owed / Available / Limit / DueDay / Rate / APR / AutoPay / Active / YearlyDate / Notes. The Type field needs to support custom types (free-form ComboBox).

Open `planning/current-sprint.md` and check off the chunk-3 items as they land. The sprint plan is up to date — chunks 1 + 2 are ticked.

---

## Session — 2026-05-15 (Phase 4 chunks 1 + 2 closed)

PayDay page + All Bills page both shipped. App builds 0 warn / 0 err; all 21 Phase 3 tests still pass.

### What landed in chunk 2 (this session)
- **`PayDay/ViewModels/BillGroup.cs`** — plain wrapper `(string Key, IReadOnlyList<Bill> Bills, int Count)` for the grouped list. No observability; the VM rebuilds the whole collection on refresh.
- **`PayDay/ViewModels/AllBillsPageViewModel.cs`** — `LoadAsync` reads `DatabaseService.GetAllBillsAsync()`, groups by `Bill.Type` in plan-§4.8 canonical order (Cards → Bills → Loans → Subscriptions → Business → People → Medical → Other; custom types follow alphabetically). Bills inside each group sorted by Name. `SaveBillAsync(Bill)` persists per-bill changes (used today only for the Active toggle). `RefreshCommand` re-runs `LoadAsync`. `TotalBillsLabel` is a computed string ("27 bills across 5 types") that auto-notifies via `[NotifyPropertyChangedFor]`.
- **`PayDay/Pages/AllBillsPage.xaml`** — title + count, refresh button, disabled "Add New Bill" (chunk-3), column header row (TYPE / NAME / COST / OWED / DUE / RATE / ACTIVE), scrollable nested `ItemsControl`: outer iterates groups, inner iterates bills. Each row has auto-pay dot, type label, name + notes (ellipsized), cost, owed, due day, rate, `ToggleSwitch` for Active. Sortable columns are deferred (chunk 3).
- **`PayDay/Pages/AllBillsPage.xaml.cs`** — `OnActiveToggled` reads the `Bill` off `ToggleSwitch.DataContext`, mutates `bill.Active = ts.IsOn`, and awaits `ViewModel.SaveBillAsync`. The bound `IsOn` is OneWay so we don't fight the UI; the handler is authoritative.
- **`PayDay/Converters/AutoPayDotConverter.cs`** — bool → green `#00B894` for auto, amber `#FDCB6E` for manual (plan §4.8 status indicator).
- **`MainWindow.xaml(.cs)`** — added the "All Bills" NavigationViewItem (Glyph `&#xE8FD;` = grid layout) and routed `bills` → `AllBillsPage`.

### What landed in chunk 1 (earlier in this session, commit `9f5482e`)
- Nav rework: Home → PayDay, deleted `Pages/HomePage.*`.
- Type pill design system: `Styles/TypeBrushes.xaml`, `TypeToBrushConverter`, `CurrencyConverter`, `BoolToVisibilityConverter`. Merged into `App.xaml`.
- `PayDayPageViewModel` + `PeriodBillRow` — wired through `PayPeriodService` + `PaymentService` for hero/auto-pay/unpaid/paid lists with `MarkPaid` / `UnmarkPaid` / `MarkAllPaid` commands.
- `Pages/PayDayPage.xaml(.cs)` — hero card, auto-pay Expander, unpaid + paid ItemsControls, footer.
- `MVVMTK0045` suppressed in `PayDay.csproj` — partial-property pattern errors under WinUI 3 (see [[winui-mvvm-partial-properties]]).

### Notable details
- **Active toggle binding pattern**: `IsOn="{x:Bind Active, Mode=OneWay}"` + `Toggled="OnActiveToggled"`. TwoWay binding to plain CLR setters in WinUI 3 has gotchas around INotifyPropertyChanged; the handler-driven model is unambiguous and lets the await chain block on the DB write.
- **`Bill` is not observable** — the VM re-renders by rebuilding `Groups` on refresh. Sufficient for the current feature set; if we later want inline cost/owed edits, we'll need to wrap each bill in a row VM like `PeriodBillRow`.
- **Type grouping order** is a static array in the VM (`TypeOrder`). Custom user-defined types fall through to `int.MaxValue` and sort alphabetically after the known ones.

### How to re-run the tests / build
```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --nologo
dotnet test  P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Tests\PayDay.Tests.csproj --nologo
# To actually launch:
dotnet run --project P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj
```

---

## Known issues / workarounds

- **Neither page has been launched yet** — they build, but neither chunk got a manual smoke test. First task next session.
- **MVVMTK0045 suppressed for `PayDay.csproj`** — see the comment in the csproj and memory [[winui-mvvm-partial-properties]]. Doesn't affect debug runtime; only AOT-published builds.
- **VMs live in the WinUI project** — `PayDayPageViewModel` / `PeriodBillRow` / `AllBillsPageViewModel` / `BillGroup` aren't testable from xunit until they move to `PayDay.Core`. Planned as the second chunk-3 task.
- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available. Install Visual Studio with the WinUI workload for the best signal: `winget install Microsoft.VisualStudio.Community --override "--add Microsoft.VisualStudio.Workload.Universal"`.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0` (or the version line that matches the SDK NuGet in `PayDay.csproj`).
- **dotnet is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **Tests cannot reference `PayDay.csproj` directly.** The WindowsAppSDK auto-initializer module init throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. Use the `PayDay.Core` project for anything you need to unit-test.
- **`dotnet run --launch-profile PayDay`** warns that the profile doesn't exist but still launches via the WinAppSDK build hook. The warning is cosmetic.
