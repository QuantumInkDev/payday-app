# Task Completion Checklist

Per `CLAUDE.md` § "After Completing Work" — this is **mandatory** at the end of every session.

## 1. Verify build & tests are green

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet build P:\Projects-Not-On-Cloud\PayDayApp\PayDay\PayDay.csproj --nologo
dotnet test  P:\Projects-Not-On-Cloud\PayDayApp\PayDay.Tests\PayDay.Tests.csproj --nologo
```
- App build: 0 warn / 0 err in our code.
- Tests: all passing (21 as of 2026-05-15 — should grow as Phase 4 adds tests).

There is no separate linter or formatter configured in the project. The compiler (with `<Nullable>enable</Nullable>`) is the lint.

## 2. Update tracking docs

- **`SESSION_STATUS.md`** — write a new `## Session — YYYY-MM-DD` block, move 🔴 **CONTINUE HERE** to the next stopping point, update the "Known issues / workarounds" list if anything new surfaced.
- **`planning/current-sprint.md`** — tick tasks `[x]`. If the sprint is fully done, rename it to `sprint-NN-{name}.md` and open a new `current-sprint.md` for the next phase.

## 3. Clear TodoWrite / Serena tasks for the session

Mark completed, delete stale.

## 4. Update memories

- Update Claude auto-memory (the `MEMORY.md` index at `C:\Users\Garcia\.claude\projects\P--Projects-Not-On-Cloud-PayDayApp\memory\`) and the Serena memories *here* if anything surprising came up — new workaround, new convention the user validated, new external system.
- Don't memorize ephemeral session state (in-progress diffs, current TODO list).

## 5. Commit and push — MANDATORY

```powershell
git add <specific files, not -A>
git commit -m "Session 2026-05-15: Brief description

- Detailed change 1
- Detailed change 2

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
git push
```
Format is fixed: `Session YYYY-MM-DD: Brief description`. Body: bullet list of changes + decisions. Pre-commit checklist in `CLAUDE.md`:
- Code formatted (compiler is the lint)
- No console errors in dev run
- Docs updated (`SESSION_STATUS.md`, sprint file)
- Session status updated

## Branch strategy (per CLAUDE.md, future-state)

- `main` — production-ready
- `develop` — integration (not yet used; single-dev)
- `feature/*`, `hotfix/*` — per-task

Currently everything lands on `main` directly.
