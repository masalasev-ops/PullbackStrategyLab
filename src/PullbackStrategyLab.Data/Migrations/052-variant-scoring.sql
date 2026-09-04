-- 052  the moved threshold a version is, and the nightly difference against the baseline
--
-- <b>Two things, and the second cannot be computed without the first.</b> `variant` gains the
-- threshold a selection version moves, in a form a machine can read; `variant_score` is one night's
-- difference between what that version selected and what the baseline selected.
--
-- <b>The register held a version nothing could score, and that is what this fixes.</b> At 051 a
-- version's `definition` is what it changes in words, which is what a person reads on the ledger and
-- is nothing a stage can act on. A selection version differs from the baseline by exactly one gate's
-- threshold, so the whole of the difference fits in five columns: the side it applies to, the gate,
-- the threshold's name, and the two values. Anything the register cannot state that way is not a
-- version this generation admits, so the columns are the admission rule as much as they are the
-- score's input.
--
-- <b>They are present exactly on a selection version, and the store says so rather than the
-- admitter.</b> The baseline moves nothing and an execution version moves no selection threshold, so
-- on both the five are null together. A row carrying three of the five is a row that reads as a
-- version and cannot be scored as one.
-- see: A version changes one threshold over the existing gate list, and structural change is out of scope for this generation
-- see: Two experiment families, selection and execution, scored differently and never mixed in one version
-- see: No execution variant is admitted in this generation, and the condition that would reopen it is named

-- The same setting 051 needed and for the same reason: a rename must be only a rename, because
-- `variant` is named in `trade_plan`'s foreign key and, from this file, in `variant_score`'s.
PRAGMA legacy_alter_table = ON;

ALTER TABLE variant RENAME TO variant_before_052;

CREATE TABLE variant (
    variant_id           TEXT    NOT NULL PRIMARY KEY,
    generation           INTEGER NOT NULL CHECK (generation >= 0),
    family               TEXT    NOT NULL CHECK (family IN ('baseline', 'selection', 'execution')),
    definition           TEXT    NOT NULL,
    target               TEXT    NOT NULL,
    minimum_sample       INTEGER NOT NULL CHECK (minimum_sample > 0),
    minimum_sample_unit  TEXT    NOT NULL
        CHECK (minimum_sample_unit IN ('effective_paired_setup_observations', 'paired_trades')),
    status               TEXT    NOT NULL
        CHECK (status IN ('open', 'accepted', 'rejected', 'unresolved')),
    resolved_at          TEXT    NULL,
    created_at           TEXT    NOT NULL,

    -- The moved threshold, which is the whole of what a selection version is. Written by
    -- VariantAdmitter at creation, with the same permission the target and the minimum sample have,
    -- which is once.
    direction            TEXT    NULL CHECK (direction IS NULL OR direction IN ('long', 'short')),
    gate                 TEXT    NULL,
    threshold_name       TEXT    NULL,

    -- Stored as text on the same terms as every other number this lab compares a price or a ratio
    -- against: these are the values a gate compares, and a REAL column would round one of them
    -- differently from the constant the baseline reads.
    threshold_from       TEXT    NULL,
    threshold_to         TEXT    NULL,

    CHECK ((status = 'open') = (resolved_at IS NULL)),
    CHECK ((family = 'execution') = (minimum_sample_unit = 'paired_trades')),

    -- Present together and exactly on a selection version. The clauses are written out one per
    -- column rather than folded into one, so a row that fails says which column is the odd one.
    CHECK ((family = 'selection') = (direction IS NOT NULL)),
    CHECK ((direction IS NULL) = (gate IS NULL)),
    CHECK ((direction IS NULL) = (threshold_name IS NULL)),
    CHECK ((direction IS NULL) = (threshold_from IS NULL)),
    CHECK ((direction IS NULL) = (threshold_to IS NULL)),

    -- A version that moved a threshold to the value it already had is the baseline under another
    -- name, and it would accumulate a difference series of nought for ever.
    CHECK (threshold_from IS NULL OR threshold_from <> threshold_to)
);

INSERT INTO variant (
    variant_id, generation, family, definition, target,
    minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
    direction, gate, threshold_name, threshold_from, threshold_to)
SELECT variant_id, generation, family, definition, target,
       minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
       NULL, NULL, NULL, NULL, NULL
  FROM variant_before_052;

DROP TABLE variant_before_052;

CREATE UNIQUE INDEX ux_variant_baseline ON variant (generation) WHERE family = 'baseline';
CREATE INDEX ix_variant_status ON variant (status);

