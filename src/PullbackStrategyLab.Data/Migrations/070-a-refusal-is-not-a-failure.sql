-- 070  pack_run, able to say a refusal the design requires
--
-- The biconditional 057 wrote keys the refusal on `outcome = 'failed'`, so the store can hold a
-- refused run only by calling it a failure. On 2026-09-12, the first morning the pack slot ever
-- ran, it refused because generation 1 has no selection rule written down, which is the decision
-- taken at 7.11 working exactly as written and a condition with no date on it. Recorded as a
-- failure it puts a red beside a correct outcome every Saturday, which is how a red beside a wrong
-- one stops being read.
--
-- What moves is the key of the biconditional and nothing else. A refused run is one carrying a
-- reason, whatever outcome it ends under; a run that produced a pack carries no reason and cannot
-- be a failure. So the two halves now read off `refused_because`, which is the column that actually
-- says whether a pack was written, and `outcome` is free to say which kind of non-pack it was:
-- 'partial' where the design required the refusal, 'failed' where a section broke.
--
-- ResearcherSeat already recorded 'partial' for this same event, having no pack to be asked
-- against, so the two stages now describe one morning one way rather than two.
--
-- No row changes value. Every refusal on disk was written as 'failed' and stays 'failed', which is
-- the right reading of every refusal before this migration: each was a section that broke.

CREATE TABLE pack_run_refusal (
    as_of                TEXT    NOT NULL,
    version              INTEGER NULL,
    body_digest          TEXT    NULL,
    body_bytes           INTEGER NULL CHECK (body_bytes IS NULL OR body_bytes > 0),
    sections_rendered    INTEGER NOT NULL CHECK (sections_rendered >= 0),
    refused_because      TEXT    NULL,
    sections_empty       INTEGER NOT NULL CHECK (sections_empty >= 0),
    signals_screened     INTEGER NOT NULL CHECK (signals_screened >= 0),
    false_discovery_bar  TEXT    NULL,
    long_setups          INTEGER NOT NULL CHECK (long_setups >= 0),
    short_setups         INTEGER NOT NULL CHECK (short_setups >= 0),
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
    --
    -- **Keyed on the reason rather than on the outcome, from 070.** Whether a pack was written is
    -- what `refused_because` says; `outcome` says how the night ended, and a refusal the design
    -- requires ends 'partial' while a section that broke ends 'failed'. Keying the biconditional on
    -- the outcome made those two one value and made a correct Saturday indistinguishable from a
    -- fault. A run that produced a pack still cannot be a failure.
    CHECK ((refused_because IS NOT NULL
                AND outcome IN ('partial', 'failed')
                AND version IS NULL
                AND body_digest IS NULL
                AND body_bytes IS NULL
                AND sections_rendered = 0)
        OR (refused_because IS NULL
                AND outcome <> 'failed'
                AND version IS NOT NULL
                AND body_digest IS NOT NULL
                AND body_bytes IS NOT NULL
                AND sections_rendered > 0))
);

INSERT INTO pack_run_refusal (
    as_of, version, body_digest, body_bytes, sections_rendered, refused_because, sections_empty,
    signals_screened, false_discovery_bar, long_setups, short_setups, null_control_planted,
    outcome, observed_at)
SELECT
    as_of, version, body_digest, body_bytes, sections_rendered, refused_because, sections_empty,
    signals_screened, false_discovery_bar, long_setups, short_setups, null_control_planted,
    outcome, observed_at
FROM pack_run;

DROP TABLE pack_run;

ALTER TABLE pack_run_refusal RENAME TO pack_run;

CREATE INDEX ix_pack_run_version ON pack_run (version, as_of);
