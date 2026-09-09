#!/bin/sh
set -eu
action=${1:-setup}
case "$action" in setup|status) ;; *) echo 'Usage: sh scripts/dev/macos.sh [setup|status]' >&2; exit 2 ;; esac
[ "$(uname -s)" = Darwin ] || { echo 'BLOCKED: macOS host required.' >&2; exit 1; }
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd -P)
cd "$repo_root"
command -v git >/dev/null 2>&1 || { echo 'BLOCKED: Git missing; provision separately.' >&2; exit 1; }
git_root=$(git rev-parse --show-toplevel)
[ "$(CDPATH= cd -- "$git_root" && pwd -P)" = "$repo_root" ] || { echo 'BLOCKED: unexpected Git root.' >&2; exit 1; }
if [ "$action" = status ]; then
    git status --short --branch
    if revision=$(git rev-parse --verify HEAD 2>/dev/null); then echo "Commit: $revision"
    else echo 'NOT RUN: no committed revision; handoff requires an initial commit.'; fi
    exit 0
fi
echo 'PASS: macOS checkout available. No installation or device operation performed.'
for tool in xcodebuild xcrun python3 node; do
    if command -v "$tool" >/dev/null 2>&1; then echo "FOUND: $tool command (functionality unverified)"
    else echo "NOT RUN: $tool unavailable; provisioning is a separate task."; fi
done
echo 'NOT RUN: Xcode signing, iPhone USB/trust, Mobile MCP, WDA and Shortcuts E2E.'
echo 'NOT RUN: Windows client build/tests; current projects target net10.0-windows.'
