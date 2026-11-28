#!/usr/bin/env bash
# bench.sh <state> <label> [filters...] : BenchmarkDotNet run of the scenarios in that state, results under scratchpad/bdn/<label>.
set -euo pipefail
W="D:/Code/dagline/.claude/worktrees/snapshot-take"
S="D:/Code/dagline/.claude/worktrees/snapshot-take/.experiment"
state="$1"; label="$2"; shift 2
if [ "$#" -eq 0 ]; then
  set -- "*LargeGraph*" "*HeldByLimits*" "*HeldByOneSmallLimit*" "*BeginAndEndWaiting*" "*WarningsBeyondKept*" "*FrequentProgress*" "*ManyReadyAtOnce*"
fi
bash "$S/switch.sh" "$state"
cd "$W"
test ! -f tests/Dagline.Tests/SnapshotTakeMeasurements.cs
mkdir -p "$S/bdn/$label"
echo "=== $label ($state) start $(date +%T)"
dotnet run -c Release --project benchmarks/Dagline.Benchmarks -- --filter "$@" --warmupCount 8 --iterationCount 20 --artifacts "$S/bdn/$label" > "$S/bdn/$label.log" 2>&1
echo "=== $label end $(date +%T) exit $?"
cat "$S/bdn/$label/results/"*-report-github.md
