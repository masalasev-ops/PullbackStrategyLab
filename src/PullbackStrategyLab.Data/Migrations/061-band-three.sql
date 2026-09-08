-- 061  the state a panel reads badly in, and the proposal a version came from
--
-- <b>Two changes, and both are about a figure that could not be computed.</b>
--
-- <b>The scoreboard could say what makes a panel read badly and could not say whether it does.</b>
-- ARCHITECTURE's figure caption says every panel carries the condition under which it reads badly,
-- and one panel states a threshold: band 0 reads red if degraded nights exceed 5% of the record.
-- The condition was a static caption on the page, nothing computed the ratio, and nothing rendered
-- anything red, which is the row raised at 3.5. **A colour is not a word**, so `surface-claims` could
-- not have seen it either: that check renders a page and reads the text that came out. The state is
-- computed by the builder and stored, on the same terms every other figure on this page is: a read
-- surface that decided it would be a second implementation of the threshold with the page as the
-- last place anybody looked
-- (see: The averages are one implementation, computed nightly and drawn on demand).
--
-- <b>The two columns are present together and the store says so.</b> A panel reading badly with no
-- reason is a red mark nobody can act on, and a reason with no state is the caption this replaces.
--
-- <b>And the success criterion could not be computed at all.</b> The scoreboard's third band is
-- proposal hit rate by pack version, which is the project's stated success criterion, and there was
-- no path from an accepted version back to the proposal that produced it: `proposal` carries the
-- pack version it was cut against and `variant` carried nothing about where it came from, so the
-- join the band is defined by did not exist. Band 3 would have read withheld for ever, for a reason
-- that is about the build rather than about the evidence
-- (see: The evidence pack is versioned, and the success criterion is proposal hit rate by pack version).
--
-- <b>`proposal_id` is written at creation like everything else on that row.</b> VariantAdmitter
-- writes it once and AcceptanceGate has no path to it, on exactly the terms the target and the
-- minimum sample are written once
-- (see: Targets and minimum samples are written at creation and are immutable). It is nullable
-- because a version need not come from a proposal: the baseline came from nobody, and an operator
-- may register a version of their own.

-- ---------------------------------------------------------------------------------------------
-- `variant`, gaining the proposal it came from
-- ---------------------------------------------------------------------------------------------

ALTER TABLE variant ADD COLUMN proposal_id TEXT NULL REFERENCES proposal (proposal_id);

CREATE INDEX ix_variant_proposal ON variant (proposal_id);

-- ---------------------------------------------------------------------------------------------
-- `scoreboard`, rebuilt for the state a panel reads badly in
-- ---------------------------------------------------------------------------------------------

CREATE TABLE scoreboard_new (
    as_of              TEXT    NOT NULL,
    panel              TEXT    NOT NULL,
    direction          TEXT    NULL CHECK (direction IS NULL OR direction IN ('long', 'short')),
    figure             TEXT    NOT NULL,
    low                TEXT    NULL,
    high               TEXT    NULL,
    n_rows             INTEGER NOT NULL,
    n_effective        INTEGER NULL,
    population         TEXT    NULL,
    n_minimum          INTEGER NULL,
    withheld_because   TEXT    NULL,
    n_sessions         INTEGER NULL,
    n_minimum_sessions INTEGER NULL,

    -- Whether the panel's own condition is met tonight, and what the condition is. Null on every
    -- panel that states no threshold, which is most of them: a condition written in prose beside a
    -- figure is a caption and belongs on the page, and only a threshold a number can be held against
    -- belongs here.
    reads_badly        INTEGER NULL CHECK (reads_badly IS NULL OR reads_badly IN (0, 1)),
    reads_badly_because TEXT   NULL,

    computed_at        TEXT    NOT NULL,

    PRIMARY KEY (as_of, panel, direction, computed_at),

    -- Present together. A panel reading badly with no reason is a mark nobody can act on, and a
    -- reason with no state is the static caption this replaces.
    CHECK ((reads_badly IS NULL) = (reads_badly_because IS NULL))
);

INSERT INTO scoreboard_new (
    as_of, panel, direction, figure, low, high, n_rows, n_effective, population, n_minimum,
    withheld_because, n_sessions, n_minimum_sessions, reads_badly, reads_badly_because, computed_at)
SELECT
    as_of, panel, direction, figure, low, high, n_rows, n_effective, population, n_minimum,
    withheld_because, n_sessions, n_minimum_sessions, NULL, NULL, computed_at
  FROM scoreboard;

DROP TABLE scoreboard;

ALTER TABLE scoreboard_new RENAME TO scoreboard;

-- The read the page makes: one day, every panel, latest generation first.
CREATE INDEX ix_scoreboard_as_of ON scoreboard (as_of, computed_at);
