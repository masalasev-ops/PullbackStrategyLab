#!/usr/bin/env bash
#
# Every step of the CI workflow, in order, against a dropped database, exiting non-zero
# on the first failure. Not a wrapper around dotnet test: a green `dotnet test` does not
# satisfy the second done condition, because it never drops the store, never migrates it
# and never runs the checks as named steps.
#
# This file and ci.ps1 are not translations of each other. `&&` is a parse error in Windows
# PowerShell, so the two differ in syntax by necessity. The ci-script-parity check asserts
# they run the same steps in the same order, not that they contain the same text.

set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
solution="$repository_root/PullbackStrategyLab.sln"
test_project="$repository_root/src/PullbackStrategyLab.Tests"
worker_project="$repository_root/src/PullbackStrategyLab.Worker"

# A data root of its own, so a green run never depends on, and never destroys, whatever
# the operator has been running against.
: "${PullbackStrategyLab__DataRoot:=$repository_root/data/ci}"
export PullbackStrategyLab__DataRoot
data_root="$PullbackStrategyLab__DataRoot"

step_number=0

step() {
    local name="$1"
    shift
    step_number=$((step_number + 1))
    printf '\n== %d %s\n' "$step_number" "$name"

    if ! "$@"; then
        local code=$?
        printf "ci: step '%s' failed with exit code %d.\n" "$name" "$code" >&2
        exit "$code"
    fi
}

run_check() {
    dotnet test "$test_project" --no-build --nologo --filter "check=$1"
}

drop_store() {
    # The store is dropped rather than migrated in place, so a migration that only works
    # against an already-populated file fails here rather than on the second machine.
    local suffix
    for suffix in '' '-wal' '-shm'; do
        rm -f "$data_root/pullbackstrategylab.db$suffix"
    done
    printf 'dropped the store under %s\n' "$data_root"
}

step 'drop-store' drop_store

step 'restore' dotnet restore "$solution" --nologo

step 'build' dotnet build "$solution" --no-restore --nologo

step 'migrate' dotnet run --project "$worker_project" --no-build -- migrate

step 'check-decision-resolves'      run_check 'decision-resolves'
step 'check-no-superseded-citation' run_check 'no-superseded-citation'
step 'check-stated-counts'          run_check 'stated-counts'
step 'check-pinned-constants'       run_check 'pinned-constants'
step 'check-path-casing'            run_check 'path-casing'
step 'check-writer-ownership'       run_check 'writer-ownership'
step 'check-api-isolation'          run_check 'api-isolation'
step 'check-ci-parity'              run_check 'ci-parity'
step 'check-clock-usage'            run_check 'clock-usage'
step 'check-bar-append-only'        run_check 'bar-append-only'
step 'check-fixture-inputs'        run_check 'fixture-inputs'

step 'check-shell-executable'       run_check 'shell-executable'

step 'suite' dotnet test "$test_project" --no-build --nologo

printf '\nci: green, %d steps.\n' "$step_number"
