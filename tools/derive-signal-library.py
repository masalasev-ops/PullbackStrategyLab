#!/usr/bin/env python3
"""Derive the signal library's own figures, and the admission population, outside the solution.

One-time verification aid, not run by CI, on the terms derive-indicators.py and
derive-minimum-sample.py are. It exists so the figures 6.2 writes into
fixtures/expectations.json are DERIVED rather than FROZEN: a frozen expectation records
what the code produced and can only ever detect a change, and the two sides of this
checkpoint are exactly the kind that agree because one was copied from the other.

Two derivations, and they are independent of different things.

  --library reads docs/SCHEMA.md's Signals section and counts it. That is independent of
  SignalLibrary.Declared, which is a C# list somebody transcribed from the same section,
  and it is the transcription that could be wrong. The parser here is deliberately not the
  one the suite uses: it splits on pipes and reads the last cell, where SchemaSignals reads
  a regex over the whole row.

  --population opens a copy of the replay store and counts the setups on each side whose
  scoring-horizon outcome is filled. That is independent of AdmissionResult, which is what
  the stage reports about the same rows. Long and short are counted apart and never added.

Usage:
    python tools/derive-signal-library.py --library
    python tools/derive-signal-library.py --population <path to a store copy>

The store copy is what PullbackStrategyLab__ReplayStoreCopy leaves behind when the fixture
replay is run with it set. Never point this at data/live: it reads only, but a figure
derived from the running lab is a figure about last night rather than about the build.
"""

import re
import sqlite3
import sys
from pathlib import Path

SECTION = "## Signals"
NEXT_SECTION = "## Trading"

# The horizon the lab scores on. Stated here rather than imported, because importing it
# from the solution would make this derivation a second reading of the thing it checks.
SCORING_HORIZON = 10


def read_section(schema_path):
    text = Path(schema_path).read_text(encoding="utf-8")
    start = text.index(SECTION)
    end = text.index(NEXT_SECTION, start)
    return text[start:end]


def rows(section):
    """Every four-column row whose first cell is a backticked identifier.

    Split on unescaped pipes rather than matched with one regex over the whole row, so this
    parser fails differently from the suite's if either of them is wrong.
    """
    found = []

    for line in section.splitlines():
        if not line.startswith("|"):
            continue

        cells = [c.strip() for c in re.split(r"(?<!\\)\|", line)[1:-1]]

        if len(cells) != 4:
            continue

        name = cells[0]

        if not (name.startswith("`") and name.endswith("`")):
            continue

        found.append((name.strip("`"), cells[1], cells[2], cells[3]))

    return found


def library(schema_path):
    found = rows(read_section(schema_path))

    if len(found) < 30:
        raise SystemExit(
            f"only {len(found)} signal(s) parsed from {schema_path}; the section held more than "
            "thirty when this was written, so the parser stopped matching rather than the "
            "library shrinking"
        )

    active = [f for f in found if f[3] == "active"]
    candidates = [f for f in found if f[3] == "candidate"]
    other = [f for f in found if f[3] not in ("active", "candidate")]

    print(f"library.declared      {len(found)}")
    print(f"library.active        {len(active)}")
    print(f"library.candidates    {len(candidates)}")
    print(f"library.nullControls  {sum(1 for f in found if f[0] == 'day_of_month')}")

    if other:
        print(f"  {len(other)} row(s) state neither status: {', '.join(f[0] for f in other)}")

    # The seed writes one row per declared signal on a first run and none on a second, so
    # both figures follow from the count above rather than from the stage.
    print(f"library.seeded        {len(found)}")
    print("library.seededOnRerun 0")


def population(store_path):
    connection = sqlite3.connect(f"file:{store_path}?mode=ro", uri=True)

    try:
        for direction in ("long", "short"):
            count = connection.execute(
                """
                SELECT COUNT(*)
                  FROM forward_return f
                  JOIN setup s ON s.setup_id = f.subject_id
                 WHERE f.subject_kind = 'setup'
                   AND f.horizon_days = ?
                   AND s.direction = ?
                """,
                (SCORING_HORIZON, direction),
            ).fetchone()[0]

            print(f"admission.{direction}Population {count}")

        total = connection.execute(
            "SELECT COUNT(*) FROM forward_return WHERE subject_kind = 'setup' AND horizon_days = ?",
            (SCORING_HORIZON,),
        ).fetchone()[0]

        # Reported beside the two and never as their sum in a figure: it is here so a store
        # holding scoring-horizon rows for a direction the setup table does not carry is
        # visible rather than absorbed into a nought.
        print(f"  scoring-horizon setup outcomes in the store, either side: {total}")
    finally:
        connection.close()


def main(argv):
    if len(argv) >= 2 and argv[1] == "--library":
        schema = argv[2] if len(argv) > 2 else Path(__file__).resolve().parent.parent / "docs" / "SCHEMA.md"
        library(schema)
        return 0

    if len(argv) == 3 and argv[1] == "--population":
        population(argv[2])
        return 0

    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
