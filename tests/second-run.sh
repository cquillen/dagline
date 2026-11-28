#!/usr/bin/env bash
# CI's second run of the suite, on the new engine (spec #139): each test class listed in tests/second-run.txt, with the engine
# switch on. Run from the repository root after a Release build. It goes at the cutover, with the list and the switch.
set -euo pipefail

results_directory=${1:?the results directory}
list=tests/second-run.txt
export DAGLINE_TEST_ENGINE=new

# Lines are "<test project>/<class>"; blank lines and lines starting with # are skipped, and a checkout's CR is dropped.
entries=$(tr -d '\r' < "$list" | grep -v -e '^[[:space:]]*$' -e '^#' | sort -u)
echo "The second run includes:"
echo "$entries"

failed=0
for project in $(echo "$entries" | cut -d/ -f1 | sort -u); do
  classes=$(echo "$entries" | grep "^$project/" | cut -d/ -f2 | paste -sd'|' -)
  echo "::group::$project on the new engine"
  dotnet test -c Release --no-build --project "tests/$project" --report-trx --results-directory "$results_directory/$project" \
    -- --treenode-filter "/*/*/($classes)/*" || failed=1
  echo "::endgroup::"
done
exit $failed
