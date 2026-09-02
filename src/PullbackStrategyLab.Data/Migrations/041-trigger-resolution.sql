-- 041  trigger_resolution, trigger_run, and the index the replay walks
--
-- What the session did to each plan resting in it: the trigger was touched at this minute, or it
-- was not touched, or the question could not be asked. The replay is what a fill is decided from,
-- so this is the row every component after 4.5 starts from.
-- see: Trades are resolved by replaying minute bars after the close, not by watching live
--
-- <b>Three outcomes and not two, because no fill and cannot-resolve are different answers.</b> A
-- plan whose name traded all day and never reached its trigger did not fire, which is the ordinary
-- result and most of this table. A plan whose session holds no minutes was never asked: the fetch
-- did not run, or the name was not in it, or the live session was not a trading day at all. Folding
-- the second into the first would record a strategy that declines to trade on exactly the nights
-- the lab was blind, and every rate computed from these rows would be wrong in the flattering
-- direction with nothing to show it.
-- see: A gate handed an absent or degenerate quantity fails rather than passing
--
-- <b>`unresolved_because` is a column rather than a fourth outcome per reason.</b> The reasons are
-- operational and there are two of them today, and a value per reason would put the count of
-- reasons into a CHECK constraint that a migration has to move every time one is added. The outcome
-- is what anything downstream branches on.
--
-- <b>No price is copied here.</b> The trigger price is the plan's and the bar is `intraday_bar`'s,
-- and `touched_at` addresses that minute exactly. Restating either would be a second statement of a
-- fact the store already holds, which is the ruling WatchlistPublisher took over a watchlist table
-- and VwapEngine took over the day's high and low. PaperBroker at 4.7 prices a fill from the plan
-- and the minute named here, so the gap fill reads the same bar the touch was found in.
--
-- <b>Grained on the plan, which is grained on the setup.</b> A plan is live in exactly one session,
-- so a resolution per plan is a resolution per plan per session with the second half derivable. 5.1
-- fans plans out per variant and this key follows `trade_plan` when it does.

CREATE TABLE trigger_resolution (
    setup_id           TEXT    NOT NULL PRIMARY KEY,
    live_session       TEXT    NOT NULL,
    ticker             TEXT    NOT NULL,
    direction          TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    outcome            TEXT    NOT NULL CHECK (outcome IN ('touched', 'not_touched', 'unresolvable')),
    touched_at         TEXT    NULL,
    minutes_walked     INTEGER NOT NULL,
    unresolved_because TEXT    NULL,
    observed_at        TEXT    NOT NULL,

    -- A touch names the minute it happened in, and nothing else may. An outcome of `touched` with
    -- no minute would be a fill nothing can price; a minute on either of the other two would be a
    -- time attached to an event that did not occur.
    CHECK ((outcome = 'touched') = (touched_at IS NOT NULL)),
    CHECK ((outcome = 'unresolvable') = (unresolved_because IS NOT NULL)),

    FOREIGN KEY (setup_id) REFERENCES trade_plan (setup_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_trigger_resolution_session ON trigger_resolution (live_session);

-- One row per run of the stage, on the pattern `plan_run`, `vwap_run` and `intraday_fetch` set.
--
-- `minutes_walked` and `names_walked` are the state of the replay itself: a session with plans in
-- it and no minutes walked is a night the resolver could not see, and it is the figure that says so
-- on the morning it happens rather than in a rate three months later.

CREATE TABLE trigger_run (
    session_date    TEXT    NOT NULL,
    setup_as_of     TEXT    NULL,
    plans           INTEGER NOT NULL,
    touched         INTEGER NOT NULL,
    not_touched     INTEGER NOT NULL,
    unresolvable    INTEGER NOT NULL,
    names_walked    INTEGER NOT NULL,
    minutes_walked  INTEGER NOT NULL,
    outcome         TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because TEXT    NULL,
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_trigger_run_session ON trigger_run (session_date);

-- The replay reads a session across every name at once, where every earlier reader took one name at
-- a time. `ix_intraday_bar_session` leads on the ticker, so a session-wide read cannot seek on it
-- and scans the table instead. This is a second index on the same rows and no second statement of
-- them.
CREATE INDEX ix_intraday_bar_replay ON intraday_bar (session_date, bar_ts, ticker);
