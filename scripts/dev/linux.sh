#!/bin/sh
set -eu
action=${1:-setup}
case "$action" in setup|status) ;; *) echo 'Usage: sh scripts/dev/linux.sh [setup|status]' >&2; exit 2 ;; esac
[ "$(uname -s)" = Linux ] || { echo 'BLOCKED: Linux host required.' >&2; exit 1; }
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
echo 'PASS: Linux checkout available. No installation, network request or service operation performed.'
for tool in docker curl; do
    if command -v "$tool" >/dev/null 2>&1; then echo "FOUND: $tool command (functionality unverified)"
    else echo "NOT RUN: $tool unavailable; provisioning is a separate task."; fi
done
echo 'NOT RUN: Docker daemon/Compose, Cloud Relay deployment, logs and health checks.'
echo 'NOT RUN: Windows client build/tests; a portable relay is not implemented here.'
