-- 020  scoreboard
--
-- What each band showed on a given day, so a panel can be read back as it stood.
--
-- The page is the one place the project answers "is this working" without reference to money, and
-- it is the page you would show someone to explain what has been established. Storing what it said
-- rather than recomputing it on demand is what lets a later reader ask when a number moved.
--
-- **`n_rows` and `n_effective` are both stored because they are different quantities.** Ten-day
-- labels overlap and same-night setups share a market factor, so the information in 3,180 rows is
-- worth fewer than 3,180 independent observations, and the ratio is a property of the realised
-- series rather than of the design. A minimum sample stated against this figure is counted in the
-- second column. Storing only the first is how a target reading "160 observations" gets satisfied
-- by 160 rows carrying far less than 160 observations' worth of information.
-- see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
--
-- **`direction` is nullable and that is deliberate rather than lax.** Band 0 is account-wide: nights
-- recorded, degraded runs, calls against the ceiling. Those are not per direction and inventing a
-- direction for them would put the same fact in two rows. Every panel that compares outcomes carries
-- one, because long and short are never pooled into a single figure.
-- see: Long and short are never pooled into one figure
--
-- `low` and `high` are null on a panel that carries no interval, which is most of band 0. A nought
-- there would read as an interval that happens to be tight.

CREATE TABLE scoreboard (
    as_of       TEXT    NOT NULL,
    panel       TEXT    NOT NULL,
    direction   TEXT    NULL CHECK (direction IS NULL OR direction IN ('long', 'short')),
    figure      TEXT    NOT NULL,
    low         TEXT    NULL,
    high        TEXT    NULL,
    n_rows      INTEGER NOT NULL,
    n_effective INTEGER NULL,
    computed_at TEXT    NOT NULL,
    PRIMARY KEY (as_of, panel, direction)
);

-- The read the page makes: one day, every panel. Without it, rendering a scoreboard is a scan.
CREATE INDEX ix_scoreboard_as_of ON scoreboard (as_of);
