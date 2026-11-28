#!/usr/bin/env bash
# switch.sh baseline|variant : puts src/ and tests/ of the worktree in that state, using the saved patch.
set -euo pipefail
W="D:/Code/dagline/.claude/worktrees/snapshot-take"
S="D:/Code/dagline/.claude/worktrees/snapshot-take/.experiment"
cd "$W"
want="$1"
if [ -f src/Dagline/SnapshotChange.cs ]; then now=baseline; else now=variant; fi
if [ "$now" != "$want" ]; then
  if [ "$want" = baseline ]; then git apply -R "$S/variant.patch"; else git apply "$S/variant.patch"; fi
fi
if [ -f src/Dagline/SnapshotChange.cs ]; then now=baseline; else now=variant; fi
echo "state: $now ; src diff vs HEAD:"
git diff --stat -- src tests | tail -1 || true
[ "$now" = "$want" ]
