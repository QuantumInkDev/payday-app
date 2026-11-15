# PayDay — Project Overview

**Purpose:** Personal finance tracker (bills, pay periods, payments, payoff projections). WinUI 3 desktop port of a prior web prototype (`planning/payday.html`). Future: sync to Notion; possible cross-platform expansion.

**Repo:** github.com/QuantumInkDev/payday-app (owner: QuantumInkDev, branch: `main`).
**Working dir:** `P:\Projects-Not-On-Cloud\PayDayApp`.
**Current phase (as of 2026-05-15):** Phase 4 — UI / Views. Phases 1–3 (scaffold, SQLite data layer, business logic) are closed. See `SESSION_STATUS.md` for the live 🔴 CONTINUE HERE marker.

**Plan of record:** `planning/PAYDAY_WINUI3_PLAN.md` (§3 = done, §4 = current).

**Three projects, no .sln:**
- `apps/windows/PayDay/` — WinUI 3 app (`net9.0-windows10.0.26100.0`). Pages, ViewModels, XAML, `DatabaseService` implementation.
- `apps/windows/PayDay.Core/` — plain `net9.0` class library. Holds Models + pure business logic. **Must not reference WinUI** — see [[payday_core_split]].
- `apps/windows/PayDay.Tests/` — xunit tests on `net9.0`. References `PayDay.Core` only.
