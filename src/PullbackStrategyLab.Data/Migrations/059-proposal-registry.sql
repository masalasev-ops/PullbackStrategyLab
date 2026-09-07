-- 059  what a screen made of a proposal, and the second kind of proposal the seat can write
--
-- <b>Two changes, and the first is a correction to what 058 could express.</b> The registry accepts
-- two kinds: a rule change over existing signals, which goes to the screen and then to a paired
-- test, and a signal request, which says the model cannot separate two setups with what it has and
-- names what would (see: Proposals come in two kinds, rule changes over existing signals and requests for a new signal).
-- `proposal` carried the first and had no shape for the second, so a seat that wanted a signal had
-- to write a rule change or nothing. The outcome gains `requested`, and a requested row carries the
-- signal it wants and none of the change fields, on exactly the terms the abstention clause already
-- sets: a required field on a document that proposes no rule is a field the model fills with
-- whatever the pack put in front of it.
--
-- <b>The status set grows from four to seven, and each new value is a state the four could not
-- say.</b> `recorded` is an abstention, which is a result and not a failure
-- (see: Abstention is a valid recorded proposal outcome). `build-task` is a signal request, which is
-- work rather than a variant. `unactionable` is a week the seat could not be asked or answered
-- something that could not be read, which is neither a result nor work. Folding those three into one
-- would put an abstention and an outage in the same bucket, and telling them apart is the whole
-- reason the seat records four outcomes rather than two.
--
-- <b>`replay_result` is created here rather than at 5.3, and its grain is not the one SCHEMA
-- declared.</b> The row said `proposal + window`. A screen is a reading rather than a definition:
-- the same proposal screened twice over the same window is two readings, because the evidence
-- underneath moved, and every other reading in this store writes a generation keyed on when it was
-- taken. Keyed on the proposal and the window alone, a re-screen would be a primary-key collision
-- against a writer that has no update path at all, so the second reading could not be recorded and
-- the first would go on reading as current. The grain is the proposal, the window and the instant
-- (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood).
--
-- <b>And the window is nullable, because no window has matured.</b> Holdout windows are quarters of
-- forward-collected evidence and the first session the evidence store holds is 2026-08-27, so the
-- earliest quarter completes after this migration is written. A screen taken over the accumulated
-- store is not a screen over a window, and a sentinel window id would make the two indistinguishable
-- in a count (see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight).
--
-- <b>A screen never admits, which is a CHECK here rather than a convention in the stage.</b> Replay
-- is free and free tests are how you overfit: a screen kills a proposal cheaply and only the forward
-- paired test says one is worth keeping (see: Replay screens proposals and the forward paired test admits them).
-- So the verdict is `killed`, `survived`, `refused` or `inconclusive`, and there is no value here
-- that means admitted.

-- ---------------------------------------------------------------------------------------------
-- `proposal`, rebuilt for the second kind
-- ---------------------------------------------------------------------------------------------

