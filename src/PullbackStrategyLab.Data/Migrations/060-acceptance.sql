-- 060  what the gate read of a version, and the two counts a score row never held
--
-- <b>Two changes, and the first is a correction to what 052 claimed about its own figures.</b>
-- `variant_score`'s comment says the mean return of what the version selected is over
-- `variant_selected` rows. It is not. The scorer takes each mean over the selections whose forward
-- return has landed, and a selection inside its horizon is in `variant_selected` and in no mean, so
-- on any night some outcome is still open the stated denominator is larger than the one the figure
-- was computed over. That is a figure named over one population and computed over another, which is
-- the shape the corpus keeps finding, and it was invisible because nothing had ever read the two
-- numbers together. `baseline_scored` and `variant_scored` are the rows each mean was actually taken
-- over, and they are recorded rather than the comment being reworded.
--
-- <b>And the win rate had nowhere to come from.</b> Acceptance measures expectancy and never win
-- rate, with the win rate reported beside it as a diagnostic, and a version that raised the win rate
-- while lowering expectancy is rejected automatically. Nothing in the store held a win count, so the
-- gate would have had to recompute one from `forward_return`, which is a second implementation of
-- what the version selected sitting beside the scorer's own. The counts belong on the row the
-- population is on (see: Acceptance measures expectancy, never win rate).
--
-- <b>The four are null exactly where the means are null.</b> A night with no figure has no
-- denominator and no win count either, and a row carrying two of the six would read as a scored
-- night and could not be settled as one.
--
-- <b>`acceptance_reading` is a reading and not a result.</b> A version's settlement is one row of
-- `variant`, and that row may hold nothing but a status and a date: the target and the minimum
-- sample are written at creation and never again, so everything the gate weighed has to live
-- somewhere it can be read back beside them
-- (see: Targets and minimum samples are written at creation and are immutable).
-- It is written on every run rather than on the run that settles,
-- because a version that never accumulates its sample is the case the failure table names and the
-- sequence of readings is what says so. The key carries the instant for that reason.
--
-- <b>The baseline has no reading and that is a refusal rather than an omission.</b> It is the arm
-- every other version is differenced against, so there is no baseline-minus-baseline series to take
-- an interval over. V0's own pre-registration says it in terms: it is not itself accepted or
-- rejected, and it closes only if the baseline is edited, which starts a new generation
-- (see: An approved proposal creates a new version from zero, and a running version is never edited).
-- `acceptance_run` counts the baselines it passed over, so a run reading nothing is told from a run
-- that found nothing registered.

-- ---------------------------------------------------------------------------------------------
-- `variant_score`, rebuilt for the two denominators and the two win counts
-- ---------------------------------------------------------------------------------------------

