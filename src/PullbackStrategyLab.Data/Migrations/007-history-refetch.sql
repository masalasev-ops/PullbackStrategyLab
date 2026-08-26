-- 007  history_refetch
--
-- When a ticker's whole series was last re-observed on one basis, recorded as the event it is
-- rather than inferred from what the re-observation happened to change.
--
-- The inference was tried and it does not work, in both directions. Taking the earliest
-- observation in the window is too strict: a refetch rewrites the bars an action moved and
-- leaves the recent ones alone, because those already carried the post-action figures, so the
-- window keeps an old earliest observation and the ticker stays blocked for ever. Taking the
-- latest is too lenient: the nightly ingest writes one new bar every night, so every demand
-- would satisfy itself by the following evening without anything having been refetched.
--
-- What the engine needs to know is not what changed. It is whether anybody looked. That is an
-- event, it has a time, and a store is where an event with a time belongs.

CREATE TABLE history_refetch (
    ticker       TEXT    NOT NULL REFERENCES security (ticker),
    refetched_at TEXT    NOT NULL,
    from_date    TEXT    NOT NULL,
    to_date      TEXT    NOT NULL,
    bars_written INTEGER NOT NULL CHECK (bars_written >= 0),
    PRIMARY KEY (ticker, refetched_at)
);

-- The read is one ticker's most recent refetch, which the primary key serves directly.
