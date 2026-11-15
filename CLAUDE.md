# PayDay App (product name: FutureCheck)

**Last Updated**: November 14, 2026
**Project Phase**: Multi-platform step 1 (repo restructure) done — next: feature scoping
**Current Version**: unknown — no version string is set in any `.csproj`, and `SESSION_STATUS.md` does not record one

## 🚀 What This App Does

PayDay is a personal finance tracker for Windows: it manages bills, credit cards, loans,
subscriptions and installment plans organized around biweekly pay periods. Data lives in a local
SQLite database, with a Notion integration that syncs bills bidirectionally and pushes payments and
balance snapshots up to three Notion databases. It is a native WinUI 3 desktop port of an earlier
single-file HTML/React app (`payday.html`) that stored data in localStorage and lost it when the
hosting context reset — durable local storage plus cloud backup is the whole reason the port exists.
(Source: `planning/PAYDAY_WINUI3_PLAN.md`.)

## 🧱 Stack & Target Frameworks

Taken from the `.csproj` files:

| Project | TargetFramework | Notes |
|---|---|---|
| `apps/windows/PayDay/PayDay.csproj` | `net9.0-windows10.0.26100.0` | **Windows-only TFM.** `OutputType=WinExe`, `UseWinUI=true`, `EnableMsixTooling=true`, `TargetPlatformMinVersion=10.0.17763.0`, `Platforms=x86;x64;ARM64`. RuntimeIdentifier defaults to `win-<host arch>`. |
| `apps/windows/PayDay.Core/PayDay.Core.csproj` | `net9.0` | Plain, cross-platform TFM. Compiles and unit-tests anywhere .NET 9 runs. |
| `apps/windows/PayDay.Tests/PayDay.Tests.csproj` | `net9.0` | Plain, cross-platform TFM. `IsPackable=false`. |

The app project's TFM is Windows-only — it can only be **built and run on Windows**. `PayDay.Core`
and `PayDay.Tests` are not, which is deliberate (see project layout below).

Key packages:

- `Microsoft.WindowsAppSDK` 2.0.1 + `Microsoft.Windows.SDK.BuildTools` 10.0.28000.1839 (WinUI 3)
- `Microsoft.Windows.SDK.BuildTools.WinApp` 0.3.1 — adds `dotnet run` support for packaged WinUI apps
- `CommunityToolkit.Mvvm` 8.4.2 (referenced by both `PayDay` and `PayDay.Core`)
- `Microsoft.Data.Sqlite` 10.0.8, `System.Net.Http.Json` 10.0.8
- `LiveChartsCore.SkiaSharpView.WinUI` 2.0.2 (Insights charts)
- Tests: `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2, `Microsoft.NET.Test.Sdk` 17.12.0, `coverlet.collector` 6.0.2

`ImplicitUsings` and `Nullable` are enabled on all three projects.

## 📁 Project Layout

- **`apps/windows/PayDay/`** — the WinUI 3 desktop app. XAML pages/views, code-behind, `App.xaml`,
  `app.manifest`, MSIX packaging assets (`Assets/`), and the packaged app identity. This is the only
  Windows-only project. Navigation is a `NavigationView` with pages: PayDay (home), Dashboard, All
  Bills, Payoff Tracker, Insights, Settings.
- **`apps/windows/PayDay.Core/`** — all logic that must be unit-testable: models, services
  (`IDatabaseService` — the `DatabaseService` implementation lives in `apps/windows/PayDay/Services/` — `PayPeriodService`, `NotionSyncService`, `TypeColorService`,
  `BackupSerializer`, …) and every view-model. **Tests cannot reference `PayDay.csproj`** — the
  WindowsAppSDK auto-initializer throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process
  without package identity. That constraint is why the VMs live here, not in the app project.
  `InternalsVisibleTo("PayDay.Tests")` keeps helpers internal but test-visible.
- **`apps/windows/PayDay.Tests/`** — xUnit suite over `PayDay.Core`. 194 tests as of the last verified run.
- **`tools/vm-build.sh`** — builds and tests a pushed commit on the arm64 Windows VM from the Mac.
- **`planning/`** — the build plan and sprint tracking. `PAYDAY_WINUI3_PLAN.md` is the master plan
  (Phases 1–8, DB schema, seed data, Notion DB IDs, design system). `current-sprint.md` tracks the
  active sprint's chunks, exit criteria, and outstanding items.
- **`tools/`** — `vm-build.sh` (builds and tests a pushed commit on the arm64 VM from the Mac) plus three PowerShell helpers and their output: `render-app-icon.ps1` and
  `preview-icon-candidates.ps1` (the app-icon pipeline, with `icon-candidates.png`) and
  `notion-diagnose.ps1`. Verified 2026-09-10.

Solution file: **there is none.** No `.sln`/`.slnx` exists anywhere in the repository (verified
2026-09-10 by a full-tree search), so this is by design, not an oversight.
Build and test commands below therefore target the project files directly, which is also what the
sprint's own exit criteria use.

## 🔨 Build, Test, Run

Run from the repository root, on Windows. The Windows projects live under `apps/windows/`; the repo is becoming multi-platform (product name FutureCheck, code name PayDay).

```powershell
# dotnet is NOT on PATH in this shell — do this first, every session:
$env:PATH = "C:\Program Files\dotnet;$env:PATH"

