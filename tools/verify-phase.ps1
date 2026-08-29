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
# **And the first version of this half did not work on the machine it was written for.** It took
# whatever `Get-Command bash` returned, and on a stock Windows 11 that is
# `C:\Windows\System32\bash.exe`, the WSL launcher, which is on the path ahead of Git for Windows
# and is not a bash that can run a script against `E:\...`. With no distribution installed it
# printed "Windows Subsystem for Linux has no installed distributions" and exited 1, which is the
# code a red phase report exits with, so the operator's documented Windows command reported a
# failing gate and ran nothing. With a distribution installed it would have run the gate inside
# Linux, against `/mnt/e`, which is a different answer rather than an error. The fallback list
# naming Git for Windows was never reached, because the path lookup had already succeeded.
#
# So the launcher is rejected by name and by behaviour, and the chosen bash is asked to prove it
# can run before the gate is handed to it. The exit codes are kept apart from the gate's own: 3 is
# no usable bash, and the script's 0, 1 and 2 are passed through untouched.
#
# see: Every phase ends in a generated phase report, not in a page somebody looks at

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot

# The WSL launchers. Both are shims that start a Linux distribution; neither is a bash that can
# run this repository's script against a Windows path. Matched on the file name and its directory
# rather than on the full string, so a different Windows install or a different user profile is
# still recognised.
function Test-IsWindowsSubsystemLauncher {
    param([Parameter(Mandatory = $true)][string] $Path)

    $directory = Split-Path -Parent $Path
    $leaf = Split-Path -Leaf $directory

    return ($leaf -eq 'System32') -or ($leaf -eq 'Sysnative') -or ($leaf -eq 'WindowsApps')
}

# It runs a bash script and reports what the script said. A shim that starts a distribution fails
# here when there is none, and answers about the wrong filesystem when there is one, so the probe
# asks for something only the right bash can answer: the repository root, seen as a real directory.
function Test-CanRunTheScript {
    param([Parameter(Mandatory = $true)][string] $Path)

    try {
        $probe = & $Path -c 'test -f tools/verify-phase && printf ok' 2>$null
        return ($LASTEXITCODE -eq 0) -and ($probe -eq 'ok')
    }
    catch {
        return $false
    }
}

# Every bash on the path, then the places Git for Windows puts itself when it is not on one.
#
# `PullbackStrategyLab__Bash` names one instead of searching, for a machine that keeps its bash
# somewhere this list does not know. It is also what makes the refusal below reachable from a
# test: pointing it at a path that is not a bash exercises the branch, where nobbling the search
# environment does not, because a child PowerShell recovers `ProgramFiles` whatever the parent
# sets. A branch that cannot be run is a branch asserted by reading it, which is what the
# previous test did and what let its `exit 3` sit unreachable.
$candidates = @()

if ($env:PullbackStrategyLab__Bash) {
    $candidates += $env:PullbackStrategyLab__Bash
}
else {
    $candidates += @(
        Get-Command bash -CommandType Application -All -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Source })
    $candidates += @(
        (Join-Path $env:ProgramFiles 'Git\bin\bash.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\bin\bash.exe'))
}
$rejected = @()
$bash = $null

Push-Location $repositoryRoot
try {
    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        if (-not (Test-Path $candidate)) {
            $rejected += "$candidate (no such file)"
            continue
        }

        if (Test-IsWindowsSubsystemLauncher $candidate) {
            $rejected += "$candidate (the Windows Subsystem for Linux launcher, not a bash for this tree)"
            continue
        }

        if (-not (Test-CanRunTheScript $candidate)) {
            $rejected += "$candidate (could not read tools/verify-phase from the repository root)"
            continue
        }

        $bash = $candidate
        break
    }

    if (-not $bash) {
        # Written to the error stream directly rather than through Write-Error, which under this
        # file's Stop preference is terminating and would unwind before the exit below. The exit
        # code was unreachable and the test asserting it read the string in a line that never ran.
        [Console]::Error.WriteLine(
            'verify-phase: no bash found that can run tools/verify-phase, so nothing was run. ' +
            'It is a bash script and PowerShell will not execute it, which is the silent no-op ' +
            'this wrapper exists to stop. Install Git for Windows, or run "bash tools/verify-phase" ' +
            'from a shell that has one. Any artifacts/phase-report.* on disk are from an earlier run.')

        foreach ($reject in $rejected) {
            [Console]::Error.WriteLine("verify-phase:   rejected $reject")
        }

        exit 3
    }

    # Which bash ran it, because "it ran" and "it ran under the one you meant" are different
    # facts and nothing but this line can tell the operator the second.
    Write-Host "verify-phase: using $bash"

    & $bash 'tools/verify-phase' @args
    $verdict = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $verdict
