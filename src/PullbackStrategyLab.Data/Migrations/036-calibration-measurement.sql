-- 036  calibration_control_setup and calibration_forward_return
--
-- The measurement half of a reconstructed read, in tables nothing downstream reads.
--
-- The detectors have written to `calibration_setup` since 2.11 and the evidence rule is why: a
-- night the lab was not running has no universe snapshot, so a reconstructed setup carries
-- survivorship bias and is not evidence. What that rule was read to mean, and what the decision
-- above it actually said, is that such a run is "useful for one thing, counting how many setups a
-- night the thresholds produce". A reconstructed read answers more than a count, and the operator
-- authorised it on 2026-08-30: the same detectors over history, with matched controls and forward
-- outcomes, producing a paired comparison per direction.
-- see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
--
-- <b>Two tables rather than a column on the evidence ones, and that is the whole safety property.</b>
-- A `source` column on `forward_return` would put reconstructed rows one predicate away from every
-- read the scoreboard makes, and a read that forgot the predicate would mix them silently: the rows
-- are real returns of real stocks, so nothing about their shape says which population they came
-- from. Separate tables make the mixing impossible to write rather than merely wrong, on the same
-- grounds `calibration_setup` is a table rather than a flag.
--
-- <b>The foreign key points at `calibration_setup`</b>, so a reconstructed control cannot be hung
-- off an evidence setup and an evidence control cannot be hung off a reconstructed one. The store
-- refuses the mixing rather than the sampler remembering not to.
--
-- <b>`mfe_atr` and `mae_atr` are nullable here and are NOT NULL on the evidence side.</b> That is
-- the one shape difference and it is deliberate. The excursions are expressed in the subject's own
-- ATR, `indicator_daily` holds no row for a reconstructed session, and the calibration run computes
-- its averages in memory and discards them, so there is no ATR to express them in. The alternative
-- was approximating one from daily bars, which is the stand-in the anchored clause of
-- `reached-ceiling` already refuses by name. A column that cannot be produced is null with the
-- reason recorded beside it rather than nought: `ForwardReturnFiller` already coalesced an
-- undefined excursion to nought on the evidence side and that is a carried obligation raised at
-- 3.5, so repeating it here would be shipping a known defect into a new table.

CREATE TABLE calibration_control_setup (
    control_id     TEXT    NOT NULL PRIMARY KEY,
    setup_id       TEXT    NOT NULL,
    control_ticker TEXT    NOT NULL,
    control_set    TEXT    NOT NULL CHECK (control_set IN ('loose', 'tight')),
    match_quality  TEXT    NOT NULL,
    rank           INTEGER NOT NULL,
    drawn_at       TEXT    NOT NULL,
    control_as_of  TEXT    NULL,
    FOREIGN KEY (setup_id) REFERENCES calibration_setup (setup_id)
);

-- One name once per set per subject, on the same terms as the evidence side. A duplicated control
-- would be counted twice by the paired difference and would read as a thicker match rather than an
-- error.
CREATE UNIQUE INDEX ux_calibration_control_draw
    ON calibration_control_setup (setup_id, control_set, control_ticker);

-- The draw reaches backwards, so the fill reads controls by their own session.
CREATE INDEX ix_calibration_control_as_of ON calibration_control_setup (control_as_of);

CREATE TABLE calibration_forward_return (
    subject_id     TEXT    NOT NULL,
    subject_kind   TEXT    NOT NULL CHECK (subject_kind IN ('setup', 'control')),
    horizon_days   INTEGER NOT NULL CHECK (horizon_days IN (1, 3, 5, 10)),
    intended_date  TEXT    NOT NULL,
    actual_date    TEXT    NOT NULL,
    return_signed  TEXT    NOT NULL,
    mfe_atr        TEXT    NULL,
    mae_atr        TEXT    NULL,
    excursions_absent_because TEXT NULL,
    filled_at      TEXT    NOT NULL,
    PRIMARY KEY (subject_id, subject_kind, horizon_days)
);

-- The read the reconstructed interval makes: every subject at one horizon. Without it the paired
-- series scans the whole table once per direction per control set.
CREATE INDEX ix_calibration_forward_horizon
    ON calibration_forward_return (horizon_days, subject_kind);
