# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 4 — chunk 2.** Three things, in order:

1. **Manual smoke test of the PayDay page.** It builds 0/0 but has never been launched. Confirm:
   - NavigationView shows "PayDay / About / Settings" (no more "Home").
   - PayDay page hero shows a "This Pay Period" label and a real date range (e.g. `May 1 – May 14, 2026` given the May 15 system date and the `2026-03-20` anchor).
   - Auto-pay `Expander` lists Google One / Spotify / Uber / YouTube Premium (the 4 seeded autopay subs) with a `$41.31` total.
   - Unpaid list shows manual bills with type pills tinted by category (Cards pink, Bills gold, etc.).
   - Click "Mark Paid" on one bill → moves to Paid list, totals update. Restart app → still in Paid list.
   - Click "Undo" → moves back to Unpaid.
   - Run from project root: `$env:PATH = "C:\Program Files\dotnet;$env:PATH"; dotnet run --project PayDay\PayDay.csproj` (or `winapp run` if a shell with WinAppCLI on PATH).

2. **All Bills page (plan §4.4).** New `Pages/AllBillsPage.xaml(.cs)` + `ViewModels/AllBillsPageViewModel.cs`. Group by `Type` with `CollectionViewSource`; `ToggleSwitch` for Active that upserts on toggle. Add a NavigationViewItem with Tag `bills` in `MainWindow.xaml` and route it in `MainWindow.xaml.cs`. The Add/Edit dialog is staged for chunk 3.

3. **VM tests in `PayDay.Tests/PayDayPageViewModelTests.cs`** using a fake `IDatabaseService`. Cases enumerated in `planning/current-sprint.md` §4.5.

Open `planning/current-sprint.md` and check off the chunk-2 items as they land. The sprint plan is up to date — chunk-1 boxes are ticked.

---

## Session — 2026-05-15 (Phase 4 chunk 1 closed)

PayDay page + shared converter/brush scaffolding shipped. App builds 0 warn / 0 err; all 21 Phase 3 tests still pass.

### What landed
- **Navigation rework**: renamed the "Home" NavigationViewItem to "PayDay" (`Tag="payday"`, glyph `&#xE8C7;` Money). Deleted `Pages/HomePage.xaml(.cs)`. Routing in `MainWindow.xaml.cs` now navigates `payday` → `PayDayPage`.
- **Type pill design system** (plan §4.8):
  - `PayDay/Styles/TypeBrushes.xaml` — 8 `SolidColorBrush` resources (`TypePillCards` pink, `TypePillBills` gold, `TypePillLoans` purple, `TypePillSubscriptions` red, `TypePillBusiness` blue, `TypePillPeople` green, `TypePillMedical` teal, `TypePillOther` gray).
  - Merged into `App.xaml` via `ResourceDictionary Source="ms-appx:///Styles/TypeBrushes.xaml"`.
  - `PayDay/Converters/TypeToBrushConverter.cs` — looks up `Application.Current.Resources[key]` by the type string; falls back to `TypePillOther` for unknown types so custom types still render.
- **Other converters**:
  - `PayDay/Converters/CurrencyConverter.cs` — formats `double`/`decimal`/`float` as `"C"` per `CultureInfo.CurrentCulture`.
  - `PayDay/Converters/BoolToVisibilityConverter.cs` — bool → Visibility with an `Invert` property (`{StaticResource BoolToCollapsed}` is the inverted alias).
- **`PayDay/ViewModels/PeriodBillRow.cs`** — view-only `ObservableObject` wrapping a `PeriodBill` with editable `AmountPaid`, `IsPaid`, `PaymentId`. Exposes a `DueDateLabel` for the row's "Due May 8" subtitle (or "Due this period" for Bi-Weekly).
- **`PayDay/ViewModels/PayDayPageViewModel.cs`** — `ObservableObject`. Constructs `PayPeriodService` and `PaymentService` from the injected `IDatabaseService`. `LoadAsync()` reads `GetCurrentPeriodsAsync()`, picks the first (the "This Pay Period"), reads its `Payment` rows, splits bills into `AutoPayBills` / `UnpaidBills` / `PaidBills`. Exposes `TotalDue / TotalPaid / Remaining / ProgressFraction / AutoPayTotal / IsAllPaid / HasCurrentPeriod / IsLoading` as `[ObservableProperty]`s. `MarkPaidCommand` / `UnmarkPaidCommand` / `MarkAllPaidCommand` use `[RelayCommand]` and round-trip to the DB via `PaymentService`.
- **`PayDay/Pages/PayDayPage.xaml`** — hero card with period label / range / `TotalDue` / `TotalPaid` / `Remaining` / `ProgressBar`, auto-pay `Expander` (header shows running total), `ItemsControl` for unpaid bills (type pill, name, `NumberBox` for amount, "Mark Paid" button bound through `ElementName=PageRoot`), `ItemsControl` for paid bills (faded, "Undo" button), all-paid `InfoBar` success state, footer "Mark All Paid" button.
- **`PayDay/Pages/PayDayPage.xaml.cs`** — instantiates the VM with `DatabaseService.Instance`, sets `DataContext`, calls `LoadAsync()` on `Loaded`.

### Notable details
- **MVVMTK0045 suppressed**: `[ObservableProperty]` on private fields isn't AOT-compatible for WinRT marshalling. The toolkit recommends partial properties. We tried — the generator does not emit implementations for partial properties under WinUI 3 today (CS9248 errors on every property). Suppressed via `<NoWarn>$(NoWarn);MVVMTK0045</NoWarn>` in `PayDay.csproj` with a comment block explaining why. Re-evaluate when targeting AOT-published builds.
- **`x:Bind` inside `DataTemplate`**: command bindings use `{Binding DataContext.MarkPaidCommand, ElementName=PageRoot}` with `CommandParameter="{x:Bind}"` — the `DataContext` is set on the page so the named element climb works.
- **Empty-period defense**: if `GetCurrentPeriodsAsync()` returns 0 entries (no anchor + no bills, or a strange clock), the VM clears all collections and `HasCurrentPeriod` is `false`. The XAML hides the section headers and Expander behind that flag.

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

- **PayDay page hasn't been launched yet** — it builds, but the chunk-1 close didn't include a manual smoke test. First task next session.
- **MVVMTK0045 suppressed for `PayDay.csproj`** — see the comment in the csproj. Doesn't affect debug runtime; only AOT-published builds.
- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available. Install Visual Studio with the WinUI workload for the best signal: `winget install Microsoft.VisualStudio.Community --override "--add Microsoft.VisualStudio.Workload.Universal"`.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0` (or the version line that matches the SDK NuGet in `PayDay.csproj`).
- **dotnet is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **Tests cannot reference `PayDay.csproj` directly.** The WindowsAppSDK auto-initializer module init throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. Use the `PayDay.Core` project for anything you need to unit-test. (The chunk-2 VM tests need to handle this — the VM type lives in `PayDay`, which means the test would need to either move the VM to `PayDay.Core` or accept that VM tests can't run from xunit. Likely solution: move `PayDayPageViewModel` + `PeriodBillRow` to `PayDay.Core/ViewModels/`, keep only the page code-behind in `PayDay`.)
- **`dotnet run --launch-profile PayDay`** warns that the profile doesn't exist but still launches via the WinAppSDK build hook. The warning is cosmetic.
