-- 019  ceiling_bound
--
-- The win rate perfect foresight could have reached, recomputed weekly.
--
-- Most of a win rate is geometry rather than skill. A give-up point at half a daily range sits
-- about 0.8 of one daily standard deviation away, so random movement hits it 42% of the time in a
-- day and 80% within ten; a coin flip with this stop wins about 20% and the observed rate is 25%.
-- The measured edge is worth about five points of win rate, so the win rate itself says almost
-- nothing and the gap between it and what was available says everything.
-- see: The win-rate ceiling is computed from the outcome distribution, never assumed
--
-- **Two denominators, and their difference is the figure.** `bound` is over the subjects that ended
-- ahead, which is what foresight would have picked. `achieved` is over everything the lab flagged.
-- A bound computed over the whole population would be the achieved rate again and the gap would be
-- nought by construction, which is a ceiling that can only ever say selection has no room.
--
-- **Per direction, in the grain rather than in a note.** A pooled bound inherits the short side's
-- borrow assumption, and the whole point of the number is the comparison against what was achieved.
--
-- `subjects` is stored because a bound without its population is a number nobody can weigh. Early
-- weeks will compute over a handful of rows and say so.
--
-- Recomputed weekly means recomputed, not revised: a later week's bound over a larger population is
-- a new dated row and the old one stays. The gap narrowing over time is itself what a reader wants.

CREATE TABLE ceiling_bound (
    as_of        TEXT    NOT NULL,
    direction    TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    horizon_days INTEGER NOT NULL,
    subjects     INTEGER NOT NULL,
    bound        TEXT    NOT NULL,
    achieved     TEXT    NOT NULL,
    computed_at  TEXT    NOT NULL,
    PRIMARY KEY (as_of, direction)
);
