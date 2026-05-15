# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 4 — chunk 3 remainder.** Two things left:

1. **Bill editor `ContentDialog` + "Add New Bill" (plan §4.4).** Today the button is disabled with a tooltip. Add a dialog with fields for Name / Type (free-form ComboBox so custom types work) / Cost / Owed / Available / Limit / DueDay / Rate / APR / AutoPay / Active / YearlyDate / Notes. Open the same dialog on row click for edit; persist via `DatabaseService.UpsertBillAsync`. New bill IDs: `Guid.NewGuid().ToString()`.
2. **Sortable columns on All Bills.** Probably wait until the Dashboard page (plan §4.3) ships — both want a sortable table and should share the style. Punt this until then.

After that: Dashboard (§4.3), Payoff Tracker (§4.5), Insights (§4.6), Settings (§4.7) — then Phase 5 Notion sync.

Open `planning/current-sprint.md` and tick off chunk-3 items as they land. Chunks 1 + 2 + 3a (VMs moved, tests landed) are all closed.

---

## Session — 2026-05-15 (Phase 4 chunks 1 + 2 + 3a closed)

Two pages shipped + smoke-tested + top-tab nav + full VM test coverage. 34 tests pass, 0 warn / 0 err.

### What landed in chunk 3a (this turn)
- **`IDatabaseService.UpsertBillAsync(Bill)`** added — the seam `AllBillsPageViewModel.SaveBillAsync` calls. `DatabaseService` already implemented it; only the interface needed the addition.
- **Moved 4 VM files** to `PayDay.Core/ViewModels/`: `PayDayPageViewModel.cs`, `PeriodBillRow.cs`, `AllBillsPageViewModel.cs`, `BillGroup.cs`. Namespaces stayed `PayDay.ViewModels` so the page code-behinds didn't change.
- **`PayDay.Core.csproj`** now references `CommunityToolkit.Mvvm 8.4.2`. Since `PayDay.Core` is plain net9.0 (no WinRT), the MVVMTK0045 partial-property warning no longer fires anywhere.
- **`AllBillsPageViewModel(IDatabaseService)`** — was concrete `DatabaseService`, now the interface. The page passes `DatabaseService.Instance` and the typecheck is implicit.
- **Removed dead `<NoWarn>MVVMTK0045</NoWarn>`** from `PayDay.csproj` — the suppression was for VMs that no longer live there.
- **`PayDay.Tests/FakeDatabaseService.cs`** — in-memory `IDatabaseService` with public mutable `Bills` / `Payments` / `Settings`. UpsertBill replaces by `Id`. Payment ids auto-increment starting at 1.
- **`PayDay.Tests/PayDayPageViewModelTests.cs`** — 7 tests covering empty DB, single manual bill, MarkPaid, UnmarkPaid, auto-pay isolation, MarkAllPaid, inactive bill exclusion. Pinned anchor `2026-03-20` + today `2026-05-22` → current period key `2026-05-15`. Bills with DueDay=20 land inside.
- **`PayDay.Tests/AllBillsPageViewModelTests.cs`** — 6 tests covering canonical group order, name sort within group, custom-type-after-known, SaveBillAsync, TotalBillsLabel pluralization, RefreshCommand re-read.
- **Top-tab nav** — `MainWindow.xaml` switched to `PaneDisplayMode="Top"`, and the title bar's pane toggle button + handler were removed (no pane to toggle).
- **Manual smoke test done** — user confirmed both pages render and asked for the nav redesign.

### What landed earlier in this session
- **Chunk 1** (`9f5482e`): PayDay page + type pill design system + nav rework.
- **Chunk 2** (`8b7c777`): All Bills page + AutoPay dot converter.

### How to re-run the tests / build
```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --nologo
dotnet test  P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Tests\PayDay.Tests.csproj --nologo
dotnet run --project P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj
```

---

## Known issues / workarounds

- **MVVMTK0045 is no longer a concern** — all `[ObservableProperty]` usages live in `PayDay.Core` (plain net9.0). The WinUI 3 partial-property generator gap (memory [[winui-mvvm-partial-properties]]) still exists upstream but doesn't affect this codebase. If a future page-only VM ever needs to live in `PayDay.csproj`, the suppression note in that memory explains the workaround.
- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available. Install Visual Studio with the WinUI workload for the best signal: `winget install Microsoft.VisualStudio.Community --override "--add Microsoft.VisualStudio.Workload.Universal"`.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0` (or the version line that matches the SDK NuGet in `PayDay.csproj`).
- **dotnet is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **Tests cannot reference `PayDay.csproj` directly.** The WindowsAppSDK auto-initializer module init throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. Use the `PayDay.Core` project for anything you need to unit-test — that's why the VMs moved.
- **`dotnet run --launch-profile PayDay`** warns that the profile doesn't exist but still launches via the WinAppSDK build hook. The warning is cosmetic.
