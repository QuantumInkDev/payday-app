# PayDay WinUI 3 — Build Plan

## Context

PayDay is a personal financial tracking app that manages bills, credit cards, loans, and subscriptions organized around biweekly pay periods. It currently exists as a single-file HTML/React app (`payday.html`, 1275 lines) that stores data in localStorage. That implementation has proven fragile — localStorage data was lost when the hosting context (Cowork preview iframe) reset, destroying weeks of bill updates and payment history.

This plan ports PayDay to a native Windows desktop app using WinUI 3 with the new `dotnet new` CLI templates, SQLite for durable local storage, and Notion API integration for cloud backup/sync.

**Owner:** Justin (dev@quantumink.dev)
**GitHub:** QuantumInkDev/payday-app (repo exists, currently empty)
**Date:** May 15, 2026

---

## Phase 1: Project Scaffolding

### 1.1 Install Prerequisites
```bash
# Install WinApp CLI
winget install Microsoft.WinAppCLI

# Install WinUI dotnet templates
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates

# Install WinUI agent plugin (for Claude Code)
# See: https://devblogs.microsoft.com/ifdef-windows/build-native-windows-apps-with-ai-agents-for-winui-and-windows-app-sdk/
# Follow instructions to install the winui-dev agent with skills
```

### 1.2 Scaffold the App
```bash
dotnet new winui-navview -n PayDay
cd PayDay
```
This gives us:
- Modern title bar with app icon
- Left NavigationView (hamburger nav)
- Multi-page architecture
- Fluent Design, dark/light theme support out of the box
- MSIX packaging via `winapp run`

### 1.3 Add NuGet Dependencies
```bash
dotnet add package Microsoft.Data.Sqlite
dotnet add package CommunityToolkit.Mvvm
dotnet add package CommunityToolkit.WinUI.Controls.DataTable
dotnet add package LiveChartsCore.SkiaSharpView.WinUI
dotnet add package System.Net.Http.Json
```

### 1.4 Verify Build
```bash
dotnet build
winapp run
```

---

## Phase 2: Data Layer (SQLite)

### 2.1 Database Schema

```sql
-- Core bill/account tracking
CREATE TABLE Bills (
    Id          TEXT PRIMARY KEY,
    Name        TEXT NOT NULL,
    Type        TEXT NOT NULL,       -- Cards, Bills, Loans, Subscriptions, Business, People, Medical, Other, or custom
    Cost        REAL NOT NULL DEFAULT 0,   -- minimum/expected payment amount
    Owed        REAL DEFAULT 0,
    Available   REAL DEFAULT 0,
    CreditLimit REAL DEFAULT 0,
    DueDay      INTEGER DEFAULT 1,         -- day of month (1-31)
    Rate        TEXT DEFAULT 'Monthly',    -- Monthly, Bi-Weekly, Yearly, Once
    APR         REAL DEFAULT 0,
    AutoPay     INTEGER DEFAULT 0,         -- 0=manual, 1=auto
    Active      INTEGER DEFAULT 1,         -- 0=inactive (hidden from calculations)
    YearlyDate  TEXT,                      -- "MM-DD" for yearly bills
    Notes       TEXT,
    CreatedAt   TEXT DEFAULT (datetime('now')),
    UpdatedAt   TEXT DEFAULT (datetime('now')),
    NotionPageId TEXT                      -- Notion page ID for sync
);

-- Payment records per pay period
CREATE TABLE Payments (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    BillId      TEXT NOT NULL REFERENCES Bills(Id),
    PeriodKey   TEXT NOT NULL,             -- "YYYY-MM-DD" (period start date)
    AmountPaid  REAL NOT NULL,
    PaidAt      TEXT DEFAULT (datetime('now')),
    NotionPageId TEXT
);

-- Balance snapshots for trend tracking
CREATE TABLE Snapshots (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    SnapshotDate TEXT NOT NULL,
    TotalOwed   REAL NOT NULL,
    Details     TEXT,                      -- JSON blob of per-bill breakdown
    NotionPageId TEXT
);

-- App settings
CREATE TABLE Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);

-- Default settings to seed:
-- PayAnchor = "2026-03-20"
-- EarlyStart = "false"
-- LastNotionSync = null
-- NotionBillsDb = "f5fe82ee-9224-4566-9ff9-22d7ce4510e8"
-- NotionPaymentsDb = "a953d84c-5b80-4c6c-baf4-ac5cd40b70ec"
-- NotionSnapshotsDb = "612d5c43-fb3c-428a-8e85-a16234ee28b7"
```

