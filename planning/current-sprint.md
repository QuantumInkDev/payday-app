# Sprint: Phase 4 — UI / Views (Chunk 1: PayDay + All Bills)

**Started:** 2026-05-15
**Scope:** First two of the seven Phase 4 pages — the PayDay (main) view and the All Bills page. These exercise both halves of the Phase 3 business-logic layer (period engine + payment tracking) and the Phase 2 DB CRUD. The remaining pages (Dashboard, Payoff Tracker, Insights, Settings, Bill editor dialog) ship in later sprints.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §4.1 (nav), §4.2 (PayDay page), §4.4 (All Bills page), §4.8 (design system).
**Prior sprint:** `sprint-03-business-logic.md` (closed 2026-05-15, commit `985fdab`).

## Architectural notes

- **MVVM:** view models use `CommunityToolkit.Mvvm` (already referenced). `ObservableObject` for plain VMs, `[ObservableProperty]` / `[RelayCommand]` source generators for the rest.
- **Dependency seams:** new VMs take `DatabaseService` + business services through their ctor. For now the page navigates via the existing `Frame.Navigate(typeof(...))` flow, so VMs are constructed inside the page's `OnNavigatedTo` (no DI container yet — Phase 7 territory).
- **Threading:** all DB calls are async and return on the UI thread (we have a `DispatcherQueue` from the page). Long lists use `ObservableCollection<T>` so binding updates incrementally.
- **Period key:** strings of the form `yyyy-MM-dd` (period.Start) — already exposed via `PayPeriod.Key`.

## Tasks

### 4.1 — Navigation rework (plan §4.1)
- [x] `MainWindow.xaml` — renamed the "Home" `NavigationViewItem` to "PayDay" (Tag `payday`, icon `&#xE8C7;` Money glyph). "All Bills" item deferred to chunk 2.
- [x] `MainWindow.xaml.cs` — `payday` → `PayDayPage`. Removed the `home` case and deleted `Pages/HomePage.xaml(.cs)`.

### 4.2 — PayDay page (plan §4.2)  ✅ landed
- [x] `ViewModels/PayDayPageViewModel.cs` — `ObservableObject`. Public surface:
  - `ObservableCollection<AssignedPayPeriod> Periods`
  - `AssignedPayPeriod? CurrentPeriod` + `CurrentPeriodKey`
  - `ObservableCollection<PeriodBillRow> UnpaidBills` / `PaidBills` / `AutoPayBills`
  - `TotalDue`, `TotalPaid`, `Remaining`, `ProgressFraction`, `AutoPayTotal`, `IsAllPaid`, `HasCurrentPeriod`, `IsLoading`
  - `LoadAsync()` — pulls everything from `PayPeriodService.GetCurrentPeriodsAsync()` + `PaymentService.GetPeriodPaymentsAsync()`. Splits into the three lists.
  - `MarkPaidCommand(PeriodBillRow row)` / `UnmarkPaidCommand(PeriodBillRow row)` / `MarkAllPaidCommand`.
- [x] `ViewModels/PeriodBillRow.cs` — `ObservableObject`, wraps `PeriodBill` with `PaymentId? long`, `AmountPaid double`, `IsPaid bool`, plus a `DueDateLabel` helper.
- [x] `Pages/PayDayPage.xaml` — hero card (period label, range, totals, progress bar), auto-pay `Expander`, unpaid `ItemsControl` with type pill / NumberBox / Mark Paid, paid `ItemsControl` (faded) with Undo, "Mark All Paid" footer, `InfoBar` for the all-paid empty state.
- [x] `Pages/PayDayPage.xaml.cs` — constructs the VM with `DatabaseService.Instance`, sets `DataContext`, calls `LoadAsync()` on `Loaded`.