# Build the app (Windows only)
dotnet build apps/windows/PayDay/PayDay.csproj

# Build just the testable core (works on any .NET 9 host)
dotnet build apps/windows/PayDay.Core/PayDay.Core.csproj

# Run the tests
dotnet test apps/windows/PayDay.Tests

# Run the app — needs an interactive Windows desktop session (see below)
dotnet run --project apps/windows/PayDay/PayDay.csproj
winapp run                       # equivalent dev-mode launch
```

Standing expectation: **0 warnings / 0 errors** on both projects, and `dotnet test` exits 0.

**Build from the Mac:** run `tools/vm-build.sh` (push first; it builds `HEAD` on the VM). Launching the app still needs the interactive Windows session.

Phase 8 packaging commands (not yet exercised):

```powershell
winapp cert generate
winapp sign
winapp pack
```

## 💻 Where This Runs

- The app is used **daily on the arm64 Windows VM**.
- **arm64 verified 2026-09-09**: `dotnet build` clean, **194/194 tests pass**. Re-verified 2026-11-14 via `tools/vm-build.sh` at the head of PR #1: 194/194, 0 warnings.
- The **packaged (appx) launch requires an INTERACTIVE Windows session**. Over SSH or from a service
  context, appx registration fails with `0x80070005`. This is a session constraint, not an app bug.
- Clones (the git history was rewritten on 2026-11-14 to remove personal data — the VM and rig clones
  must be reset or re-cloned before anyone pushes from them; see `SESSION_STATUS.md`):
  - `/Users/quantumdev/Projects/PayDayApp` — this Mac
  - `C:\Projects\PayDayApp` — arm64 VM
  - `P:\Projects-Not-On-Cloud\PayDayApp` — rig (the x64 original)

## 📊 Current State

**Phase 7 — Polish is functionally complete and user-smoke-tested.** Eleven chunks shipped in the
2026-05-17 session (7a–7k), each committed and pushed individually so it stays independently
revertable:

| Chunk | Commit | What |
|---|---|---|
| 7a | `8f73958` | Sortable All Bills columns |
| 7b | `8d7935a` | Bill auto-sync on edit |
| 7c | `122be56` | Full rename Cost→Payment, Owed→Remaining (model + DB migration + Notion + UI) |
| 7d | `8c0e8fd` | Sync Now at top of nav + Pay decrements Remaining |
| 7e | `c928deb` | Dashboard polish |
| 7f | `72149aa` | All Bills overhaul (Expander groups, new columns, ordinals, auto-pay icon, custom types) |
| 7g | `89188d1` | Type tag color customization |
| 7h | `d1a8c02` | Installments type |
| 7i | `7c4fa77` | Insights snapshot delete/clear + Payoff snowball/avalanche |
| 7j | `08b5882` | Exclude inactive bills from BillGroup subtotals |
| 7k | `57a97b8` | PayDay sortable column toolbar + 3 PM early-start pay-period shift |

Tests went 150 → **194** across Phase 7. Phase 5 (Notion sync) and Phase 6 (backup/restore,
auto-backup) are closed.

**In flight:** nothing is mid-implementation. Work for the multi-platform design happens on
`feature/multiplatform` until merged to `main` (repo restructure done: Windows projects moved to
`apps/windows/`). That branch grew from the `wip/backup-2026-09-09` line, which sits on top of `cc74064`
(the 2026-05-17 end-of-night status commit that closed Phase 7). The next step is design §9 step 3
(`planning/2026-11-14-multiplatform-design.md`): scope the new features. **Phase 8 — Ship** (MSIX
packaging + sideload/Store distribution, `PAYDAY_WINUI3_PLAN.md` §8) is now design §9 step 12.
The Phase 7 leftovers below can be picked up alongside that work or as a Phase 7.5 mini-sprint; none
are blockers.

## 🕳️ Known Gaps

**Unfinished, per `SESSION_STATUS.md` and `planning/current-sprint.md`:**

- §7.1 carryovers:
  - Bill editor tweaks — **no specifics captured**; revisit when the user raises it.
  - Real app icon — still the temporary Segoe Fluent glyph (`e825`).
  - Notion archive-on-delete — needs a tombstone table; TODO sits in `SyncBillsAsync`.
  - NotionPageId write-back on Payments/Snapshots — only meaningful once push-resume-after-failure exists.
- §7.2 backlog: dark theme tuning across all pages; toast notifications (mark-paid, snapshot saved,
  sync ok/fail, backup created, restore applied); early-start sync (auto-kick on launch when a token
  is saved).
- §7.3 UI/UX consistency: empty states on All Bills / PayDay / Insights / Payoff; spacing + alignment
  sweep; focus rings + keyboard nav order review; accessibility audit
  (`AutomationProperties` + non-color affordances).
- Smoke coverage: Dashboard polish, custom types, type colors, snapshot manager, snowball/avalanche,
  PayDay sort, and 3 PM early-start were **not individually walked through** — covered by unit tests
  plus an end-of-night browse; the user signed off "good for now."

**User action still required (Notion DB schema, from chunk 7c):**

1. Rename Bills data-source column **"Owed" → "Remaining"**.
2. Rename Snapshots data-source column **"Total Owed" → "Total Remaining"**.
3. Add **"Installments"** as a select option on the Bills "Type" column (only if the type will be used).

Reads still tolerate the legacy "Owed" column via a fallback path, but **writes fail with a Notion
validation error** until the rename happens. `SESSION_STATUS.md` (2026-05-17) records that the user did the renames and added "Installments".

**Contents of `tools/`** — resolved 2026-09-10: `vm-build.sh` plus three PowerShell scripts (see Project Layout).

## 🚨 Development Notes & Workarounds

- **`dotnet` is not on `PATH`.** Prepend it at the top of every PowerShell session (see Build section).
- **`PasswordBox.Password` is not a dependency property in WinUI 3** — two-way binding does not work.
  The Settings page reads `TokenBox.Password` in the click handler and calls `SaveTokenAsync(...)`.
- **Notion API**: `Notion-Version: 2025-09-03` is required for the data-sources API. The legacy
  `databases/{id}/query` endpoint will not accept our data-source IDs. `parent` payloads use
  `{ "type": "data_source_id", "data_source_id": "..." }` — **not** `{ "database_id": "..." }`.
- **Fire-and-forget Notion pushes are testable via `PendingNotionPush`** — the VM assigns the latest
  started push task to that public property; tests await it after the VM action. Push failure surfaces
  (`LastNotionPushStatus=Failed`) but does **not** roll back local state.
- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain
  issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run`
  fails with `0x80073CF3` after a fresh setup: `winget install Microsoft.WindowsAppRuntime.2.0`.