### 2.2 Data Access Layer

Create `Services/DatabaseService.cs`:
- Singleton service wrapping `Microsoft.Data.Sqlite`
- DB file stored in `ApplicationData.Current.LocalFolder` (persists across app updates)
- Auto-create tables on first run
- Auto-migration support for schema changes
- CRUD methods for Bills, Payments, Snapshots, Settings
- Transaction support for bulk operations

### 2.3 Data Seeding

On first launch (no DB file exists), seed the Bills table with the 27 bills from the current app. Here is the complete seed data:

```
Bills (4):
- id:13, Electric, Bills, cost:400, dueDay:8, Monthly, manual, notes:"Variable - estimate"
- id:12, Phone, Bills, cost:151, dueDay:24, Monthly, manual
- id:10, Storage, Bills, cost:368.23, dueDay:5, Monthly, manual
- id:11, Storage Clari, Bills, cost:90.63, dueDay:20, Monthly, manual

Cards (15):
- id:1, Amazon, Cards, cost:87, owed:1545.06, avail:954, limit:2500, dueDay:1, Monthly, manual
- id:19, Apple, Cards, cost:61, owed:1647.50, avail:352.50, limit:2000, dueDay:31, Monthly, manual
- id:21, Burlington, Cards, cost:49, owed:0, avail:1030, limit:1030, dueDay:7, Monthly, manual
- id:16, Capital One – Platinum, Cards, cost:25, owed:0, avail:400, limit:400, dueDay:15, Monthly, manual
- id:7, Capital One – QuickSilver, Cards, cost:45, owed:0, avail:1300, limit:1300, dueDay:10, Monthly, manual
- id:6, Capital One – Savor, Cards, cost:25, owed:0, avail:1300, limit:1300, dueDay:8, Monthly, manual
- id:4, Citibank – BestBuy, Cards, cost:180, owed:6000, avail:100, limit:6100, dueDay:8, Monthly, manual
- id:5, Citibank – Home Depot, Cards, cost:30.66, owed:0, avail:750, limit:750, dueDay:16, Monthly, manual
- id:3, Citibank – Simplicity, Cards, cost:20, owed:1767.27, avail:750, limit:2500, dueDay:23, Monthly, manual
- id:17, Credit One – Amex, Cards, cost:42, owed:0, avail:1250, limit:1250, dueDay:9, Monthly, manual
- id:18, Credit One – Amex 2, Cards, cost:61, owed:0, avail:800, limit:800, dueDay:20, Monthly, manual
- id:20, Discovery Card, Cards, cost:63, owed:0, avail:2000, limit:2000, dueDay:6, Monthly, manual
- id:9, PayPal, Cards, cost:64, owed:2752.06, avail:415, limit:3200, dueDay:14, Monthly, manual
- id:8, Raymour, Cards, cost:92, owed:500, avail:3500, limit:4000, dueDay:8, Monthly, manual, notes:"Furniture financing"
- id:2, Target, Cards, cost:30, owed:0, avail:400, limit:400, dueDay:2, Monthly, manual

Loans (3):
- id:14, 401K Loan #2, Loans, cost:112.93, dueDay:15, Bi-Weekly, autoPay, notes:"Auto-deducted from paycheck"
- id:26, 401K Loan #1, Loans, cost:0, dueDay:15, Bi-Weekly, autoPay, notes:"Auto-deducted from paycheck"
- id:27, Prosper, Loans, cost:0, dueDay:1, Monthly, manual

People (1):
- id:25, Mom, People, cost:0, dueDay:1, Bi-Weekly, manual

Subscriptions (4):
- id:23, Google One, Subscriptions, cost:9.99, dueDay:12, Monthly, autoPay
- id:15, Spotify, Subscriptions, cost:10.65, dueDay:8, Monthly, autoPay
- id:24, Uber, Subscriptions, cost:10, dueDay:22, Monthly, autoPay
- id:22, YouTube Premium, Subscriptions, cost:10.67, dueDay:27, Monthly, autoPay
```

