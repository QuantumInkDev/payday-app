# Sprint: Phase 2 — SQLite Data Layer

**Started:** 2026-05-15
**Closed:** 2026-05-15
**Scope:** Add NuGet dependencies, define the schema in `Services/DatabaseService.cs`, seed the 27 bills from the original HTML app on first launch.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §1.3, §2.1, §2.2, §2.3.
**Prior sprint:** `sprint-01-bootstrap.md` (closed 2026-05-15, commit `6dce721`).

## Tasks

### 2.0 — NuGet dependencies (plan §1.3)
- [x] `dotnet add package Microsoft.Data.Sqlite` → 10.0.8
- [x] `dotnet add package CommunityToolkit.Mvvm` → 8.4.2
- [~] ~~`dotnet add package CommunityToolkit.WinUI.Controls.DataTable`~~ — **deferred**; not published on NuGet under that ID (lives in CommunityToolkit/Labs-Windows on GitHub but not released). Re-evaluate when wiring the Bills page UI (Phase 4/5); pick a real published control then.
- [x] `dotnet add package LiveChartsCore.SkiaSharpView.WinUI` → 2.0.2
- [x] `dotnet add package System.Net.Http.Json` → 10.0.8
- [x] Verify `dotnet build` still exits 0 after additions — 0 warn / 0 err, 7.16s

### 2.1 — Schema (plan §2.1)
- [x] Create `PayDay/Services/DatabaseService.cs` (singleton, wraps `Microsoft.Data.Sqlite`)
- [x] DB file location: `ApplicationData.Current.LocalFolder` (persists across app updates)
- [x] `CREATE TABLE Bills` — 17 columns, primary key `Id TEXT`, including `NotionPageId` for sync
- [x] `CREATE TABLE Payments` — autoincrement Id, FK to Bills, `PeriodKey TEXT` ("YYYY-MM-DD")
- [x] `CREATE TABLE Snapshots` — autoincrement Id, `SnapshotDate`, `TotalOwed`, JSON `Details`
- [x] `CREATE TABLE Settings` — key/value
- [x] All tables created idempotently on first run (`CREATE TABLE IF NOT EXISTS`)

### 2.2 — Data access layer (plan §2.2)
- [x] CRUD methods for Bills (Upsert/Count/Get/GetAll/Delete), Payments (Insert/Delete/GetByPeriod), Snapshots (Insert/GetAll), Settings (Get/Set)
- [x] Transaction support for bulk operations (`UpsertBillsAsync` wraps in `BeginTransactionAsync`)
- [x] Auto-migration scaffold (`RunMigrationsAsync` + `SchemaVersion` Settings row, `CurrentSchemaVersion = 1`)
- [x] Models in `PayDay/Models/`: `Bill.cs`, `Payment.cs`, `Snapshot.cs`

### 2.3 — Seed data (plan §2.3)
- [x] On first launch (Bills count == 0), insert 27 bills via `SeedData.Bills`:
  - 4 Bills (Electric, Phone, Storage, Storage Clari)
  - 15 Cards (Amazon, Apple, Burlington, 3× Capital One, 3× Citibank, 2× Credit One, Discovery, PayPal, Raymour, Target)
  - 3 Loans (2× 401K, Prosper)
  - 1 People (Mom)
  - 4 Subscriptions (Google One, Spotify, Uber, YouTube Premium)
- [x] Seed default Settings rows: `PayAnchor`, `EarlyStart`, `LastNotionSync`, `NotionBillsDb`, `NotionPaymentsDb`, `NotionSnapshotsDb`

### Sprint exit criteria
- [x] App launches, DB file created at `LocalState\payday.db` on first run, 27 bills present.
- [x] Re-launch does not re-seed — count stays 27, 0 duplicate IDs.
- [x] `SELECT COUNT(*) FROM Bills` returns 27 (verified via sqlite3 CLI against the LocalState DB).
- [x] Commit + push.

## Next sprint

**Phase 3 — Business logic.** Pay period engine (`Services/PayPeriodService.cs`), payment tracking helpers, payoff calculator. See `PAYDAY_WINUI3_PLAN.md` §3.
