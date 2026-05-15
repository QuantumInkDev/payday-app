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

### 5c — Auto-sync on payment + snapshot  ✅ landed
- [x] `NotionPushStatus` enum (`NotConfigured` / `Ok` / `Failed`) in `PayDay.Core/Services/NotionSyncResult.cs`.
- [x] `PayDayPageViewModel` — optional `NotionSyncService?` ctor param. `MarkPaid` / `MarkAllPaid` kick off `PushPaymentSafeAsync` (fire-and-forget). Public `PendingNotionPush` Task surfaces the latest push for deterministic tests. `LastNotionPushStatus` + `LastNotionPushError` observable for UI binding.
- [x] `InsightsPageViewModel` — same pattern for `SaveSnapshotAsync` → `PushSnapshotSafeAsync`.
- [x] `App.xaml.cs` — process-wide singletons: `App.Credentials` (WindowsCredentialStore) and `App.Notion` (NotionSyncService). PayDayPage and InsightsPage construct their VMs with `App.Notion`.
- [x] `PayDay.Tests/AutoSyncTests.cs` — 8 new tests: PayDay (no service / no token / push ok / push fail / mark-all pushes everything) + Insights (no service / push ok / push fail). Push failures verified to NOT roll back the local insert.
- [x] **NotionPageId write-back on payments/snapshots is deferred.** It's not read by anything yet (push-only flow). If we ever add resume-after-failure, we'll add an `is_synced` column then.
- [x] 108/108 tests pass. Build 0 warn / 0 err.

### 5d — Settings UI  ✅ landed (smoke test pending)
- [x] `SettingsPage.xaml` — Notion card flesh-out: `PasswordBox` for token, Save / Clear buttons, "Test connection" + "Sync now" buttons (disabled until token set), `ProgressRing` for in-flight ops, status row (Ellipse dot + status label + last-synced label).
- [x] `Converters/NotionStatusToBrushConverter.cs` — `NotionPushStatus` → green/red/gray status-dot brush.
- [x] `SettingsPageViewModel` — `NotionSyncService? _notion` ctor param + `NotionTokenSet`, `IsTesting`, `IsSyncing`, `IsNotionBusy`, `NotionStatus`, `NotionStatusLabel`, `LastSyncedLabel`, `NotionAvailable`, `NotionSectionEnabled`. Methods: `SaveTokenAsync`, `ClearTokenAsync`, `TestConnectionAsync`, `SyncNowAsync`. `LoadAsync` extended to read Notion state.
- [x] `App.NotionAvailable` static — production wires up the real `WindowsCredentialStore` + `NotionSyncService` singleton; SettingsPage now passes it in.
- [x] `PayDay.Tests/SettingsPageNotionTests.cs` — 10 new tests covering: no notion service, no token, token present, save token, blank save no-op, clear token, test connection success / failure, sync now success / failure.
- [x] 118/118 tests pass. Build 0 warn / 0 err.
- [x] **Manual smoke test (user, 2026-05-15)** — paste a real Notion integration token, hit Test → green, Sync Now → bills round-trip (after chunk 5e fix). Payment + snapshot push smoke pending user confirmation.

### 5e — select-property fix + diagnostic tool  ✅ landed
- [x] **Discovery**: the Notion Bills DB has `Type` and `Frequency` as **select** properties, not `rich_text`. The plan §5.2 schema was wrong on those two. First Sync Now reported 27 validation errors ("Type is expected to be select").
- [x] `NotionSyncService.BuildBillProperties` — `Type` and `Frequency` now go through a new `Select(...)` helper that emits `{"select": {"name": value}}` or `{"select": null}` for empty values.
- [x] `NotionPage.FromElement` — `Type` and `Frequency` are read via a new `ReadSelect(props, name)` that pulls `select.name` from the Notion response.
- [x] `tools/notion-diagnose.ps1` — reads the token from Credential Manager, lists databases the integration can see, dumps the schema of each seeded data source, and probes the bills `/query` endpoint. Saved permanently so future schema mismatches can be diagnosed in seconds.
- [x] `NotionSyncServiceTests` — `PageJson` helper updated to emit select shape. Three new tests: BuildBillProperties has `select` for Type + Frequency, empty values become `{"select": null}`, sync round-trip reads `Type` from `select.name`.
- [x] 121/121 tests pass (was 118).

## Sprint exit criteria

- [ ] `dotnet test PayDay.Tests` exits 0 — all prior 84 tests still pass plus new chunk-5b / 5c VM tests.
- [ ] `dotnet build PayDay/PayDay.csproj` exits 0, 0 warnings.
- [ ] Manual smoke test confirmed by user.
- [ ] All four chunks committed and pushed.
