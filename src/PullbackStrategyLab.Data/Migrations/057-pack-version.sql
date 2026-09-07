-- 057  the pack version, and the run row that says what one cut of the pack actually held
--
-- <b>Two tables, on the reasoning 056 arrived at one checkpoint earlier.</b> `pack_version` holds
-- the identity a proposal cites: what the model was shown and what it was judged under. `pack_run`
-- holds what one cut of the pack contained, section by section, and it exists because five of the
-- nine sections rest on outcomes that have not closed and will render empty for months. With the
-- version alone, a pack whose sections were all empty would be indistinguishable from a pack that
-- was never cut, and the version row would say nothing about either, because a version is
-- deliberately the same across every night it is cut on.
--
-- <b>The version is what the model saw and judged under, and never what it saw about.</b> A pack
-- cut on two different nights is the same version over different evidence, which is the whole point
-- of holding the version fixed while the evidence accumulates: the success criterion is proposal
-- hit rate by pack version, and a version that forked every night would leave every version holding
-- one proposal and nothing to compare (see: The evidence pack is versioned, and the success criterion is proposal hit rate by pack version).
--
-- <b>So the family-wise threshold is a column here and the realised false-discovery bar is not.</b>
-- The first is the level divided by the number of signals screened, determined entirely by the
-- version's own inputs. The second is what the Benjamini-Hochberg step-up yields against the
-- p-values, which are a reading of the store on one night. Storing it here would fork the version
-- whenever the evidence moved, and the failure would be silent: every check would pass and the
-- hit-rate table would quietly grow one row per night
-- (see: The realised false-discovery bar is a reading of a pack and the version carries the procedure).
-- It is a column on `pack_run` instead, which is the row that is about one night.
--
-- <b>No update path: a version is never edited and a re-cut is a new run row.</b> The fingerprint
-- is the identity, so a packer that computes the same tuple reuses the version it already has
-- rather than writing a second row for it. A tuple that differs in any component is a different
-- version and gets the next ordinal. That is the one place in this store where a rerun does not
-- write a generation, and the reason is that the version is not a reading: it is a definition, and
-- the same definition twice is the same row (see: An approved proposal creates a new version from zero, and a running version is never edited).

CREATE TABLE pack_version (
    -- The ordinal a person says out loud: "proposals against version 4 beat those against version
    -- 2". Ascending in creation order and never reused.
    version              INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),

    -- SHA-256 of the canonical rendering of the tuple, lowercase hex. This is the identity the
    -- ordinal is a label for: two cuts computing the same tuple find this row rather than making a
    -- second one.
    fingerprint          TEXT    NOT NULL UNIQUE,

    -- The sections the model was shown, in the order it was shown them. Order is part of the
    -- identity here, unlike the screened set below, because a reordered pack is a different pack.
    sections             TEXT    NOT NULL,

    -- The signals screened, sorted ordinal, and their count. Sorted rather than in library order so
    -- that reordering SCHEMA's Signals section without changing a signal does not fork the version.
    -- The count is stored beside the list rather than derived on read, because it is what both
    -- thresholds were computed over and a figure's population belongs on its own row.
    signals_screened     TEXT    NOT NULL,
    signals_screened_count INTEGER NOT NULL CHECK (signals_screened_count >= 0),

    -- What admission is decided under, and the level both corrections are taken at.
    correction_form      TEXT    NOT NULL,
    correction_level     TEXT    NOT NULL,

    -- The level over the count screened. Null only where nothing was screened, which is a library
    -- with no signals in it rather than a night with no evidence.
    family_wise_threshold TEXT   NULL,

    -- The model the seat is pinned to. In the version because it is a confounder for the phase's
    -- own success criterion: a change to it forks the record rather than continuing it
    -- (see: The model is a frozen parameter of the pack version, and changing it forks the record).
    model_identifier     TEXT    NOT NULL,

    created_at           TEXT    NOT NULL,

    -- A version with no signals screened has no family-wise threshold, and one with signals has
    -- one. Stated as a biconditional so a null cannot arrive from a packer that simply failed to
    -- compute it.
    CHECK ((signals_screened_count = 0 AND family_wise_threshold IS NULL)
        OR (signals_screened_count > 0 AND family_wise_threshold IS NOT NULL))
);

