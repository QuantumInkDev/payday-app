# Sprint: Phase 7 — Polish

**Started:** 2026-05-17
**Scope:** Three buckets per `PAYDAY_WINUI3_PLAN.md` §7 — known-issue carryovers (§7.1), original item-11 backlog (§7.2), UI/UX consistency pass (§7.3). No ship until every item is checked or explicitly deferred with rationale.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §7.
**Prior sprint:** Phase 6 chunks 6a–6c (closed 2026-05-17, commits `9874397` → `78b314a` + icon `425be56`).
**Entering tests/build:** 150/150 green, 0 warn / 0 err.

## Buckets

### §7.1 — Known-issue carryovers

- [ ] **Sortable All Bills columns** — retrofit the Dashboard sortable-column pattern onto the All Bills column header (chunk 7a, in progress).
- [ ] **Bill editor tweaks** — pending specifics from the user (carryover from chunk 3b smoke test).
- [ ] **Real app icon** — replace temporary Segoe Fluent glyph `e825` with a designed icon.
- [ ] **Notion archive-on-delete** — needs a tombstone table to distinguish "never existed locally" from "deleted locally last session". TODO already in `NotionSyncService.SyncBillsAsync`.
- [ ] **NotionPageId write-back on Payments/Snapshots** — they're push-only today. Wire when push-resume-after-failure becomes a need.

### §7.2 — Original item-11 backlog

- [ ] **Dark theme tuning** across every page.
- [ ] **Toast notifications** — mark-paid, snapshot saved, sync ok/fail, backup created, restore applied.
- [ ] **Early-start sync** — auto-kick Notion sync on launch when a token is saved, optional "skip if last sync < N min ago" guard.
- [x] **Auto-backup** — shipped in Phase 6.

### §7.3 — UI/UX consistency pass

- [ ] **Empty states** on All Bills / PayDay / Insights / Payoff.
- [ ] **Spacing + alignment sweep** across every page.
- [ ] **Focus rings + keyboard nav order** review.
- [ ] **Accessibility audit** — `AutomationProperties` everywhere, non-color affordances (color + glyph/text for status, etc.).

## Chunks

### 7a — Sortable All Bills columns  🚧 in progress

- [ ] Add `AllBillsSortColumn` enum + sort state on `AllBillsPageViewModel` (mirrors `DashboardPeriodSection` shape).
- [ ] `SortByCommand` flips direction on same-column repeat, resets to ascending on new column.
- [ ] `ApplySort` re-orders each `BillGroup.Bills` collection in place; `BillGroup.Bills` becomes an `ObservableCollection<Bill>` so XAML picks up the reorder.
- [ ] `LoadAsync` calls `ApplySort` at the end so Refresh preserves current sort state.
- [ ] XAML column header row: replace the static TextBlocks for NAME / COST / OWED / DUE / RATE with `HyperlinkButton` + indicator pattern (TYPE and ACTIVE stay static).
- [ ] Tests cover default sort, direction flip, column switch resets ascending, every group reorders, indicators, unknown column is a no-op, Refresh preserves sort.

## Sprint exit criteria

- [ ] Every §7.x item checked or explicitly deferred with rationale.
- [ ] `dotnet test PayDay.Tests` exits 0.
- [ ] `dotnet build PayDay/PayDay.csproj` exits 0 with 0 warnings.
- [ ] Manual smoke pass across every page in Light + Dark.

## After Phase 7

**Phase 8 — Ship** (MSIX packaging + sideload/Store distribution per `PAYDAY_WINUI3_PLAN.md` §8).
