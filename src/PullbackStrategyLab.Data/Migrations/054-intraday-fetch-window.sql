-- 054  the fetch records the width it ran at, and the quantity its outcome now turns on
--
-- <b>Two columns for two faults, and the second is why the first is legible.</b>
--
-- `window_sessions` is the first. `IntradayFetcher` bought one session a night as built, while a
-- swing sits three to twenty-seven sessions back, so the anchored level had nothing to read; the
-- ruling of 2026-09-04 widened the buy to the twenty-seven session anchor window. Short's
-- twenty-session count starts on the first night the fetch runs at that width and not on the night
-- the code lands, and those are different dates. Without this column the only way to tell one from
-- the other is to date a run against a commit, which is an inference rather than a record. A night
-- at one session is a permanent forfeit of that session's anchors, so what the window covered is
-- written down rather than reconstructed.
-- see: The intraday fetch buys the twenty-seven session anchor window, and the count starts on the first night it runs at that width
--
-- <b>The width admits nought.</b> A night with no prior flagged session asks for nothing and buys
-- no window at all, which is a real state rather than a missing value: recording it as one session
-- would say the fetch ran at the width it had before the ruling. The store cannot always give
-- twenty-seven either, since the window is the sessions the store knows about and it has held
-- fewer than twenty-seven for most of the lab's life, so the column is what the night covered and
-- never what it was aiming at.
--
-- `stored` is the second, and it exists because the outcome had nothing honest to turn on. On the
-- night of 2026-09-04 the stage asked 92 names, was answered by all 92 with nothing, spent 460
-- calls, wrote 0 bars and recorded `clean` with `stopped_because` NULL. `bars_written` was already
-- 0 on that row and nothing read it; the fault is that the outcome did not depend on it. It cannot
-- depend on `bars_written` either, because a rerun over a session already held writes nought bars
-- and has lost nothing: `IntradayBarReader.IsStoredUnchanged` skips every bar the store already
-- has. `stored` is what the night's asking left the store holding for that window, being the bars
-- written plus the bars already there unchanged, which is the quantity that separates a night that
-- bought nothing from a night that needed to buy nothing. It is the third of the three quantities
-- 4.2's row already names, asked, returned and stored, and it is the one that was missing.
--
-- <b>Rebuilt rather than altered, on the terms 045, 048, 049 and 050 were.</b> SQLite adds a NOT
-- NULL column only with a constant default, and a default of one would write the pre-ruling width
-- onto rows whose width nobody recorded, while a default of nought on `stored` would say every
-- night before this one bought nothing. Nothing holds a foreign key into `intraday_fetch`.

DROP INDEX IF EXISTS ix_intraday_fetch_session;

ALTER TABLE intraday_fetch RENAME TO intraday_fetch_before_054;

CREATE TABLE intraday_fetch (
    session_date    TEXT    NOT NULL,
    setup_as_of     TEXT    NOT NULL,
    requested       INTEGER NOT NULL,
    fetched         INTEGER NOT NULL,
    empty           INTEGER NOT NULL,
    bars_written    INTEGER NOT NULL,
    stored          INTEGER NOT NULL,
    window_sessions INTEGER NOT NULL CHECK (window_sessions >= 0),
    outcome         TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because TEXT    NULL,
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at),

    -- A night that fell short says which shortfall it was. This is the half of the 2026-09-04
    -- fault a constraint can hold: the stage decides the outcome, and the store refuses an outcome
    -- that declines to say why. It is one-directional on purpose. The first night of the lab's
    -- life records `clean` and carries a phrase saying there was no prior flagged session to ask
    -- about, which is a night that asked for nothing rather than a night that fell short, and a
    -- biconditional here would force that phrase off the row or force the night to call itself
    -- partial. Neither is true, so `stopped_because` is what the row has to say about the shape of
    -- its night, and `outcome` is whether the night lost anything.
    CHECK (outcome <> 'partial' OR stopped_because IS NOT NULL)
);

INSERT INTO intraday_fetch (
    session_date, setup_as_of, requested, fetched, empty, bars_written, stored,
    window_sessions, outcome, stopped_because, observed_at)
SELECT
    session_date, setup_as_of, requested, fetched, empty, bars_written,

    -- `bars_written` and not nought, and the two are the same figure on every row that exists.
    -- `stored` differs from it only where a night re-asked for minutes the store already held
    -- unchanged, and no night before this migration ever did: the stage bought one session, and
    -- the one session it bought was the one that had just closed.
    bars_written,

    -- One session where the night asked for something, nought where it asked for nothing. Both
    -- are what those nights actually did rather than a default standing in for an unknown.
    CASE WHEN requested > 0 THEN 1 ELSE 0 END,
    outcome, stopped_because, observed_at
  FROM intraday_fetch_before_054;

DROP TABLE intraday_fetch_before_054;

CREATE INDEX ix_intraday_fetch_session ON intraday_fetch (session_date);
