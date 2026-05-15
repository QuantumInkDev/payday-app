# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 5 — Notion sync.** Phase 4 (UI / Views) closed 2026-05-15 across four chunks (4a–4d, commits `a4aa445` → `f0cafb9`). All seven §4.x pages ship, 84/84 tests pass, build 0 warn / 0 err.

Next: build `Services/NotionSyncService.cs` (plan §5) — bidirectional bill sync, push-only Payments + Snapshots. Three Notion data-source IDs already live in the seeded Settings table (`NotionBillsDb`, `NotionPaymentsDb`, `NotionSnapshotsDb`). Integration token goes into Windows Credential Manager (DPAPI), **not** the SQLite Settings table. The Settings page already has a Notion section stubbed under a `PHASE 5` tag — flesh that out (token field, "Test connection" button, "Sync now" button, last-synced timestamp).

After Phase 5: Phase 6 (auto-backup rotation in `LocalFolder/backups/` per §6.2 — manual JSON export/import already shipped in chunk 4d as part of Settings), then Phase 7 (ship).

Open `planning/current-sprint.md` (a fresh sprint file when starting Phase 5). Chunks 1–4 of Phase 4 are closed.

---

## Session — 2026-05-15 (Phase 4 closed)

Four chunk-4 pages + Settings backup wiring shipped. 84 tests pass, build clean. All Phase 4 §4.x pages are now live.

### What landed across chunks 4a–4d

- **4a Dashboard** (`a4aa445`) — 4 headline stats (monthly obligations rate-normalized, total owed, credit utilization %, bills due this period) + 3 period sections (This/Next/Following). Each section separates auto-pay vs manual and has client-side sortable columns (Type/Name/Due/Cost) — same-column click flips asc↔desc, switching columns resets to ascending. Sort indicator (▲/▼) bound via `[NotifyPropertyChangedFor]`.
- **4b Payoff Tracker** (`1d010fc`) — Card per active bill with `Owed > 0`. Per-item: type pill, name, payment label (with APR if present), owed, optional limit-utilization progress bar (clamped 0..1), payoff estimate label. Sorted by `(bucket, months)` tuple: concrete months first, "never at this rate" next (when payment ≤ monthly interest), uncomputable last.
- **4c Insights** (`b3f73b4`) — LiveCharts2 CartesianChart for total-owed-over-time (line + fill, accent purple), PieChart donut for type breakdown using pill-matching SkiaSharp colors, color-coded legend. "Save Snapshot" persists current total + JSON-ish per-bill breakdown and re-loads charts. Empty-state overlay when no snapshots yet. Page code-behind builds `ISeries[]` from VM plain data so `PayDay.Core` stays free of LiveCharts/SkiaSharp.
- **4d Settings** (`f0cafb9`) — Five cards: Pay Period (DatePicker writes via `PayPeriodService.SetPayAnchorAsync`), Appearance (RadioButtons; applies live via `MainWindow.ApplyTheme` and persists to Settings table), **Backup & Restore (functional JSON export + import using FileSavePicker/FileOpenPicker, with confirmation ContentDialog on import)**, Notion sync placeholder with PHASE 5 tag, About card. Theme applied at startup via `MainWindow.ApplyPersistedThemeAsync`.

### Notable details

- **`App.MainWindow` static** was required so file pickers can call `WinRT.Interop.WindowNative.GetWindowHandle(window)` + `InitializeWithWindow.Initialize`. Made it a public static property on the `App` class. Without it, picker.PickSaveFileAsync throws on WinUI 3.
- **`DateTimeOffset(dateTime, TimeSpan.Zero)` gotcha** — if `dateTime.Kind == Local` and the machine's UTC offset isn't zero, the ctor throws `ArgumentException`. `PayPeriodService.GetPayAnchorAsync` parses with `DateTimeStyles.AssumeLocal`, so it returns a `Local`-kind DateTime. Fix: construct with `new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero)` to strip the Kind. Hit it once in tests; fixed in `SettingsPageViewModel.LoadAsync`.
- **`RadioButtons.SelectedIndex` is `int`, not `enum`** — bound to a proxy `SelectedThemeIndex` property on the VM that converts to/from `AppTheme`. `[NotifyPropertyChangedFor(nameof(SelectedThemeIndex))]` on the underlying enum field keeps the index in sync.
- **`BackupSerializer.FromJson` refuses future-version files** — anything with `formatVersion` > 1 throws with a clear message. Forward-compatibility shim for when Phase 5/6 bumps the format.
- **`ReplaceAllDataAsync` is transactional** in the real DB — single `BeginTransaction → DELETE * → INSERT * → COMMIT`. If parsing the JSON succeeds but a row insert later fails, the DB rolls back. Tests cover the happy path; the bad-JSON test in `SettingsPageViewModelTests.ImportAsync_BadJson_ThrowsBeforeMutating` confirms parse-time failure leaves the DB untouched.
- **Live theme switching** — set `RequestedTheme` on the named `RootGrid` element. Application-level theme is immutable at runtime in WinUI 3, but `FrameworkElement.RequestedTheme` propagates to descendants.
- **Stale `PayDay.exe` blocks rebuilds** — `dotnet run` failed mid-session because PID 68368 from a prior smoke test was still holding `AppX\PayDay.exe`. `Get-Process PayDay | Stop-Process -Force` cleared it. Pattern: always make sure the previous smoke-test instance exited before re-running. Easy to miss because the WinUI host doesn't always close cleanly.

### How to re-run the tests / build

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --nologo
dotnet test  P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Tests\PayDay.Tests.csproj --nologo
dotnet run --project P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj
# If `dotnet run` fails with a file-lock error, kill the stale PayDay process first:
Get-Process PayDay -ErrorAction SilentlyContinue | Stop-Process -Force
```

---

## Known issues / workarounds

- **Sortable columns on All Bills are still deferred** — the sortable-table pattern shipped on the Dashboard's manual subsection should retrofit cleanly. Not blocking; revisit in a polish pass.
- **Bill editor tweaks pending** — user noted "we can make some tweaks later" after the chunk-3b smoke test. No specifics captured; revisit when they raise it.
- **MVVMTK0045 is no longer a concern** — all `[ObservableProperty]` usages live in `PayDay.Core` (plain net9.0). The WinUI 3 partial-property generator gap (memory [[winui-mvvm-partial-properties]]) still exists upstream but doesn't affect this codebase.
- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0`.
- **`dotnet` is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **Tests cannot reference `PayDay.csproj` directly.** The WindowsAppSDK auto-initializer module init throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. Use `PayDay.Core` for anything you need to unit-test — that's why the VMs live there.
- **`dotnet run --launch-profile PayDay`** warns that the profile doesn't exist but still launches via the WinAppSDK build hook. The warning is cosmetic.
- **Stale `PayDay.exe` after a prior smoke test** can lock `AppX\PayDay.exe`. Kill before re-running: `Get-Process PayDay | Stop-Process -Force`.
