# Moves the production checkout to the tip of main, once a night, before the first slot.
#
# The night's slots then all run one build, which is the property this buys and it is worth more
# than the fault that prompted it. Until this existed the schedule ran from the same working tree
# phase work lives in, so a branch checked out during the slot window cost slots: five of them
# across 2026-09-01 and 2026-09-02, and on 2026-08-28 one night's slots ran from six different
# commits with nothing saying so at the time.
#
# It refuses rather than repairing. A production checkout with edits in it, sitting on a branch,
# or holding a tree that does not build is a state a person has to look at, and the night runs the
# previous build in the meantime, which is a known-good one. The one thing it does repair is its
# own fast-forward: a tree that does not compile is rolled back to the commit it came from,
# because a night on a build that does not compile is every slot failing.
#
# It is not a slot and it is not in RUNBOOK's schedule table. It spends no vendor call, runs no
# stage and writes nothing to the store, so `slot-roster` has nothing to reconcile it against.
#
# Every native call runs under $ErrorActionPreference = 'Continue', for the reason nightly.ps1
# states at Invoke-Stage: under Stop the first line a native command writes to stderr is
# terminating, and git writes its ordinary progress there.
#
# see: The nightly runs from its own checkout, updated once a night before the first slot

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'shell-provenance.ps1')

$repository = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $repository 'data\live\logs'
if (-not (Test-Path $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory | Out-Null }
$log = Join-Path $logDirectory ("nightly-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

# The night's own log, so the update and the slots read as one story. Until this ran, the log
# recorded which commit each slot used and nothing recorded when that commit last moved.
function Write-Line([string]$text) {
    $stamped = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
    Write-Output $stamped
    Add-Content -Path $log -Value $stamped -Encoding utf8
}

function Invoke-Native {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [switch]$Echo)

    $ErrorActionPreference = 'Continue'
    $output = & $Arguments[0] @($Arguments[1..($Arguments.Length - 1)]) 2>&1
    $script:NativeExitCode = $LASTEXITCODE
    if ($Echo) { foreach ($line in $output) { Write-Line ("    {0}" -f $line) } }
    return $output
}

Write-Line ("update starting, checkout {0}" -f $repository)

# Which shell and which machine, into the night's own log, on the terms nightly.ps1 writes it: the
# update and the slots read as one story and both halves of it say what produced them.
Write-Line (Get-ShellProvenance -Name 'update-nightly')

$before = (Invoke-Native @('git', '-C', $repository, 'rev-parse', '--short', 'HEAD') | Out-String).Trim()
$branch = (Invoke-Native @('git', '-C', $repository, 'rev-parse', '--abbrev-ref', 'HEAD') | Out-String).Trim()

# The same guard the slots carry, one directory over and four hours earlier. A production checkout
# that has drifted to a branch is the fault this script exists to remove, arriving in the place
# built to remove it.
if ($branch -ne 'main') {
    Write-Line ("  refusing: the production checkout is on '{0}', not main. Nothing updated; tonight runs {1}." -f $branch, $before)
    exit 2
}

$dirty = Invoke-Native @('git', '-C', $repository, 'status', '--porcelain')
if ($dirty) {
    Write-Line ("  refusing: the production checkout has uncommitted changes, so the build it runs is not the commit it names. Nothing updated; tonight runs {0}." -f $before)
    foreach ($line in $dirty) { Write-Line ("    {0}" -f $line) }
    exit 3
}

Invoke-Native @('git', '-C', $repository, 'fetch', 'origin') -Echo | Out-Null
if ($script:NativeExitCode -ne 0) {
    Write-Line ("  refusing: origin could not be reached. Nothing updated; tonight runs {0}." -f $before)
    exit 4
}

# Fast-forward only. A merge commit or a rebase in a production checkout is a build nobody
# reviewed, reached by a script at five in the afternoon.
Invoke-Native @('git', '-C', $repository, 'merge', '--ff-only', 'origin/main') -Echo | Out-Null
if ($script:NativeExitCode -ne 0) {
    Write-Line ("  refusing: main would not fast-forward, so this checkout holds something main does not. Nothing updated; tonight runs {0}." -f $before)
    exit 5
}

$after = (Invoke-Native @('git', '-C', $repository, 'rev-parse', '--short', 'HEAD') | Out-String).Trim()
Write-Line ("  {0} to {1}" -f $before, $after)

# Built here rather than at 17:15, so a compile error is found with fifteen minutes in hand
# instead of by the first slot failing while the vendor's data is waiting.
Invoke-Native @('dotnet', 'build', (Join-Path $repository 'PullbackStrategyLab.sln'), '--nologo', '-v', 'quiet') -Echo | Out-Null

if ($script:NativeExitCode -ne 0) {
    # Safe because the tree was proved clean above, so this puts the checkout back where it was
    # rather than discarding anybody's work.
    Invoke-Native @('git', '-C', $repository, 'reset', '--hard', $before) | Out-Null
    Write-Line ("  the tree at {0} does not build; rolled back to {1}, which tonight runs" -f $after, $before)
    exit 6
}

Write-Line ("  update clean, tonight runs {0}" -f $after)
exit 0
