-- 058  the proposal: what the researcher returned, or why it returned nothing
--
-- <b>One table, and the answer's shape is held by the store rather than by the stage.</b> A proposal
-- is the direction, the gate, the threshold name, the value it moves from and the value it moves to,
-- which are the five columns `variant` already holds. That tie is the point rather than a
-- coincidence: an accepted proposal becomes a version, and if the two carried different shapes there
-- would be a translation step between them and a place for the meaning to move
-- (see: A proposal is a document whose one change is the five fields the version register already stores).
--
-- <b>The change columns are conditional on the outcome, and that is the 6.4 finding closed.</b>
-- Abstention is a value of the outcome and not an absent change. Until 6.5 the schema in the
-- document required a change whatever the answer, so a model that abstained had to put something in
-- the gate field; a model shown the near-empty pack put the planted null there, and under the
-- tripwire as written that correct abstention failed pack version 1. Here an abstention has no
-- change columns to fill, so there is nowhere for it to happen
-- (see: Abstention is a valid recorded proposal outcome).
--
-- <b>The tripwire is a CHECK rather than a convention, and its scope is in the clause.</b> A pack
-- version is failed only where the proposal rests on the planted null, and never by a stopgap: the
-- rule's logic is that a competent reader citing a meaningless signal indicts the pack, while a
-- weaker reader citing it indicts itself, and the rule cannot tell the two apart
-- (see: One meaningless signal is planted in the conditional tables)
-- (see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate).
--
-- <b>Four outcomes rather than two, because a week with no proposal has three different causes.</b>
-- A seat that could not be asked, a seat that answered something that is not the agreed document,
-- and a seat that considered the evidence and declined are three different facts about the lab, and
-- one column that read "no proposal" for all three would be a silence the loop was built to break.
--
-- One correction to 057's own comments, recorded here rather than by editing an applied migration:
-- they say "the declared nine" of the pack's sections and there are ten from 6.5, the tenth being
-- the rule in force. `sections_rendered` counts what was rendered and asserts nothing about nine.

