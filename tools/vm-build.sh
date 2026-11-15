#!/usr/bin/env bash
# Build and test the Windows app on the arm64 Windows VM (Parallels) at a given commit.
# Usage: tools/vm-build.sh [git-ref]   (default HEAD)
# Exit: 0 ok; guest exit code on build/test failure; 2 if the commit is not on GitHub.
# Env:  FC_VM (default "Win 11 Arm Dev"), FC_VM_DIR (default C:\Builds\payday-app)
set -euo pipefail

FC_VM="${FC_VM:-Win 11 Arm Dev}"
DIR="${FC_VM_DIR:-C:\\Builds\\payday-app}"
REPO_URL="https://github.com/QuantumInkDev/payday-app.git"
DOTNET='"C:\Program Files\dotnet\dotnet.exe"'

SHA=$(git rev-parse "${1:-HEAD}^{commit}")
SHORT=$(git rev-parse --short "$SHA")

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

CMD="if not exist $DIR\\.git git clone --quiet $REPO_URL $DIR"
CMD="$CMD && git -C $DIR fetch --quiet origin"
CMD="$CMD && git -C $DIR checkout --quiet --force --detach $SHA"
CMD="$CMD && git -C $DIR clean -fdxq"
CMD="$CMD && $DOTNET build $DIR\\$ROOT\\PayDay\\PayDay.csproj -nologo -p:TreatWarningsAsErrors=true"
CMD="$CMD && $DOTNET test $DIR\\$ROOT\\PayDay.Tests -nologo -p:TreatWarningsAsErrors=true"

prlctl exec "$FC_VM" cmd /c "$CMD"

echo "VM build OK at $SHORT ($ROOT)"
