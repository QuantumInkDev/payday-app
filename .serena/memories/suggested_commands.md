# Suggested Commands (Windows / PowerShell)

## ⚠️ dotnet is not on PATH

Every new PowerShell session must prepend `dotnet` to PATH before invoking it:
```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
```

## Build

```powershell
# WinUI app (the launchable one)
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --nologo

# Class library only (faster — no WinUI build hooks)
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Core\PayDay.Core.csproj --nologo
```

**Note:** XAML compiler diagnostics are sometimes empty under `dotnet build` (known WinUI 3 toolchain issue). For clearer errors use the `winui-dev-workflow` skill's `BuildAndRun.ps1` (prefers MSBuild when present) or install Visual Studio with the Universal workload.

## Test

```powershell
dotnet test P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Tests\PayDay.Tests.csproj --nologo
```
**As of 2026-05-15:** 21 tests pass (13 PayPeriod + 8 PayoffCalculator). Tests MUST NOT reference `PayDay.csproj` — the WinAppSDK auto-initializer throws `COMException 0x80040154 REGDB_E_CLASSNOTREG` in any process without package identity. See [[payday_core_split]].

## Run the app

```powershell
# Standard — uses Microsoft.Windows.SDK.BuildTools.WinApp build hook
dotnet run --project P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --launch-profile PayDay
```
The "launch profile doesn't exist" warning is cosmetic; the app still launches with package identity.

Alternative: `winapp run` (from the `winui:winui-dev-workflow` skill) — registers debug identity then launches via AUMID.

## Inspect the SQLite DB without running the app

```powershell
# 1. Resolve the LocalState path (publisher hash varies per machine)
(Get-AppxPackage | Where-Object Name -eq '01D3B109-C28A-428F-95A8-2C937B8D7A18').PackageFamilyName
# 2. DB path:
$db = "$env:LOCALAPPDATA\Packages\<PackageFamilyName>\LocalState\payday.db"
sqlite3 $db ".tables"
sqlite3 $db "SELECT * FROM Bills LIMIT 5;"
```

## Common WinAppSDK runtime issues

- `winapp run` fails with `0x80073CF3` → install the matching Windows App Runtime framework package:
  ```powershell
  winget install Microsoft.WindowsAppRuntime.2.0
  ```
  (Match the SDK version to `Microsoft.WindowsAppSDK` in `PayDay.csproj`.)

## Git

```powershell
git status
git log --oneline -10
git diff
# Commits MUST follow: "Session YYYY-MM-DD: Brief description" (see CLAUDE.md)
```

Main/working branch: `main`. No `develop` or feature branches in use yet (single-developer).

## Windows shell helpers (PowerShell 7+)

`ls` ≡ `Get-ChildItem`. `cat` ≡ `Get-Content`. `rm -rf` ≡ `Remove-Item -Recurse -Force`. Variables: `$env:NAME` for env vars. Pipeline chain operators `&&`/`||` work in pwsh.

For repo navigation prefer the Serena tools (`find_file`, `search_for_pattern`, `find_symbol`) over `Get-ChildItem -Recurse` — they respect `.gitignore` and skip `bin/`/`obj/`.
