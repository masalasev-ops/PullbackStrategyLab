-- 030  the scoreboard's account-wide panels are actually unique
--
-- `scoreboard` declares `PRIMARY KEY (as_of, panel, direction)` and `direction` is null on every
-- band 0 panel, because those are account-wide. **SQLite treats nulls as distinct in a unique
-- index**, so that key never constrains an account-wide row: two `band0.setupsOnFile` rows for one
-- date do not conflict with each other, and `ON CONFLICT ... DO NOTHING` never fires for them.
--
-- The consequence was the opposite of the one anybody was looking for. A rebuild of a date that
-- already carries panels was understood to write nothing, and that is true of the six panels that
-- carry a direction. The five that do not were **inserted again**, so a second build left the store
-- with two of every band 0 panel and a third build with three, and `LabScoreboard` would have handed
-- the page one row per duplicate. Found at 3.9(e) by a test written for the no-op, which measured
-- eleven attempted and six skipped and named the five that were neither.
--
-- Two halves, and the first is a repair rather than a guard:
--
--   the dedupe keeps the earliest row per date and panel, by `rowid`, which is the one the build
--   that first wrote the date produced. A later duplicate is a rebuild's copy and carries no
--   information the first does not;
--
--   the partial unique index is what the primary key was believed to be. It covers exactly the rows
--   the primary key cannot, so the two together constrain every row rather than most of them.
--
-- The insert's conflict target goes with this: `ON CONFLICT (as_of, panel, direction)` names the
-- primary key specifically and would raise on a violation of this index rather than doing nothing,
-- so the stage uses a bare `ON CONFLICT DO NOTHING` and treats any uniqueness violation the same
-- way.
-- see: Long and short are never pooled into one figure

DELETE FROM scoreboard
 WHERE direction IS NULL
   AND rowid NOT IN (
       SELECT MIN(rowid) FROM scoreboard WHERE direction IS NULL GROUP BY as_of, panel);

CREATE UNIQUE INDEX IF NOT EXISTS scoreboard_account_wide
    ON scoreboard (as_of, panel)
 WHERE direction IS NULL
