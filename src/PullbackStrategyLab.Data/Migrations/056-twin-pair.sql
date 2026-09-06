-- 056  the twin pairs, and the window figure that has to survive a run finding none
--
-- <b>Two tables, and the second is the one this checkpoint could not do without.</b> `twin_pair`
-- holds the pairs. `twin_run` holds what one run looked at, and it exists because the figure 6.3
-- owes is a property of the run rather than of any pair: the metric standardises every signal over
-- the trailing 250 setups, the store holds a small fraction of that and will for months, and the
-- row has to report how many setups the window actually held rather than treating a short window
-- as a full one. With pairs alone, a run that qualified nothing would write nothing, and the
-- window figure would be lost on exactly the runs where it matters most.
-- see: The twin-pair threshold is reviewed at the first full window rather than at a phase
--
-- <b>Both are per direction, and the outcome is why.</b> `forward_return.return_signed` is signed
-- by direction, so a long that rose 12 points and a short that fell 12 points both read +12. A pair
-- drawn across the two sides could differ by 20 points while both names did the same thing, and the
-- distance between them would be a distance between two books. The finder is run once a side and
-- the two are never added.
-- see: Long and short are never pooled into one figure
--
-- <b>The distance and the gap travel with the pair because neither can be recomputed later.</b> The
-- z-scores are taken over the window the run held, and that window grows every night. A pair found
-- today at 0.42 over 38 setups is not the same measurement as the same two setups at 0.42 over 250,
-- and a table storing only the two ids would silently invite the second reading of the first
-- answer. `window_setups` and `signals_compared` are on the pair row for the same reason: they are
-- what the figures beside them were computed over.
--
-- <b>No update path and no delete path: a rerun is a new generation beside the old.</b> The pairs
-- of a date are a function of the window that date could see, and that window grows as outcomes
-- fill, so a second run of one date can honestly produce a different answer. The obvious shape is
-- to clear the date and rewrite it, and this corpus does not do that: `observed_at` is in the key,
-- a rerun writes its own generation, and a reader takes the latest generation at or before its
-- bound. That is the shape `scoreboard` took at 049 and it is taken here for the same reason, which
-- is that the stale generation is what a person saw and deleting it removes the only record of
-- that (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood).

CREATE TABLE twin_pair (
    pair_id         TEXT    NOT NULL,
    as_of           TEXT    NOT NULL,
    direction       TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    -- The two members, in settled order: the lower id is always the left, so a pair has one
    -- identity whichever order the walk reached it in.
    left_setup_id   TEXT    NOT NULL,
    right_setup_id  TEXT    NOT NULL,

    -- In standard deviations across `signals_compared` axes, standardised over the window this run
    -- held. Below the threshold or the row does not exist.
    distance        TEXT    NOT NULL,

    -- In percentage points of the ten-day return. Above the threshold or the row does not exist.
    gap_points      TEXT    NOT NULL,

    -- Each member's own outcome, so the pair reads without a join and so the gap can be checked
    -- against the two figures it came from.
    left_outcome    TEXT    NOT NULL,
    right_outcome   TEXT    NOT NULL,

    -- What the two figures above were computed over. Neither is a fact about the pair alone.
    signals_compared INTEGER NOT NULL CHECK (signals_compared > 0),
    window_setups    INTEGER NOT NULL CHECK (window_setups >= 2),

    observed_at     TEXT    NOT NULL,

    PRIMARY KEY (pair_id, as_of, observed_at),

    -- A setup is never its own twin, and the order is the identity rather than a convention the
    -- stage remembers.
    CHECK (left_setup_id < right_setup_id),

    FOREIGN KEY (left_setup_id) REFERENCES setup (setup_id),
    FOREIGN KEY (right_setup_id) REFERENCES setup (setup_id)
);

CREATE INDEX ix_twin_pair_session ON twin_pair (as_of, direction, observed_at);

CREATE TABLE twin_run (
    as_of            TEXT    NOT NULL,
    direction        TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    -- The window this run could form, against the window the metric is defined over. The two are
    -- stored side by side rather than one being derived from a constant, because the constant can
    -- move and this row is a measurement of a date.
    window_setups    INTEGER NOT NULL CHECK (window_setups >= 0),
    window_wanted    INTEGER NOT NULL CHECK (window_wanted > 0),

    -- How many signals every setup in the window carried as a number, which is the width of the
    -- space the distance was taken in. Nought where the window was too thin to form a space.
    signals_compared INTEGER NOT NULL CHECK (signals_compared >= 0),

    -- What the run looked at against what it found. Nought pairs over four setups and nought pairs
    -- over two hundred are different statements and only the second is evidence about the
    -- thresholds.
    candidate_pairs  INTEGER NOT NULL CHECK (candidate_pairs >= 0),
    pairs_found      INTEGER NOT NULL CHECK (pairs_found >= 0),

    -- Why a run held nothing, on exactly the runs that held nothing. A window under two forms no
    -- pair at all, and a window with no numeric signal common to every row forms no space; those
    -- are different absences and neither is "no twins were found".
    empty_because    TEXT    NULL,

    outcome          TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    observed_at      TEXT    NOT NULL,

    PRIMARY KEY (as_of, direction, observed_at),

    -- A run that found nothing says which shape of nothing it was, and a run that found something
    -- does not carry a reason it found nothing.
    CHECK ((pairs_found = 0 AND empty_because IS NOT NULL) OR (pairs_found > 0 AND empty_because IS NULL))
);

CREATE INDEX ix_twin_run_session ON twin_run (as_of);
