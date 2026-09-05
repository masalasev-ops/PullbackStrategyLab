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
# **The search, the probe and the refusal moved to shell-provenance.ps1 at 6.10**, unchanged in
# behaviour, because the two cells this repair had left alone needed the same three and copying
# them would have made three of each. The reasoning above is why they exist and stays here; the
# code is one implementation beside the two other wrappers that now use it.
#
# see: Every phase ends in a generated phase report, not in a page somebody looks at

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'shell-provenance.ps1')

Write-ShellProvenance -Name 'verify-phase'

Invoke-BashEntryPoint `
    -Name 'verify-phase' `
    -RepositoryRoot $repositoryRoot `
    -Script 'tools/verify-phase' `
    -Arguments $args

exit $script:BashEntryExitCode
