#
# What ran, and what it ran under. Dot-sourced by every PowerShell entry point in tools/.
#
# **A green states what produced it.** That sentence is in CLAUDE.md's Commands table and until
# 6.10 it was addressed to a session reading a column, because no script said it of itself. The
# mechanism it guards is that a gate can return success without executing: calling an extensionless
# bash script by name from PowerShell writes nothing, leaves `$LASTEXITCODE` unset and leaves `$?`
# true, so a gate that never ran is indistinguishable from one that passed. The other direction is
# loud but only just: `pwsh` absent, `tools/ci.ps1` exits 127 and says so, and the same command
# through a pipe exits 0 because the exit code belongs to the last stage.
#
# So each entry point opens by naming its shell and its host, on the discipline the phase report
# already carries with its commit. A line saying which interpreter produced a run is what makes a
# transcript readable months later, and it is what turns "it was green" into "it was green under
# this shell, on this machine".
#
# This file is not an entry point and carries no shebang: it is dot-sourced, never executed.
#
# see: Every phase ends in a generated phase report, not in a page somebody looks at

# One line, at the top of a run, naming the interpreter and the machine.
#
# `$PSVersionTable.PSEdition` separates Windows PowerShell 5.1 from PowerShell 7, which matters
# because the two differ on `&&`, on `2>&1` against a native command and on the default encoding of
# a redirect, and a transcript that does not say which was used cannot be read for any of those.
function Get-ShellProvenance {
    param([Parameter(Mandatory = $true)][string] $Name)

    # ContainsKey and not a truthiness test on the property. `$PSVersionTable` is a hashtable, and
    # `OS` is one of the keys PowerShell 7 has and Windows PowerShell 5.1 does not; under
    # `Set-StrictMode -Version Latest`, which `tools/ci.ps1` sets, reading an absent property throws
    # rather than yielding null. The first version of this line did exactly that and took the whole
    # CI script down on its first statement, which is the loudest possible form of the fault this
    # file exists to make less quiet.
    $edition = if ($PSVersionTable.ContainsKey('PSEdition')) { $PSVersionTable['PSEdition'] } else { 'Desktop' }
    $os = if ($PSVersionTable.ContainsKey('OS')) { $PSVersionTable['OS'] } else { [System.Environment]::OSVersion.VersionString }

    return "$Name`: shell PowerShell $($PSVersionTable.PSVersion) ($edition), host $([System.Environment]::MachineName), $os"
}

# The line, written to the host stream.
#
# Write-Host rather than Write-Output on purpose: `slot-log-verdict.ps1` is a predicate whose
# standard output the two runner jobs parse, and a script that dot-sources this file and then wrote
# a provenance line to the output stream would put it in whatever captured that script. The host
# stream reaches a terminal and a transcript and is not captured by an assignment.
#
# A script whose own output is a file, as `nightly.ps1`'s is, calls Get-ShellProvenance and routes
# the string through its own writer instead, so the line lands in the log the night is read from
# rather than in a console nobody keeps.
function Write-ShellProvenance {
    param([Parameter(Mandatory = $true)][string] $Name)

    Write-Host (Get-ShellProvenance -Name $Name)
}

# The WSL launchers. Both are shims that start a Linux distribution; neither is a bash that can run
# this repository's scripts against a Windows path. Matched on the file name's directory rather than
# on the full string, so a different Windows install or a different user profile is still recognised.
function Test-IsWindowsSubsystemLauncher {
    param([Parameter(Mandatory = $true)][string] $Path)

    $directory = Split-Path -Parent $Path
    $leaf = Split-Path -Leaf $directory

    return ($leaf -eq 'System32') -or ($leaf -eq 'Sysnative') -or ($leaf -eq 'WindowsApps')
}

# A bash that can run the named script from the repository root, or $null with the reasons every
# candidate was rejected.
#
# **The probe is the half that matters.** Taking whatever `Get-Command bash` returns is what the
# first version of the phase-gate wrapper did, and on a stock Windows 11 that is the WSL launcher in
# System32, ahead of Git for Windows on the path. With no distribution installed it printed a WSL
# message and exited 1, which is the code a red phase report exits with, so the operator's
# documented command reported a failing gate and ran nothing; with one installed it would have run
# against `/mnt/e`, which is a different answer rather than an error. So the chosen bash is asked to
# prove it can read the script before the work is handed to it.
#
# `PullbackStrategyLab__Bash` names one instead of searching, for a machine that keeps its bash
# somewhere this list does not know. It is also what makes the refusal reachable from a test:
# pointing it at a path that is not a bash exercises the branch, where nobbling the search
# environment does not, because a child PowerShell recovers `ProgramFiles` whatever the parent sets.
function Find-UsableBash {
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string] $Script
    )

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
    $chosen = $null

    Push-Location $RepositoryRoot
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

            $canRun = $false
            try {
                $probe = & $candidate -c "test -f $Script && printf ok" 2>$null
                $canRun = ($LASTEXITCODE -eq 0) -and ($probe -eq 'ok')
            }
            catch {
                $canRun = $false
            }

            if (-not $canRun) {
                $rejected += "$candidate (could not read $Script from the repository root)"
                continue
            }

            $chosen = $candidate
            break
        }
    }
    finally {
        Pop-Location
    }

    return [pscustomobject]@{ Path = $chosen; Rejected = $rejected }
}

# Run a bash script through a bash this machine has, or refuse loudly with exit code 3.
#
# **Three is kept apart from the script's own codes**, so "no usable interpreter" is never confused
# with "the script ran and said no". That distinction is the whole point: a wrapper that exited 1
# when it found no bash would report a failing gate, which is what the first phase-gate wrapper did.
#
# **The exit code goes into a script-scoped variable rather than being returned**, which is the
# idiom `nightly.ps1` already documents and it is here for the same reason one level worse. A
# PowerShell function's return value is its output stream, so everything the bash script writes to
# standard output joins it: the first version of this returned `$LASTEXITCODE`, the caller wrote
# `exit (Invoke-BashEntryPoint ...)`, and the whole of the script's output was consumed into that
# expression and never printed. It exited 0 and said nothing, which is a wrapper reproducing the
# silent no-op it was written to remove, one layer up. Caught by running it.
function Invoke-BashEntryPoint {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string] $Script,
        [string[]] $Arguments = @()
    )

    $script:BashEntryExitCode = 3
    $found = Find-UsableBash -RepositoryRoot $RepositoryRoot -Script $Script

    if (-not $found.Path) {
        # Written to the error stream directly rather than through Write-Error, which under a Stop
        # preference is terminating and would unwind before the exit below.
        [Console]::Error.WriteLine(
            "$Name`: no bash found that can run $Script, so nothing was run. It is a bash script " +
            'and PowerShell will not execute it, which is the silent no-op this wrapper exists to ' +
            'stop: called by name it returns 0 having done nothing. Install Git for Windows, or ' +
            "run `"bash $Script`" from a shell that has one.")

        foreach ($reject in $found.Rejected) {
            [Console]::Error.WriteLine("$Name`:   rejected $reject")
        }

        return
    }

    # Which bash ran it, because "it ran" and "it ran under the one you meant" are different facts
    # and nothing but this line can tell the operator the second.
    Write-Host "$Name`: using $($found.Path)"

    Push-Location $RepositoryRoot
    try {
        & $found.Path $Script @Arguments
        $script:BashEntryExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
