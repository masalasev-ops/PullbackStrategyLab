-- 013  regime_daily
--
-- One market-mood label a night, from two scores summed.
--
-- Index trend: how many of SPY, QQQ and IWM closed above their own 21-day average, scoring +1 for
-- three, -1 for none, 0 for one or two. Breadth: the ratio of universe names passing the long
-- ladder to those passing the short ladder, scoring +1 above 1.5, -1 below 0.67, 0 between. The sum
-- gives risk_on at +2, risk_off at -2 and mixed otherwise.
--
-- Both raw scores are stored beside the label, and so are both raw ladder counts. The label is
-- three buckets over a continuous thing, and a later proposal wanting the continuous form should
-- not have to recompute it from bars that may since have been restated.
--
-- The label filters nothing in the baseline. It is recorded against every setup and gates no
-- decision, which is what keeps it available as a clean experiment: baking it in now would be an
-- untested assumption, and adding it later as a version is a measurement.
-- see: The market-mood label is recorded on every setup and filters nothing in the baseline
--
-- Grain is the date. One row a night, and the primary key says so: a second label for one session
-- would be two answers to a question that has one.
--
-- Scores are INTEGER because they are counts on a three-point scale, not measurements. The ratio
-- they are derived from is not stored, because it is reconstructible from the two counts that are
-- and storing it would be the same fact twice.

CREATE TABLE regime_daily (
    as_of              TEXT    NOT NULL PRIMARY KEY,
    index_score        INTEGER NOT NULL CHECK (index_score IN (-1, 0, 1)),
    breadth_score      INTEGER NOT NULL CHECK (breadth_score IN (-1, 0, 1)),
    label              TEXT    NOT NULL CHECK (label IN ('risk_on', 'mixed', 'risk_off')),
    long_ladder_count  INTEGER NOT NULL,
    short_ladder_count INTEGER NOT NULL,
    indexes_above      INTEGER NOT NULL CHECK (indexes_above BETWEEN 0 AND 3)
);
