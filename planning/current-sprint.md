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

### Sprint exit criteria (chunks 1 + 2 + 3a + 3b closed)
- [x] `dotnet test PayDay.Tests` exits 0 — 40 tests pass (21 Phase 3 + 13 chunk-3a VM tests + 6 chunk-3b editor tests).
- [x] `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings, no suppressions.
- [x] **Manual smoke tests done** — user confirmed PayDay + All Bills render, asked for top tabs (`PaneDisplayMode="Top"`), confirmed bill editor dialog works (Add + edit).
- [x] Commit + push.

### Chunk 3b — Bill editor  ✅ landed
- [x] `PayDay.Core/ViewModels/BillEditorViewModel.cs` — wraps a Bill, exposes typed form fields, `CanSave` (Name not empty), `ApplyToOriginal()` copies back with clamping (DueDay 1–31) and trimming.
- [x] `PayDay/Dialogs/BillEditorDialog.xaml(.cs)` — ContentDialog with Name / Type (editable ComboBox, custom types allowed) / Cost / APR / Owed / Available / Limit / DueDay / Rate / YearlyDate / AutoPay / Active / Notes. Static `ShowAsync(XamlRoot, Bill, isAddMode)` returns true on Save (and applies the edits back to the bill) or false on Cancel.
- [x] `AllBillsPage` — "Add New Bill" button now creates a `Guid.NewGuid()`-id bill and opens the dialog. Row Border `Tapped` opens edit. Guard: tap on the Active ToggleSwitch doesn't bubble into the edit handler (`IsAncestorOrSelf<ToggleSwitch>` check on `OriginalSource`).
- [x] `PayDay.Tests/BillEditorViewModelTests.cs` — 6 tests covering ctor population, add-mode defaults, CanSave validation, ApplyToOriginal round-trip, DueDay clamp, null-on-blank for Notes/YearlyDate.
- [x] Smoke-tested live by user.

### Still open
- [ ] Sortable columns on All Bills (probably wait until the Dashboard ships, since the table style choice should be shared).
- [ ] Tweaks to the bill editor — user mentioned holding for now.

## Phase 4 chunk 4 — closed 2026-05-15

All four §4.x pages shipped + smoke-tested. Build 0 warn / 0 err. 84/84 tests pass (was 40 entering chunk 4).

- [x] **4a Dashboard** (`a4aa445`) — `DashboardPageViewModel` + `DashboardPeriodSection` in `PayDay.Core/ViewModels/`. Stats grid (4 tiles: monthly obligations rate-normalized, total owed, credit utilization %, bills due this period) + 3 period sections with sortable columns (Type/Name/Due/Cost). Same-column click flips asc↔desc; switching columns resets to ascending. ▲/▼ indicators bound via `[NotifyPropertyChangedFor]`. Nav glyph E9D9 (BIDashboard). 10 new tests.
- [x] **4b Payoff Tracker** (`1d010fc`) — `PayoffTrackerPageViewModel` + `PayoffItem` in `PayDay.Core/ViewModels/`. Filters active bills with `Owed > 0`. PayoffItem wraps Bill + `PayoffCalculator.EstimatePayoff` result + clamped credit-limit progress fraction. Sort by `(Bucket, Months)` tuple — concrete months first, "never at this rate" next, uncomputable (zero payment) last; tiebreak by name. Nav glyph EB05 (PieSingle). 10 new tests.
- [x] **4c Insights** (`b3f73b4`) — `InsightsPageViewModel` aggregates `History` (ordered `SnapshotPoint` list) + `TypeBreakdown` (entries sorted descending by total cost). `SaveSnapshotAsync` persists current state + JSON-ish per-bill breakdown and re-loads. LiveCharts2 `CartesianChart` for owed-over-time line + `PieChart` donut with pill-matching SkiaSharp colors. Code-behind builds `ISeries[]` so `PayDay.Core` stays free of chart deps. Empty-state overlay when no snapshots saved. Nav glyph E9D2 (PieSingle). Added `InsertSnapshotAsync` / `GetAllSnapshotsAsync` to `IDatabaseService`. 9 new tests.
- [x] **4d Settings** (`f0cafb9`) — `SettingsPageViewModel` + `BackupSerializer` in `PayDay.Core/`. Five-card page: Pay Period (DatePicker writing via `PayPeriodService.SetPayAnchorAsync`), Appearance (RadioButtons; live theme switch on `MainWindow.RootGrid.RequestedTheme`, persisted to Settings table), Backup & Restore (FileSavePicker/FileOpenPicker round-trip, confirmation ContentDialog on import), Notion sync placeholder with PHASE 5 tag, About card. Theme applied at startup via `MainWindow.ApplyPersistedThemeAsync`. `IDatabaseService` extended with `GetAllPaymentsAsync`, `GetAllSettingsAsync`, `ReplaceAllDataAsync` (transactional). `BackupSerializer` is pure System.Text.Json with `formatVersion=1` + `exportedAt` header; refuses future versions. `App.MainWindow` is now a public static for picker `InitializeWithWindow`. 15 new tests.

### Still open (deferred)

- [ ] **Sortable columns on All Bills** — the Dashboard's sortable-table pattern (HyperlinkButton headers + `[NotifyPropertyChangedFor]` indicators) should retrofit. Probably wait for a polish pass.
- [ ] **Bill editor tweaks** — user noted "we can make some tweaks later" after chunk-3b. No specifics; revisit when raised.

## Next sprint

**Phase 5 — Notion sync** (`PAYDAY_WINUI3_PLAN.md` §5). Three Notion data-source IDs already seeded in the Settings table (`NotionBillsDb`, `NotionPaymentsDb`, `NotionSnapshotsDb`). Build `Services/NotionSyncService.cs` in the **app project** (not `PayDay.Core`, because credential storage uses Windows-only APIs). Bills sync bidirectional; Payments + Snapshots push-only.

Integration token storage: Windows Credential Manager (DPAPI), keyed off something like `PayDay.NotionToken`. The Settings page's Notion card already has the PHASE 5 stub — flesh it out with a `PasswordBox` for the token, "Test connection" button, "Sync now" button, and last-synced timestamp.

After Phase 5: Phase 6 (auto-rotating backups in `LocalFolder/backups/` — manual export/import already shipped), then Phase 7 (ship).
