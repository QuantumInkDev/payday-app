# Sprint: Phase 6 — Auto-backup rotation

**Started:** 2026-05-15
**Scope:** Auto-rotating JSON backups to `LocalFolder/backups/`. Writes on every mark-paid + snapshot save; keeps the most recent 10. First-launch restore prompt when the DB is empty but backups exist.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §6.2.
**Prior sprint:** Phase 5 chunks 5a–5e (closed 2026-05-15, commits `23f99dc` → `1b60120`).

## Architectural notes

- **Project split** — pure rotation logic (filename pattern, trim-to-N, ordering) lives in `PayDay.Core` behind a new `IBackupStore` seam. The Windows-only implementation that touches `Windows.Storage` lives in the app project. Same shape as the Notion sync split.
- **Filename pattern** — `payday-backup-{yyyyMMdd-HHmmss}.json`. Sortable as a string and per-second collisions are vanishingly unlikely from VM-thread writes.
- **Trim ordering** — `OrderByDescending(LastWriteUtc)`, keep first 10, delete the rest. Non-matching filenames are ignored so a stray file in the folder doesn't get rotated out.
- **Fire-and-forget** — same `PendingAutoBackup` pattern as `PendingNotionPush`; VM exposes the task for deterministic tests, production never awaits.

## Chunks

### 6a — BackupRotationService + IBackupStore  ✅ landed
- [x] `PayDay.Core/Services/IBackupStore.cs` — `WriteAsync` / `ListAsync` / `ReadAsync` / `DeleteAsync` over a generic backup folder. `BackupEntry(FileName, LastWriteUtc)` record.
- [x] `PayDay.Core/Services/BackupRotationService.cs` — ctor `(IDatabaseService, IBackupStore, Func<DateTime>? utcNow = null)`. Public surface: `CreateAsync` (snapshot + write + trim), `ListAsync`, `LatestAsync`, `ReadAsync`, `TrimAsync`. Constants: `MaxBackups = 10`, `FileNamePrefix = "payday-backup-"`, `FileNameExtension = ".json"`. Static `FormatFileName(DateTime)` helper.
- [x] Trim is filename-pattern-aware so stray files in the folder are ignored.
- [x] `PayDay.Tests/InMemoryBackupStore.cs` — test fake with `WriteHistory`, `DeleteHistory`, `Seed(name, content, lastWrite)`, and `NextWriteTimestamp` for deterministic time control.
- [x] `PayDay.Tests/BackupRotationServiceTests.cs` — 16 new tests covering filename format, snapshot contents, under-10 keeps all, at-11 trims oldest, many-extra trims all, ordering, pattern filtering, latest, read, and tail invariant (25 creates → 10 files).
- [x] 137/137 tests pass (was 121). Build 0 warn / 0 err on both PayDay.csproj and PayDay.Tests.csproj.

### 6b — WindowsBackupStore + VM wiring  ✅ landed
- [x] `PayDay/Services/WindowsBackupStore.cs` — `IBackupStore` against `ApplicationData.Current.LocalFolder/backups/`. Lazy-create folder via `CreateFolderAsync(OpenIfExists)`, `ReplaceExisting` on write, `PermanentDelete` on delete, silent on missing files. Read/write via `FileIO.ReadTextAsync` / `WriteTextAsync`. `BackupEntry.LastWriteUtc` sourced from `BasicProperties.DateModified.UtcDateTime`.
- [x] `App.xaml.cs` — `App.Backups` singleton (`new BackupRotationService(DatabaseService.Instance, new WindowsBackupStore())`).
- [x] `PayDayPageViewModel` — optional `BackupRotationService?` ctor param. After local payment insert in `MarkPaidAsync` and at the end of `MarkAllPaidAsync` (one rotation per batch, not one per row), kicks off `BackupSafeAsync` fire-and-forget. Public `PendingAutoBackup` task; `LastBackupStatus` + `LastBackupError` observable for UI.
- [x] `InsightsPageViewModel` — same pattern in `SaveSnapshotAsync` after the local insert + LoadAsync re-fetch.
- [x] `BackupStatus` enum (`NotConfigured` / `Ok` / `Failed`) added to `BackupRotationService.cs`.
- [x] `PayDayPage.xaml.cs` + `InsightsPage.xaml.cs` — pass `App.Backups` into the VM ctor.
- [x] `PayDay.Tests/AutoBackupTests.cs` — 7 new tests: no-service path / backup ok / backup fail (local row still persists) / mark-all triggers exactly one backup, mirrored for snapshot save. Uses inline `ThrowingBackupStore` fake.
- [x] 144/144 tests pass (was 137). Build 0 warn / 0 err on PayDay.csproj and PayDay.Tests.csproj.

### 6c — First-launch restore prompt  ✅ landed
- [x] `PayDay.Core/Services/BackupRestorePrompt.cs` — pure-logic helper. `GetCandidateAsync()` returns the newest backup *only* when the Bills table is empty and the backup folder is non-empty; `ApplyAsync(BackupEntry)` reads + parses via `BackupSerializer.FromJson` and calls `IDatabaseService.ReplaceAllDataAsync`. Parse-time validation runs before `ReplaceAllDataAsync` so a corrupt file leaves the DB untouched.
- [x] `MainWindow.MainWindow_Activated` — one-shot handler (gated by `_restorePromptChecked`) checks `BackupRestorePrompt.GetCandidateAsync`. If a candidate exists, shows a `ContentDialog` rooted at `RootGrid.XamlRoot` with "Restore" (primary) / "Start fresh" buttons. On Restore, calls `ApplyAsync` and navigates back to `PayDayPage` so the freshly restored data reloads.
- [x] Restore failure surfaces via a secondary `ContentDialog` (title "Restore failed", body = exception message). The local DB isn't modified because `BackupSerializer.FromJson` validates before the transactional `ReplaceAllDataAsync`.
- [x] `PayDay.Tests/BackupRestorePromptTests.cs` — 6 new tests: empty DB + no backups → null; non-empty DB → null even with backups; empty DB + backups → newest; ApplyAsync restores bills/payments/snapshots/settings; round-trip via `BackupRotationService.CreateAsync` → wipe → `ApplyAsync`; bad-JSON throws before mutating DB.
- [x] 150/150 tests pass (was 144). Build 0 warn / 0 err.
- [x] **Manual smoke test (user)** — passed 2026-05-17. Deleted `payday.db` from `LocalState`, relaunched, dialog fired and accepting restored all data.

## Sprint exit criteria

- [x] `dotnet test PayDay.Tests` exits 0 — **150 tests pass** (was 121 entering sprint; +29 across 6a, 6b, 6c).
- [x] `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings.
- [x] Manual smoke test (user) — passed 2026-05-17. Restore prompt fires when DB is wiped and accepts cleanly.
- [x] All three chunks committed and pushed.

## Phase 6 closed — 2026-05-17

Smoke test passed 2026-05-17, closing the last open exit-criteria item. Code chunks themselves landed 2026-05-15 (`9874397`, `474b4f3`, `78b314a` + icon `425be56`).

After Phase 6: **Phase 7 — Polish** (known-issue carryovers, dark theme tuning, toast notifications, UI/UX consistency pass). Phase 8 is Ship. See `PAYDAY_WINUI3_PLAN.md` §7.
