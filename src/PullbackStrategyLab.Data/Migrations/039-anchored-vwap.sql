-- 039  anchored_vwap
--
-- The declining average price the short side's `reached-ceiling` clause reads, which existed in
-- ARCHITECTURE as a phrase and in SCHEMA not at all.
-- see: The anchored average price is anchored at the swing the thrust ran from
--
-- <b>The anchor is a stored instant, not a description.</b> The gate asks whether the bounce came
-- back to "the declining average price anchored to the last swing high", and until this migration
-- nothing said which bar the swing high was or which minute inside it. Two sessions computing that
-- level would not have had to agree, and both answers would have been plausible prices. So the row
-- carries the session the swing sits in, the minute inside it the extreme actually traded in, and
-- what kind of swing it is: a reader can reconstruct the level from the stored minutes without
-- knowing which component wrote it.
--
-- `anchor_kind` is `swing-high` or `swing-low` rather than a direction, because the direction is a
-- fact about the setup and this is a fact about the level. The short side anchors at a high and the
-- long side would anchor at a low, and a column reading `short` would have said which detector
-- asked rather than what was measured.
--
-- <b>`through_session` is the other half of the anchor and is the half that is easy to lose.</b> An
-- anchored average is a level as at a moment, and the moment is the last session the average
-- includes. The stage runs at 21:00 after the minute bars land at 20:30, and those bars are for the
-- session before the evening they arrive on, so the level a detector reads on the evening of N is
-- computed through session N-1 and is one session behind by construction. That is point-in-time
-- clean and it is not the same number as a level through N, so the row says which it is instead of
-- leaving a reader to date it from `observed_at`.
--
-- <b>Absent is a row, not a missing row.</b> `value` is null with `absent_because` filled where the
-- engine had an anchor and could not reach it, which is overwhelmingly that the store holds no
-- minute bars back to the anchor session: IntradayFetcher buys one session a night per flagged
-- name, and a swing high three to twenty-seven sessions back is inside the vendor's reach and
-- outside the store's. A night that wrote nothing and a night whose anchors were all unreachable
-- are different facts and only the first is worth waking anybody for.
--
-- `bars` and `volume` are what the figure was computed over. A volume-weighted average taken over
-- eleven minutes of a thin name is a number with the same name and none of the authority, and
-- nothing downstream can tell the two apart from the price alone.
--
-- Prices are TEXT holding a decimal, never REAL. `volume` is INTEGER because it is a count of
-- shares.
--
-- <b>Append-only in the same idiom as the bar tables</b>, with `observed_at` in the key: a level
-- recomputed after a vendor correction to the minutes underneath it is a new row, and a reader
-- takes the latest observation at or before its as-of.

CREATE TABLE anchored_vwap (
    ticker          TEXT    NOT NULL,
    anchor_session  TEXT    NOT NULL,
    anchor_ts       TEXT    NULL,
    anchor_kind     TEXT    NOT NULL CHECK (anchor_kind IN ('swing-high', 'swing-low')),
    through_session TEXT    NOT NULL,
    setup_as_of     TEXT    NOT NULL,
    value           TEXT    NULL,
    bars            INTEGER NOT NULL,
    volume          INTEGER NOT NULL,
    absent_because  TEXT    NULL,
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (ticker, anchor_session, through_session, observed_at),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

-- What a detector asks: this name's anchored level as at a session, latest observation first.
CREATE INDEX ix_anchored_vwap_read ON anchored_vwap (ticker, through_session, observed_at);

-- What one night's engine did, written whatever the outcome, on the same terms `intraday_fetch`
-- records the fetch above it. A night with no row here is a night nobody ran, which is a different
-- fact from a night that ran and could anchor nothing.
CREATE TABLE vwap_run (
    session_date    TEXT    NOT NULL,
    setup_as_of     TEXT    NOT NULL,
    names           INTEGER NOT NULL,
    sessions_priced INTEGER NOT NULL,
    bars_annotated  INTEGER NOT NULL,
    anchors_asked   INTEGER NOT NULL,
    anchors_priced  INTEGER NOT NULL,
    outcome         TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because TEXT    NULL,
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_vwap_run_session ON vwap_run (session_date);
