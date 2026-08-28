# Runs one slot of the nightly job, in the order RUNBOOK's table states.
#
# One invocation per slot, so a failed 6pm stage is rerun by hand with the same command the
# scheduler used. The application holds no scheduling logic and this script holds none either:
# it maps a slot name to the verbs RUNBOOK gives that slot, and Task Scheduler decides when.
#
# The store is addressed absolutely rather than through the working directory. DataRoot resolves
# with Path.GetFullPath, so a relative root means the store a run writes to depends on where the
# scheduler happened to start it, which is not a property anybody should have to reason about at
# three in the morning.
#
# Every run records the commit it ran from. The job runs from a working tree, so what it executes
# changes when the branch does; refusing on a branch mismatch was considered and rejected, because
# it would stop accumulation exactly when phase 3 merges to main and nothing would say why. Logging
# the commit removes the silence without adding a way to fail.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('universe', 'actions', 'bars', 'rebuild', 'index', 'indicators', 'scans',
                 'sectors', 'regime', 'detect', 'seal', 'controls', 'cap',
                 'forward', 'scoreboard', 'ceiling', 'snapshot')]
    [string]$Slot
)

$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
$worker = Join-Path $repository 'src\PullbackStrategyLab.Worker'
$dataRoot = Join-Path $repository 'data\live'

# The slots, and the verbs each runs in order. Two verbs in one slot means the second reads what
# the first writes, which is why they are a slot rather than two entries a minute apart.
$slots = @{
    'universe'   = @(, @('universe-build'))
    'actions'    = @(, @('actions'))
    'bars'       = @(, @('daily-bars'))
    'rebuild'    = @(, @('backfill', '--rebuild'))
    'index'      = @(, @('index-bars'))
    'indicators' = @(, @('indicators'))
    'scans'      = @(@('scans'), @('tiers'))
    'sectors'    = @(, @('sectors'))
    'regime'     = @(@('clusters'), @('regime'))
    'detect'     = @(@('detect-long'), @('detect-short'))
    'seal'       = @(@('vectorize'), @('journal'))
    'controls'   = @(, @('controls'))
    'cap'        = @(, @('cap'))
    'forward'    = @(, @('forward-returns'))
    'scoreboard' = @(, @('scoreboard'))
    'ceiling'    = @(, @('ceiling'))
    'snapshot'   = @(, @('snapshot-db'))
}

$logDirectory = Join-Path $dataRoot 'logs'
if (-not (Test-Path $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory | Out-Null }
$log = Join-Path $logDirectory ("nightly-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

function Write-Line([string]$text) {
    $stamped = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
    Write-Output $stamped
    Add-Content -Path $log -Value $stamped -Encoding utf8
}

# Runs one stage, puts both of its streams in the log, and leaves its exit code in
# $script:StageExitCode.
#
# $ErrorActionPreference is Continue here and nowhere else, and that is the whole repair.
# Windows PowerShell wraps every line a native command writes to stderr in a NativeCommandError
# record. Under Stop the first one is terminating, so the pipeline below died before Write-Line
# ran: the stage's diagnostic never reached the log, the "exited N" line never ran, the slot
# stopped with no line saying it had, and PowerShell's own exit code of 1 replaced the stage's.
# The application writes its message correctly, on stderr, and this script was discarding it.
# Every stage had that property, not one of them. Found after `sectors` spent 149 vendor calls
# on 2026-08-27 and left a log that stops mid-slot.
#
# A function rather than a script-scope assignment, because the isolation is the point: Stop is
# wanted everywhere else in this file and a preference set at script scope would lift it there
# too. Assigning inside a function scopes it to the function and the pipeline it runs.
#
# The exit code goes into a script-scoped variable rather than being returned, because Write-Line
# calls Write-Output and a returned value would arrive mixed into the log lines.
function Invoke-Stage {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $ErrorActionPreference = 'Continue'

    # --no-launch-profile so the scheduled run cannot pick up a developer profile that points
    # somewhere else.
    & dotnet run --project $worker --no-launch-profile -- @Arguments 2>&1 |
        ForEach-Object { Write-Line ("    {0}" -f $_) }

    $script:StageExitCode = $LASTEXITCODE
}

$commit = 'unknown'
$branch = 'unknown'
try {
    $commit = (git -C $repository rev-parse --short HEAD).Trim()
    $branch = (git -C $repository rev-parse --abbrev-ref HEAD).Trim()
} catch { }

Write-Line ("slot {0} starting, {1} at {2}, store {3}" -f $Slot, $branch, $commit, $dataRoot)

$env:PullbackStrategyLab__DataRoot = $dataRoot

foreach ($verb in $slots[$Slot]) {
    Write-Line ("  running {0}" -f ($verb -join ' '))

    # The exit code is the stage's own; a slot stops at the first failure rather than running
    # the stage that reads what the failed one should have written.
    Invoke-Stage $verb

    $code = $script:StageExitCode
    if ($code -ne 0) {
        Write-Line ("  {0} exited {1}; slot {2} stops here" -f ($verb -join ' '), $code, $Slot)
        exit $code
    }
}

Write-Line ("slot {0} clean" -f $Slot)
exit 0
