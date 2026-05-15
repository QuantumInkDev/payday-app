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

### 6b — WindowsBackupStore + VM wiring  ⏳ next
- [ ] `PayDay/Services/WindowsBackupStore.cs` — `IBackupStore` against `ApplicationData.Current.LocalFolder/backups/`. Lazy-create folder, `ReplaceExisting` on write, `PermanentDelete` on delete. Read/write via `FileIO.ReadTextAsync` / `WriteTextAsync`.
- [ ] `App.xaml.cs` — `App.Backups` singleton (`new BackupRotationService(DatabaseService.Instance, new WindowsBackupStore())`).
- [ ] `PayDayPageViewModel` — optional `BackupRotationService?` ctor param. After local payment insert in `MarkPaidAsync` / `MarkAllPaidAsync`, kick off `BackupSafeAsync` fire-and-forget. Public `PendingAutoBackup`, `LastBackupStatus`, `LastBackupError`.
- [ ] `InsightsPageViewModel` — same pattern in `SaveSnapshotAsync`.
- [ ] `PayDayPage.xaml.cs` + `InsightsPage.xaml.cs` — pass `App.Backups` into the VM ctor.
- [ ] `PayDay.Tests/AutoBackupTests.cs` — fire-and-forget backup on mark-paid / mark-all / snapshot save; no-service path; backup-failure surfaces but doesn't roll back the local insert.

### 6c — First-launch restore prompt  ⏳ later
- [ ] `App.OnLaunched` — after `DatabaseService.Instance.InitializeAsync()`, check if `Bills` is empty and `App.Backups.LatestAsync()` returns non-null. Show a `ContentDialog` offering to restore from the newest backup.
- [ ] Restore path reuses `BackupSerializer.FromJson` + `DatabaseService.ReplaceAllDataAsync` (same as the existing manual import path).
- [ ] Manual smoke test: nuke the DB, relaunch, accept restore, verify rows return.

## Sprint exit criteria

- [ ] `dotnet test PayDay.Tests` exits 0 — 121 entering sprint; 6a is +16 already (137).
- [ ] `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings.
- [ ] Manual smoke test: mark a payment, confirm a file appears in `LocalFolder/backups/`. Take 11 backups, confirm only 10 survive. Wipe DB → relaunch → accept restore prompt → bills return.
- [ ] All three chunks committed and pushed.

After Phase 6: Phase 7 — ship (MSIX packaging, certificate, Store/sideload distribution).
