# Sprint: Phase 5 — Notion Sync

**Started:** 2026-05-15
**Scope:** Bidirectional bill sync + push-only payments/snapshots sync against the existing Notion "Money Matters" databases. Integration token stored in Windows Credential Manager (DPAPI). Includes auto-sync on payment.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §5.
**Prior sprint:** Phase 4 chunks 4a–4d (closed 2026-05-15, commits `a4aa445` → `f0cafb9`).

## Architectural notes

- **Project split (per memory `[[payday-core-split]]`)**: the Windows-only credential store lives in `PayDay/` (P/Invoke to `Advapi32.dll`). Everything else — sync orchestration, JSON request/response shaping — lives in `PayDay.Core/` behind an `HttpMessageHandler` seam, so tests run on plain `net9.0` without the WinAppSDK auto-initializer crashing.
- **Test seam**: `NotionSyncService` will take `(IDatabaseService, ICredentialStore, HttpMessageHandler)` in its ctor; production wires up the real `HttpClientHandler`, tests inject a recording fake.
- **First-sync matching**: bills are matched by the `Bill ID` (text) property on the Notion side, falling back to creating a new page if absent. The local `Bill.NotionPageId` column caches the result.
- **Conflict resolution**: last-write-wins on `local.UpdatedAt` vs Notion's `last_edited_time`.
- **Last-synced timestamp**: stored under Settings key `LastNotionSync` (already seeded as `null`).

## Chunks

### 5a — Credential store  ✅ landed
- [x] `PayDay.Core/Services/ICredentialStore.cs` — `Get` / `Set` / `Delete` / `Exists` over a per-user secret store.
- [x] `PayDay/Services/WindowsCredentialStore.cs` — P/Invoke implementation hitting `CredReadW` / `CredWriteW` / `CredDeleteW` / `CredFree` from `Advapi32.dll`. Target name format: `PayDay:{key}`. `CRED_TYPE_GENERIC` + `CRED_PERSIST_LOCAL_MACHINE`. Secret blob is UTF-16LE bytes.
- [x] Build clean.
- **Deferred to 5b**: `InMemoryCredentialStore` in `PayDay.Tests/`. Defer until 5b actually uses it (writing the fake without a consumer is dead code).

### 5b — NotionSyncService (bills bidirectional + push helpers)  ✅ landed
- [x] `PayDay.Core/Services/NotionSyncService.cs` — ctor `(IDatabaseService, ICredentialStore, HttpMessageHandler? = null)`. Disposable. Surface: `TestConnectionAsync`, `SyncBillsAsync`, `PushPaymentAsync`, `PushSnapshotAsync`, `GetLastSyncedAsync` / `SetLastSyncedAsync`, `HasToken` / `SaveToken` / `DeleteToken`. Header `Notion-Version: 2025-09-03` (data-sources API). Auth via `Bearer {token}` from credential store.
- [x] `PayDay.Core/Services/NotionSyncResult.cs` — record with `Created` / `Updated` / `Pulled` / `Archived` / `Errors`.
- [x] **Bill ↔ Notion property mapping** — title (Name), checkbox (Auto-Pay, Active), number (Payment, Owed, Available, Credit Limit, Due Day, APR), rich_text (Type, Frequency, Bill ID, Yearly Date, Notes). Schema matches plan §5.2.
- [x] **Bidirectional sync rules** — match on `Bill ID` text property; last-write-wins on `UpdatedAt` (SQLite UTC) vs `last_edited_time` (ISO 8601). New local → create page (parent `data_source_id`). New remote → upsert local. Archive-on-delete deferred (needs a tombstone table, marked with TODO).
- [x] **Per-page error isolation** — a single page failure adds a string to `Result.Errors` and the sync continues. No early bailout.
- [x] `PayDay.Tests/InMemoryCredentialStore.cs` + `RecordingHttpHandler.cs` + `NotionSyncServiceTests.cs` — 13 new tests covering TestConnection (no-token / 200 / 401), SyncBills preflight, create / update / pull / Bill-ID match / remote-only pull / last-synced stamp / per-page error isolation, push payment + snapshot, ParseSqliteUtc edge cases.
- [x] **`InternalsVisibleTo PayDay.Tests`** added to `PayDay.Core.csproj` so internal helpers (`ParseSqliteUtc`, `BuildBillProperties`, `NotionPage`) are visible in tests without widening the public surface.
- [x] 100/100 tests pass. Build 0 warn / 0 err.

### 5c — Payments + snapshots push, auto-sync on payment
- [ ] Extend `NotionSyncService` with `Task PushPaymentAsync(Payment)` and `Task PushSnapshotAsync(Snapshot)`. Each writes its `NotionPageId` back to the local row.
- [ ] Hook into `PaymentService.MarkPaidAsync` (in `PayDay.Core`) — fire-and-forget Notion push after the local insert succeeds. Skip silently if no credential or if `TestConnection` previously failed (track a `LastSyncStatus` flag).
- [ ] Tests: push creates page, push stores `NotionPageId`, mark-paid still succeeds locally when Notion push fails.

### 5d — Settings UI + smoke test
- [ ] Replace the PHASE 5 placeholder card on `SettingsPage.xaml` with:
  - `PasswordBox` for the integration token + Save button.
  - "Test connection" button + status label (with green/red dot).
  - "Sync now" button + last-synced timestamp label.
  - Stub for re-running auto-sync after offline reconnects (deferred to polish).
- [ ] Settings VM: `NotionTokenSet` bool (true if `ICredentialStore` has the key), `IsTesting` / `IsSyncing` flags, `LastSyncedLabel`, status string.
- [ ] Manual smoke test: paste a real Notion integration token in, hit Test → green, Sync Now → bills round-trip, mark a bill paid on PayDay page → Notion gets a new Payments row within ~2s.
- [ ] Update `SESSION_STATUS.md` and close the sprint.

## Sprint exit criteria

- [ ] `dotnet test PayDay.Tests` exits 0 — all prior 84 tests still pass plus new chunk-5b / 5c VM tests.
- [ ] `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings.
- [ ] Manual smoke test confirmed by user.
- [ ] All four chunks committed and pushed.