-- One night's difference between what a version selected and what the baseline selected.
--
-- <b>Direction is in the key rather than in a note.</b> A pooled score would add a long side's
-- forward return to a short side's, and the two are different questions over different populations;
-- the pooling rule is the one this table would break most quietly, because a version that helps one
-- side and hurts the other reads as no difference at all.
-- see: Long and short are never pooled into one figure
--
-- <b>Every figure states the population it was computed over, as a count in the same row.</b> The
-- mean return of what the version selected is over `variant_selected` rows and the baseline's is
-- over `baseline_selected`, and those are different populations on any night the version moved
-- anything. The difference is the difference of two means and it is not a mean of differences:
-- there is no per-name pairing to take, because the two sets are not the same set. What is paired
-- is the night.
--
-- <b>The refusal is a column here rather than a vendor call at the capture.</b> A version selecting
-- a name outside the night's capped sixty meets a name with minutes and no spread, so its order is
-- refused a fill and it trades nothing. That is a recorded fact about the version, and it is what
-- makes a version scoring poorly because it selected outside the cap distinguishable from one
-- scoring poorly on its merits.
-- see: The spread capture stays at the capped sixty, and a version selecting outside it is scored as refused
CREATE TABLE variant_score (
    variant_id            TEXT    NOT NULL,
    session_date          TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    -- Carried rather than joined, because a score is read back as it stood and a generation that
    -- turned over afterwards would otherwise restate what the row meant.
    generation            INTEGER NOT NULL,
    family                TEXT    NOT NULL CHECK (family = 'selection'),
    horizon_days          INTEGER NOT NULL CHECK (horizon_days > 0),

    -- The populations, counted apart and never summed.
    flagged               INTEGER NOT NULL CHECK (flagged >= 0),
    baseline_selected     INTEGER NOT NULL CHECK (baseline_selected >= 0),
    variant_selected      INTEGER NOT NULL CHECK (variant_selected >= 0),
    both_selected         INTEGER NOT NULL CHECK (both_selected >= 0),
    variant_only          INTEGER NOT NULL CHECK (variant_only >= 0),
    baseline_only         INTEGER NOT NULL CHECK (baseline_only >= 0),

    -- The figures, present together or absent together.
    baseline_mean_return  TEXT    NULL,
    variant_mean_return   TEXT    NULL,
    mean_difference       TEXT    NULL,

    -- How many of each side's selections could not be filled, because the night's cap did not reach
    -- them. Both sides carry the count: the baseline's own selections past the sixtieth rank are
    -- refused on exactly the same terms, and a column that existed only on the version would read
    -- as a penalty the version alone pays.
    baseline_outside_cap  INTEGER NOT NULL CHECK (baseline_outside_cap >= 0),
    variant_outside_cap   INTEGER NOT NULL CHECK (variant_outside_cap >= 0),

    -- Setups of this night the version's rule could not be judged over, because the frozen signals
    -- do not rebuild the gate's evidence. Counted rather than dropped: a night scored over nine of
    -- eleven names is a different fact from a night scored over eleven.
    unscoreable           INTEGER NOT NULL CHECK (unscoreable >= 0),

    -- Why the row carries no figure, on exactly the rows that carry none. A scored night that could
    -- not produce a difference is a row rather than an absence.
    withheld_because      TEXT    NULL,

    computed_at           TEXT    NOT NULL,

    PRIMARY KEY (variant_id, session_date, direction),

    CHECK ((baseline_mean_return IS NULL) = (variant_mean_return IS NULL)),
    CHECK ((baseline_mean_return IS NULL) = (mean_difference IS NULL)),
    CHECK ((mean_difference IS NULL) = (withheld_because IS NOT NULL)),
    CHECK (both_selected <= baseline_selected AND both_selected <= variant_selected),
    CHECK (baseline_only = baseline_selected - both_selected),
    CHECK (variant_only = variant_selected - both_selected),

    FOREIGN KEY (variant_id) REFERENCES variant (variant_id)
);

CREATE INDEX ix_variant_score_session ON variant_score (session_date);

-- What one run of the scorer did.
--
-- <b>`versions_scored` and `versions_live` are two numbers because they answer different
-- questions.</b> A night with one live version scores none of them, the baseline being the thing
-- everything else is differenced against rather than a version carrying a difference of its own, and
-- a run reporting a single nought could not be told from a run that found nothing live at all.
CREATE TABLE score_run (
    session_date       TEXT    NOT NULL,
    observed_at        TEXT    NOT NULL,

    versions_live      INTEGER NOT NULL CHECK (versions_live >= 0),
    versions_scored    INTEGER NOT NULL CHECK (versions_scored >= 0),

    -- Nights whose forward returns had landed and which this run wrote a score for, and nights a
    -- live version was running on whose returns have not. The second is the ordinary state of every
    -- recent night and is not a fault.
    nights_scored      INTEGER NOT NULL CHECK (nights_scored >= 0),
    nights_waiting     INTEGER NOT NULL CHECK (nights_waiting >= 0),

    longs              INTEGER NOT NULL CHECK (longs >= 0),
    shorts             INTEGER NOT NULL CHECK (shorts >= 0),
    unscoreable        INTEGER NOT NULL CHECK (unscoreable >= 0),

    outcome            TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because    TEXT    NULL,

    PRIMARY KEY (session_date, observed_at)
);

PRAGMA legacy_alter_table = OFF;