### 4.3 — All Bills page (plan §4.4)  ✅ landed
- [x] `ViewModels/BillGroup.cs` — plain wrapper exposing `Key` (type string) and `Bills` (ordered list). No observability; the VM rebuilds it on refresh.
- [x] `ViewModels/AllBillsPageViewModel.cs` — `LoadAsync` reads `DatabaseService.GetAllBillsAsync()`, groups by `Bill.Type` in plan-§4.8 order (Cards → Bills → Loans → Subscriptions → Business → People → Medical → Other, then any custom types alphabetically), bills within each group sorted by Name. `SaveBillAsync(Bill)` is the seam called by the Toggled handler. `RefreshCommand` re-runs `LoadAsync`.
- [x] `Pages/AllBillsPage.xaml` — title + count, refresh button, disabled "Add New Bill" (chunk-3), column header row, scrollable nested `ItemsControl` (outer = groups, inner = bills). Each row: auto-pay dot + type label, name + notes, cost, owed, due day, rate, active `ToggleSwitch`. Sortable columns are deferred — chunk-3 work along with the bill editor dialog.
- [x] `Pages/AllBillsPage.xaml.cs` — `OnActiveToggled` reads `Bill` from the toggle's `DataContext` and calls `ViewModel.SaveBillAsync`. Loads on `Loaded`.
- [x] `Converters/AutoPayDotConverter.cs` — bool → green (#00B894) for auto, amber (#FDCB6E) for manual.
- [x] `MainWindow.xaml(.cs)` — added the `bills` NavigationViewItem (Glyph `&#xE8FD;` = grid layout) and routed it to `AllBillsPage`.

### 4.5 — Tests  ✅ landed (chunk 3)
- [x] Moved `PayDayPageViewModel`, `PeriodBillRow`, `AllBillsPageViewModel`, `BillGroup` from `PayDay/ViewModels/` to `PayDay.Core/ViewModels/`. Added `CommunityToolkit.Mvvm` to `PayDay.Core.csproj`. `AllBillsPageViewModel` ctor switched to `IDatabaseService`. Added `UpsertBillAsync` to `IDatabaseService`. Dropped the now-dead `<NoWarn>MVVMTK0045</NoWarn>` from `PayDay.csproj`.
- [x] `PayDay.Tests/FakeDatabaseService.cs` — in-memory `IDatabaseService` with public `Bills` / `Payments` / `Settings` for direct test inspection.
- [x] `PayDay.Tests/PayDayPageViewModelTests.cs` — 7 tests: empty DB; one manual unpaid bill; `MarkPaidCommand` moves + persists; `UnmarkPaidCommand` restores + deletes; auto-pay bill isolated and doesn't block `IsAllPaid`; `MarkAllPaidCommand` clears the manual list; inactive bill excluded.
- [x] `PayDay.Tests/AllBillsPageViewModelTests.cs` — 6 tests: canonical group order; bills sorted within group; custom type falls after known types; `SaveBillAsync` upserts; `TotalBillsLabel` pluralizes; `RefreshCommand` re-reads.
- [x] **34 tests pass** total (21 Phase 3 + 13 chunk-3 VM tests).

### 4.4 — Shared resources  ✅ landed
- [x] `Styles/TypeBrushes.xaml` — 8 type pill brushes per plan §4.8 (`TypePillCards`, `TypePillBills`, `TypePillLoans`, `TypePillSubscriptions`, `TypePillBusiness`, `TypePillPeople`, `TypePillMedical`, `TypePillOther`). Merged into `App.xaml`.
- [x] `Converters/TypeToBrushConverter.cs` — string → `Brush`, falls back to `TypePillOther` for unknown types.
- [x] `Converters/BoolToVisibilityConverter.cs` — bool → Visibility (with `Invert` property for collapsed-when-true).
- [x] `Converters/CurrencyConverter.cs` — double/decimal/float → `"C"` format using `CultureInfo.CurrentCulture`.

### 4.5 — Tests — deferred to chunk 2
- [ ] `PayDay.Tests/PayDayPageViewModelTests.cs` against a fake `IDatabaseService`. Cases listed previously.

### Known tradeoff (resolved in chunk 3)
- **MVVMTK0045 no longer fires.** All `[ObservableProperty]` usages now live in `PayDay.Core` (plain net9.0, no WinRT). The WinUI 3 partial-property generator gap (memory [[winui-mvvm-partial-properties]]) is now irrelevant for our codebase. Suppression removed.

### Sprint exit criteria (chunks 1 + 2 + 3a closed)
- [x] `dotnet test PayDay.Tests` exits 0 — 34 tests pass (21 Phase 3 + 13 chunk-3 VM tests).
- [x] `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings, no suppressions.
- [x] **Manual smoke test done** — user launched the app twice this session, confirmed PayDay + All Bills render, then asked for the nav to switch from left pane to top tabs (now `PaneDisplayMode="Top"`).
- [x] Commit + push.

### Still open for chunk 3
- [ ] Bill editor `ContentDialog` + wire up "Add New Bill" button.
- [ ] Sortable columns on All Bills (probably wait until the Dashboard ships, since the table style choice should be shared).

## Next sprint

**Phase 4 chunk 3 (remaining)** — bill editor `ContentDialog`, sortable columns, then Dashboard / Payoff Tracker / Insights pages. After that, Phase 5 (Notion sync), Phase 6 (backup), Phase 7 (ship).
