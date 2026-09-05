#requires -Version 5.1
<#
  Runs tools/snapshot-db, which is a bash script with no extension.

  The same wrapper as migrate.ps1 beside it and for the same reason, with one that is its own:
  snapshots are the recovery path and there is no other. A snapshot command that returns 0 having
  done nothing leaves an operator believing they have a copy of the store, and the moment that
  belief is tested is the moment the store is already gone.

  Exit codes: 3 is no usable bash, and the script's own are passed through untouched.
#>

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'shell-provenance.ps1')

Write-ShellProvenance -Name 'snapshot-db'

Invoke-BashEntryPoint `
    -Name 'snapshot-db' `
    -RepositoryRoot $repositoryRoot `
    -Script 'tools/snapshot-db' `
    -Arguments $args

exit $script:BashEntryExitCode
