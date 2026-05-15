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

### 5b — NotionSyncService (bills, bidirectional) — next
- [ ] `PayDay.Core/Services/NotionSyncService.cs` — ctor `(IDatabaseService, ICredentialStore, HttpMessageHandler? handler = null)`. Public surface:
  - `Task<bool> TestConnectionAsync()` — pings `GET /v1/users/me`, returns true on 2xx.
  - `Task SyncBillsAsync(CancellationToken)` — fetches Notion bills via `POST /v1/data_sources/{id}/query`, compares timestamps, applies last-write-wins, creates new pages for unmatched local bills, archives Notion pages for locally-deleted bills. Updates `Bill.NotionPageId` on the local row.
  - `Task<DateTimeOffset?> GetLastSyncedAsync()` / `Task SetLastSyncedAsync(DateTimeOffset)` — reads/writes the `LastNotionSync` setting.
- [ ] Notion API client — pure `HttpClient` (constructed from the injected handler) with the standard headers: `Authorization: Bearer {token}`, `Notion-Version: 2022-06-28`.
- [ ] JSON shaping: convert `Bill` ↔ Notion page properties matching plan §5.2 schema (Name title, Type/Bill ID/Yearly Date/Notes/Frequency text, Payment/Owed/Available/Credit Limit/Due Day/APR number, Auto-Pay/Active checkbox).
- [ ] `PayDay.Tests/InMemoryCredentialStore.cs`.
- [ ] `PayDay.Tests/RecordingHttpHandler.cs` — captures requests and replays canned responses.
- [ ] `PayDay.Tests/NotionSyncServiceTests.cs` — at least: TestConnection happy/sad paths, bills sync creates new page when no `NotionPageId`, bills sync updates Notion when local is newer, bills sync overwrites local when Notion is newer, bills sync archives a Notion page when local is missing, Bill ID matching populates `NotionPageId` on first sync, request headers correct.

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
