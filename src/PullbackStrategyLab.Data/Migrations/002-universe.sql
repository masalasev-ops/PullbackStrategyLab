-- 002  security, universe_member, universe_snapshot
--
-- The reference stores. Prices and money are TEXT holding a decimal, never REAL, so
-- market_cap is TEXT like every other money column.
--
-- universe_snapshot is what makes replay free of survivorship bias. It records who was
-- listed on a given night and it cannot be reconstructed later, which is why it is written
-- every night without exception rather than only when something changes.

CREATE TABLE security (
    ticker             TEXT NOT NULL PRIMARY KEY,
    name               TEXT NOT NULL,
    exchange           TEXT NOT NULL,
    type               TEXT NOT NULL,
    first_seen         TEXT NOT NULL,
    sector             TEXT NULL,
    industry           TEXT NULL,
    market_cap         TEXT NULL,
    sector_resolved_at TEXT NULL
);

-- Membership is state, not a filter: a name that leaves keeps its row and gains a
-- removed_on, so a setup recorded while it was a member still resolves to a security.
CREATE TABLE universe_member (
    ticker     TEXT NOT NULL PRIMARY KEY REFERENCES security (ticker),
    added_on   TEXT NOT NULL,
    removed_on TEXT NULL
);

CREATE INDEX ix_universe_member_removed_on ON universe_member (removed_on);

CREATE TABLE universe_snapshot (
    as_of  TEXT NOT NULL,
    ticker TEXT NOT NULL REFERENCES security (ticker),
    PRIMARY KEY (as_of, ticker)
);
