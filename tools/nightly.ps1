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

    # --no-launch-profile so the scheduled run cannot pick up a developer profile that points
    # somewhere else. The exit code is the stage's own; a slot stops at the first failure rather
    # than running the stage that reads what the failed one should have written.
    & dotnet run --project $worker --no-launch-profile -- @verb 2>&1 |
        ForEach-Object { Write-Line ("    {0}" -f $_) }

    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Line ("  {0} exited {1}; slot {2} stops here" -f ($verb -join ' '), $code, $Slot)
        exit $code
    }
}

Write-Line ("slot {0} clean" -f $Slot)
exit 0
