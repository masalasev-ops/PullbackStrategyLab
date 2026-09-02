#requires -Version 5.1
<#
  Every step of the CI workflow, in order, against a dropped database, exiting non-zero
  on the first failure. Not a wrapper around dotnet test: a green `dotnet test` does not
  satisfy the second done condition, because it never drops the store, never migrates it
  and never runs the checks as named steps.

  This file and ci.sh are not translations of each other. `&&` is a parse error in Windows
  PowerShell, so the two differ in syntax by necessity. The ci-script-parity check asserts
  they run the same steps in the same order, not that they contain the same text.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepositoryRoot 'PullbackStrategyLab.sln'
$TestProject = Join-Path $RepositoryRoot 'src/PullbackStrategyLab.Tests'
$WorkerProject = Join-Path $RepositoryRoot 'src/PullbackStrategyLab.Worker'

# A data root of its own, so a green run never depends on, and never destroys, whatever
# the operator has been running against.
#
# Assigned rather than defaulted, which is the whole of it. The `if (-not $env:...)` form yielded
# to an exported value, and step 1 of this script deletes the store under whatever it yields to. An
# operator who exports the root the RUNBOOK tells them to configure, then runs the suite, loses
# `data/live` and every night in it. The decision this holds is the one that says the entry points
# set the root, and it says so because they are the only place that can.
# see: The lab keeps one store per purpose under one data root, and CI never opens the operator's
$env:PullbackStrategyLab__DataRoot = Join-Path $RepositoryRoot 'data/ci'
$DataRoot = $env:PullbackStrategyLab__DataRoot

$script:StepNumber = 0

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][scriptblock] $Body
    )

    $script:StepNumber++
    Write-Host ''
    Write-Host "== $script:StepNumber $Name" -ForegroundColor Cyan

    & $Body

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ci: step '$Name' failed with exit code $LASTEXITCODE." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

function Invoke-Check {
    param([Parameter(Mandatory = $true)][string] $Name)
    dotnet test $TestProject --no-build --nologo --filter "check=$Name"
}

Invoke-Step 'drop-store' {
    # The store is dropped rather than migrated in place, so a migration that only works
    # against an already-populated file fails here rather than on the second machine.
    foreach ($suffix in @('', '-wal', '-shm')) {
        $file = Join-Path $DataRoot "pullbackstrategylab.db$suffix"
        if (Test-Path $file) { Remove-Item $file -Force }
    }
    Write-Host "dropped the store under $DataRoot"
    $global:LASTEXITCODE = 0
}

Invoke-Step 'restore' { dotnet restore $Solution --nologo }

Invoke-Step 'build' { dotnet build $Solution --no-restore --nologo }

Invoke-Step 'migrate' { dotnet run --project $WorkerProject --no-build -- migrate }

Invoke-Step 'check-decision-resolves'      { Invoke-Check 'decision-resolves' }
Invoke-Step 'check-no-superseded-citation' { Invoke-Check 'no-superseded-citation' }
Invoke-Step 'check-stated-counts'          { Invoke-Check 'stated-counts' }
Invoke-Step 'check-pinned-constants'       { Invoke-Check 'pinned-constants' }
Invoke-Step 'check-path-casing'            { Invoke-Check 'path-casing' }
Invoke-Step 'check-writer-ownership'       { Invoke-Check 'writer-ownership' }
Invoke-Step 'check-order-provenance'       { Invoke-Check 'order-provenance' }
Invoke-Step 'check-api-isolation'          { Invoke-Check 'api-isolation' }
Invoke-Step 'check-ci-parity'              { Invoke-Check 'ci-parity' }
Invoke-Step 'check-slot-diagnostics'       { Invoke-Check 'slot-diagnostics' }
Invoke-Step 'check-slot-roster'            { Invoke-Check 'slot-roster' }
Invoke-Step 'check-clock-usage'            { Invoke-Check 'clock-usage' }
Invoke-Step 'check-bar-append-only'        { Invoke-Check 'bar-append-only' }
Invoke-Step 'check-fixture-inputs'         { Invoke-Check 'fixture-inputs' }
Invoke-Step 'check-fixture-replay'         { Invoke-Check 'fixture-replay' }
Invoke-Step 'check-check-completeness'     { Invoke-Check 'check-completeness' }
Invoke-Step 'check-carried-obligations'    { Invoke-Check 'carried-obligations' }
Invoke-Step 'check-checkpoint-test-count'  { Invoke-Check 'checkpoint-test-count' }
Invoke-Step 'check-surface-claims'         { Invoke-Check 'surface-claims' }
Invoke-Step 'check-point-in-time'         { Invoke-Check 'point-in-time' }
Invoke-Step 'check-architecture-conformance' { Invoke-Check 'architecture-conformance' }
Invoke-Step 'check-store-portability'      { Invoke-Check 'store-portability' }
Invoke-Step 'check-price-storage-form'     { Invoke-Check 'price-storage-form' }

Invoke-Step 'check-shell-executable'       { Invoke-Check 'shell-executable' }

# Last of the checks, because it is the one that says the others were there. It reconciles
# CLAUDE.md's roster against the traits in the suite and the steps in this file, and writes the
# roster the phase report requires a coverage record against.
Invoke-Step 'check-coverage-reported'      { Invoke-Check 'coverage-reported' }

Invoke-Step 'suite' { dotnet test $TestProject --no-build --nologo }

Write-Host ''
Write-Host "ci: green, $script:StepNumber steps." -ForegroundColor Green
exit 0
