# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 4 — chunk 4.** Remaining Phase 4 pages, in plan order:

1. **Dashboard (plan §4.3)** — Stats grid (total monthly obligations, total owed, credit utilization %, bills due this period) + sortable period overview table (bills assigned to This / Next / Following). Auto-pay separated from manual within each section. The "sortable table" pattern landed here should also retrofit into All Bills.
2. **Payoff Tracker (plan §4.5)** — bills/loans with `Owed > 0`. Card per item: name, owed, payment, progress bar, estimated payoff months via `PayoffCalculator.EstimatePayoff`. Sorted by payoff timeline.
3. **Insights (plan §4.6)** — total-owed-over-time line chart (from `Snapshots`), spending-breakdown donut by Type, "Save Snapshot" button.
4. **Settings (plan §4.7)** — pay anchor configuration, theme toggle, Notion sync (deferred to Phase 5), Export/Import JSON.

After those: Phase 5 (Notion sync), Phase 6 (backup), Phase 7 (ship).

Open `planning/current-sprint.md` and tick off chunk-4 items as they land. Chunks 1–3 are closed.

---

## Session — 2026-05-15 (Phase 4 chunks 1 + 2 + 3a + 3b closed)

Three pages + bill editor dialog shipped + smoke-tested. **40 tests pass**, build 0 warn / 0 err.

### What landed in chunk 3b (this turn)
- **`PayDay.Core/ViewModels/BillEditorViewModel.cs`** — wraps a Bill, exposes typed `[ObservableProperty]` form fields. `CanSave` is true when Name is non-blank. `ApplyToOriginal()` copies back, clamps DueDay to 1–31, trims strings, nulls out blank Notes/YearlyDate. `KnownTypes` and `Rates` are public static arrays the dialog binds to for the ComboBoxes.
- **`PayDay/Dialogs/BillEditorDialog.xaml(.cs)`** — `ContentDialog` with Name, Type (editable ComboBox so users can type a custom value), Cost, APR, Owed/Available/Limit row, DueDay (1–31 spinner) + Rate, YearlyDate (MM-DD), AutoPay/Active toggles, Notes. `IsPrimaryButtonEnabled` bound to `ViewModel.CanSave`. Static `BillEditorDialog.ShowAsync(XamlRoot, Bill, isAddMode)` returns `true` on Save (with edits applied to the bill via `ApplyToOriginal`), `false` on Cancel.
- **`AllBillsPage`** — "Add New Bill" enabled with `Click="OnAddBillClick"`; creates a fresh Bill with `Guid.NewGuid().ToString()` id and opens the editor. Each bill row Border has `Tapped="OnBillRowTapped"` to open the editor for the existing bill. Guard: `IsAncestorOrSelf<ToggleSwitch>` walks the visual tree on `e.OriginalSource` so taps on the Active toggle don't bubble into the editor.
- **`PayDay.Tests/BillEditorViewModelTests.cs`** — 6 tests: ctor population, add-mode defaults (Type=Bills, Rate=Monthly), CanSave validation (empty + whitespace), ApplyToOriginal round-trip (incl. trimming), DueDay clamp, null-on-blank for Notes/YearlyDate.

### Notable details
- **`[ObservableProperty] private double _apr;` → property `Apr`** (not `APR`). The CommunityToolkit.Mvvm source generator drops the underscore and Pascal-cases the first letter only — leading acronyms aren't preserved as a unit. Hit it once during chunk-3b build and fixed in `ApplyToOriginal`.
- **Editable ComboBox for Type** — `IsEditable="True"` + `ItemsSource="{x:Bind vm:BillEditorViewModel.KnownTypes}"` + `Text="{x:Bind ViewModel.Type, Mode=TwoWay}"`. Users get the 8 canonical types as a dropdown but can type any custom string, satisfying plan §4.4 "custom type categories supported".
- **Tap-vs-toggle disambiguation** — ToggleSwitch consumes pointer events internally but Tapped is a routed event that bubbles. Belt-and-suspenders: visual-tree walk in `OnBillRowTapped` short-circuits if the original source descends from a ToggleSwitch.

### What landed earlier in this session
- **Chunk 1** (`9f5482e`): PayDay page + type pill design system + nav rework (Home → PayDay).
- **Chunk 2** (`8b7c777`): All Bills page + AutoPay dot converter.
- **Chunk 3a** (`5fb1b13`): VMs moved to `PayDay.Core/ViewModels/`, 13 VM tests added, top-tab nav.

### How to re-run the tests / build
```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --nologo
dotnet test  P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Tests\PayDay.Tests.csproj --nologo
dotnet run --project P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj
```

---

## Known issues / workarounds

- **Bill editor tweaks pending** — user noted "we can make some tweaks later" after the smoke test. No specifics captured; revisit when they raise it.
- **MVVMTK0045 is no longer a concern** — all `[ObservableProperty]` usages live in `PayDay.Core` (plain net9.0). The WinUI 3 partial-property generator gap (memory [[winui-mvvm-partial-properties]]) still exists upstream but doesn't affect this codebase.
- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available. Install Visual Studio with the WinUI workload for the best signal: `winget install Microsoft.VisualStudio.Community --override "--add Microsoft.VisualStudio.Workload.Universal"`.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0` (or the version line that matches the SDK NuGet in `PayDay.csproj`).
- **dotnet is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **Tests cannot reference `PayDay.csproj` directly.** The WindowsAppSDK auto-initializer module init throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. Use `PayDay.Core` for anything you need to unit-test — that's why the VMs live there.
- **`dotnet run --launch-profile PayDay`** warns that the profile doesn't exist but still launches via the WinAppSDK build hook. The warning is cosmetic.
