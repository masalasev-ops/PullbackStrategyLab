#
# Runs tools/verify-phase, which is a bash script with no extension.
#
# This is a wrapper and not a second implementation, on purpose. Two implementations of the gate
# a phase signs off against is the defect one level up: they would drift, and the one that drifted
# would be the one somebody ran. So this finds a bash and hands the work to the one script.
#
# What it exists to stop. `tools/verify-phase` has no extension, so PowerShell does not execute it:
# invoked from a PowerShell session on Windows the call returns 0 having done nothing, and the
# previous run's `artifacts/phase-report.json` stays on disk reading as current. The script's own
# rm block at the top is the guard for exactly that, and it is inside the thing that did not run.
# The 3.12 sign-off did this and quoted an earlier run's artifacts before catching it.
#
# The other half of the fix is in PhaseReportStage, which now stamps every report with the commit
# that produced it and refuses to write one it cannot stamp. That half makes a stale report
# identifiable; this half stops it being produced silently in the first place.
#
# see: Every phase ends in a generated phase report, not in a page somebody looks at

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot

# Whatever bash the machine has, in order of how much we trust it to be the real one. Git for
# Windows installs the first; the last two are where it puts itself when it is not on the path.
$bash = $null
$onThePath = Get-Command bash -CommandType Application -ErrorAction SilentlyContinue
if ($onThePath) {
    $bash = $onThePath.Source
}

if (-not $bash) {
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'Git\bin\bash.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\bin\bash.exe')
    )) {
        if ($candidate -and (Test-Path $candidate)) {
            $bash = $candidate
            break
        }
    }
}

if (-not $bash) {
    Write-Error (
        'verify-phase: no bash found, so tools/verify-phase cannot run. It is a bash script and ' +
        'PowerShell will not execute it, which is the silent no-op this wrapper exists to stop. ' +
        'Install Git for Windows, or run "bash tools/verify-phase" from a shell that has one. ' +
        'Nothing was run and any artifacts/phase-report.* on disk are from an earlier run.')
    exit 3
}

# From the repository root, so the script's own path arithmetic and the relative path agree.
Push-Location $repositoryRoot
try {
    & $bash 'tools/verify-phase' @args
    $verdict = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $verdict
