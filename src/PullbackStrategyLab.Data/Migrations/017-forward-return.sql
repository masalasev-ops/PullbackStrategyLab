-- 017  forward_return
--
-- What every flagged setup actually did, whether or not it was ever traded.
--
-- This is the table the whole project is built to fill. Recording only trades makes "was the
-- pattern worth spotting" and "was the execution any good" indistinguishable, and they have
-- different fixes: if the pattern works and the trades lose, the fault is entry timing; if the
-- trades win and flagged names behave like anything else, the selection is worthless and the
-- result was luck.
-- see: Forward returns are recorded for every flagged setup, traded or not
--
-- One row per subject per horizon. The subject is a setup or a control, because the comparison
-- needs both measured identically and a second table would be a second implementation of the same
-- arithmetic. `subject_kind` is part of the key rather than a description: a surrogate that
-- happened to collide would silently overwrite one with the other.
--
-- **Two dates, and the pair is the point.** `intended_date` is where a naive calendar step lands,
-- being the as-of plus the horizon in days. `actual_date` is the session actually used, which is
-- the Nth trading session after the as-of. They agree over a quiet week and differ across every
-- weekend and holiday. Storing only the second would make a follow-up silently later than it
-- claims; storing only the first would measure to a day with no bar. The failure table names this
-- case, and this is where it is answered.
--
-- **The horizon is trading sessions, not calendar days.** A ten-day return that quietly became
-- fourteen over Thanksgiving is not comparable with one that did not, and the ceiling arithmetic
-- is defined at the scoring horizon. The column keeps the name `horizon_days` because that is what
-- SCHEMA has always called it, and the note here says what it counts.
--
-- **`filled_at` is what makes this stage point-in-time honest**, and it is the one stage in the
-- system that reads bars dated after its subject's own date by design. The resolution is that the
-- fill's as-of is the date the lab is filling on rather than the date it flagged: a setup written
-- on Monday has no ten-session outcome until the Monday fortnight after, and the row appears when
-- the outcome exists rather than being backdated to the night that flagged it. A reader bounded on
-- `filled_at` sees exactly what the lab could have known when it asked.
--
-- Prices and returns are TEXT holding a decimal, never REAL. The excursions are in ATR, which is a
-- ratio, and the give-up distance the ceiling compares them against is in daily ranges: the
-- conversion happens at the point of use and is named there rather than either figure being stored
-- twice, because two columns that must agree are two columns that will not.

CREATE TABLE forward_return (
    subject_id     TEXT    NOT NULL,
    subject_kind   TEXT    NOT NULL CHECK (subject_kind IN ('setup', 'control')),
    horizon_days   INTEGER NOT NULL CHECK (horizon_days IN (1, 3, 5, 10)),
    intended_date  TEXT    NOT NULL,
    actual_date    TEXT    NOT NULL,
    return_signed  TEXT    NOT NULL,
    mfe_atr        TEXT    NOT NULL,
    mae_atr        TEXT    NOT NULL,
    filled_at      TEXT    NOT NULL,
    PRIMARY KEY (subject_id, subject_kind, horizon_days)
);

-- The read the scoreboard makes: every subject at one horizon, as of a fill date. Without it,
-- band 1 scans the whole table once per panel.
CREATE INDEX ix_forward_return_horizon ON forward_return (horizon_days, filled_at);