---

## Phase 3: Business Logic Layer

### 3.1 Pay Period Engine (`Services/PayPeriodService.cs`)

Port the JavaScript pay period logic to C#. Critical rules:

- **Anchor date:** March 20, 2026 (stored in Settings table, user-configurable)
- **Period length:** 14 days (biweekly)
- **Period calculation:** From anchor, walk forward/backward by 14 days to find the current period. Show 3 periods: This, Next, Following.
- **Bill assignment:** Monthly bills assigned by due day falling within period date range. Bi-Weekly bills appear in every period. Yearly bills use `YearlyDate` (MM-DD) to check if due date falls in period. "Once" bills don't auto-assign.
- **Early Start:** User can opt to start working on the next period's bills before the current period ends (e.g., paycheck cleared early). This state is persisted to Settings.

### 3.2 Payment Tracking

- `markPaid(periodKey, billId, amount)` → inserts Payments row
- `unmarkPaid(periodKey, billId)` → deletes Payments row
- `getPeriodPayments(periodKey)` → returns all payments for a period
- `isAllPaid(periodKey)` → checks if all manual (non-autoPay) bills are paid

### 3.3 Payoff Calculator

```csharp
// If APR = 0: months = ceil(owed / payment)
// If APR > 0: months = ceil(-ln(1 - (owed * r) / payment) / ln(1 + r)) where r = APR/12/100
// If payment <= owed * r: returns Infinity (never pays off)
```

---

## Phase 4: UI / Views (XAML + MVVM)

Use NavigationView with these pages:

### 4.1 Navigation Structure
```
PayDay (home/payday mode) ← default, green accent
Dashboard
All Bills
Payoff Tracker
Insights
Settings
```

### 4.2 PayDay Page (Main View)
The core experience. When the user opens the app on payday:

**Active state (bills to pay):**
- Hero section: period dates, total due, remaining amount, progress bar
- Auto-pay summary card (collapsible): lists auto-deducted items with total, no action needed
- Unpaid bills list: each row shows name, type tag, due date, amount, pay input field, "Mark Paid" button
- "Mark All Paid" bulk action button
- Paid bills list (faded): shows what's been paid with undo option

**Completed state (all paid):**
- Summary card: period dates, total paid, breakdown by type
- List of all paid items with amounts
- "Start Paying Now" button to jump to next period (early start)
- Coming up: preview of next period's bills

### 4.3 Dashboard Page
- Stats grid: Total monthly obligations, Total owed, Credit utilization %, Bills due this period
- Period overview table (sortable columns): bills assigned to This Period, Next Period, Following Period
- Each section shows auto-pay items separately from manual

### 4.4 All Bills Page
- Grouped by type (Cards, Bills, Loans, etc.) with type tag pills
- Sortable columns: Name, Cost, Owed, Available, Limit, Due Day, Rate, Auto-Pay, Active
- Inline toggle for active/inactive (inactive bills excluded from all calculations and period assignments)
- Edit button opens bill editor dialog
- Add New Bill button
- Custom type categories supported (user can create new types, colored with hash-based color generation)

### 4.5 Payoff Tracker Page
- Cards/loans with balances > 0
- Each shows: name, owed, payment, progress bar (owed/limit), estimated payoff months
- Sorted by payoff timeline

### 4.6 Insights Page
- Chart: total owed over time (from Snapshots history)
- Spending breakdown by type (pie/donut chart)
- Type distribution stats
- "Save Snapshot" button to capture current state