CREATE TABLE proposal_new (
    proposal_id          TEXT    NOT NULL PRIMARY KEY,

    as_of                TEXT    NOT NULL,
    pack_version         INTEGER NOT NULL,
    pack_digest          TEXT    NOT NULL,

    transport            TEXT    NOT NULL CHECK (transport IN ('subscription', 'api', 'local')),
    configured_model     TEXT    NOT NULL,
    served_model         TEXT    NULL,

    counts_toward_hit_rate INTEGER NOT NULL CHECK (counts_toward_hit_rate IN (0, 1)),

    -- `requested` is the second kind and joins the four 058 wrote.
    outcome              TEXT    NOT NULL
        CHECK (outcome IN ('proposed', 'requested', 'abstained', 'unavailable', 'unreadable')),

    direction            TEXT    NULL CHECK (direction IS NULL OR direction IN ('long', 'short')),
    gate                 TEXT    NULL,
    threshold_name       TEXT    NULL,
    from_value           TEXT    NULL,
    to_value             TEXT    NULL,

    family               TEXT    NULL CHECK (family IS NULL OR family IN ('selection', 'execution')),

    -- The signal a request wants computed, present exactly on a request. A name rather than a
    -- formula: the request is a build task and what to compute is decided by a person reading it,
    -- which is what keeps the library a hard ceiling the model cannot lift for itself.
    requested_signal     TEXT    NULL,

    -- Which axis of measurement the request belongs to, so a run of requests all naming price-path
    -- signals is legible as the library's actual shape rather than as eight separate ideas.
    requested_axis       TEXT    NULL,

    mechanism            TEXT    NULL,
    evidence_setup_ids   TEXT    NULL,
    evidence_signals     TEXT    NULL,

    refutation           TEXT    NULL,
    observations_to_settle INTEGER NULL CHECK (observations_to_settle IS NULL OR observations_to_settle > 0),

    abstained_because    TEXT    NULL,

    unavailable_because  TEXT    NULL,
    answer_problems      TEXT    NULL,
    answer_text          TEXT    NULL,
    pins                 TEXT    NULL,

    cites_null_control   INTEGER NOT NULL CHECK (cites_null_control IN (0, 1)),
    fails_pack_version   INTEGER NOT NULL CHECK (fails_pack_version IN (0, 1)),

    -- Seven states, and the three added here are three the four could not say. `screened` is a rule
    -- change replay let through, `discarded` one it killed, `admitted` one a version was created
    -- from, `recorded` an abstention, `build-task` a signal request, and `unactionable` a week with
    -- no answer in it.
    status               TEXT    NOT NULL
        CHECK (status IN ('filed', 'screened', 'discarded', 'admitted',
                          'recorded', 'build-task', 'unactionable')),

    observed_at          TEXT    NOT NULL,

    FOREIGN KEY (pack_version) REFERENCES pack_version (version),

    CHECK (outcome <> 'proposed' OR (
        direction IS NOT NULL AND gate IS NOT NULL AND threshold_name IS NOT NULL
        AND from_value IS NOT NULL AND to_value IS NOT NULL
        AND family IS NOT NULL AND mechanism IS NOT NULL
        AND refutation IS NOT NULL AND observations_to_settle IS NOT NULL
        AND abstained_because IS NULL
        AND requested_signal IS NULL)),

    -- A request names the signal it wants and the setups it could not separate, and carries no
    -- change of any kind. It states no observation count because nothing is being settled: the
    -- question a request asks is answered by computing the signal and vetting it, not by waiting.
    CHECK (outcome <> 'requested' OR (
        requested_signal IS NOT NULL AND mechanism IS NOT NULL
        AND evidence_setup_ids IS NOT NULL
        AND direction IS NULL AND gate IS NULL AND threshold_name IS NULL
        AND from_value IS NULL AND to_value IS NULL
        AND family IS NULL AND observations_to_settle IS NULL
        AND abstained_because IS NULL)),

    CHECK (outcome <> 'abstained' OR (
        abstained_because IS NOT NULL
        AND direction IS NULL AND gate IS NULL AND threshold_name IS NULL
        AND from_value IS NULL AND to_value IS NULL
        AND family IS NULL AND observations_to_settle IS NULL
        AND requested_signal IS NULL)),

    CHECK (outcome NOT IN ('unavailable', 'unreadable') OR (
        (unavailable_because IS NOT NULL OR answer_problems IS NOT NULL)
        AND direction IS NULL AND gate IS NULL AND threshold_name IS NULL
        AND from_value IS NULL AND to_value IS NULL
        AND family IS NULL AND observations_to_settle IS NULL
        AND abstained_because IS NULL
        AND requested_signal IS NULL)),

    -- The tripwire's scope, unchanged from 058 and extended to the second kind by the same clause:
    -- only a rule change can rest on the planted null, because only a rule change rests on a
    -- threshold. A request naming the control is a request to compute something already computed,
    -- which the admission test refuses on correlation rather than the tripwire on citation.
    CHECK (fails_pack_version = 0 OR (
        cites_null_control = 1 AND outcome = 'proposed' AND transport <> 'local')),

    CHECK ((transport = 'local' AND counts_toward_hit_rate = 0)
        OR (transport <> 'local' AND counts_toward_hit_rate = 1))
);

INSERT INTO proposal_new
    (proposal_id, as_of, pack_version, pack_digest, transport, configured_model, served_model,
     counts_toward_hit_rate, outcome, direction, gate, threshold_name, from_value, to_value,
     family, mechanism, evidence_setup_ids, evidence_signals, refutation, observations_to_settle,
     abstained_because, unavailable_because, answer_problems, answer_text, pins,
     cites_null_control, fails_pack_version, status, observed_at)
SELECT
     proposal_id, as_of, pack_version, pack_digest, transport, configured_model, served_model,
     counts_toward_hit_rate, outcome, direction, gate, threshold_name, from_value, to_value,
     family, mechanism, evidence_setup_ids, evidence_signals, refutation, observations_to_settle,
     abstained_because, unavailable_because, answer_problems, answer_text, pins,
     cites_null_control, fails_pack_version, status, observed_at
  FROM proposal;

