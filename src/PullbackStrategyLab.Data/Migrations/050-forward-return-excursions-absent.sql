-- 050  an undefined excursion is null with its reason, not nought
--
-- <b>A row that could not be measured was indistinguishable from one whose path never moved.</b>
-- `ForwardOutcome` returns no excursions where the subject has no ATR to express them in, and the
-- evidence insert coalesced both to nought, so `mae_atr` of nought read as a subject that never went
-- against its entry. Latent rather than live: the ceiling refuses a subject with no range before it
-- reads the excursion, and no evidence row in the live store carried the pair, but a stored value
-- that means two things is exactly what a freeze makes permanent. The calibration side already
-- took the other shape at 036, null with the reason beside it, and this brings the evidence side
-- to the same one.
-- see: A gate handed an absent or degenerate quantity fails rather than passing
--
-- <b>Rebuilt rather than altered, on the terms 045, 048 and 049 were.</b> SQLite cannot relax NOT
-- NULL in place or add a table CHECK; nothing holds a foreign key into `forward_return`, and every
-- row is copied with its excursions as they stood, none of which is the absent pair.

DROP INDEX IF EXISTS ix_forward_return_horizon;

ALTER TABLE forward_return RENAME TO forward_return_before_050;

CREATE TABLE forward_return (
    subject_id                TEXT    NOT NULL,
    subject_kind              TEXT    NOT NULL CHECK (subject_kind IN ('setup', 'control')),
    horizon_days              INTEGER NOT NULL CHECK (horizon_days IN (1, 3, 5, 10)),
    intended_date             TEXT    NOT NULL,
    actual_date               TEXT    NOT NULL,
    return_signed             TEXT    NOT NULL,
    mfe_atr                   TEXT    NULL,
    mae_atr                   TEXT    NULL,
    excursions_absent_because TEXT    NULL,
    filled_at                 TEXT    NOT NULL,
    PRIMARY KEY (subject_id, subject_kind, horizon_days),

    -- The two excursions arrive together or not at all, and a row with none says why, so an
    -- absent pair cannot be told from a measured one only by a reader that remembers to ask.
    CHECK ((mfe_atr IS NULL) = (mae_atr IS NULL)),
    CHECK ((mfe_atr IS NULL) = (excursions_absent_because IS NOT NULL))
);

INSERT INTO forward_return (
    subject_id, subject_kind, horizon_days, intended_date, actual_date, return_signed,
    mfe_atr, mae_atr, excursions_absent_because, filled_at)
SELECT
    subject_id, subject_kind, horizon_days, intended_date, actual_date, return_signed,
    mfe_atr, mae_atr, NULL, filled_at
  FROM forward_return_before_050;

DROP TABLE forward_return_before_050;

CREATE INDEX ix_forward_return_horizon ON forward_return (horizon_days, filled_at);
