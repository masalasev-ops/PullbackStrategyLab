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
# Every run records the commit it ran from, and refuses to run from a branch unless told to.
#
# The job runs from a working tree, so what it executes changes when the branch does. Logging the
# commit was the first answer and it was not enough: the log is a file nobody opens, so the branch
# was recorded on three separate occasions and read on none of them. On 2026-08-28 every slot of one
# night ran from `phase-3-measurement` at six different commits and the lab flagged nothing. The
# guard refuses instead, and it carries the escape the first attempt lacked, which is why that
# attempt was removed: -AllowBranch is an explicit act by an operator who means it, so the refusal
# cannot stop accumulation on the day a phase merges.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    # Every key of $slots below, and nothing else. The two lists are one fact written twice because
    # a ValidateSet attribute takes literals and cannot be derived, so `slot-roster` reconciles them
    # in both directions on every CI run. They had drifted by four before it existed: 'spread-open',
    # 'spread-close', 'watchlist' and 'vwap' were declared as slots and rejected by this line, so
    # four stages built between 4.1 and 4.4 could not be dispatched at all.
    [ValidateSet('spread-open', 'spread-close', 'universe', 'actions', 'bars', 'rebuild', 'index',
                 'indicators', 'scans', 'sectors', 'regime', 'detect', 'seal', 'controls', 'cap',
                 'plans', 'watchlist', 'intraday', 'vwap', 'resolve', 'orders', 'fills', 'manage',
                 'trades', 'audit', 'forward',
                 'scoreboard',
                 'ceiling', 'snapshot')]
    [string]$Slot,

    # The escape, and the reason the guard is safe to have. A phase that merges to `main` leaves the
    # tree on a branch for as long as the merge takes, and a guard with no way through would stop the
    # night's accumulation for exactly that window. Naming the switch is the operator saying they
    # know where the tree is.
    [switch]$AllowBranch
)

$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
$worker = Join-Path $repository 'src\PullbackStrategyLab.Worker'
$dataRoot = Join-Path $repository 'data\live'

