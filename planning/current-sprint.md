# Sprint: Phase 7 — Polish

**Started:** 2026-05-17
**Status:** Functionally complete, pending end-of-session smoke test.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §7.
**Prior sprint:** Phase 6 chunks 6a–6c (closed 2026-05-17).

## Chunks shipped

| Chunk | Commit | Description |
|---|---|---|
| 7a | `61ebb96` | Sortable All Bills columns |
| 7b | `3c19932` | Bill auto-sync on edit |
| 7c | `b067460` | Full rename Cost→Payment, Owed→Remaining |
| 7d | `971e1b9` | Sync Now at top of nav + Pay decrements Remaining |
| 7e | `2ce6916` | Dashboard polish |
| 7f | `180b8f4` | All Bills overhaul |
| 7g | `38d08a9` | Type tag color customization |
| 7h | `fd5bcf1` | Installments type |
| 7i | `9fa95ec` | Insights snapshot management + Payoff strategies |
| 7j | `87e2f87` | Exclude inactive bills from BillGroup subtotals (live-refresh on toggle) |
| 7k | `38e9e6e` | PayDay sortable column toolbar + 3 PM early-start pay-period shift |

**Entering tests/build:** 150/150 green, 0 warn / 0 err.
**Exiting tests/build:** 194/194 green, 0 warn / 0 err.

## Late-session additions (after first user smoke-test)

16. ✅ Inactive bills shouldn't contribute to All Bills section subtotals → 7j
17. ✅ Sortable column headers on PayDay page (like Dashboard) → 7k
18. ✅ Pay period opens at 3 PM the day before its start date → 7k

## User polish list (15 items, all addressed)

1. ✅ Sync Now + last-synced surfaced outside Settings → 7d
2. ✅ BillEditorDialog scrollbar overlap → 7f
3. ✅ Rename Cost→Payment, Owed→Remaining (full) → 7c
4. ✅ Type pills → rounded square boxes; All Bills sections collapsible → 7f
5. ✅ Custom types remembered in editor dropdown → 7f
6. ✅ Collapsible group subtotals (payment + remaining) → 7f
7. ✅ APR/Available/Limit columns + auto-pay icon → 7f
8. ✅ Dashboard subtotal numbers larger → 7e
9. ✅ Type tag color customization → 7g
10. ✅ Pay button decrements Bill.Remaining → 7d
11. ✅ Insights — clear all or delete specific snapshots → 7i
12. ✅ Dashboard rows → open bill editor → 7e
13. ✅ Due column shows ordinals → 7f (applied to All Bills only — Dashboard/PayDay show full dates)
14. ✅ Payoff alternate view — snowball vs avalanche → 7i
15. ✅ Installments type (AfterPay-style microloans, new top-level Type) → 7h

## Outstanding from Phase 7 plan (not in user list)

- **§7.1**: bill editor tweaks (TBD), real app icon, Notion archive-on-delete (needs tombstone), NotionPageId write-back on Payments/Snapshots.
- **§7.2**: dark theme tuning, toast notifications, early-start sync (auto-kick on launch).
- **§7.3**: empty states, spacing+alignment sweep, focus rings + keyboard nav, accessibility audit.

These can land as a Phase 7.5 mini-sprint or alongside Phase 8 polish. None are blockers.

## Sprint exit criteria

- [x] `dotnet test PayDay.Tests` exits 0 — **194 tests pass**.
- [x] `dotnet build PayDay/PayDay.csproj` exits 0 with 0 warnings.
- [x] Manual smoke test — All Bills + bill auto-sync verified mid-session; balance of items end-of-night browse, user signed off "good for now."
- [x] All chunks committed and pushed individually.

## Required user action — Notion DB schema

Before the next Notion sync, the user needs to:

1. Rename Bills DB column "Owed" → "Remaining".
2. Rename Snapshots DB column "Total Owed" → "Total Remaining".
3. (Optional) Add "Installments" as a select option on the Bills "Type" column.

The code reads still tolerate "Owed" (fallback), but writes will fail until the columns are renamed in Notion.

## After Phase 7

**Phase 8 — Ship** (MSIX packaging + sideload/Store distribution per `PAYDAY_WINUI3_PLAN.md` §8).
