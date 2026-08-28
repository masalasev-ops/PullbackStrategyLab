-- 016  screened_over_sessions, screen_carried
--
-- Whether a night's membership was freshly screened or carried from the last complete screen.
--
-- The question was raised at 1.3 and recorded only in that entry's `Carried` block, so it reached
-- no table and nothing read it until 3.0. It is this: the liquidity floor is a median over twenty
-- sessions, so a night that can see fewer than five sessions cannot screen. Should it write
-- nothing, or carry the last complete screen's membership?
--
-- The count distribution answers it. A night flags a median of 44 long names and 13 short out of
-- 2,016, while membership drifts by a handful a month. Carrying a five-session-old membership
-- misstates a night by far less than skipping the night removes from it, and phase 3 is where a
-- missing night starts costing a sample rather than a row.
--
-- So a night carries, and says that it carried. That second half is the whole reason this is a
-- migration rather than a document edit: a carried night that looks like a screened one is a
-- survivorship claim the store cannot support, and every count downstream would read it as fresh.
-- `screened_over_sessions` says how many sessions the screen actually saw, and `screen_carried`
-- says whether the membership was taken from an earlier night.
--
-- Nullable, because every snapshot written before this migration was written without the fact and
-- inventing one for it would be worse than admitting it is absent. A null here means "written
-- before 3.0 and not recorded", which is a different thing from a night that screened over nought
-- sessions.

ALTER TABLE universe_snapshot ADD COLUMN screened_over_sessions INTEGER NULL;
ALTER TABLE universe_snapshot ADD COLUMN screen_carried INTEGER NULL
    CHECK (screen_carried IS NULL OR screen_carried IN (0, 1));

-- The read a later session makes of this is "which nights were carried", over a date range, which
-- the primary key on (as_of, ticker) does not answer without scanning every member of every night.
CREATE INDEX ix_universe_snapshot_carried ON universe_snapshot (screen_carried, as_of);
