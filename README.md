# FutureCheck

FutureCheck is the multi-platform successor to PayDay, a personal finance tracker built around
biweekly pay periods. The repo keeps its code name, PayDay.

## Layout

Only folders marked "present" exist today; the rest are planned (design section 5).

```
PayDayApp/
├── apps/
│   ├── site/             # present: marketing website (Astro); see apps/site/README.md
│   ├── windows/          # present: PayDay/, PayDay.Core/, PayDay.Tests/
│   ├── apple/            # planned: macOS + iOS, with PayDayKit Swift package
│   ├── android/          # planned
│   └── web/              # planned
├── packages/             # present: brand/ (shared brand tokens for site and web)
├── spec/                 # planned: data model, sync protocol, conformance fixtures
├── design/               # planned: icon sources, palette, store screenshots
├── tools/                # present: PowerShell helpers, vm-build.sh
├── planning/             # present
└── CLAUDE.md, SESSION_STATUS.md, README.md   # present
```

## Build

Windows (run from the repository root; put `C:\Program Files\dotnet` on `PATH` first):

```powershell
dotnet build apps/windows/PayDay/PayDay.csproj
dotnet build apps/windows/PayDay.Core/PayDay.Core.csproj
dotnet test apps/windows/PayDay.Tests
```

From the Mac: push, then run `tools/vm-build.sh [git-ref]`. It builds and tests that commit
(default `HEAD`) on the arm64 Windows VM.

## More

- Design: [planning/2026-11-14-multiplatform-design.html](planning/2026-11-14-multiplatform-design.html)
- Developer guide: [CLAUDE.md](CLAUDE.md)