CREATE TABLE variant_score_new (
    variant_id            TEXT    NOT NULL,
    session_date          TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    generation            INTEGER NOT NULL,
    family                TEXT    NOT NULL CHECK (family = 'selection'),
    horizon_days          INTEGER NOT NULL CHECK (horizon_days > 0),

    flagged               INTEGER NOT NULL CHECK (flagged >= 0),
    baseline_selected     INTEGER NOT NULL CHECK (baseline_selected >= 0),
    variant_selected      INTEGER NOT NULL CHECK (variant_selected >= 0),
    both_selected         INTEGER NOT NULL CHECK (both_selected >= 0),
    variant_only          INTEGER NOT NULL CHECK (variant_only >= 0),
    baseline_only         INTEGER NOT NULL CHECK (baseline_only >= 0),

    baseline_mean_return  TEXT    NULL,
    variant_mean_return   TEXT    NULL,
    mean_difference       TEXT    NULL,

    -- The rows each mean was taken over, which is what each side selected less whatever is still
    -- inside its horizon. Never above the selection it is drawn from, and the store says so rather
    -- than the stage.
    baseline_scored       INTEGER NULL CHECK (baseline_scored IS NULL OR baseline_scored >= 0),
    variant_scored        INTEGER NULL CHECK (variant_scored IS NULL OR variant_scored >= 0),

    -- How many of those rows ended ahead, direction-signed. The diagnostic and never the thing a
    -- version is settled on: win rate is trivially improvable in the wrong direction, and a version
    -- that bought it with expectancy is rejected under that name.
    baseline_wins         INTEGER NULL CHECK (baseline_wins IS NULL OR baseline_wins >= 0),
    variant_wins          INTEGER NULL CHECK (variant_wins IS NULL OR variant_wins >= 0),

    baseline_outside_cap  INTEGER NOT NULL CHECK (baseline_outside_cap >= 0),
    variant_outside_cap   INTEGER NOT NULL CHECK (variant_outside_cap >= 0),

    unscoreable           INTEGER NOT NULL CHECK (unscoreable >= 0),
    withheld_because      TEXT    NULL,

    computed_at           TEXT    NOT NULL,

    PRIMARY KEY (variant_id, session_date, direction),

    CHECK ((baseline_mean_return IS NULL) = (variant_mean_return IS NULL)),
    CHECK ((baseline_mean_return IS NULL) = (mean_difference IS NULL)),
    CHECK ((mean_difference IS NULL) = (withheld_because IS NOT NULL)),

    -- The four new columns are present exactly on a row carrying a figure, written out one per
    -- column so a row that fails says which of the four is the odd one.
    CHECK ((mean_difference IS NULL) = (baseline_scored IS NULL)),
    CHECK ((mean_difference IS NULL) = (variant_scored IS NULL)),
    CHECK ((mean_difference IS NULL) = (baseline_wins IS NULL)),
    CHECK ((mean_difference IS NULL) = (variant_wins IS NULL)),

    -- A mean is taken over rows that were selected and have landed, so the denominator cannot
    -- exceed the selection; and a win is one of those rows.
    CHECK (baseline_scored IS NULL OR baseline_scored <= baseline_selected),
    CHECK (variant_scored IS NULL OR variant_scored <= variant_selected),
    CHECK (baseline_wins IS NULL OR baseline_wins <= baseline_scored),
    CHECK (variant_wins IS NULL OR variant_wins <= variant_scored),

    -- A row carrying a figure was taken over at least one row on each side, which is the condition
    -- the scorer withholds on and is held here so the two cannot disagree.
    CHECK (baseline_scored IS NULL OR (baseline_scored > 0 AND variant_scored > 0)),

    CHECK (both_selected <= baseline_selected AND both_selected <= variant_selected),
    CHECK (baseline_only = baseline_selected - both_selected),
    CHECK (variant_only = variant_selected - both_selected),

    FOREIGN KEY (variant_id) REFERENCES variant (variant_id)
);

-- <b>Carried verbatim, and a row carrying a figure makes this migration refuse.</b> The four new
-- columns are null on every copied row, because a row written before this file recorded no
-- denominator and no win count, and deriving one from the selection count would assert exactly the
-- equality this migration exists to say is false. That leaves a copied row with a figure and no
-- denominator, which the biconditional above refuses, and refusing is the right answer: blanking the
-- figure instead would destroy a night of research evidence to make a schema change go through
-- quietly. No store holds such a row today, measured against the live store and the fixture on
-- 2026-09-07, so this refuses nothing; if one ever does, a person decides what the figure was over.
INSERT INTO variant_score_new (
    variant_id, session_date, direction, generation, family, horizon_days,
    flagged, baseline_selected, variant_selected, both_selected, variant_only, baseline_only,
    baseline_mean_return, variant_mean_return, mean_difference,
    baseline_scored, variant_scored, baseline_wins, variant_wins,
    baseline_outside_cap, variant_outside_cap, unscoreable, withheld_because, computed_at)
SELECT variant_id, session_date, direction, generation, family, horizon_days,
       flagged, baseline_selected, variant_selected, both_selected, variant_only, baseline_only,
       baseline_mean_return, variant_mean_return, mean_difference,
       NULL, NULL, NULL, NULL,
       baseline_outside_cap, variant_outside_cap, unscoreable, withheld_because, computed_at
  FROM variant_score;

DROP TABLE variant_score;

ALTER TABLE variant_score_new RENAME TO variant_score;

CREATE INDEX ix_variant_score_session ON variant_score (session_date);

-- ---------------------------------------------------------------------------------------------
-- `acceptance_reading`
-- ---------------------------------------------------------------------------------------------

