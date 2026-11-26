# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## Session 2026-11-14: multi-platform design and repo restructure

- Multi-platform design approved (`planning/2026-11-14-multiplatform-design.md`); product name locked in as **FutureCheck** (code name PayDay stays in the code).
- Repo restructured: `PayDay/`, `PayDay.Core/`, `PayDay.Tests/` moved with `git mv` into `apps/windows/` (all pure renames, history preserved, `Package.appxmanifest` unchanged). New root `README.md`.
- `tools/vm-build.sh [git-ref]` builds and tests a pushed commit on the arm64 VM from the Mac. Verified on 2026-11-14 at the head of PR #1 (after all scrub passes, on a freshly re-cloned VM checkout): 0 warnings, 0 errors, 194/194 tests passed. The earlier "OK" at `cfd55c6` was a silent no-op caused by a `vm-build.sh` bug that `ee9d898` fixed.

- **Git history rewritten to remove personal data (2026-11-14).** The repo is public, and `SeedData.cs`, `planning/PAYDAY_WINUI3_PLAN.md`, `planning/payday.html`, a few planning notes, the bill-editor placeholder text and several test fixtures held real bill names, balances, credit limits and Notion IDs. Every commit was rewritten with `git filter-repo` (five passes: the second and third caught card figures and two utility-bill amounts in test fixtures, the fourth fixed a derived test total, and the fifth replaced the sample-data due days, swapped brand names in test fixtures for made-up ones, and changed the pay anchor and the dates in the C# code and tests, so the real pay cadence can't be read off them) to use fictional sample data (Microsoft-style names such as Woodgrove, Contoso, Northwind; test fixtures use generic names such as Electric, Phone, Amber and Sparrow, and every amount is fictional) and placeholder Notion IDs (`00000000-…`). Installed apps keep their saved pay anchor; only a brand-new install starts from the new default. All branches on GitHub were force-pushed. **Every commit ID from before this changed**, and the IDs quoted in these notes were updated to the rewritten history. The old commits can stay reachable on GitHub by their old IDs until GitHub purges them, so a GitHub Support sensitive-data removal request was filed on 2026-11-15 (confirmation goes to the account email). The old-to-new maps and a full pre-scrub backup are kept off-repo in `~/Projects/archive/` on the Mac.
- `tools/notion-diagnose.ps1` now compares against placeholder IDs; pass your real data-source IDs if you use it. Installed apps keep their own Notion settings in their local database, so live sync is unaffected.

**Old clones must not push.** The rig (`P:\Projects-Not-On-Cloud\PayDayApp`), the VM daily clone (`C:\Projects\PayDayApp`) and any other copy still hold the old history with the real data. Pushing any branch from them would put it back on GitHub. (The VM's build checkout `C:\Builds\payday-app` was deleted on 2026-11-14; `tools/vm-build.sh` re-clones it.) Before doing anything else in an old clone, run `git fetch --prune origin`, then for each local branch `git switch <branch> && git reset --hard origin/<branch>` or simply re-clone. If an old clone has uncommitted edits you want to keep, copy those files out by hand first, because the reset discards them.

**On the rig and the VM daily clone** (these hold live data, so follow the steps in order):

1. Export a JSON backup from Settings, and copy `LocalState\payday.db` and `LocalState\backups\` somewhere outside the package folder (path pattern is in `CLAUDE.md`, "Inspecting the SQLite DB").
2. After this branch is merged: `git fetch --prune origin`, `git switch main`, `git reset --hard origin/main`. Don't use `git pull`: the history was rewritten, and a pull would merge the old history (with the real data) back in.
3. Move `PayDay\Properties\PublishProfiles\` (untracked, ignored `*.pubxml`) into `apps\windows\PayDay\Properties\`.
4. Build and run from `apps\windows\` and confirm your bills show. If the app opens with sample bills (Woodgrove, Contoso, Northwind…), it didn't find your database and seeded a fresh one: stop, restore your backup, and don't connect Notion from that install.
5. Only then delete the old root `PayDay`, `PayDay.Core` and `PayDay.Tests` folders. They hold only `bin/` and `obj/` plus any untracked files such as publish profiles.
6. If registration fails ("already installed from a different location"), do NOT uninstall the app, because that deletes LocalState. Stop and ask, or use `Remove-AppxPackage -PreserveApplicationData` only if you know what it does.

The package identity is unchanged, which is necessary but not enough on its own: following the steps above in order is what keeps the data safe.

---

## Session 2026-11-15: Windows sync (design step 5), PayDayKit

- PR #2: v1 feature scope, `spec/` (data model, sync protocol) and the conformance suite. Two agent reviews fixed a checkpoint double-subtract, same-day irregular paychecks, number-typing disagreements, compaction data loss, and restore handling of Notion keys and legacy colours.
- PR #4: spec edge cases found by the implementations (whole-string ASCII matching, pairing text folding, strict JSON incl. lone surrogates, vaultId, envelope check order). 423 sync cases.
- PR #3: C# sync engine library in `apps/windows/PayDay.Core/Sync/` passing every sync case.
- PR #5: SQLite storage for sync inside `payday.db` (new tables only, created when sync is turned on), `SyncRoutingDatabaseService` in front of every write (pass-through when sync is off), one-time migration that runs only on "turn on sync" and writes a never-rotated `presync` backup first.
- PR #6: sync loop against a drive interface (`SyncCoordinator`, `SyncScheduler`, in-memory drive with fault injection, multi-device end-to-end tests) plus turn-on safety fixes (Notion page matching after the ID migration, write gate, changed-fields-only saves).
- This branch: OneDrive provider (Microsoft Graph app folder, MSAL public client, no client ID committed), the Sync page, and the PR #6 review fixes (Notion owner push-first flag, no auto-claim, archive-on-delete, single-colour writes, persisted 24-hour timers).
- `apps/apple/PayDayKit` (branch `feature/apple-app`): Swift package with the core rules and the sync library, created and tested with the Xcode MCP; 599 tests incl. every core and sync case.
- The GitHub Support purge request was filed on 2026-11-15.

---

## Session 2026-11-25: Google Drive provider

- A OneDrive app ID needs an Entra tenant now (personal accounts can't register apps on their own), so Google Drive was added as the default provider: `apps/windows/PayDay.Core/Sync/Drive/GoogleDrive/` (installed-app OAuth with PKCE and a loopback redirect, refresh token in Credential Manager, Drive API v3 in the hidden app-data folder, duplicate folder and file names handled, permanent deletes). No client ID or secret is committed.
- `DriveChoice` picks the provider per device (Google Drive first, then OneDrive); the Sync page offers the choice while sync is off and keeps it fixed while in a vault. The page's text names the chosen provider.
- Tests: 1313 on the Mac (fake Google Drive and token endpoint; two devices syncing end to end through the Google provider).

---

## 🔴 CONTINUE HERE

**Waiting on the developer, then design step 5's exit test:**

1. **Google OAuth client** (free, any Google account, no billing). In console.cloud.google.com:
   1. Create a project (for example "FutureCheck").
   2. APIs & Services → Library → enable **Google Drive API**.
   3. Google Auth Platform → Get started: app name FutureCheck, your support e-mail, audience **External**.
   4. Data Access → Add or remove scopes: `.../auth/drive.appdata`, `openid`, `.../auth/userinfo.email`. All three are non-sensitive, so no Google review is needed.
   5. Audience → **Publish app** (status "In production"). In "Testing", Google expires sign-ins after 7 days.
   6. Clients → Create client → type **Desktop app**. Copy the client ID and the client secret (the secret is shown once; download the JSON).
   7. On each PC: `setx FUTURECHECK_GOOGLE_CLIENT_ID <id>` and `setx FUTURECHECK_GOOGLE_CLIENT_SECRET <secret>`, then sign out of Windows and back in (or restart) so the app sees them.

   Later platforms' clients (iOS, Android, web) must be created in the **same project**: the app-data folder belongs to the project.
   Then the manual test: Sync → Sync through "Google Drive" → Turn on sync (browser sign-in) → create the vault → save the phrase → Add a device; on the second PC join with the code; then edit on each side, lock/unlock, Notion owner, offline, turn off. Exit test: the VM and the rig syncing.

   OneDrive stays available for whoever has an Entra app ID (`FUTURECHECK_ONEDRIVE_CLIENT_ID`; registration details in `OneDriveConfig.cs`).
2. **Link PayDayKit in Xcode** (no MCP tool can add a package dependency): File → Add Package Dependencies… → Add Local… → `apps/apple/PayDayKit` → add to the FutureCheck target. Then merge `feature/apple-app` and start the Mac app's screens (design step 7).

**Before seeding the vault** (design §11): export JSON backups from both the VM and the rig and decide which holds the real data.

**Still open from the core fixtures** (C# quirks recorded as-is; see `spec/conformance/core/README.md`): old backups lose snapshot totals on restore (data-loss bug, fix), `EarlyStart` is never read, due days overflow into the next month, rate/type matching is case-sensitive, any payment marks a bill paid.

---

## Previous marker (Phase 7)

**Phase 7 — Polish** is functionally complete and user-smoke-tested. Eleven chunks shipped this session:

| Chunk | Commit | What |
|---|---|---|
| 7a | `8f73958` | Sortable All Bills columns |
| 7b | `8d7935a` | Bill auto-sync on edit (PushBillAsync + AllBills wiring) |
| 7c | `122be56` | Full rename Cost→Payment, Owed→Remaining (model + DB migration + Notion + UI) |
| 7d | `8c0e8fd` | Sync Now button at top of nav + Pay decrements Remaining |
| 7e | `c928deb` | Dashboard polish (row→editor, bigger subtotals) |
| 7f | `72149aa` | All Bills overhaul (Expander groups, new columns, ordinals, auto-pay icon, custom types, scrollbar fix) |
| 7g | `89188d1` | Type tag color customization (TypeColorService + Settings UI) |
| 7h | `d1a8c02` | Installments type |
| 7i | `7c4fa77` | Insights snapshot delete/clear + Payoff snowball/avalanche |
| 7j | `08b5882` | Exclude inactive bills from BillGroup subtotals (live-refresh on toggle) |
| 7k | `57a97b8` | PayDay sortable column toolbar + 3 PM early-start pay-period shift |

**Tests:** 194/194 green (was 150 entering Phase 7 — +44). Build 0 warn / 0 err on both projects.

**Smoke-test status:** Sortable All Bills + bill auto-sync verified by user mid-session; Notion DB columns renamed (Owed→Remaining, Total Owed→Total Remaining) and "Installments" select option added by user. Remaining smoke items (Dashboard polish, custom types, type colors, snapshot manager, snowball/avalanche, PayDay sort, 3 PM early-start) not individually walked through this evening — covered by unit tests + brief end-of-night browse; user signed off "good for now."

**Outstanding from Phase 7 plan (§7.1/§7.2/§7.3):**

- **§7.1 carryovers:**
  - [x] Sortable All Bills (7a) ✅
  - [ ] Bill editor tweaks — no specifics captured; revisit when user raises
  - [ ] Real app icon — still the temporary Segoe Fluent glyph (`e825`)
  - [ ] Notion archive-on-delete — needs tombstone table; TODO in `SyncBillsAsync`
  - [ ] NotionPageId write-back on Payments/Snapshots — only meaningful when push-resume-after-failure is built
- **§7.2 backlog:**
  - [ ] Dark theme tuning across all pages
  - [ ] Toast notifications (mark-paid, snapshot saved, sync ok/fail, backup created, restore applied)
  - [ ] Early-start sync (auto-kick on launch when a token is saved)
  - [x] Auto-backup (Phase 6) ✅
- **§7.3 UI/UX consistency:**
  - [ ] Empty states on All Bills / PayDay / Insights / Payoff
  - [ ] Spacing + alignment sweep
  - [ ] Focus rings + keyboard nav order review
  - [ ] Accessibility audit (AutomationProperties + non-color affordances)

**🔧 User action required after this session (Notion DB schema changes):**

1. **Rename column "Owed" → "Remaining"** in the Bills data source.
2. **Rename column "Total Owed" → "Total Remaining"** in the Snapshots data source.
3. **Add "Installments" as a select option** on the Bills "Type" column (only if you plan to use the new type).

Reads still tolerate the legacy "Owed" column (fallback path), but **writes will fail with a Notion validation error** until the column is renamed.

After smoke test passes and the user confirms, the next phase is **Phase 8 — Ship** (MSIX packaging + sideload/Store distribution per `PAYDAY_WINUI3_PLAN.md` §8). The Phase 7 §7.x leftovers above can be picked up alongside Phase 8 or as a Phase 7.5 mini-sprint.

---

## Session — 2026-05-17 (Phase 7 chunks 7a–7i)

Worked through the user's polish list — captured 15 items rapid-fire, confirmed, and shipped them as 7 back-to-back chunks (7c–7i) after 7a + 7b which landed earlier in the session. Every chunk committed and pushed individually so each is independently revertable.

### Late-session additions (after first smoke test)

- **7j** — `BillGroup.TotalPayment` / `TotalRemaining` now filter on `Active`; All Bills code-behind calls `LoadAsync()` after toggling Active so the affected group's header updates live. Inactive bills still appear in the list so they can be toggled back on.
- **7k** — Sortable column toolbar on PayDay page (single global selection applied to Unpaid / Paid / Auto-Pay), plus `PayPeriodService` shifts `today` forward 9 hours before truncating to `.Date` so a new pay period opens "for paying" at 3 PM the day before its official start. `GetCurrentPeriodsAsync` now reads `DateTime.Now` (not `DateTime.Today`) so the time-of-day component is preserved.

### Key implementation notes

- **Cost→Payment / Owed→Remaining rename (7c)** was the deepest change. Touched the Bill model, Snapshot model, SQLite schema (v1→v2 migration with `ALTER TABLE RENAME COLUMN`), Notion property names, BackupSerializer (with backward-compat for legacy `cost`/`owed` JSON keys), every VM, every XAML, every test. The DB migration is idempotent (checks `PRAGMA table_info` for the old column name before renaming).
- **Pay decrements Remaining (7d)** — `PayDayPageViewModel.MarkPaidAsync` now also `UpsertBillAsync` after subtracting the paid amount (clamped to zero) and fires a fire-and-forget `PushBillAsync` alongside the existing payment push. `UnmarkPaidAsync` mirrors for the refund case. No APR/interest math — that's on the user.
- **Sync Now at top of nav (7d)** — `NavigationView.PaneFooter` hosts the button + last-synced label, accessible from every page.
- **Expander groups on All Bills (7f)** — collapsible per-type; header shows the rounded-square (CornerRadius=4) type pill plus TotalPayment + TotalRemaining subtotals computed on `BillGroup`.
- **Custom types in editor (7f)** — `BillEditorDialog.ShowAsync` queries distinct `Type` values from existing bills and seeds them into `BillEditorViewModel.TypeOptions` alongside `KnownTypes`. No separate settings table.
- **TypeColorService (7g)** — process-wide static state, loaded from Settings JSON at app startup. `TypeToBrushConverter` and `InsightsPage.ColorForType` both read through it. Tests use `ResetInMemory()` to isolate state between cases.
- **Snowball/avalanche (7i)** — `PayoffTrackerPageViewModel` caches the unsorted list and re-sorts in place when `StrategyIndex` changes. A `RadioButtons` group at the top of the page selects between Time-to-payoff (default), Snowball, Avalanche.

### What changed at the surface

- All UI references "Payment" and "Remaining" (was "Cost" and "Owed").
- All Bills page: collapsible Expander groups, rounded-square type pills, new APR/Available/Limit columns, sync-glyph for auto-pay rows, ordinal Due (1st/2nd/12th).
- Dashboard: rows are clickable (opens editor); subtotal totals bigger than per-row amounts.
- Insights: "Manage…" button → delete individual snapshots or clear all.
- Payoff: sort strategy radio buttons (Time / Snowball / Avalanche).
- Settings: new "Type Colors" card with per-type ColorPicker.
- Top nav: Sync Now + last-synced label always visible.

---

## Session — 2026-05-15 (Phase 5 closed)

Phase 5 (Notion sync) shipped across five chunks (5a–5e). Build 0 warn / 0 err. 121 tests pass (was 84 at start of session). End-to-end smoke test passed: token save → Test → Sync Now → mark-paid pushes to Payments DB → Save Snapshot pushes to Snapshots DB.

### Notable details (Phase 5)

- **`Notion-Version: 2025-09-03`** is required for the data-sources API. The legacy `databases/{id}/query` endpoint won't accept the data-source IDs in our settings table. `parent` payload uses `{ "type": "data_source_id", "data_source_id": "..." }` — **not** `{ "database_id": "..." }`.
- **`PasswordBox.Password` is NOT a dependency property in WinUI 3** — two-way binding doesn't work. The page handler reads `TokenBox.Password` on click and calls `ViewModel.SaveTokenAsync(...)`.
- **Fire-and-forget pushes are deterministically testable via `PendingNotionPush`** — the VM assigns the latest started push task to this public property; tests await it after the VM action.
- **Push failure surfaces but doesn't roll back local state.** `PushPaymentSafeAsync` catches everything, sets `LastNotionPushStatus=Failed` + `LastNotionPushError`, and returns normally.
- **`InternalsVisibleTo PayDay.Tests`** on `PayDay.Core.csproj` lets internal helpers (`ParseSqliteUtc`, `BuildBillProperties`, `NotionPage`) stay internal while remaining test-visible.

---

## Known issues / workarounds

- **Notion DB column rename (post-7c)** — see "User action required" above.
- **Bill editor tweaks pending** — user noted "we can make some tweaks later" after the chunk-3b smoke test. No specifics captured; revisit when they raise it.
- **MVVMTK0045 is no longer a concern** — all `[ObservableProperty]` usages live in `PayDay.Core` (plain net9.0).
- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0`.
- **`dotnet` is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
- **Tests cannot reference `PayDay.csproj` directly.** The WindowsAppSDK auto-initializer module init throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. Use `PayDay.Core` for anything you need to unit-test — that's why the VMs live there.
- **`dotnet run --launch-profile PayDay`** warns that the profile doesn't exist but still launches via the WinAppSDK build hook. The warning is cosmetic.
- **Stale `PayDay.exe` after a prior smoke test** can lock `AppX\PayDay.exe`. Kill before re-running: `Get-Process PayDay | Stop-Process -Force`.