- **`dotnet run --launch-profile PayDay`** warns the profile does not exist but still launches via the
  WinAppSDK build hook. The warning is cosmetic.
- **Stale `PayDay.exe` locks `AppX\PayDay.exe`** after a prior smoke test. Kill it first:
  `Get-Process PayDay | Stop-Process -Force`.
- **Inspecting the SQLite DB from outside the app:**
  `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`.
  Get the hash via
  `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **MVVMTK0045 is no longer a concern** — all `[ObservableProperty]` usages live in `PayDay.Core`
  (plain `net9.0`).

## 📋 Session Management

### Before Starting Work

1. **ALWAYS** read `SESSION_STATUS.md` for current status and the 🔴 **CONTINUE HERE** marker.
2. Review `planning/current-sprint.md` for the active phase's tasks.
3. Check Claude and Serena Memory for project context.

### After Completing Work

1. Ensure `dotnet test apps/windows/PayDay.Tests` exits 0 and `dotnet build apps/windows/PayDay/PayDay.csproj` is 0 warn / 0 err.
2. Update `SESSION_STATUS.md` with progress and next steps.
3. Move the 🔴 **CONTINUE HERE** marker to the stopping point.
4. Update `planning/current-sprint.md` with task status.
5. Clean up the TodoWrite list.
6. Update Claude and Serena Memory with session insights.
7. Update the phase and version at the top of this file.
8. **MANDATORY**: Commit and push to GitHub.

## 🔄 Git Workflow

**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Main branch:** `main`. Work for the multi-platform design happens on `feature/multiplatform` until merged to `main`.

### Branch Strategy

- **main**: production-ready code
- **feature/\***: individual feature development
- **hotfix/\***: critical bug fixes
- **wip/\***: backup branches for uncommitted work carried between machines

(The original plan also called for a `develop` integration branch; it has not been used — work has
landed as chunk commits.)

### Commit Message Format

```text
Session YYYY-MM-DD: Brief description of changes

- Detailed change 1
- Detailed change 2
- Important notes or decisions made

Closes #issue-number (if applicable)
```

Ship one commit per chunk so each is independently revertable — that is how Phases 5–7 were built.

### Pre-Commit Checklist

- [ ] `dotnet build apps/windows/PayDay/PayDay.csproj` — 0 warnings, 0 errors
- [ ] `dotnet test apps/windows/PayDay.Tests` — exits 0
- [ ] Documentation updated
- [ ] `SESSION_STATUS.md` updated

**Next Session**: See `SESSION_STATUS.md` for current status and the continue marker.