CREATE TABLE pack_run (
    as_of                TEXT    NOT NULL,

    -- Null on a refused run, which is the whole of what "no pack is written" means: a night that
    -- could not build every section writes this row and no version row at all.
    version              INTEGER NULL,

    -- The pack's own digest, so two runs at one commit over one store state can be compared without
    -- storing the body. Byte-stability is a property of the packer and this is what makes it
    -- checkable from the store rather than only from a test holding two strings
    -- (see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable).
    body_digest          TEXT    NULL,
    body_bytes           INTEGER NULL CHECK (body_bytes IS NULL OR body_bytes > 0),

    -- Every section is present in every pack, so this is the count of sections rendered and it is
    -- expected to equal the declared nine. Stored rather than assumed, because a packer that
    -- dropped one would otherwise leave no trace. Nought on a refused run.
    sections_rendered    INTEGER NOT NULL CHECK (sections_rendered >= 0),

    -- Why no pack was written, on exactly the nights none was. **A pack missing a section is not a
    -- smaller pack**: the correction is computed over the signals screened, so a pack that dropped
    -- a section would carry a threshold for a set it did not screen and every claim against it
    -- would be judged against the wrong number. So the night refuses and says which section it
    -- could not build, rather than cutting a short one and looking complete.
    refused_because      TEXT    NULL,

    -- How many of the nine had nothing to say. A section over an empty population renders with a
    -- count of nought rather than being left out, and this figure is what says how much of the pack
    -- that was on the night it was cut. Five of the nine rest on outcomes that have not closed.
    sections_empty       INTEGER NOT NULL CHECK (sections_empty >= 0),

    -- What the multiple-comparison section stated on this night. The realised bar lives here rather
    -- than on the version, because it is a reading of the store and the version is not.
    signals_screened     INTEGER NOT NULL CHECK (signals_screened >= 0),
    false_discovery_bar  TEXT    NULL,

    -- The populations the sections were built over, counted apart and never added
    -- (see: Long and short are never pooled into one figure).
    long_setups          INTEGER NOT NULL CHECK (long_setups >= 0),
    short_setups         INTEGER NOT NULL CHECK (short_setups >= 0),

    -- Whether the null control reached the conditional tables. It is the tripwire, so a pack that
    -- failed to plant it is a pack whose tripwire is not armed, and that has to be legible from the
    -- row rather than inferred from the body (see: One meaningless signal is planted in the conditional tables).
    null_control_planted INTEGER NOT NULL CHECK (null_control_planted IN (0, 1)),

    outcome              TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    observed_at          TEXT    NOT NULL,

    PRIMARY KEY (as_of, observed_at),

    FOREIGN KEY (version) REFERENCES pack_version (version),

    -- No section is rendered and empty at the same time in a way that exceeds the whole.
    CHECK (sections_empty <= sections_rendered),

    -- A refused run says why and carries no pack, and a run that produced one does not carry a
    -- reason it did not. Stated as a biconditional in both directions rather than as a convention
    -- the stage keeps, so a half-written refusal is refused by the store.
    CHECK ((outcome = 'failed'
                AND refused_because IS NOT NULL
                AND version IS NULL
                AND body_digest IS NULL
                AND body_bytes IS NULL
                AND sections_rendered = 0)
        OR (outcome <> 'failed'
                AND refused_because IS NULL
                AND version IS NOT NULL
                AND body_digest IS NOT NULL
                AND body_bytes IS NOT NULL
                AND sections_rendered > 0))
);

CREATE INDEX ix_pack_run_version ON pack_run (version, as_of);
