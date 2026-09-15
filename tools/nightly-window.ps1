# Runs one of the five scheduled windows: its slots back to back, each through tools/nightly.ps1.
#
# Thirty-seven tasks became five at 7.17, because five start times are load-bearing and the rest were
# spacing. The two spread passes read a live book at their own minute of the session; the evening
# needs the day's bulk prices, published by 17:15; the night needs the minute bars, published two to
# three hours after the close, and 20:30 is past midnight UTC so the fetch spends first in its quota
# day; and the weekly slots read the week's record on Saturday morning. On 2026-09-04 the sixteen
# evening slots did three minutes of work across eighty-five, and that night's slots ran from five
# different commits because the checkout moved between tasks.
#
# Every slot goes through tools/nightly.ps1 exactly as a task sent it there before, in a process of its
# own. So the operator's pause, the tree guard, the line saying which commit ran, the did-not-run line
# the next night reads, and a slot's own verbs stopping at their first failure are all that script's
# and are unchanged. This file adds the order and nothing about how one slot runs.
#
# A slot that fails does not stop the window. Thirty-seven separate tasks never did, and on 2026-09-04
# three night slots refused on the tree guard and every slot after them still ran. The window runs
# them all and exits with the first code that was not nought, so the scheduler still shows the night
# needs looking at.
#
# The window lines carry no leading space and never read "slot <name> starting," or "slot <name> clean",
# because tools/slot-log-verdict.ps1 and the reconciliation read those as a slot's own words.
#
# see: A slot that did not run is recorded the next night from its log, and a holiday is read from the index history

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    # Every key of $windows below, and nothing else. `slot-roster` holds the two to each other and to
    # NightlySchedule.Windows in every direction.
    [ValidateSet('spread-open', 'spread-close', 'evening', 'night', 'weekly')]
    [string]$Window,

    # The script each slot is dispatched through. Always tools/nightly.ps1 on the machine. A test passes
    # a probe in its place, so the window's own behaviour runs without a store or a worker.
    [string]$Dispatcher,

    # Where the night's log is written. Always the production data root's logs on the machine; a test
    # passes a temporary directory so a proof never writes into the live log.
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'shell-provenance.ps1')

$repository = Split-Path -Parent $PSScriptRoot
if (-not $Dispatcher) { $Dispatcher = Join-Path $PSScriptRoot 'nightly.ps1' }
if (-not $LogDirectory) { $LogDirectory = Join-Path $repository 'data\live\logs' }

# The slots each window runs, in the order NightlySchedule.Slots declares them.
$windows = [ordered]@{
    'spread-open'  = @('spread-open')
    'spread-close' = @('spread-close')
    'evening'      = @('universe', 'actions', 'bars', 'rebuild', 'index', 'indicators', 'scans', 'sectors',
                       'regime', 'detect', 'seal', 'controls', 'cap', 'versions', 'plans', 'watchlist')
    'night'        = @('intraday', 'vwap', 'resolve', 'orders', 'fills', 'manage', 'trades', 'audit',
                       'forward', 'losses', 'scores', 'acceptance', 'scoreboard', 'snapshot')
    'weekly'       = @('ceiling', 'twins', 'signals', 'pack', 'seat', 'registry')
}

if (-not (Test-Path $LogDirectory)) { New-Item -ItemType Directory -Path $LogDirectory | Out-Null }
$log = Join-Path $LogDirectory ("nightly-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

function Write-Line([string]$text) {
    $stamped = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text
    Write-Output $stamped
    Add-Content -Path $log -Value $stamped -Encoding utf8
}

# One slot, in a process of its own, with its exit code left in $script:SlotExitCode.
#
# A process of its own because nightly.ps1 ends every path with `exit`, which would end this script too
# if it ran here. Under Continue for the reason nightly.ps1 gives at Invoke-Stage: under Stop the first
# line a child writes to stderr is terminating, and the window would stop on a slot's diagnostic.
# The child's output is passed through to the scheduler's history and not written to the log again,
# because the child writes its own lines to the same log.
function Invoke-Slot {
    param([Parameter(Mandatory = $true)][string]$Slot)

    $ErrorActionPreference = 'Continue'
    $shell = (Get-Process -Id $PID).Path
    & $shell -NoProfile -ExecutionPolicy Bypass -File $Dispatcher -Slot $Slot 2>&1 |
        ForEach-Object { Write-Output ("    {0}" -f $_) }
    $script:SlotExitCode = $LASTEXITCODE
}

$slotsOfWindow = $windows[$Window]

Write-Line ("window {0} begins, {1} slot(s): {2}" -f $Window, $slotsOfWindow.Count, ($slotsOfWindow -join ' '))
Write-Line (Get-ShellProvenance -Name 'nightly-window')

$firstFailure = 0
$failed = 0

foreach ($slot in $slotsOfWindow) {
    Invoke-Slot -Slot $slot
    $code = $script:SlotExitCode

    Write-Line ("window {0}: {1} exited {2}" -f $Window, $slot, $code)

    if ($code -ne 0) {
        $failed++
        if ($firstFailure -eq 0) { $firstFailure = $code }
    }
}

Write-Line ("window {0} ends, {1} of {2} slot(s) exited other than nought" -f $Window, $failed, $slotsOfWindow.Count)
exit $firstFailure
