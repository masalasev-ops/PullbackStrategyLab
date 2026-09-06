-- 055  the signal library becomes data, and a rejection becomes something the store can hold
--
-- <b>The section is the specification and this is the runtime form of it.</b> SCHEMA.md's Signals
-- section stays where it is: it is where a signal's formula and its source columns are written
-- down, which is what the point-in-time test is asserted against and what makes a signal
-- proposable at all. This table is seeded from that section through `SignalLibrary`, and
-- `signal-library` reconciles the two in both directions, so a signal in one and not the other
-- fails rather than reading as new.
-- see: The signal library stays a spec section and gains a runtime table, reconciled in both directions
--
-- <b>What the table adds is what a section cannot carry</b>, being a status the admission test
-- writes, the date a verdict was taken, and what the verdict was taken on.
--
-- <b>`decided_at` and not `admitted_on`, which is the one column this migration renames out of the
-- declared shape.</b> SCHEMA declared `admitted_on`, a date only an admission can carry, and the
-- failure table requires a rejection to be recorded with the correlation it was measured at and
-- the signal it was measured against. A rejection has a date too, and a column that is null on
-- every rejection cannot hold it. The two could have sat side by side, and then every admitted row
-- would carry the same instant twice: two columns that must agree are two columns that will not.
-- So one column holds the date of the last verdict, whatever the verdict was, and `status` says
-- which verdict it dates.
--
-- <b>Every figure is per side, and that is the pooling rule rather than a preference.</b> The
-- tightness ratio is a mean over pairs of setups whose outcomes are near each other, and a long
-- setup and a short setup are not near each other in any sense the statistic means: the outcome is
-- signed by direction, so pooling them would measure the gap between the two books and call it
-- discrimination. Six columns a side, named for the side, never added together.
-- see: Long and short are never pooled into one figure
--
-- <b>Direction is not in the grain, and the alternative is worth naming.</b> `ceiling_bound` puts
-- direction in its key because every column of that row is a per-direction figure. Here half the
-- row is the specification, being the formula, the source columns and the null-control flag, and
-- those are facts about the signal rather than about a side. A grain of signal plus direction would
-- write each of them twice per signal and leave two copies that have to agree.
--
-- <b>Four outcomes reach three statuses, and the missing one is deliberate.</b> A candidate that
-- was measured and did not tighten stays a candidate, because nothing about it was refused: it
-- can be asked again over a wider population, and a fourth status would say the question was
-- closed. What separates it from a candidate nobody has measured is `decided_at`, which is null on
-- the second and set on the first.
--
-- <b>The status is one value over two sides, and the rule is stated here rather than inferred.</b>
-- A signal earns its place if it tightens on either side, because the library is one library and a
-- signal admitted for shorts is computed on every setup. It is refused only where both sides refuse
-- it. The per-side outcomes stay on the row, so a signal that discriminates on one side and not the
-- other is legible as exactly that rather than as a plain admission.

CREATE TABLE signal_definition (
    signal_name       TEXT    NOT NULL PRIMARY KEY,

    -- Held as the section writes it, with the markup normalised away. A formula nothing can read
    -- is a formula nobody can check a proposal against.
    formula           TEXT    NOT NULL,
    source_columns    TEXT    NOT NULL,

    status            TEXT    NOT NULL CHECK (status IN ('active', 'candidate', 'rejected_correlation')),

    -- The planted tripwire, which is a fact about the signal rather than about a run.
    -- see: One meaningless signal is planted in the conditional tables
    is_null_control   INTEGER NOT NULL CHECK (is_null_control IN (0, 1)),

    -- When the last verdict was taken, null where none has been.
    decided_at        TEXT    NULL,

    -- The long side's verdict, and what it was taken on.
    --
    -- `long_because` carries the reason on every outcome that is not a plain admission, which
    -- includes the undecided one: the lab is under the minimum population today and will be for
    -- months, so this is the column that separates "the test refused it" from "nothing could be
    -- asked", which are the same silence otherwise.
    --
    -- The two tightness figures are null together, because one of them alone is a number with no
    -- comparison in it. They are the ratio of outcome-similar pair distance to all-pair distance,
    -- without the candidate and with it.
    long_outcome           TEXT NULL CHECK (long_outcome IN ('admitted', 'rejected_correlation', 'not_tightened', 'undecided')),
    long_because           TEXT NULL,
    long_tightness_before  TEXT NULL,
    long_tightness_after   TEXT NULL,
    long_correlation       TEXT NULL,
    long_correlated_with   TEXT NULL,

    -- The short side's, on exactly the same terms and never added to the above.
    short_outcome          TEXT NULL CHECK (short_outcome IN ('admitted', 'rejected_correlation', 'not_tightened', 'undecided')),
    short_because          TEXT NULL,
    short_tightness_before TEXT NULL,
    short_tightness_after  TEXT NULL,
    short_correlation      TEXT NULL,
    short_correlated_with  TEXT NULL,

    observed_at       TEXT    NOT NULL,

    -- A rejection says what it was measured at and against what, per side. This is the half of the
    -- failure table's row a constraint can hold: the stage decides, and the store refuses a
    -- rejection that declines to say why.
    CHECK (long_outcome  <> 'rejected_correlation' OR (long_correlation  IS NOT NULL AND long_correlated_with  IS NOT NULL)),
    CHECK (short_outcome <> 'rejected_correlation' OR (short_correlation IS NOT NULL AND short_correlated_with IS NOT NULL)),

    -- A signal whose status records a rejection was rejected on both sides, which is what the
    -- status rule says in the header. One side refusing it is a signal that discriminates on the
    -- other, and the row says so rather than the library losing it.
    CHECK (status <> 'rejected_correlation'
           OR (long_outcome = 'rejected_correlation' AND short_outcome = 'rejected_correlation')),

    -- A side that reached an outcome other than a plain admission says why.
    CHECK (long_outcome  IS NULL OR long_outcome  = 'admitted' OR long_because  IS NOT NULL),
    CHECK (short_outcome IS NULL OR short_outcome = 'admitted' OR short_because IS NOT NULL),

    -- A candidate carries either a verdict or nothing, and a row that was decided says when. Not
    -- asserted of an active signal, because the active set was declared by the specification rather
    -- than admitted by this test.
    CHECK ((decided_at IS NULL) = (long_outcome IS NULL AND short_outcome IS NULL)),

    -- The comparison or neither side of it, per side.
    CHECK ((long_tightness_before  IS NULL) = (long_tightness_after  IS NULL)),
    CHECK ((short_tightness_before IS NULL) = (short_tightness_after IS NULL))
);

CREATE INDEX ix_signal_definition_status ON signal_definition (status);