### 4.7 Settings Page
- Pay anchor date configuration
- Notion sync settings (database IDs, sync now button, last synced timestamp)
- Backup: Export to JSON file, Import from JSON file
- About / version

### 4.8 Design System
- **Dark theme by default** (match current: bg #0f1117, surface #1a1d27, accent #6c5ce7)
- Also support light theme via WinUI system theme
- Type tag pills with category-specific colors:
  - Cards: pink (#fd79a8)
  - Bills: gold (#b2945b)
  - Loans: purple (#6c5ce7)
  - Subscriptions: red (#e17055)
  - Business: blue (#74b9ff)
  - People: green (#00b894)
  - Medical: teal (#55efc4)
  - Other: gray (#8b8fa3)
  - Custom types: hash-based color from palette
- Status indicators: green dot = auto-pay, yellow dot = manual
- Fluent Design: use Mica/Acrylic materials where appropriate
- Toast notifications for actions (saved, synced, error)

---

## Phase 5: Notion Sync

### 5.1 Existing Notion Databases

Three databases already exist under the "Money Matters" page:

| Database | Notion DB ID | Data Source Collection ID |
|----------|-------------|--------------------------|
| PayDay — Bills | `6772a9f1-9b11-42b3-b5c2-28d198f23186` | `f5fe82ee-9224-4566-9ff9-22d7ce4510e8` |
| PayDay — Payments | `64d5884a-4f31-44c6-949d-e4585e80c557` | `a953d84c-5b80-4c6c-baf4-ac5cd40b70ec` |
| PayDay — Snapshots | `c2071e49-b081-4c22-9559-59ff9966aa7c` | `612d5c43-fb3c-428a-8e85-a16234ee28b7` |

### 5.2 Notion API Integration (`Services/NotionSyncService.cs`)

**Bills sync (bidirectional):**
- Schema: Name (title), Type, Payment (number), Owed (number), Available (number), Credit Limit (number), Due Day (number), Frequency (text), APR (number), Auto-Pay (checkbox), Active (checkbox), Bill ID (text), Yearly Date (text), Notes (text), Last Synced (last_edited_time)
- On sync: compare local UpdatedAt vs Notion LastSynced. Newer wins.
- New local bills → create Notion page
- Deleted local bills → archive Notion page (don't hard delete)

**Payments sync (push only, local → Notion):**
- Schema: Name (title), Bill ID (text), Period (text), Amount Paid (number), Paid At (text), Last Synced (last_edited_time)
- Each markPaid creates a local record AND queues a Notion push
- Sync can be manual ("Sync Now" button) or automatic on payment

**Snapshots sync (push only):**
- Schema: Name (title), Date (date), Total Owed (number), Details (text/JSON), Last Synced (last_edited_time)

### 5.3 Notion Auth
- Store Notion integration token in Windows Credential Manager (DPAPI encrypted), NOT in plaintext
- Settings page has field to enter/update the token
- Test connection button

### 5.4 Sync Strategy
- **Manual sync** via button in Settings or header
- **Auto-sync on payment** — when a bill is marked paid, sync that payment to Notion immediately
- **Conflict resolution** — last-write-wins based on timestamps
- **Offline resilient** — queue changes when offline, push when connection restored
- Show last synced timestamp in the UI header

---

## Phase 6: Backup & Restore

### 6.1 Local Backup
- Export: serialize entire DB (bills, payments, snapshots, settings) to JSON file
- Import: parse JSON, replace all local data with backup data
- File picker dialog for save/load location
- Backup file format version for forward compatibility

### 6.2 Auto-Backup
- On every payment action, auto-save a rolling backup to `LocalFolder/backups/`
- Keep last 10 auto-backups, rotate oldest
- On app launch, check if DB is empty but backups exist → prompt restore

---

## Phase 7: Polish

No ship until the rough edges are gone. Scope covers three buckets — known-issue carryovers, the legacy "item 11" list, and a fresh UI/UX consistency sweep.

### 7.1 Known-issue carryovers
- **Sortable columns on All Bills** — retrofit the Dashboard's sortable-column pattern (same-column click flips asc↔desc, switching columns resets to ascending, ▲/▼ indicator).
- **Bill editor tweaks** — specifics deferred from chunk 3b smoke test. Capture when raised.
- **Real app icon** — replace the temporary Segoe Fluent glyph (commit `425be56`, `e825`) with a proper asset set across all required sizes.
- **Notion archive-on-delete** — needs a tombstone table to distinguish "never existed locally" from "deleted locally last session". TODO sits in `SyncBillsAsync`.
- **NotionPageId write-back on Payments/Snapshots** — store page IDs at push time so a future push-resume-after-failure flow can pick up where it left off. Add an `is_synced` column.

### 7.2 Original plan-item-11 backlog
- **Dark theme tuning** — review every page in Dark + HighContrast, fix any low-contrast pairs or untheme'd brushes.
- **Toast notifications** — mark-paid, mark-all-paid, snapshot saved, Notion sync success/failure, backup created, restore applied.
- **Early-start sync** — auto-kick a Notion sync on app launch when a token is saved, so the user doesn't have to hit Sync Now manually. Fire-and-forget against the same `NotionSyncService.PendingSync` pattern; status surfaces on the Settings card. Consider gating on a "skip if last sync < N minutes ago" guard to avoid redundant calls on quick relaunches.
- ~~Auto-backup~~ — shipped in Phase 6.

### 7.3 UI/UX consistency pass
- **Empty states** — All Bills, PayDay, Insights, Payoff Tracker. Friendly copy + a primary action where it makes sense.
- **Spacing + alignment** — sweep all pages for inconsistent padding/margin against a single token set.
- **Focus rings + keyboard nav** — every interactive control reachable by Tab in a sane order, visible focus state in all themes.
- **Accessibility audit** — `AutomationProperties` names on controls without inline labels, color-only signals get a non-color affordance (e.g., status dot + text label is already correct on Notion card — replicate elsewhere).

### 7.4 Exit criteria
- All 7.1 / 7.2 / 7.3 items checked off or explicitly deferred with rationale.
- `dotnet test PayDay.Tests` exits 0 with no regressions.
- `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings.
- Manual smoke pass across every page in both Light and Dark themes.

---

## Phase 8: Build & Ship

### 8.1 Build Commands
```bash
dotnet build
winapp run                    # dev mode
winapp cert generate          # create signing cert
winapp sign                   # sign the package
winapp pack                   # create MSIX installer
```

### 8.2 Git
- Push to `QuantumInkDev/payday-app`
- `.gitignore`: bin/, obj/, *.user, *.pfx, AppPackages/

---

## Implementation Order

Build in this sequence so you have a working app at each step:

1. **Scaffold** — `dotnet new winui-navview`, verify it builds and runs
2. **SQLite + seed data** — DatabaseService, create tables, seed 27 bills
3. **All Bills page** — CRUD operations, verify data round-trips to DB
4. **Pay Period engine** — port JS logic, unit test it
5. **PayDay page** — the main experience, mark paid/unpaid with real DB writes
6. **Dashboard page** — period overview with sortable tables
7. **Payoff + Insights pages** — charts with LiveCharts2
8. **Backup/Restore** — JSON export/import
9. **Settings page** — pay anchor config, theme
10. **Notion sync** — API integration, bidirectional bill sync, payment push
11. **Polish** — known-issue carryovers, dark theme tuning, toast notifications, UI/UX consistency
12. **Ship** — MSIX packaging, code-sign, sideload/Store distribution

---

## Reference: Original Source

The complete original HTML app is at: `payday.html` (included alongside this plan).
Key JS functions to port:
- `getPayPeriods()` — pay period calculation from anchor date
- `getBillDueDate()` — maps due day to a date within a period
- `assignBillsToPeriods()` — assigns bills to periods based on rate/due date
- `estimatePayoff()` — months-to-payoff calculator with APR support
- `periodKey()` — generates "YYYY-MM-DD" key for a period

The original app's Notion database IDs and schemas are already wired up and contain bill data from April 8, 2026.
