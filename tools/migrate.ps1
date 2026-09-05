#requires -Version 5.1
<#
  Runs tools/migrate, which is a bash script with no extension.

  **This is the cell the 3.14 repair did not reach.** `verify-phase` got its wrapper then, and it
  got it alone: `tools/migrate` and `tools/snapshot-db` are extensionless bash scripts whose cells
  in CLAUDE.md's Commands table read "same", and "same" is true of the file and false of the shell.
  Called by name from a PowerShell session the call returns 0 having done nothing, leaves
  `$LASTEXITCODE` unset and leaves `$?` true, so an operator following RUNBOOK step 6 or the
  stale-store recovery gets a silent no-op that reads exactly like a success. The store on
  2026-08-28 was never migrated, four stages died on a missing column and the lab flagged nothing;
  no cause of this kind is claimed for that night, and this is the mechanism that would produce one.

  A wrapper and not a second implementation, for the reason verify-phase.ps1 gives: two
  implementations of a migration entry point would drift, and the one that drifted would be the one
  somebody ran. The search, the probe and the loud refusal live in shell-provenance.ps1 beside it,
  so all three wrappers share one of each rather than three copies.

  Exit codes: 3 is no usable bash, and the script's own are passed through untouched.
#>

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'shell-provenance.ps1')

Write-ShellProvenance -Name 'migrate'

Invoke-BashEntryPoint `
    -Name 'migrate' `
    -RepositoryRoot $repositoryRoot `
    -Script 'tools/migrate' `
    -Arguments $args

exit $script:BashEntryExitCode