CREATE TABLE acceptance_reading (
    variant_id            TEXT    NOT NULL,

    -- The instant the reading was taken. In the key because a reading is a reading: the series
    -- underneath grows every night, so the same version read twice is two rows and the older one
    -- stays readable as it stood
    -- (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood).
    observed_at           TEXT    NOT NULL,

    session_date          TEXT    NOT NULL,

    -- The side the version belongs to. On the row rather than in a note, because every figure below
    -- is one side's and a reading over the pair would be the pooling this lab does not do
    -- (see: Long and short are never pooled into one figure).
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    generation            INTEGER NOT NULL,

    -- How old the version was, in days, at the moment of this reading. The failure table's own
    -- clause: a version whose sample never accumulates stays open and the ledger shows its age.
    age_days              INTEGER NOT NULL CHECK (age_days >= 0),

    -- Four counts and never one. Nights the scorer wrote; nights carrying a figure; nights the two
    -- rules selected identically on, which carry a difference of exactly nought by construction;
    -- and the nights the interval was actually taken over. One total would let the third hide
    -- inside the first and the version would mature on nights it was never exercised on.
    nights_scored         INTEGER NOT NULL CHECK (nights_scored >= 0),
    nights_with_a_figure  INTEGER NOT NULL CHECK (nights_with_a_figure >= 0),
    nights_identical      INTEGER NOT NULL CHECK (nights_identical >= 0),
    nights_in_series      INTEGER NOT NULL CHECK (nights_in_series >= 0),

    -- The setups the two rules disagreed about across the series, which is the population the
    -- difference rests on.
    disagreements         INTEGER NOT NULL CHECK (disagreements >= 0),

    -- What the series is worth, counted the way `PairedInterval.Disperse` counts it, against the
    -- figure written at creation and never since. Both here, because a reading holding one of them
    -- would be a figure with no denominator.
    effective_observations INTEGER NOT NULL CHECK (effective_observations >= 0),
    minimum_sample        INTEGER NOT NULL CHECK (minimum_sample > 0),
    minimum_sample_unit   TEXT    NOT NULL
        CHECK (minimum_sample_unit IN ('effective_paired_setup_observations', 'paired_trades')),
    matured               INTEGER NOT NULL CHECK (matured IN (0, 1)),

    -- The estimate, present together or absent together, and text on the same terms every other
    -- ratio in this store is text.
    mean_difference       TEXT    NULL,
    interval_low          TEXT    NULL,
    interval_high         TEXT    NULL,

    -- The diagnostic. Two rates because the two rules selected different names, so one denominator
    -- would put one rule's wins over the other's population.
    baseline_win_rate     TEXT    NULL,
    variant_win_rate      TEXT    NULL,

    verdict               TEXT    NOT NULL CHECK (verdict IN ('open', 'accepted', 'rejected')),

    -- Why it settled, on the readings that settled it, and why it did not, on the readings that did
    -- not. Exactly one of the two, held in both directions: a settlement with no reason and a wait
    -- with no shortfall are both rows nobody could act on.
    settled_because       TEXT    NULL,
    withheld_because      TEXT    NULL,

    -- The rows every figure above was computed over, in words, on the row that carries them. A
    -- figure whose population lives anywhere but beside it is a figure a later reader will pair with
    -- the wrong one.
    population            TEXT    NOT NULL,

    PRIMARY KEY (variant_id, observed_at),

    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),

    CHECK ((mean_difference IS NULL) = (interval_low IS NULL)),
    CHECK ((mean_difference IS NULL) = (interval_high IS NULL)),
    CHECK ((baseline_win_rate IS NULL) = (variant_win_rate IS NULL)),

    CHECK ((verdict = 'open') = (withheld_because IS NOT NULL)),
    CHECK ((verdict = 'open') = (settled_because IS NULL)),

    -- A settled reading is one that reached its minimum and produced an interval. Nothing settles a
    -- version on a calendar, and the store refuses the row rather than the stage remembering not to
    -- write it.
    CHECK (verdict = 'open' OR (matured = 1 AND mean_difference IS NOT NULL)),

    CHECK (nights_in_series <= nights_scored),
    CHECK (nights_with_a_figure <= nights_scored),
    CHECK (nights_identical <= nights_scored)
);

CREATE INDEX ix_acceptance_reading_session ON acceptance_reading (session_date, observed_at);

-- ---------------------------------------------------------------------------------------------
-- `acceptance_run`
-- ---------------------------------------------------------------------------------------------

CREATE TABLE acceptance_run (
    session_date       TEXT    NOT NULL,
    observed_at        TEXT    NOT NULL,

    -- Live versions, and the ones this run took a reading of. The two differ by the baselines, which
    -- are counted rather than subtracted: a run reporting one number could not be told from a run
    -- that found nothing registered at all.
    versions_live      INTEGER NOT NULL CHECK (versions_live >= 0),
    versions_read      INTEGER NOT NULL CHECK (versions_read >= 0),
    baselines_passed   INTEGER NOT NULL CHECK (baselines_passed >= 0),

    versions_matured   INTEGER NOT NULL CHECK (versions_matured >= 0),
    accepted           INTEGER NOT NULL CHECK (accepted >= 0),
    rejected           INTEGER NOT NULL CHECK (rejected >= 0),
    left_open          INTEGER NOT NULL CHECK (left_open >= 0),

    outcome            TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because    TEXT    NULL,

    PRIMARY KEY (session_date, observed_at),

    CHECK (versions_read = accepted + rejected + left_open)
);
