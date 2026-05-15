# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 5 — Notion sync.** Code complete across four chunks (5a–5d). 118/118 tests pass, build 0 warn / 0 err. **Manual smoke test pending** — see "Smoke test" section below.

Once the smoke test passes:
- Close Phase 5 in `SESSION_STATUS.md` + `planning/current-sprint.md`.
- Next: Phase 6 — auto-backup rotation in `LocalFolder/backups/` per §6.2 (manual JSON export/import already shipped in 4d).
- Then Phase 7 — package & ship.

If the smoke test surfaces issues (Notion property-name mismatch, token-saving HRESULTs, etc.), fix in a new chunk and update the sprint file.

## Smoke test — Phase 5

1. `dotnet run --project PayDay\PayDay.csproj` (kill any stale `PayDay.exe` first per [[winui-stale-process-lock]]).
2. Settings → Notion Sync → paste a Notion integration token → **Save**. Token should disappear from the box; status dot → gray with "Token saved. Run Test connection to verify."
3. **Test connection** → expect green dot + "Connected — token verified."
4. **Sync now** → expect green dot + "Synced — created N, updated M, pulled P." `LastNotionSync` setting persisted; the timestamp label updates.
5. Navigate to PayDay → mark a manual bill paid. Within ~1–2s, refresh the Notion Payments database — a new page should appear with `Bill ID = local Id`, `Period = period key`, `Amount Paid = cost`, `Name = "{Bill name} — {period key}"`.
6. Navigate to Insights → **Save Snapshot**. The Notion Snapshots database should receive a new page with `Date = today`, `Total Owed = sum`, `Name = "Snapshot YYYY-MM-DD"`.
7. (Optional) Edit a bill on the Notion side, hit Sync now → verify the local row picks up the change (last-write-wins on `last_edited_time`).

---

## Session — 2026-05-15 (Phase 5 code complete)

Phase 5 (Notion sync) shipped across four chunks. Build 0 warn / 0 err. 118 tests pass (was 84 at start of session).

### What landed across chunks 5a–5d

- **5a Credential store** (`23f99dc`) — `ICredentialStore` in `PayDay.Core`; `WindowsCredentialStore` (Advapi32 P/Invoke, `CRED_TYPE_GENERIC`, target `PayDay:{key}`, UTF-16LE blob) in the app project. No tests yet — abstraction consumed in 5b.
- **5b NotionSyncService** (`4beadf5`) — bills bidirectional sync via the 2025-09-03 data-sources API (`POST /v1/data_sources/{id}/query`, `POST /v1/pages` with `parent.type=data_source_id`). Last-write-wins on `UpdatedAt` vs `last_edited_time`. Match by `Bill ID` text property. Push helpers for Payments + Snapshots. Notion-Version header `2025-09-03`. `InternalsVisibleTo PayDay.Tests` added to `PayDay.Core.csproj`. `InMemoryCredentialStore` + `RecordingHttpHandler` test fakes. 13 NotionSyncServiceTests.
- **5c Auto-sync on payment + snapshot** (`c5a1ef8`) — `NotionPushStatus` enum (NotConfigured/Ok/Failed). `PayDayPageViewModel` + `InsightsPageViewModel` each take an optional `NotionSyncService?`; after a local persist they kick off `PushPayment/SnapshotSafeAsync` (fire-and-forget). Public `PendingNotionPush` Task surfaces the latest push for deterministic tests. `App.Credentials` + `App.Notion` are process-wide singletons. 8 AutoSyncTests.
- **5d Settings UI** (this commit) — Notion card on `SettingsPage.xaml`: PasswordBox + Save/Clear, Test connection + Sync now buttons (disabled until token set), ProgressRing, status row (Ellipse dot + status + last-synced label). `NotionStatusToBrushConverter` for the dot. `SettingsPageViewModel` extended with token + status state and `SaveTokenAsync` / `ClearTokenAsync` / `TestConnectionAsync` / `SyncNowAsync`. 10 SettingsPageNotionTests.

### Notable details

- **`Notion-Version: 2025-09-03`** is required for the data-sources API. The legacy `databases/{id}/query` endpoint won't accept the data-source IDs in our settings table. `parent` payload uses `{ "type": "data_source_id", "data_source_id": "..." }` — **not** `{ "database_id": "..." }`.
- **`PasswordBox.Password` is NOT a dependency property in WinUI 3** — two-way binding doesn't work. The page handler reads `TokenBox.Password` on click and calls `ViewModel.SaveTokenAsync(...)`.
- **Fire-and-forget pushes are deterministically testable via `PendingNotionPush`** — the VM assigns the latest started push task to this public property; tests await it after the VM action. Production code never awaits it (real fire-and-forget feel).
- **Push failure surfaces but doesn't roll back local state.** `PushPaymentSafeAsync` catches everything, sets `LastNotionPushStatus=Failed` + `LastNotionPushError`, and returns normally. Mark-paid stays applied locally.
- **Archive-on-delete (plan §5.2 last bullet) is deferred** — needs a tombstone table to distinguish "never existed locally" from "deleted locally last session". A TODO sits in `SyncBillsAsync`.
- **NotionPageId write-back on Payments/Snapshots is deferred** — they're push-only, and nothing reads the column yet. Add an `is_synced` column when we ever build push-resume-after-failure.
- **`InternalsVisibleTo PayDay.Tests`** on `PayDay.Core.csproj` lets internal helpers (`ParseSqliteUtc`, `BuildBillProperties`, `NotionPage`) stay internal while remaining test-visible.

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
