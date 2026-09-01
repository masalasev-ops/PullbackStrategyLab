#!/usr/bin/env python3
"""Restate the authored-parameters figures independently of the check that asserts them.

A one-time verification aid on the same footing as derive-indicators.py, and not run by CI. It
exists so 4.15's DERIVED expectations are produced by something that shares no code with
PhaseReplay.AuthoredParameterFigures: a different language, a different HTML reading, and a row
splitter written from the markup rather than borrowed from the C# helper.

The figures it prints are the ones fixtures/expectations.json carries for 4.15. If it and the
replay ever disagree, one of the two has changed how it reads the table, and that is the thing
worth knowing rather than which number is larger.

Usage: python tools/derive-authored-parameters.py
"""

from __future__ import annotations

import pathlib
import re
import sys
from html.parser import HTMLParser


class Section(HTMLParser):
    """The first table after the Authored parameters heading, as a list of rows of cell text.

    Written against the parser in the standard library rather than against a regex, which is the
    other half of sharing no code with the thing under test: the C# side matches markup with its
    own reader, and a bug in either would have to be reproduced independently to go unnoticed.
    """

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.reached = False
        self.done = False
        self.in_table = False
        self.rows: list[list[str]] = []
        self._cell: list[str] | None = None
        self._text: list[str] = []
        self._header = False

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if self.done:
            return
        if tag == "h2" and dict(attrs).get("id") == "authored-parameters":
            self.reached = True
        elif tag == "table" and self.reached:
            self.in_table = True
        elif tag == "tr" and self.in_table:
            self._cell = []
            self._header = False
        elif tag in ("td", "th") and self._cell is not None:
            self._text = []
            self._header = self._header or tag == "th"

    def handle_endtag(self, tag: str) -> None:
        if self.done:
            return
        if tag in ("td", "th") and self._cell is not None:
            self._cell.append("".join(self._text))
            self._text = []
        elif tag == "tr" and self._cell is not None:
            if not self._header:
                self.rows.append(self._cell)
            self._cell = None
        elif tag == "table" and self.in_table:
            self.in_table = False
            self.done = True

    def handle_data(self, data: str) -> None:
        if self._cell is not None:
            self._text.append(data)


def main() -> int:
    root = pathlib.Path(__file__).resolve().parent.parent
    html = (root / "docs" / "ARCHITECTURE.html").read_text(encoding="utf-8")

    parser = Section()
    parser.feed(html)
    rows = parser.rows

    # The parser drops markup, so the OPEN mark arrives as bare text in the parameter cell. That is
    # deliberate: the C# side looks for the substring in the cell it built, and a reading that only
    # matched <b>OPEN</b> would agree with it for the wrong reason.
    open_rows = [r[0] for r in rows if r and "OPEN" in r[0]]

    # The citation marker is assembled rather than written out, and the avoidance is deliberate
    # enough to need saying so the next reader does not helpfully inline it. `decision-resolves`
    # scans every source file for the marker and requires what follows it to be a decision name; a
    # tool that reads citations therefore cannot spell the marker, because its own occurrences would
    # be read as citations of nothing. The same reason ARCHITECTURE's build-order rows name two
    # components by description instead of by name.
    marker = "(" + "see" + ": "
    citing = [r[0] for r in rows if len(r) > 3 and marker in r[3]]

    print(f"authored.rows             {len(rows)}")
    print(f"authored.open             {len(open_rows)}")
    print(f"authored.filled           {len(rows) - len(open_rows)}")
    print(f"authored.citingADecision  {len(citing)}")

    if open_rows:
        print("\nstill open:")
        for name in open_rows:
            print(f"  {name}")

    print("\nciting a decision:")
    for name in citing:
        print(f"  {name}")

    claim = "Nothing here is left open"
    print(f"\ncompleteness claim present: {claim in html}")

    # Every name cited from this table has to be a decision name, which is decision-resolves' job
    # and is restated here because a citation added in the same pass that adds the decision is the
    # one most likely to be a paraphrase of it.
    decisions = (root / "docs" / "DECISIONS.md").read_text(encoding="utf-8")
    names = {m.strip() for m in re.findall(r"^\*\*(.+?)\*\*$", decisions, re.MULTILINE)}
    cited = set()
    for r in rows:
        if len(r) > 3:
            cited.update(
                m.strip() for m in re.findall(re.escape(marker) + r"(.+?)\)", r[3]))

    unresolved = sorted(c for c in cited if c not in names)
    print(f"names cited from this table: {len(cited)}, unresolved: {len(unresolved)}")
    for name in unresolved:
        print(f"  UNRESOLVED  {name}")

    return 1 if unresolved or open_rows else 0


if __name__ == "__main__":
    sys.exit(main())
