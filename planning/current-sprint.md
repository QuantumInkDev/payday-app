# Sprint: Phase 2 — SQLite Data Layer

**Started:** TBD (Phase 2 kickoff)
**Scope:** Add NuGet dependencies, define the schema in `Services/DatabaseService.cs`, seed the 27 bills from the original HTML app on first launch.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §1.3, §2.1, §2.2, §2.3.
**Prior sprint:** `sprint-01-bootstrap.md` (closed 2026-05-15, commit `6dce721`).

## Tasks

### 2.0 — NuGet dependencies (plan §1.3)
- [ ] `dotnet add package Microsoft.Data.Sqlite`
- [ ] `dotnet add package CommunityToolkit.Mvvm`
- [ ] `dotnet add package CommunityToolkit.WinUI.Controls.DataTable`
- [ ] `dotnet add package LiveChartsCore.SkiaSharpView.WinUI`
- [ ] `dotnet add package System.Net.Http.Json`
- [ ] Verify `dotnet build` still exits 0 after additions

### 2.1 — Schema (plan §2.1)
- [ ] Create `PayDay/Services/DatabaseService.cs` (singleton, wraps `Microsoft.Data.Sqlite`)
- [ ] DB file location: `ApplicationData.Current.LocalFolder` (persists across app updates)
- [ ] `CREATE TABLE Bills` — 16 columns, primary key `Id TEXT`, including `NotionPageId` for sync
- [ ] `CREATE TABLE Payments` — autoincrement Id, FK to Bills, `PeriodKey TEXT` ("YYYY-MM-DD")
- [ ] `CREATE TABLE Snapshots` — autoincrement Id, `SnapshotDate`, `TotalOwed`, JSON `Details`
- [ ] `CREATE TABLE Settings` — key/value
- [ ] All tables created idempotently on first run (`CREATE TABLE IF NOT EXISTS`)

### 2.2 — Data access layer (plan §2.2)
- [ ] CRUD methods for Bills, Payments, Snapshots, Settings
- [ ] Transaction support for bulk operations (seeding, restore)
- [ ] Auto-migration scaffold for future schema changes (version row in Settings)

### 2.3 — Seed data (plan §2.3)
- [ ] On first launch (no DB file exists), insert 27 bills from plan §2.3:
  - 4 Bills (Electric, Phone, Storage, Storage Clari)
  - 15 Cards (Amazon, Apple, Burlington, 3× Capital One, 3× Citibank, 2× Credit One, Discovery, PayPal, Raymour, Target)
  - 3 Loans (2× 401K, Prosper)
  - 1 People (Mom)
  - 4 Subscriptions (Google One, Spotify, Uber, YouTube Premium)
- [ ] Seed default Settings rows: `PayAnchor`, `EarlyStart`, `LastNotionSync`, the three Notion DB IDs

### Sprint exit criteria
- [ ] App launches, DB file created in `LocalFolder` on first run, 27 bills present.
- [ ] Re-launch does not re-seed.
- [ ] `SELECT COUNT(*) FROM Bills` returns 27 from a quick debug query.
- [ ] Commit + push.

## Next sprint

**Phase 3 — Business logic.** Pay period engine (`Services/PayPeriodService.cs`), payment tracking helpers, payoff calculator. See `PAYDAY_WINUI3_PLAN.md` §3.
