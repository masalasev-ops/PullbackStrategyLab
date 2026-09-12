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

# **The two entry points that open a store and set no root refuse rather than guessing one.**
#
# Ruled at the 6.9 sign-off and taken at 7.14. Of the ten entry points `shell-executable`
# reconciles, four assign a data root and all four run stages; `migrate` and `snapshot-db` open the
# store and assign none, so both take `appsettings.json`'s default, which is the relative path
# `data` resolved against the process working directory. Run from the repository root on 2026-09-08
# `tools/migrate` created an empty store at a third root, applied sixty-one migrations and reported
# `version 0 to 61` while the live store stayed at 57 with 2,531,141 rows. **That is worse than a
# silent no-op**, because the output is a transcript of work that really happened somewhere else.
#
# **A refusal and not a default**, which is the half the obligation could not have seen. A root
# derived from the script's own location would put `data/live` under whichever checkout the script
# ran from, falsifying the sentence saying the working tree holds no live store, and it would remove
# the symptom the fault was found by: the operator saw a path ending `data` where one ending
# `data/live` belonged, and a wrong root spelled `data/live` reads exactly right.
#
# Exit 2 is the operator's own mistake, kept apart from 3, which is the wrapper saying it found no
# bash, and from the Worker's own codes. No caller breaks: `tools/ci.*` run the Worker verb directly
# and never these scripts, the nightly's snapshot slot runs the stage under its own root, and the
# rehearsal job passes a root inline.
# see: The lab keeps one store per purpose under one data root, and CI never opens the operator's
refuse_without_a_data_root() {
    if [ -n "${PullbackStrategyLab__DataRoot:-}" ]; then
        return 0
    fi

    printf '%s: PullbackStrategyLab__DataRoot is not set, so nothing was run.\n' "$1" >&2
    printf '%s: this command opens a store and sets no root of its own, and the default is a root that is neither store.\n' "$1" >&2
    printf '%s: set it to the root you mean for the one command, and read the path the command prints back.\n' "$1" >&2

    local data="$2/data"

    if [ -d "$data" ]; then
        printf '%s: roots under %s:\n' "$1" "$data" >&2
        for candidate in "$data"/*/; do
            [ -d "$candidate" ] || continue
            if [ -f "${candidate}pullbackstrategylab.db" ]; then
                printf '%s:   %s (holds a store)\n' "$1" "${candidate%/}" >&2
            else
                printf '%s:   %s\n' "$1" "${candidate%/}" >&2
            fi
        done
    else
        printf '%s: no data directory exists at %s.\n' "$1" "$data" >&2
    fi

    exit 2
}
