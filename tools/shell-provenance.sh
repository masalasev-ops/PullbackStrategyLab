#
# What ran, and what it ran under. Sourced by every bash entry point in tools/.
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
# This file is not an entry point and carries no shebang: it is sourced, never executed.
#
# see: Every phase ends in a generated phase report, not in a page somebody looks at

shell_provenance() {
    printf '%s: shell bash %s at %s, host %s, %s\n' \
        "$1" \
        "${BASH_VERSION:-unknown}" \
        "${BASH:-unknown}" \
        "$(hostname 2>/dev/null || printf 'unknown')" \
        "$(uname -srm 2>/dev/null || printf 'unknown')"
}