CREATE TABLE proposal (
    proposal_id          TEXT    NOT NULL PRIMARY KEY,

    -- The as-of the pack was cut for, and the digest of the body actually shown. The version says
    -- what the model was judged under and the digest says which cut it read, which are different
    -- questions: a version is deliberately the same across every night it is cut on.
    as_of                TEXT    NOT NULL,
    pack_version         INTEGER NOT NULL,
    pack_digest          TEXT    NOT NULL,

    -- The three recordings every proposal carries. The served string is the strongest of them,
    -- because it says what actually ran rather than what was asked for, and it is nullable because a
    -- transport that stopped reporting it has stopped pinning anything and a copy of the configured
    -- value would hide that (see: The model is a frozen parameter of the pack version, and changing it forks the record).
    transport            TEXT    NOT NULL CHECK (transport IN ('subscription', 'api', 'local')),
    configured_model     TEXT    NOT NULL,
    served_model         TEXT    NULL,

    -- Whether this proposal counts toward proposal hit rate by pack version. Stored rather than
    -- derived on read so the exclusion is a fact on the row: a reading that filtered on the
    -- transport would be one query away from forgetting to
    -- (see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate).
    counts_toward_hit_rate INTEGER NOT NULL CHECK (counts_toward_hit_rate IN (0, 1)),

    outcome              TEXT    NOT NULL
        CHECK (outcome IN ('proposed', 'abstained', 'unavailable', 'unreadable')),

    -- The one thing that changes, present exactly on a proposal. Values are TEXT because a threshold
    -- is compared against prices and ratios: prices are decimal in code and TEXT in storage, and
    -- never REAL. That is a hard rule of this repository rather than a decision, and it is the half
    -- of the rule code review cannot see, which is why `price-storage-form` reads the migrations.
    direction            TEXT    NULL CHECK (direction IS NULL OR direction IN ('long', 'short')),
    gate                 TEXT    NULL,
    threshold_name       TEXT    NULL,
    from_value           TEXT    NULL,
    to_value             TEXT    NULL,

    -- Selection or execution. A proposal spanning both is refused before it reaches here.
    family               TEXT    NULL CHECK (family IS NULL OR family IN ('selection', 'execution')),

    -- Why it should work, in one sentence, committed before any result exists. A mechanism stated in
    -- advance is a prediction; the same sentence written afterwards is a story.
    mechanism            TEXT    NULL,

    -- The evidence, as setup ids already in the store, and the signals the reasoning rests on. The
    -- second is the tripwire's subject and is a column of its own for that reason: which signals a
    -- proposal rests on is a shorter and more answerable question than which words its prose used.
    evidence_setup_ids   TEXT    NULL,
    evidence_signals     TEXT    NULL,

    refutation           TEXT    NULL,
    observations_to_settle INTEGER NULL CHECK (observations_to_settle IS NULL OR observations_to_settle > 0),

    -- Why there was nothing to propose, on exactly the weeks the seat declined.
    abstained_because    TEXT    NULL,

    -- Why nothing was asked, or why what came back could not be read. Kept apart from the abstention
    -- reason because a seat that could not ask and a seat that considered the evidence and declined
    -- are opposite facts about the same empty week.
    unavailable_because  TEXT    NULL,
    answer_problems      TEXT    NULL,

    -- What the seat actually returned, stored whatever became of it. A week the model returned prose
    -- is only distinguishable from a week it was never asked if the prose is kept.
    answer_text          TEXT    NULL,

    -- What pins this answer beyond the model string, as name=value lines. Empty on the two hosted
    -- transports and populated on the local one, where the weights digest, the quantisation, the
    -- runtime and the sampling together make the proposal reproducible.
    pins                 TEXT    NULL,

    -- The tripwire, in two columns because the citation and the consequence are different facts.
    cites_null_control   INTEGER NOT NULL CHECK (cites_null_control IN (0, 1)),
    fails_pack_version   INTEGER NOT NULL CHECK (fails_pack_version IN (0, 1)),

    -- Written by ResearcherSeat and thereafter the only column ProposalRegistry may move.
    status               TEXT    NOT NULL CHECK (status IN ('filed', 'screened', 'admitted', 'discarded')),

    observed_at          TEXT    NOT NULL,

    FOREIGN KEY (pack_version) REFERENCES pack_version (version),

    -- A proposal carries all five change fields, its family, its mechanism, its refutation and its
    -- observation count, and no abstention reason. Stated as a biconditional so a half-written
    -- proposal is refused by the store rather than filed and read later as a whole one.
    CHECK (outcome <> 'proposed' OR (
        direction IS NOT NULL AND gate IS NOT NULL AND threshold_name IS NOT NULL
        AND from_value IS NOT NULL AND to_value IS NOT NULL
        AND family IS NOT NULL AND mechanism IS NOT NULL
        AND refutation IS NOT NULL AND observations_to_settle IS NOT NULL
        AND abstained_because IS NULL)),

    -- An abstention states why and carries no change, no family and no observation count. This is
    -- the clause the 6.4 finding bought: the fields are absent rather than empty, so a required
    -- field cannot be filled with whatever the pack put in front of the model.
    CHECK (outcome <> 'abstained' OR (
        abstained_because IS NOT NULL
        AND direction IS NULL AND gate IS NULL AND threshold_name IS NULL
        AND from_value IS NULL AND to_value IS NULL
        AND family IS NULL AND observations_to_settle IS NULL)),

    -- A week with no answer carries a reason and no change of any kind.
    CHECK (outcome NOT IN ('unavailable', 'unreadable') OR (
        (unavailable_because IS NOT NULL OR answer_problems IS NOT NULL)
        AND direction IS NULL AND gate IS NULL AND threshold_name IS NULL
        AND from_value IS NULL AND to_value IS NULL
        AND family IS NULL AND observations_to_settle IS NULL
        AND abstained_because IS NULL)),

    -- The tripwire's scope, held by the store. A pack version is failed only by a proposal that
    -- rests on the planted null, so an abstention that named it cannot fail one, a week with no
    -- answer cannot, and a stopgap cannot whatever it cited.
    CHECK (fails_pack_version = 0 OR (
        cites_null_control = 1 AND outcome = 'proposed' AND transport <> 'local')),

    -- The exclusion, held the same way rather than left to whoever writes the row.
    CHECK ((transport = 'local' AND counts_toward_hit_rate = 0)
        OR (transport <> 'local' AND counts_toward_hit_rate = 1))
);

CREATE INDEX ix_proposal_version ON proposal (pack_version, as_of);

CREATE INDEX ix_proposal_as_of ON proposal (as_of);
