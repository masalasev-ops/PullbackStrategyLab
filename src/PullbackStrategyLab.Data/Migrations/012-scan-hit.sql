-- 012  scan_hit
--
-- Six scans a night, three per direction, each taking the top fifty universe names by its own
-- magnitude. A stock must appear on one to be eligible, because the premise of the whole strategy
-- is that something happened.
--
-- Rank rather than a threshold on the move, and the column is what says so. A rank cut can be
-- calibrated against nightly counts with no forward return in the store, which is what 2.11 does;
-- a percentage floor cannot, because whether eight percent is strict is a fact about market
-- volatility over the sample rather than about the corpus. Rank is also the only thing that makes
-- the six comparable to each other: a one-day move of eight percent and a twenty-session move of
-- twenty-five percent are not the same strictness and nothing says what would make them so.
-- see: The scans select a fixed count by rank, not a threshold on the move
--
-- The magnitude is stored beside the rank. It is what the thrust signals freeze, and recomputing
-- it later from bars would be a second implementation of the same arithmetic in the one place a
-- disagreement is invisible: a wrong magnitude still produces a plausible ranked list.
--
-- cluster_count is written by ThemeClusterer at 2.6 and is null until then. Same shape as
-- ladder_grade on indicator_daily: the column arrives with the table and the component that fills
-- it arrives later, which writer-ownership reports as deferred to that checkpoint by name.
--
-- Grain is ticker + date + scan. Append-only in practice rather than by rule: the scans are a
-- function of stored bars and a night is computed once, so a rerun of the same night finds the
-- rows already there and writes nothing.

CREATE TABLE scan_hit (
    ticker        TEXT    NOT NULL,
    as_of         TEXT    NOT NULL,
    scan          TEXT    NOT NULL CHECK (scan IN ('gainer', 'gapper', 'leader', 'decliner', 'gapdown', 'laggard')),
    rank          INTEGER NOT NULL,
    magnitude     TEXT    NOT NULL,
    cluster_count INTEGER NULL,
    PRIMARY KEY (ticker, as_of, scan),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

-- The nightly read is one scan on one night in rank order, which the primary key does not give:
-- it leads on ticker. The thrust check makes the other read, one ticker across the last ten
-- sessions, and the primary key already serves that one.
CREATE INDEX ix_scan_hit_as_of_scan ON scan_hit (as_of, scan, rank);
