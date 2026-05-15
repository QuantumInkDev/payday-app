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

### 4.3 — All Bills page (plan §4.4) — deferred to chunk 2
- [ ] `ViewModels/AllBillsPageViewModel.cs`, `Pages/AllBillsPage.xaml(.cs)`, group-by-Type with `CollectionViewSource`, active `ToggleSwitch` round-trips to DB.

### 4.4 — Shared resources  ✅ landed
- [x] `Styles/TypeBrushes.xaml` — 8 type pill brushes per plan §4.8 (`TypePillCards`, `TypePillBills`, `TypePillLoans`, `TypePillSubscriptions`, `TypePillBusiness`, `TypePillPeople`, `TypePillMedical`, `TypePillOther`). Merged into `App.xaml`.
- [x] `Converters/TypeToBrushConverter.cs` — string → `Brush`, falls back to `TypePillOther` for unknown types.
- [x] `Converters/BoolToVisibilityConverter.cs` — bool → Visibility (with `Invert` property for collapsed-when-true).
- [x] `Converters/CurrencyConverter.cs` — double/decimal/float → `"C"` format using `CultureInfo.CurrentCulture`.

### 4.5 — Tests — deferred to chunk 2
- [ ] `PayDay.Tests/PayDayPageViewModelTests.cs` against a fake `IDatabaseService`. Cases listed previously.

### Known tradeoff
- **MVVMTK0045 suppressed in `PayDay.csproj`.** `[ObservableProperty]` on private fields is not AOT-compatible for WinRT marshalling, and the toolkit recommends partial properties. We tried — the source generator does not emit implementations for partial properties in WinUI 3 projects (CS9248 errors). Suppressed until upstream lands the WinUI/CsWinRT path. Doesn't affect debug runtime, only future AOT publishing.

### Sprint exit criteria (chunk 1 closed)
- [x] `dotnet test PayDay.Tests` exits 0 with all 21 tests passing.
- [x] `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings (after MVVMTK0045 suppression).
- [ ] **Manual smoke test pending** — app needs to be launched to confirm the PayDay page actually renders the three periods with seeded bills assigned, and that Mark Paid persists across restart. (Couldn't run interactively from the remote-control session.)
- [x] Commit + push.

## Next sprint

**Phase 4 chunk 2** — Dashboard, Payoff Tracker, Insights pages, Bill editor dialog. Then Phase 5 (Notion sync), Phase 6 (backup), Phase 7 (ship).
