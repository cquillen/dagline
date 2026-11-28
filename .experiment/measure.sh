#!/usr/bin/env bash
# measure.sh baseline|variant : runs the two throwaway measurements in that state, each in a fresh process, Release.
set -euo pipefail
W="D:/Code/dagline/.claude/worktrees/snapshot-take"
S="D:/Code/dagline/.claude/worktrees/snapshot-take/.experiment"
state="$1"
bash "$S/switch.sh" "$state"
cd "$W"
cp "$S/SnapshotTakeMeasurements.cs" tests/Dagline.Tests/SnapshotTakeMeasurements.cs
trap 'rm -f "$W/tests/Dagline.Tests/SnapshotTakeMeasurements.cs"' EXIT
export DAGLINE_MEASURE_OUT="$S/measurements.txt"
export DAGLINE_MEASURE_LABEL="$state"
dotnet build -c Release tests/Dagline.Tests 2>&1 | tail -3
for round in 1 2 3; do
  for name in Heap_held_by_the_decided_first_step Peak_heap_of_whole_runs; do
    dotnet test -c Release --no-build --project tests/Dagline.Tests -- --treenode-filter "/*/*/SnapshotTakeMeasurements/$name" 2>&1 | grep -E "total:|failed:|succeeded:" | tr '\n' ' '
    echo
  done
done
