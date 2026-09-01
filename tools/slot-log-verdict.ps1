# What a slot's log says about a stage that was supposed to run, as one verdict.
#
# It exists because the two runner jobs that read a slot log were asking a question that cannot
# separate two states. Both asked whether the log carries some string. A log the slot script wrote
# and no stage contributed to answers that question perfectly well: it does not carry the string,
# and it does not carry any string, because nothing ran. The real job then failed saying the script
# was discarding a stage's message, which was false; the inverted job, whose assertion is that the
# log *lacks* a message no stage ever writes, passed, because that is true of an empty log too.
#
# The 4.2 tree guard produced exactly that state on every runner, since a runner is never on main.
# The real job went red for a reason it named wrongly and the inverted job stayed green through all
# of it, which is a green over nothing inside the instrument built to prevent greens over nothing.
#
# So the verdict has three values and not two, and the empty case is its own.
#
#   empty    no stage produced output at all. Nothing can be concluded about the slot script's
#            handling of a stage's message, because there was no message.
#   missing  a stage produced output and the wanted pattern is not in it. This is the defect the
#            real job exists to catch.
#   ok       a stage produced output and the wanted pattern is in it.
#
# It always exits 0. The verdict is the output and the caller decides what each one means, which is
# what lets the inverted job require `empty` and `missing` from two deliberately produced runs while
# the real job requires `ok` from one.

param(
    [Parameter(Mandatory = $true)][string]$Log,
    [Parameter(Mandatory = $true)][string]$Wanted,

    # The verb the slot was expected to dispatch. Named rather than inferred, because "some stage
    # ran" is not the question: a slot with two verbs that dispatched the first and died would
    # otherwise read as a slot that ran the one being asked about.
    [string]$Verb = 'universe-build'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Log)) {
    Write-Output "empty the slot wrote no log at all, so no stage can have produced output"
    exit 0
}

$lines = @(Get-Content $Log)

# The lines the slot script writes about itself, as it writes them. A line matching one of these
# came from the script and not from a stage, which is the whole distinction this file exists for.
$slotOwn = @(
    'slot \S+ starting,',
    'slot \S+ clean$',
    '\s+refusing:',
    '\s+production checkout,',
    '\s+Return the tree to main,',
    '\s+Pass -AllowBranch',
    '\s+running ',
    'exited \d+; slot \S+ stops here'
)

$dispatchedAt = -1
for ($at = 0; $at -lt $lines.Count; $at++) {
    if ($lines[$at] -match ('\s+running .*' + [regex]::Escape($Verb))) {
        $dispatchedAt = $at
        break
    }
}

if ($dispatchedAt -lt 0) {
    Write-Output "empty the log has no line dispatching $Verb, so the slot stopped before any stage ran"
    exit 0
}

# Everything after the dispatch that the script did not write itself. A stage that ran and said
# nothing lands here too, and it is the same verdict for the same reason: there is no message, so
# nothing can be concluded about whether a message would have survived.
$fromStage = @(
    $lines[$dispatchedAt..($lines.Count - 1)] |
        Where-Object {
            $line = $_
            $line.Trim() -and -not ($slotOwn | Where-Object { $line -match $_ })
        }
)

if ($fromStage.Count -eq 0) {
    Write-Output "empty $Verb was dispatched and wrote nothing the log kept, so there is no message to have survived"
    exit 0
}

if (-not ($lines -match $Wanted)) {
    Write-Output "missing $Verb wrote $($fromStage.Count) line(s) and none of them matched the wanted pattern"
    exit 0
}

Write-Output "ok $Verb wrote $($fromStage.Count) line(s) and the wanted pattern is among them"
exit 0
