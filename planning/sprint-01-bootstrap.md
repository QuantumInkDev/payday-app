# Sprint: Bootstrap + Phase 1 Scaffold

**Started:** 2026-05-15
**Scope:** Install prerequisites, install win-dev-skills plugin, init git, scaffold empty WinUI 3 navview app, push first commit.
**Plan reference:** `PAYDAY_WINUI3_PLAN.md` §Phase 1.

## Tasks

- [x] Install .NET SDK 9 (`winget install Microsoft.DotNet.SDK.9`)
- [x] Install WinAppCLI (`winget install Microsoft.WinAppCli`)
- [x] Install WinUI C# dotnet templates (`dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`)
- [x] Install Claude Code plugin `winui@win-dev-skills`
- [x] Create `SESSION_STATUS.md`
- [x] Create `planning/current-sprint.md` (this file)
- [x] `git init -b main`
- [x] Verify `QuantumInkDev/payday-app` exists; add remote
- [x] Write `.gitignore`
- [x] `dotnet new winui-navview -n PayDay`
- [x] `dotnet build` exits 0
- [x] Smoke test: `winapp run` launched the packaged app cleanly (PID 67444) on 2026-05-15. Required two extra prereqs not handled by `winui-setup`: (a) Developer Mode enabled via UAC, (b) `Microsoft.WindowsAppRuntime.2.0` installed via winget (the SDK 2.0.1 NuGet alone is insufficient — also need the matching runtime framework package or launch fails with `0x80073CF3`).
- [x] First commit pushed to `origin/main`

## Next sprint

**Phase 2 — SQLite data layer.** Open the repo in a fresh Claude Code session so the `winui-dev` agent loads, then start with §2.1 schema definition.