DROP TABLE proposal;

ALTER TABLE proposal_new RENAME TO proposal;

CREATE INDEX ix_proposal_version ON proposal (pack_version, as_of);

CREATE INDEX ix_proposal_as_of ON proposal (as_of);

CREATE INDEX ix_proposal_status ON proposal (status, as_of);

-- ---------------------------------------------------------------------------------------------
-- `replay_result`, which now has something to belong to
-- ---------------------------------------------------------------------------------------------

CREATE TABLE replay_result (
    proposal_id          TEXT    NOT NULL,

    -- The holdout window the screen was run over, or null where it was run over the accumulated
    -- store. Null and a window id are different screens and the count of each is a different fact,
    -- which is why there is no sentinel here.
    window_id            TEXT    NULL,

    -- The instant, which is what makes a re-screen a second reading rather than a collision.
    observed_at          TEXT    NOT NULL,

    as_of                TEXT    NOT NULL,

    -- One side, and the row says which. A version is one side's, because a threshold belongs to one
    -- side's gate list (see: Long and short are never pooled into one figure).
    direction            TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    -- What the walk read. The cost of a screen is a function of how many nights the store holds
    -- rather than of how many rows they hold between them, so both are recorded.
    sessions_read        INTEGER NOT NULL CHECK (sessions_read >= 0),
    rows_examined        INTEGER NOT NULL CHECK (rows_examined >= 0),

    -- The two selections and their overlap. The three counts are what a screen is: a proposal that
    -- selects exactly what the baseline selects has changed nothing, and one that selects almost
    -- nothing has changed too much.
    baseline_selected    INTEGER NOT NULL CHECK (baseline_selected >= 0),
    candidate_selected   INTEGER NOT NULL CHECK (candidate_selected >= 0),
    both_selected        INTEGER NOT NULL CHECK (both_selected >= 0),
    candidate_only       INTEGER NOT NULL CHECK (candidate_only >= 0),
    baseline_only        INTEGER NOT NULL CHECK (baseline_only >= 0),

    -- What the walk could not judge, kept apart from what it judged. A screen over a population it
    -- could not read is a screen with a smaller subject, and a figure that hid it would be a figure
    -- over a population other than the one its name says.
    unjudgeable          INTEGER NOT NULL CHECK (unjudgeable >= 0),
    unmeasured_verdicts  INTEGER NOT NULL CHECK (unmeasured_verdicts >= 0),
    disagreements        INTEGER NOT NULL CHECK (disagreements >= 0),

    -- **A screen kills or lets through and never admits.** Replay is free and free tests are how you
    -- overfit; only the forward paired test says a proposal is worth keeping. There is no value here
    -- that means admitted, which is the decision held by the store rather than by the stage
    -- (see: Replay screens proposals and the forward paired test admits them).
    -- `inconclusive` is the state the funnel makes ordinary rather than rare: over a population
    -- where the baseline selected nothing, a candidate selecting nothing too has not changed
    -- nothing, it has said nothing, and calling that `killed` would be a verdict over a population
    -- of none. The funnel passes a median of nought candidates a night, so this is the commonest
    -- verdict this lab can produce today and it is a state of its own for that reason.
    verdict              TEXT    NOT NULL
        CHECK (verdict IN ('killed', 'survived', 'refused', 'inconclusive')),

    -- Why the screen refused to run at all, on exactly the screens that did. A candidate the
    -- register would not take as a version is not screenable: reporting on it would report on
    -- something that could never run.
    refused_because      TEXT    NULL,

    elapsed_ms           INTEGER NOT NULL CHECK (elapsed_ms >= 0),

    PRIMARY KEY (proposal_id, observed_at),

    FOREIGN KEY (proposal_id) REFERENCES proposal (proposal_id),

    -- A refused screen carries its reason and read nothing; a screen that ran carries no reason and
    -- did read. Stated in both directions so a half-written refusal is refused by the store.
    CHECK ((verdict = 'refused'
                AND refused_because IS NOT NULL
                AND sessions_read = 0
                AND rows_examined = 0)
        OR (verdict <> 'refused'
                AND refused_because IS NULL)),

    -- The overlap is bounded by both sides of it, so a row that could not be a set relation is
    -- refused rather than filed and read later as one.
    CHECK (both_selected <= baseline_selected AND both_selected <= candidate_selected),
    CHECK (candidate_only <= candidate_selected AND baseline_only <= baseline_selected)
);

CREATE INDEX ix_replay_result_window ON replay_result (window_id, observed_at);
