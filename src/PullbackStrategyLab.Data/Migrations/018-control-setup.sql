-- 018  control_setup
--
-- The comparison population. Without it every figure phase 3 produces is a number with nothing
-- beside it.
--
-- Flagged setups returning 2% over ten days is not a result: the whole market may have returned 2%
-- that fortnight. Two sets, both free because they come from daily bars already stored. The loose
-- set matches on liquidity and daily-range decile and measures the whole funnel, thrust scan
-- included. The tight set also matches on the trend ladder, and answers the sharper question: is
-- this pattern worth anything beyond simply owning stocks in uptrends.
-- see: Matched control populations are drawn nightly, loose and tight
--
-- **`control_id` is a surrogate and it exists for one reason.** `forward_return` records outcomes
-- for setups and controls in one table, because a second table would be a second implementation of
-- the same arithmetic, and a control had no single column to be named by. The alternative was a
-- three-column subject on every outcome row.
--
-- **`match_quality` is per dimension, never blended.** A single distance cannot say which dimension
-- the match was bad on, and that is exactly what a later reader needs when a comparison looks
-- surprising. Stored as JSON so a dimension added later does not need a migration and an old row
-- does not acquire a column it never measured.
--
-- **`rank` runs 1 to 5 by distance with ticker as the tiebreak**, so the fifth control is by
-- construction the worst of the five. The draw is deterministic nearest neighbour rather than
-- random: a seed would be a second thing to keep point in time, a value the phase report cannot
-- diff, and a number nobody could reproduce from the store alone.
--
-- Drawn at 18:26, before the cap at 18:28, so the controls answer for the flagged population rather
-- than for the sixty that survived truncation.

CREATE TABLE control_setup (
    control_id     TEXT    NOT NULL PRIMARY KEY,
    setup_id       TEXT    NOT NULL,
    control_ticker TEXT    NOT NULL,
    control_set    TEXT    NOT NULL CHECK (control_set IN ('loose', 'tight')),
    match_quality  TEXT    NOT NULL,
    rank           INTEGER NOT NULL,
    drawn_at       TEXT    NOT NULL,
    FOREIGN KEY (setup_id) REFERENCES setup (setup_id)
);

-- One name once per set per setup, asserted by the store rather than by the sampler. A duplicated
-- control would be counted twice by every comparison downstream and would look like a thicker
-- match rather than an error.
CREATE UNIQUE INDEX ux_control_setup_draw ON control_setup (setup_id, control_set, control_ticker);

-- The read the scoreboard makes: a setup's controls, by set. Without it, pairing every setup with
-- its own controls is a scan per setup.
CREATE INDEX ix_control_setup_by_setup ON control_setup (setup_id, control_set, rank);