# The slots, and the verbs each runs in order. Two verbs in one slot means the second reads what
# the first writes, which is why they are a slot rather than two entries a minute apart.
$slots = @{
    # The two intraday slots, and they run inside the session rather than after it. They are the
    # only slots in this map that fire while the market is open, which is what makes them
    # unrecoverable in a harder sense than the minute bars: a quote has no history to buy back, so a
    # pass that does not fire is a sample that never existed. The two are separate slots rather than
    # one with two verbs, because the whole point of them is that they happen five and a half hours
    # apart.
    'spread-open'  = @(, @('spreads', 'after_open'))
    'spread-close' = @(, @('spreads', 'before_close'))
    'universe'   = @(, @('universe-build'))
    'actions'    = @(, @('actions'))
    'bars'       = @(, @('daily-bars'))
    'rebuild'    = @(, @('backfill', '--rebuild'))
    'index'      = @(, @('index-bars'))
    'indicators' = @(, @('indicators'))
    'scans'      = @(@('scans'), @('tiers'))
    # Twice, and the second pass is usually free. A name the vendor refused or answered
    # unreadably is counted and left unstamped, so the second pass asks exactly those and
    # costs one call each; where the first pass finished its list the second finds nothing
    # unresolved and costs nothing at all. It is here rather than in the stage because the
    # window it has to happen inside is short and nobody is watching it: every reader bounds
    # on when the lookup was made, so a sector resolved after 23:59:59.999Z is invisible to
    # the session it was wanted for, and `clusters` runs three minutes after this slot.
    # On 2026-08-27 one name took the other 86 with it and fifteen setups recorded a cluster
    # verdict with no value, permanently.
    'sectors'    = @(@('sectors'), @('sectors'))
    'regime'     = @(@('clusters'), @('regime'))
    'detect'     = @(@('detect-long'), @('detect-short'))
    'seal'       = @(@('vectorize'), @('journal'))
    'controls'   = @(, @('controls'))
    'cap'        = @(, @('cap'))
    # 18:30, after the cap and before the watchlist publishes what it wrote. Absent from this map
    # until 4.5, so the stage built at 4.16 was scheduled by the runbook, dispatched by the worker
    # and run by nothing.
    'plans'      = @(, @('plans'))
    # 18:40. It writes nothing: the page projects the setups, and this slot is where a night
    # that capped nothing, or was never capped at all, is noticed without opening a browser.
    'watchlist'  = @(, @('publish-watchlist'))
    # The one unrecoverable slot. Minute bars publish two to three hours after the close, so
    # this runs at 20:30 for the session that has just closed, and resolves the setups flagged
    # on the evening before it. A night this does not run is a session of minute bars that
    # cannot be bought back at any price, which is why it is scheduled rather than left to
    # be run by hand.
    'intraday'   = @(, @('intraday-bars'))
    # 21:00, half an hour after the bars land, and it spends no vendor call. Its own slot rather
    # than a second verb inside 'intraday', because the two have different failure consequences:
    # a fetch that does not run loses minutes for ever, and an averaging that does not run can be
    # rerun from the stored minutes any evening after.
    'vwap'       = @(, @('vwap'))
    # 21:05, after the averages and over the same stored minutes. It walks the session one minute at
    # a time for every name carrying a plan at once, because the earliest trigger is what fills when
    # the caps bind and that is a comparison across names. Its own slot rather than a second verb
    # inside 'vwap': an averaging that does not run can be rerun any evening, and so can this, but a
    # session with plans resting in it and no minutes is a night the lab was blind on, and the run
    # row says partial rather than clean.
    'resolve'    = @(, @('resolve-triggers'))
    # 21:10, over the triggers the replay recorded. It applies the caps in the order the
    # triggers happened, so a full book blocks the later ones, and it writes a row for every
    # refusal: a blocked order is evidence about the caps rather than an absence of evidence.
    'orders'     = @(, @('orders'))
    # 21:15, over the orders the gate placed. It prices what each resting order actually got,
    # charging the session's widest captured spread the wrong way. A name the session quoted no
    # usable book for is not filled, and the row says so rather than the order disappearing.
    'fills'      = @(, @('fills'))

    # 21:20, over every position open at any point in the session, including the ones the slot
    # above opened a moment ago. It runs the two rule sets, the long trail on the 9-day average and
    # the short trim at 3R with its exit on an hourly close back above the 50-day, and it runs the
    # give-up point alongside them, because the exit is whichever is reached first and that is a
    # comparison one component has to make.
    'manage'     = @(, @('manage'))

    # 21:25, over the positions the slot above closed. It states each one's result in R after the
    # borrow a short is charged for the calendar days it was held, which is the whole reason a trade
    # is a row rather than a view over a position.
    'trades'     = @(, @('trades'))

    # 21:26, after the trades exist, because an audit points at one. It holds the plan against what
    # happened in three pairs: the price each instruction named against the price it got, the plan's
    # stop against where the trade ended, and the size the plan carried against the size the gate
    # placed. It changes no result, which is what keeps it an audit.
    'audit'      = @(, @('audit'))
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

# The tree is this repository's production checkout, and the ref is a property of the tree that
# nothing else in this corpus can see. `tools/ci.*` reads the source, the documents and a store it
# builds; `tools/verify-phase` reads those and the golden fixture. Neither can tell which ref
# produced the tree it is reading, and both are green on a branch by design, so a night run from a
# half-finished branch is green everywhere and wrong in the one place that matters.
#
# It refuses rather than warning, because a warning goes in the log and the log is what failed three
# times. A refusal exits non-zero, which the scheduler surfaces.
if ($branch -ne 'main' -and -not $AllowBranch) {
    Write-Line ("  refusing: the tree is on '{0}' and not on main. The nightly runs from this repository's" -f $branch)
    Write-Line "  production checkout, so a slot dispatched from a branch runs whatever that branch happens to hold."
    Write-Line "  Return the tree to main, or pass -AllowBranch if you mean to run from this one."
    exit 4
}

# A ref nothing could read is not a ref that says main. `git` missing, or a tree that is not a
# repository, both land here, and both are states where the guard cannot do its job: it says so
# rather than passing.
if ($branch -eq 'unknown' -and -not $AllowBranch) {
    Write-Line "  refusing: the tree's branch could not be read, so this slot cannot confirm it runs from main."
    Write-Line "  Pass -AllowBranch if you mean to run from a checkout git cannot describe."
    exit 4
}

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
