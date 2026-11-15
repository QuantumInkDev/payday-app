#!/usr/bin/env bash
# Build and test the Windows app on the arm64 Windows VM (Parallels) at a given commit.
# Usage: tools/vm-build.sh [git-ref]   (default HEAD)
# Exit: 0 ok; guest exit code on build/test failure; 1 if the guest output has no passing
#       test summary; 2 if the commit is not on GitHub.
# Env:  FC_VM (default "Win 11 Arm Dev"), FC_VM_DIR (default C:\Builds\payday-app)
set -euo pipefail

FC_VM="${FC_VM:-Win 11 Arm Dev}"
DIR="${FC_VM_DIR:-C:\\Builds\\payday-app}"
REPO_URL="https://github.com/QuantumInkDev/payday-app.git"
DOTNET='"C:\Program Files\dotnet\dotnet.exe"'

SHA=$(git rev-parse "${1:-HEAD}^{commit}")
SHORT=$(git rev-parse --short "$SHA")

if [ -n "$(git status --porcelain)" ]; then
  echo "Warning: you have uncommitted changes; the VM builds $SHORT, not your working tree." >&2
fi

git fetch --quiet origin
if [ -z "$(git branch -r --contains "$SHA")" ]; then
  echo "Commit $SHORT is not on GitHub yet. Push it first (git push), then re-run." >&2
  exit 2
fi

if git cat-file -e "$SHA:apps/windows/PayDay/PayDay.csproj" 2>/dev/null; then
  ROOT='apps\windows'
else
  ROOT='.'
fi

CMD="(if not exist $DIR\\.git git clone --quiet $REPO_URL $DIR)"
CMD="$CMD && git -C $DIR fetch --quiet origin"
CMD="$CMD && git -C $DIR checkout --quiet --force --detach $SHA"
CMD="$CMD && git -C $DIR clean -fdxq"
CMD="$CMD && $DOTNET build $DIR\\$ROOT\\PayDay\\PayDay.csproj -nologo -p:TreatWarningsAsErrors=true"
CMD="$CMD && $DOTNET test $DIR\\$ROOT\\PayDay.Tests -nologo -p:TreatWarningsAsErrors=true"

LOG=$(mktemp)
trap 'rm -f "$LOG"' EXIT

# pipefail makes the pipeline status the guest's exit code (tee always succeeds)
prlctl exec "$FC_VM" cmd /c "$CMD" 2>&1 | tee "$LOG"

if ! grep -Eq 'Passed!.*Passed: *[1-9]' "$LOG"; then
  echo "VM run produced no test summary; treating as failure." >&2
  exit 1
fi

echo "VM build OK at $SHORT ($ROOT)"
