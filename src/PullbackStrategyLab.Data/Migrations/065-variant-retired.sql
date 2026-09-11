-- 065  variant, gaining the status a closed generation's baseline carries
--
-- <b>The act that closes a generation had no status to write on the baseline row.</b> Editing the
-- baseline closes every open version of its generation as `unresolved` and starts a new generation,
-- and the register could say that of every version but the baseline itself. `unresolved` is wrong for
-- it: an unresolved version was never measured because what it was compared against stopped existing,
-- and the baseline is the thing that stopped existing. `accepted` and `rejected` are wrong for it on
-- the baseline's own pre-registration, which says it is not itself accepted or rejected, being the arm
-- the paired comparison subtracts. So the baseline's closed state is a fourth answer, `retired`, and
-- the store holds it to the baseline alone.
-- see: A selection version's target is derived from the settling rule and is not typed
-- see: An approved proposal creates a new version from zero, and a running version is never edited
--
-- <b>Two clauses rather than one.</b> `retired` is written on a baseline and nothing else, and a
-- baseline is either open or retired and never settled, so a version reading as retired and a baseline
-- reading as accepted are both refused by the store rather than filed and reconciled by whoever reads
-- them.
--
-- <b>A rebuild, because SQLite cannot widen a CHECK in place.</b> Every column travels, the five 052
-- added and the proposal 061 added included, and every index is recreated. `legacy_alter_table` is on
-- for the reason 051 and 052 give: seven tables name `variant` in a foreign key, and without it the
-- rename would repoint every one of them at the transient.

PRAGMA legacy_alter_table = ON;

ALTER TABLE variant RENAME TO variant_before_065;

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
        CHECK (status IN ('open', 'accepted', 'rejected', 'unresolved', 'retired')),
    resolved_at          TEXT    NULL,
    created_at           TEXT    NOT NULL,
    direction            TEXT    NULL CHECK (direction IS NULL OR direction IN ('long', 'short')),
    gate                 TEXT    NULL,
    threshold_name       TEXT    NULL,
    threshold_from       TEXT    NULL,
    threshold_to         TEXT    NULL,
    proposal_id          TEXT    NULL REFERENCES proposal (proposal_id),

    CHECK ((status = 'open') = (resolved_at IS NULL)),
    CHECK ((family = 'execution') = (minimum_sample_unit = 'paired_trades')),

    CHECK ((family = 'selection') = (direction IS NOT NULL)),
    CHECK ((direction IS NULL) = (gate IS NULL)),
    CHECK ((direction IS NULL) = (threshold_name IS NULL)),
    CHECK ((direction IS NULL) = (threshold_from IS NULL)),
    CHECK ((direction IS NULL) = (threshold_to IS NULL)),
    CHECK (threshold_from IS NULL OR threshold_from <> threshold_to),

    -- The baseline's closed state, and only the baseline's.
    CHECK (status <> 'retired' OR family = 'baseline'),

    -- A baseline is never settled. It is open while its generation is in force and retired once the
    -- act closing that generation has run.
    CHECK (family <> 'baseline' OR status IN ('open', 'retired'))
);

INSERT INTO variant (
    variant_id, generation, family, definition, target,
    minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
    direction, gate, threshold_name, threshold_from, threshold_to, proposal_id)
SELECT variant_id, generation, family, definition, target,
       minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
       direction, gate, threshold_name, threshold_from, threshold_to, proposal_id
  FROM variant_before_065;

DROP TABLE variant_before_065;

CREATE UNIQUE INDEX ux_variant_baseline ON variant (generation) WHERE family = 'baseline';
CREATE INDEX ix_variant_status ON variant (status);
CREATE INDEX ix_variant_proposal ON variant (proposal_id);

PRAGMA legacy_alter_table = OFF;
