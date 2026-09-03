-- 049  scoreboard generations
--
-- <b>A rebuilt date's panels are a new generation beside the old, never a replacement.</b> The
-- table's own grain sentence is that a panel can be read back as it stood, and until now the only
-- route to restating a night whose inputs arrived after its scoreboard ran was to restore a
-- snapshot or delete the date's panels, which no declared writer does and which would make the
-- stale reading unreadable. 2026-08-28's band 0 still says one night recorded and forty-four
-- setups on file, computed before that night's seventy-three setups existed, and that is a dated
-- measurement rather than an error; what was missing was a supported way to put the corrected
-- reading beside it. So `computed_at` joins the key, a rebuild inserts every panel again under its
-- own instant, and a reader takes the latest generation at or before its bound: a replay standing
-- between the two builds sees the first, exactly as it stood.
-- see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood
--
-- <b>The ordinary build is unchanged in what it refuses.</b> Without the rebuild flag a date that
-- already carries panels still writes none and fails, on the terms 3.9(e) set, because an
-- accidental second run must not quietly open a generation. The presence test moves from the
-- insert's conflict to a read in the same transaction, since a key carrying the instant cannot
-- conflict across instants.
--
-- <b>Rebuilt rather than altered, on the terms 045 and 048 were.</b> SQLite cannot change a primary
-- key in place; nothing holds a foreign key into `scoreboard`, and every row is copied.

DROP INDEX IF EXISTS ix_scoreboard_as_of;
DROP INDEX IF EXISTS scoreboard_account_wide;

ALTER TABLE scoreboard RENAME TO scoreboard_before_049;

CREATE TABLE scoreboard (
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
    computed_at        TEXT    NOT NULL,
    PRIMARY KEY (as_of, panel, direction, computed_at)
);

INSERT INTO scoreboard (
    as_of, panel, direction, figure, low, high, n_rows, n_effective, population, n_minimum,
    withheld_because, n_sessions, n_minimum_sessions, computed_at)
SELECT
    as_of, panel, direction, figure, low, high, n_rows, n_effective, population, n_minimum,
    withheld_because, n_sessions, n_minimum_sessions, computed_at
  FROM scoreboard_before_049;

DROP TABLE scoreboard_before_049;

-- The read the page makes: one day, every panel, latest generation first.
CREATE INDEX ix_scoreboard_as_of ON scoreboard (as_of, computed_at);

-- The primary key does not constrain an account-wide panel, because SQLite treats nulls as
-- distinct and `direction` is null on every band 0 row; this is migration 030's index with the
-- instant added, so one generation holds one copy of each.
CREATE UNIQUE INDEX scoreboard_account_wide
    ON scoreboard (as_of, panel, computed_at)
 WHERE direction IS NULL;
