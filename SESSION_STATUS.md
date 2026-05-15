# PayDay — Session Status

**Project:** PayDay (personal finance tracker, WinUI 3 desktop port)
**Repo:** [QuantumInkDev/payday-app](https://github.com/QuantumInkDev/payday-app)
**Plan:** `planning/PAYDAY_WINUI3_PLAN.md`

---

## 🔴 CONTINUE HERE

**Phase 3 — Business logic.** Pay period engine (`Services/PayPeriodService.cs`), payment tracking helpers, payoff calculator. See `planning/PAYDAY_WINUI3_PLAN.md` §3.

Start by creating `planning/current-sprint.md` (archive Phase 2 → `sprint-02-data-layer.md` first) and porting `getPayPeriods()` from the original HTML app — anchor date is in `Settings.PayAnchor`, period length is 14 days.

---

## Session — 2026-05-15 (Phase 2 closed)

Phase 2 (SQLite data layer) — closed in this session.

- NuGet adds: `Microsoft.Data.Sqlite 10.0.8`, `CommunityToolkit.Mvvm 8.4.2`, `LiveChartsCore.SkiaSharpView.WinUI 2.0.2`, `System.Net.Http.Json 10.0.8`.
- `CommunityToolkit.WinUI.Controls.DataTable` from plan §1.3 was **deferred** — not published on NuGet under that ID. Re-evaluate when wiring the Bills page (Phase 4/5).
- Added `PayDay/Services/DatabaseService.cs` — singleton wrapping `Microsoft.Data.Sqlite`, DB at `ApplicationData.Current.LocalFolder\payday.db`, 4 tables created via `CREATE TABLE IF NOT EXISTS` in a single transaction, foreign keys on, migration scaffold via `SchemaVersion` Settings row.
- Added `PayDay/Services/SeedData.cs` with the 27 bills + 6 default Settings (including 3 Notion DB IDs from plan §5.1).
- Models in `PayDay/Models/`: `Bill.cs`, `Payment.cs`, `Snapshot.cs`.
- `App.OnLaunched` now awaits `DatabaseService.Instance.InitializeAsync()` before activating the window.
- **Verified end-to-end** via `dotnet run --launch-profile PayDay` and `sqlite3` CLI:
  - DB created at `C:\Users\Garcia\AppData\Local\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_1z32rh13vfry6\LocalState\payday.db`.
  - `SELECT COUNT(*) FROM Bills` = 27 (Bills=4, Cards=15, Loans=3, People=1, Subscriptions=4).
  - 7 Settings rows (`PayAnchor`, `EarlyStart`, `LastNotionSync`, 3 Notion DB IDs, `SchemaVersion=1`).
  - Re-launch leaves count at 27 with 0 duplicate IDs — idempotency confirmed.
- Installed `sqlite3` CLI via winget (`SQLite.SQLite 3.53.1`) — useful for ad-hoc inspection of the LocalState DB.

---

## Known issues / workarounds

- **XAML compiler diagnostics are sometimes empty** under `dotnet build` (known WinUI 3 toolchain issue). The `winui-dev-workflow` skill ships a `BuildAndRun.ps1` helper that prefers MSBuild when available. Install Visual Studio with the WinUI workload for the best signal: `winget install Microsoft.VisualStudio.Community --override "--add Microsoft.VisualStudio.Workload.Universal"`.
- **`winui-setup` does not check for the Windows App Runtime framework package.** If `winapp run` fails with `0x80073CF3` after a fresh setup, install the matching runtime: `winget install Microsoft.WindowsAppRuntime.2.0` (or the version line that matches the SDK NuGet in `PayDay.csproj`).
- **dotnet is not on `PATH` in this shell.** Use `$env:PATH = "C:\Program Files\dotnet;$env:PATH"` at the top of any PowerShell session before invoking `dotnet`.
- **Inspecting the SQLite DB from outside the app:** the LocalState path is `$env:LOCALAPPDATA\Packages\01D3B109-C28A-428F-95A8-2C937B8D7A18_<publisher-hash>\LocalState\payday.db`. Get the publisher hash via `(Get-AppxPackage | Where Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName`.
