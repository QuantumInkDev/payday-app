# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 7 — Polish** is functionally complete pending smoke test. Nine chunks shipped this session:

| Chunk | Commit | What |
|---|---|---|
| 7a | `61ebb96` | Sortable All Bills columns |
| 7b | `3c19932` | Bill auto-sync on edit (PushBillAsync + AllBills wiring) |
| 7c | `b067460` | Full rename Cost→Payment, Owed→Remaining (model + DB migration + Notion + UI) |
| 7d | `971e1b9` | Sync Now button at top of nav + Pay decrements Remaining |
| 7e | `2ce6916` | Dashboard polish (row→editor, bigger subtotals) |
| 7f | `180b8f4` | All Bills overhaul (Expander groups, new columns, ordinals, auto-pay icon, custom types, scrollbar fix) |
| 7g | `38d08a9` | Type tag color customization (TypeColorService + Settings UI) |
| 7h | `fd5bcf1` | Installments type |
| 7i | `9fa95ec` | Insights snapshot delete/clear + Payoff snowball/avalanche |

**Tests:** 187/187 green (was 150 entering Phase 7 — +37). Build 0 warn / 0 err on both projects.

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
