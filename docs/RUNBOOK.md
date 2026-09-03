# RUNBOOK.md

Operator procedures. How to set the lab up, run it, move it and recover it. Written for the person at the keyboard, not for the build session.

---

## First-time setup

1. Install the .NET SDK. Confirm `dotnet --info` reports the arm64 runtime on Apple Silicon.
2. Clone, then `dotnet restore` and `dotnet build`.
3. Nothing to create. The lab keeps two stores under two data roots inside the repository, `data/live` for the nightly job and `data/ci` for a scratch store `tools/ci.*` drops and recreates on every run, and `/data` is gitignored (see: The lab keeps one store per purpose under one data root, and CI never opens the operator's). **Do not set `PullbackStrategyLab:DataRoot` and do not point it at a synced folder.** A sync client copying an open database mid-write is a real corruption risk rather than a theoretical one, and the repository is the one place both entry points already agree about. **This step said to create a root outside the repository until 4.17**, which contradicted the decision the shipped code follows and is what once armed the fault 3.14 corrected: an operator who followed it and exported the variable had `tools/ci.*` delete whatever it pointed at on its first step. The scripts no longer yield to the variable, so nothing is armed today.
4. Put the vendor API key in `appsettings.Secrets.json`, beside `appsettings.json` in each project that needs it. Gitignored, never committed. It is plaintext, so treat it like a key file: it travels by deliberate copy rather than by accident, and it stays out of any backup that leaves the machine.
5. Confirm `ANTHROPIC_API_KEY` is **not** set anywhere in the environment. It stays out on both researcher transports: on the subscription path its presence silently defeats plan auth and bills API rates, and on the API path the key belongs in `appsettings.Secrets.json` with every other secret, so one in the environment means two places supply the same credential and nothing on the surface says which won.
6. `tools/migrate` to create the schema. It calls `tools/snapshot-db` first and refuses to run without a successful snapshot.
7. `tools/ci.ps1` or `tools/ci.sh`. Green before anything else.

### Backfill, one time

**Order matters more than depth.** The liquidity floor cannot be applied without bars, and fetching full history for every listed name would blow the daily ceiling. Screen first on cheap bulk data, then fetch history only for the survivors.

**Depth: two to three years.** The end-of-day endpoint returns a ticker's whole history for one call, so ten years costs what one year costs. The first 150 sessions are warm-up, because a 50-day exponential average needs roughly three times its period to converge, and the rest gives the calibration run something to count over.

**Two endpoints, priced differently, which is the whole reason for the split.** Bulk end-of-day is charged per day of market data, so twenty sessions costs 2,000 calls and six hundred would cost 60,000. The per-ticker endpoint is charged per ticker regardless of depth, so one call returns a name's entire history. Going deep is free on one and ruinous on the other.

**The screen is one call per surviving name, and that number is measured rather than estimated.** Two counts matter and both are cheap to get before writing any code. The exchange symbol list is one call and returns every US ticker with a type field, so counting common stock gives N's upper bound today. The floors then cut that to N itself, which step 3 measures.

**The backfill is not counted against the nightly ceiling.** The ceiling guards the evening's job, and a one-time operation is not the evening's job; charging the two against each other is what once made this look like a two-day procedure. The run records its calls like any other and the nightly total does not see them, which the run log says outright rather than leaving to be inferred from the stage name.

| Order | Job | Calls |
|---|---|---|
| 1 | Symbol list | ~5 |
| 2 | Bulk end-of-day, last 20 sessions, whole market | ~2,000 |
| 3 | Apply the price and liquidity floors. Survivor count **N**, measured not assumed | 0 |
| 4 | Full daily history for the survivors, one call each, any depth | N |
| 5 | Minute bars for 200 names to calibrate the fill model | ~1,000 |
| | **Total** | **~3,005 + N** |

**Size, measured rather than estimated.** N was 2,070 when this was first run, so step 4 is 2,070 calls and the whole procedure is about 5,075. It is one operation and it runs in one sitting; the order within it is what matters, not the calendar.

**There is no split-history step, and there was never any code for one.** The table used to carry a second per-name pass for the full split history of every survivor, a second N calls. It was dropped at the 1.12 sign-off after the review found that the vendor client has only the bulk per-date splits endpoint and the per-ticker daily-history endpoint: nothing anywhere fetches one name's splits, so the step described work that had no implementation and the obligation raised at 1.9 was never a matter of spending calls. What it would have bought is the history of splits from before the lab started running. Nothing depends on that. Splits arrive nightly from the bulk endpoint, so every split from the first night onward is recorded; and the one thing that would read older splits, a detector run over stored history, goes to `calibration_setup` at 2.11 rather than to `setup`, where survivorship bias already rules those rows out as evidence (see: The evidence store holds only setups flagged forward, never setups reconstructed from history). If a reason to want it appears later, it arrives as a checkpoint that builds the endpoint, captures a fixture input for it and states its expectations, like any other ingestion path.

### The delisted purchase, spread across nights

**What it buys and why.** The daily history of every name the exchange has delisted, one call each. A reconstructed walk runs today's members over yesterday's dates, so a name that traded in 2024 and was delisted in 2025 is absent from every night it actually traded on; buying its bars puts it in the same store as the survivors, where the walk finds it on the dates its bars say it traded. The reason it is bought now rather than later is that phases 5 and 6 are built and tested against stored history (see: Delisted daily history is bought so a reconstructed walk is not confined to survivors).

**Two verbs, and the order is not a preference.** `daily_bar` has a foreign key to `security`, so the names have to be recorded before their bars can be stored.

| Order | Command | Calls |
|---|---|---|
| 1 | `delisted-list` | 5 |
| 2 | `backfill --delisted` | one per name not yet fetched, until the ceiling stops it |

**It has been run and it is complete.** On 2026-08-31 the list recorded 15,998 names and all
15,998 were fetched, writing 736,190 bars for the 2,515 of them that traded inside the three-year
window. Nothing is outstanding. What is below is what to do when it is run again, which is what a
name delisted after that date needs.

**Size, measured rather than estimated.** The list returned 59,920 rows, of which 32,851 are common
stock and about 16,000 of those are on NASDAQ or NYSE. One call per ticker regardless of depth, so
the whole purchase is about 16,000 calls. Two bounds produce that and both are configuration rather
than code: the security type, which is the same filter the nightly universe uses, and
`Universe.DelistedExchanges`, which is the larger of the two. Covering every venue instead costs
about 17,000 more calls and buys the delisted history of places the current universe holds 30 names
on out of 2,005.

**How many nights it takes depends on the allowance, and the arithmetic here was wrong once.** It
read "about 4,197 spare calls a night, so about 3.8 nights", taking the spare from
`ARCHITECTURE.html`'s estimate that a night uses about 700. **A night measured from the run log
costs about 2,553**, of which `universe-build` alone is 2,005, so under the 5,000 allowance the
spare after an evening is about **2,450** and the purchase is about **six and a half nights**, not
four. The purchase was finished in one day instead, on the operator's instruction and against the
vendor's own headroom rather than the project's allowance.

**It is charged against the daily ceiling, unlike the one-time backfill above, and that is what spreads it.** It takes whatever the evening's stages left, stops on the budget rather than overrunning it, and the next night resumes from `history_refetch`. So it is run **after** the night's own slots, never before, and it is run again each night until `backfill --delisted` reports nought selected.

**"After the night's own slots" is the whole of it, and it was got wrong on the first run.** The
allowance is counted per **UTC** day and the schedule's first slot fires at 17:15 local, which is
21:15 UTC the **same** day. So a purchase run in the morning and an evening run eight hours later
share one allowance, and the morning one takes all of it. On 2026-08-31 that left `universe-build`
with nothing: it screened 9 of the 20 sessions it needs, found no survivors, and wrote a **carried**
snapshot that no rerun can replace, and `actions` fetched no splits or dividends at all until it was
rerun by hand. Run this after the evening, or on a day the evening does not need.

**What each night should print, and what to do if it does not.**

- `delisted-list: N delisted name(s), M of type Common Stock, K newly recorded`. K falls to nought once the list has been read; the run is still worth its five calls each night, because a name delisted today is new to the list tomorrow.
- `backfill --delisted: C delisted name(s) recorded, A already fetched on an earlier night`, then the usual selected/fetched line and `partial` or `clean` with the night's spend. **Partial is the expected outcome until the last night**: it means the ceiling stopped the run, not that anything failed.
- `C` at nought with `delisted-list` having recorded names means the lister did not run or its transaction did not commit. The fetch buys nothing in that case rather than failing on every insert, which is deliberate.
- The night's spend is in the run log like every other stage's, which is where "how much did this cost" is answered rather than from the console.

**It never makes a delisted name tradable.** The lister writes `security` and nothing else, so no delisted name reaches `universe_member`, a plan or an order. If one ever does, that is a defect in the universe builder rather than in this procedure.

---

## Daily operation

The nightly job is one CLI entrypoint per stage, invoked by Task Scheduler on Windows or launchd on macOS. The application holds no scheduling logic, which is what makes a failed 6pm stage easy to rerun by hand.

| Time (ET) | Stage | Calls |
|---|---|---|
| 10:15 | `spreads` `after_open`, the capped names in batches, at 1 call each. Past the opening auction, whose quotes describe an event rather than a name, and inside the first hour, where a trigger most often fires | 60 |
| 15:45 | `spreads` `before_close`, the same names again. Late enough to catch a book that widened through the day and outside the closing auction. **The two sessions this lab runs inside**, and the only slots here that fire while the market is open | 60 |
| 17:15 | `universe-build`, the symbol list and then the screening window, being one bulk end-of-day request per session until twenty sessions have been screened | ~2,005 |
| 17:20 | `actions`, splits bulk. One invocation covers both halves | 100 |
| 17:20 | `actions`, dividends bulk. Nightly since 2026-08-25: weekly left a stock computing for up to four sessions on a series that had already moved | 100 |
| 17:30 | `daily-bars`, the whole market in one bulk request | 100 |
| 17:45 | `backfill --rebuild`, one call per name carrying an open rebuild demand | ~25 |
| 17:50 | `index-bars`, one call a tracker | 3 |
| 18:00 | `indicators` | 0 |
| 18:10 | `scans`, then `tiers` for the ladder grade | 0 |
| 18:12 | `sectors`, resolved once per name and cached, **run twice**. Moved here from 19:00 on 2026-08-26: it ran after the three stages that read what it writes. The second pass retries the names the first could not read and costs nothing where there are none, which is what keeps a failure inside the window below | ~50 |
| 18:15 | `clusters`, then `regime` | 0 |
| 18:20 | `detect-long`, then `detect-short` | 0 |
| 18:25 | `vectorize`, the signal freeze, then `journal`, which seals the night | 0 |
| 18:26 | `controls`, loose and tight per flagged setup, before the cap | 0 |
| 18:28 | `cap`, the night truncated to sixty by rank | 0 |
| 18:30 | `plans`, one committed instruction per capped candidate: trigger, give-up point and a share count. PlanBuilder sizes and the size is the plan's; RiskGate may reduce or block it at trigger and never recomputes it. A candidate with no trade geometry gets no plan and the run row counts the refusal by reason. One plan per candidate today and per variant per candidate from 5.1 | 0 |
| 18:40 | `publish-watchlist`, which writes nothing. It reports what the page would show, so a night that was never capped is noticed here rather than by somebody opening a browser | 0 |
| 20:30 | `intraday-bars`, one request per distinct flagged name, at 5 calls each. Not the capped sixty: a variant that selects a name the baseline passed on must still be resolvable, and a name whose minutes were never bought is one no variant can ever be scored on | 220 to 415 |
| 21:00 | `vwap`, over the minutes the fetch stored half an hour earlier. It spends no vendor call: the anchored average is priced for every short setup whose swing the store can reach back to. An anchor out of reach is a row with a reason rather than a silence, and `anchors_asked` against `anchors_priced` is the state of the third ceiling clause on the night | 0 |
| 21:05 | `resolve-triggers`, the session walked one minute at a time over the minutes the fetch stored, deciding whether each plan resting in it was touched and in which minute. It spends no vendor call. One clock for the session rather than one per name, because the earliest trigger is what fills when the caps bind and that is a comparison across names. **A session with plans resting in it and no stored minute is reported partial rather than clean**: that is a night the lab was blind on, and a plan whose live session turns out to be a market holiday lands here as unresolvable with the reason rather than as a plan that did not fire | 0 |
| 21:10 | `orders`, the caps applied to each trigger in the order it happened. It spends no vendor call. Every refusal is a row with the cap that bound and what that cap saw, because a night on which three setups triggered into one free slot is evidence about the caps and is indistinguishable from a quiet night unless the refusals are stored | 0 |
| 21:15 | `fills`, over the orders the gate placed. It spends no vendor call. Each resting order is priced at what it actually got: the trigger plus the whole spread the session captured, the wrong way, or the open of the minute it would have filled in where that minute opened past the trigger, which costs nothing on top because the gap is the crossing. A name the session quoted no usable two-sided book for is **not filled**, and the row says which blindness it was rather than the order disappearing: a fill charged nought is a free entry that clears every threshold written as a maximum. **It runs no exit from 4.8**, because the exit is whichever rule is reached first and that comparison cannot be made by a stage that sees one side of it | 0 |
| 21:20 | `manage`, over every position open at any point in the session, including the ones the slot above opened five minutes earlier. It spends no vendor call. It runs the two rule sets and the give-up point together: the long trail on a daily close below the 9-day average, filling at the next open; the short trim of 15% of the planned size once at 3R, and the short exit on an hourly bar closing back above the 50-day average. **Neither rule takes over from the fixed stop**, so the exit is whichever is reached first and a tie inside one minute resolves as a give-up. A name the session quoted no book for is held rather than closed at a price nobody measured. The row counts each exit under the rule that produced it, and `closed_in_their_own_session` is what the caps could not see at 21:10 | 0 |
| 21:25 | `trades`, over the positions the slot above closed. It spends no vendor call. Each closed position becomes a trade stating its result in R **after** the borrow a short is charged, at the rate that position stamped on itself when it opened rather than at whatever the constant says tonight. `position.realised_r` is the same figure before that charge, so the two are equal on every long and differ by the borrow line on every short. A trimmed short's money is the trim's plus the close's, and its exit covered what the trim left | 0 |
| 21:26 | `audit`, over the trades the slot above wrote, because an audit points at one. It spends no vendor call. It holds the plan against what happened in three pairs, which are three different questions: the price each instruction named against the price it got, at both ends and in basis points; the plan's stop against where the trade actually ended, which is the same number only on a give-up exit; and the size the plan carried against the size the gate placed, with the cap that bound. **It changes no result**, and the ordering is what makes that so: the result was written before this ran | 0 |
| 21:30 | `forward-returns`, every flagged setup at 1, 3, 5 and 10 sessions | 0 |
| 21:35 | `losses`, after the forward returns because half of what it answers is one of them. It spends no vendor call. **Two passes, because the two answers arrive at different times.** The mechanism of every loss that closed tonight, read from the exit fill's basis: a gap is an exit that filled at an open already past the price it named, and everything else is ordinary. Then the aftermath of every earlier loss whose ten-session horizon has since closed, at +1R on the direction-signed return from the trigger: at or above it the stop-out was noise, below it the setup failed. **A row still waiting on a horizon carries no aftermath and is not `unclassified`**, which is what the horizon having closed with no figure looks like, and the two are counted apart on the night's row | 0 |
| 21:40 | variant scoring | 0 |
| 21:50 | `scoreboard`, the three bands, every panel with its own count | 0 |
| 22:00 | `snapshot-db`, the night's copy, which is the recovery path | 0 |
| **total** | | **~2,723 against a 5,000 ceiling** |

**`universe-build` was missing from this table until 2026-08-27 and it is the one row that cannot be recovered by rerunning tomorrow.** `UniverseSnapshotReader.Members` matches the snapshot date exactly and offers no fallback, deliberately: a stage that quietly read current membership on a night with no snapshot would produce a reconstructed answer indistinguishable from a real one. So a night without this stage flags nothing, and the run reports **clean** while recording it. Every other row here can be rerun for its date; a delisted name is simply absent from tomorrow's symbol list, so a missing snapshot is a permanent hole in the evidence (see: The evidence store holds only setups flagged forward, never setups reconstructed from history).

**`universe-build` cost `~5` in this table until 3.10 and it costs `~2,005`.** The row named the symbol list and stopped there, and the stage does the symbol list and then the screening window: `BulkEndOfDayCost` is 100 and `LiquidityWindowSessions` is 20, which `UniverseBuilderTests` has asserted as `Assert.Equal(2005, result.CallsUsed)` since it was written. A holiday inside the forty-five day search costs a further 100 each, so the real range for the stage is 2,005 to 3,205 and the night is 2,803 to 4,003. **The headroom is under twice expected usage, not the seven times the configuration comment claimed.** A holiday week plus a large `backfill --rebuild` reaches the ceiling, and `daily-bars` runs after `universe`, so the stage that stops short is the one that stores the night's bars.

The job counts calls as it goes and stops rather than overrunning the ceiling. A stopped job writes a partial-run row and the affected setups are marked degraded.

**The ceiling's unit is the weighted cost, not the number of requests, and the two differ by up to a hundredfold.** One fundamentals lookup is one request and one call. One whole-market bulk request is one request and a hundred calls, because that is what it replaces. Every stage prints both figures, so a line reading "15 request(s), 15 call(s)" and one reading "1 request(s), 100 call(s)" are both readable against the same 5,000, and neither can be mistaken for the other. The per-endpoint costs are ARCHITECTURE's data budget table.

### The schedule as installed

Registered on 2026-08-27 on the Windows machine. Seventeen tasks named `PullbackStrategyLab-<slot>`,
each running `tools/nightly.ps1 -Slot <slot>`, weekdays for the nightly slots and Saturday 08:00 for
`ceiling`. The machine's own timezone is Eastern, so the table's ET times are its local times and no
conversion is involved; a machine in another zone converts them.

`tools/nightly.ps1` maps a slot to the verbs that slot runs and nothing else. It addresses the store
by absolute path, because `DataRoot` resolves through the working directory and a scheduled task's
working directory is not something anybody should have to reason about at three in the morning. It
logs to `<data root>/logs/nightly-YYYY-MM-DD.log` and records the commit it ran from on every line of
that log's first entry, since the job runs from a working tree and what it executes changes when the
branch does.

**The ref the job runs from is `main`, and as of 4.2 the script refuses to run from anything else.**
It is still not configured anywhere: the slot script runs whatever the working tree is checked out
to. What changed is that it now reads the ref before it dispatches and exits 4 without running a
stage when the tree is not on `main`, printing which branch it found. `-AllowBranch` is the escape,
and it is the reason the guard is safe to have: a phase that merges leaves the tree on a branch for
as long as the merge takes, and a guard with no way through would stop the night's accumulation for
exactly that window. That is why the first attempt at this, written at 3.6, was removed rather than
given a switch.

**The log stays and is no longer the only thing.** The first line of every night's entry still names
the branch and the commit, and a night that ran from something else still says so in its own record.
What the three instances below establish is that nobody reads it: the branch was recorded on each
occasion and read on none, and the third was found by a review typing `git status`. A refusal exits
non-zero, which the scheduler surfaces, where a line in a file surfaces nothing.

**It has now been on a branch three times, the second cost a night, and the third happened inside
the pass that closed the second.** Before `6f27926` the job ran from `phase-3-corrections`. Every
slot of 2026-08-28 then ran from `phase-3-verification-repair`, at six different commits during one
night, and that is the night the lab flagged nothing
(see: A phase branch merges on CI green, and the sign-off reviews what is already on the default branch).
The third is the one worth reading twice: the 3.12 sign-off closed "production running from a branch"
by returning the tree to `main`, then created `phase-3-signoff` and committed the closure onto it, so
the tree was on a branch again in the act of recording that it was not. It stayed there from
13:32Z until PR #8 merged at 14:26Z, which is inside one day and outside no slot, and it was found by
a review reading `git status` rather than by anything the corpus runs.
The merge rule was moved to CI green alone precisely so production would not run from a branch, and
between the rule and the checkout there is nothing but somebody remembering. Anyone leaving the tree
on a branch overnight is choosing which code runs the night, and the way to undo it is
`git checkout main` in the repository the tasks point at.

**They run only while the user is logged on, and that is a real limitation rather than a preference.**
Registering a task that runs whether or not anybody is logged on needs either an elevated shell, to
set an S4U principal, or a stored password. Neither was available when these were created, so a
logged-out evening is a lost night, and a lost night's universe snapshot is the one thing that cannot
be recovered by rerunning tomorrow. Raised as an obligation.

**To remove or re-create them:** `Get-ScheduledTask -TaskName 'PullbackStrategyLab-*'`, then
`Unregister-ScheduledTask`. On macOS they become launchd definitions, which is step 7 of the move.

### Every morning

Open the watchlist. Check the status band: **the store at the schema the build needs**, run clean, calls within budget, positions and risk within caps. If the run is marked degraded, that night is excluded from variant scoring automatically and nothing needs doing.

**The schema pair reads `schema 30 of 32` when the store is behind**, with a line above the band saying what will fail and what to run. It is first in that list because it is the one fault that stops the night producing anything at all rather than degrading what it produces, and because the band is where it becomes visible: on 2026-08-28 the number was on every page all night with nothing beside it to read it against.

**What the morning read can and cannot repair after a stage died part-way through its list** is worth knowing here rather than discovering it. A sector resolved this morning is *late* for last night's session, because every reader bounds on when the lookup was made. It is admitted only where the session itself asked for it and it arrived inside the lateness bound, and where it is admitted the row carries how late it was. The slot already retries within its own window, and "The repair window" under Recovery says what is left if that was not enough.

### Every week

Run `ceiling`, which recomputes the win-rate bound per direction over every setup whose tenth
session has closed. Weekly rather than nightly on purpose: the bound moves with the population
rather than with a session, and a figure recomputed every night over one more row than yesterday
invites reading noise as movement. It makes no vendor call and a week recomputed leaves earlier
weeks standing, because the gap narrowing over time is the thing worth looking at.

Open the scoreboard. Band 1 is the one that matters. If the tight-control comparison has been flat for a quarter, that is the project's answer and it is worth taking seriously rather than waiting for it to improve.

**Band 1 states both halves of the decision point's trigger and neither substitutes for the other.** Each panel reads `n <rows> rows, <effective> effective of 1802 needed, over <sessions> session(s) of 20 needed`, and below it either the sentence saying both conditions are reached or the one naming what it is short of. Sessions are what the block bootstrap needs before an interval exists at all; effective observations are what the decision needs before the interval means anything. A fortnight of very wide nights reaches the second before the first, and a year of thin ones does the reverse, so the panel says which one is holding rather than leaving it to be worked out.

### After a merge that carries a migration

**Run `tools/migrate` against the live store, the same day, before the next nightly slot.** A merge moves the checkout the schedule runs from; it does not touch the store. Every stage but `migrate`, `snapshot-db` and `list-stages` refuses before opening a store whose version is not the build's, in both directions, so the mismatch is loud rather than silent. It still costs the night: a refusing stage writes nothing and the stages after it read what it should have written.

**The order is merge, then migrate, and not the other way round.** A store migrated ahead of the checkout is refused on the same footing as one behind it, because an older binary reading a migrated store reads columns whose meaning has moved. So migrating before the merge lands buys the same lost night from the other side.

This is here because it has happened: migrations 031 and 032 landed on 2026-08-28, the live store was never migrated, four stages died and the lab flagged nothing. Nothing in the verification harness can catch it, because every check in this project takes its subject from the source, the documents, the golden fixture, or a store the check itself builds, and the running lab is in none of those.

---

## Recovery

| Symptom | Do this |
|---|---|
| A nightly stage failed | Rerun that stage alone. Every stage is idempotent for its date |
| A stage failed naming a column the store has not got | The store is behind its migrations. Every stage but `migrate`, `snapshot-db` and `list-stages` now refuses before opening the store and names both versions, so this is what the refusal looks like from the other side. Run `tools/migrate`, which snapshots first, then **rerun that night's stages for their own date**, in slot order: a stage that refused wrote nothing, and the stages after it read what it should have written. On 2026-08-28 four stages died this way and the night flagged nothing at all |
| A stage walked part of its list and stopped | Read the slot's log. A stage that stops names why on its own line, and the run entry says `partial` or `failed` with `skipped` counting the names it passed over. Rerun the stage for its date; a name left unstamped is asked again. **Then read the paragraph below about the window**, because rerunning tomorrow does not repair tonight |
| Vendor returned bad or partial data | Do not delete anything. Re-ingest; the later `observed_at` wins on read |
| A corporate action was missed | Rerun `actions` for that date, with `--with-dividends` if a dividend is what was missed. It writes the action and raises the rebuild demand, and until that demand is satisfied, calculations for that ticker refuse to run. No other ticker is touched |
| Database will not open | Restore the most recent snapshot from the data root, then re-run the nightly stages for the missing dates in order |

**The lab keeps the last 7 snapshots and removes the rest, which bounds what a restore can reach back to.** There was no retention until 3.11 and twenty-four had accumulated in four days, 4.6 GB against a store holding one session of setups and growing about 290 MB a night. Seven is a week, which covers a fault found on a Monday that happened before the weekend, and it is `PullbackStrategyLab:SnapshotsKept` if a machine wants a different number.

**Removal happens only after the new snapshot has passed both its checks**, the row counts matching and `integrity_check` answering ok, so a short disk cannot cost a week of recovery points. Each removal is named in the night's log.

**To keep one past the window, rename it.** Retention only ever deletes files matching the name the lab generates, `pullbackstrategylab-YYYYMMDD-HHMMSS.db`, so a copy renamed to anything else, `before-the-4.1-migration.db` say, survives indefinitely and is invisible to the policy.
| `git` permission error mid-commit on Windows | Run `git fsck` before retrying. Usually a real-time scanner or file indexer holding a handle on a loose object. Add a scanner exclusion for the repository folder |
| Researcher produced nothing | Check whether the usage allowance is exhausted. The job queues rather than returning a degraded proposal, and this is expected behaviour |

Snapshots are taken before every migration and nightly. They are the recovery path; there is no other.

### The repair window, which has two edges and closes 24 hours after the session's own end of day

A stage that dies part-way through its list leaves the stages after it reading a store it half filled, and one of those consequences cannot be repaired the next morning.

**Why there is a window at all, and where its two edges are.** Every reader in the lab bounds a lookup on when it was made, and that bound is the last instant of the session's own day **in Eastern time**: `2026-08-28T03:59:59.999Z` for the session of the 27th, which is 23:59:59 Eastern. **That instant is the origin every lateness figure in this lab is measured from**, and it is not the instant the stage that failed ran at; the two differ by the length of the evening, which is how one event comes to have two plausible-looking numbers. Inside the first edge a repair needs no mark at all. Past it, an answer the session itself asked for is still admitted for a further 24 hours and the row records how late it was, in minutes; past that it is refused (see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound). The `sectors` slot runs at 18:12, so a failure there leaves five hours forty-eight before the first edge and a further day before the second, and those figures no longer move with the clock change.

**So the first line of defence is not a person.** The `sectors` slot runs the stage twice. A name the vendor refused or answered unreadably is counted and left unstamped, so the second pass asks exactly those and costs one call each; where the first pass finished, the second finds nothing and costs nothing. That happens at 18:12, inside the window, without anybody watching.

**If a check verdict was recorded with no value anyway**, `recheck --as-of <date> --check cluster` reports what it would correct and writes nothing, and `--apply` writes it. It refuses any gating check outright, refuses a verdict that already carries a number, refuses a row already corrected, and refuses any row whose input arrived more than the lateness bound after the session's own end of day, naming both instants and exiting non-zero. A corrected row records `corrected_at`, `corrected_because`, `corrected_check`, the lateness in minutes and the check results exactly as they stood before, so a later reader can exclude corrected rows, sum how much of a figure rests on late answers, and put any row back the way it was.

**The arguments go in any order and `--as-of` is the form to write.** A bare date still works, so `recheck <date> --check cluster` parses as it always did, and the two forms must agree if both are given. This is worth a sentence because until 3.13 it was false: the date was whatever argument was neither a flag nor the check's own name, so `recheck --check cluster --expect 15 2026-08-27` read `15` as the date and exited on the format. Three of the four orderings anybody would write did that, and the one this line documented was the one that happened to work. Every flag now declares whether it takes a value, an option the stage does not know is refused by name rather than ignored, and `--restore` puts back only the rows corrected for the check named.

**The count it recomputes is taken over the night's whole scan population**, not over the rows being repaired. That matters because a count over the repaired set would make every figure it produces an artefact of how many rows happened to be broken, and two of the fifteen came back failing at a cluster of one, which is exactly the number that shape would produce. A scan name with no setup row at all is counted, which is the form of the property no reading of "the repaired set" can produce.

**And when the window has closed, it has closed.** On 2026-08-27 the sector walk died on its 149th name and fifteen setups recorded a cluster verdict of failed with no value. Fifteen of **forty-four**, and forty-four is every setup that night's detectors flagged, forty long and four short; it is the denominator for the fifteen and for the `passed_all` count that did not move. The names were resolved at `2026-08-28T04:19:33.201Z`, which is 00:19 Eastern: about six hours after the walk died and **20 minutes after that session's own end of day**. Lateness is the second of those, so all fifteen were inside the 24-hour bound, were repaired, and carry the mark. Had the rerun waited a further day, `recheck` would have refused all fifteen and they would have kept that verdict permanently. There is no command for that case and there should not be one: the alternative is reading a value back into a night that did not have it, which is the thing the whole point-in-time rule exists to stop. What is owed instead is a decision about whether a lazily-resolved attribute carries a validity date separate from its lookup instant, and that is carried as an obligation. **What the column holds since is three populations, and every run of `recheck` says so.** The fifteen carry a count over the 234 scan names the recompute saw; twenty-eight of the other twenty-nine carry what `clusters` computed at 18:15 over the 148 the walk had reached; and one, `INFQ`, at 4 against 6 and 13, matches neither. So a figure stated over that night's `cluster` column is stated as that split and never as one number (see: Long and short are never pooled into one figure), and the stage prints the split on every run, dry or applied: rows at the count the whole population gives, rows at another value with both figures named, rows without a value, and rows whose whole-population count cannot be formed. It still touches none of the twenty-nine, because each carries a value and a value is a measurement the night made; whether a verdict computed from a partial input may be revisited has not been ruled on and is not ruled on here.

`VACUUM INTO` writes a full copy each time, so the backup total grows faster than the store: thirty nightlies in year three is around 70 GB against a 2.5 GB database. Keep a short rolling window of nightlies plus one monthly, and prune rather than accumulating.

---

## Moving the store to another machine

The SQLite file format is architecture and OS independent, so the database is portable with no conversion. All the care is in how it is copied.

**The failure this avoids.** Copying a live database gives a torn file, and write-ahead logging means the most recent writes live in a `-wal` sibling. Copying only the `.db` loses data silently and produces a store that opens cleanly and is missing several nights.

| # | Step | Detail |
|---|---|---|
| 1 | Stop the worker | Confirm no stage is mid-run. A partial nightly leaves setups without their signal rows |
| 2 | Record source counts | A row count for every table the store holds, derived from the schema rather than from a list here, **taken before anything is copied** or there is nothing to compare against. `tools/snapshot-db` does it: a list in this document goes stale at the migration that adds a table, and a count that silently omits one is the failure this step exists to catch |
| 3 | Write one clean file | `VACUUM INTO '/path/pullbackstrategylab-migrate.db';` Folds in the log, drops free pages, leaves no siblings to forget |
| 4 | Copy bar files too | If bars are outside the database, copy that directory alongside |
| 5 | Verify on arrival | `PRAGMA integrity_check;` then re-run the step 2 counts and compare. `tools/snapshot-db` does both against the copy and exits non-zero on either. Integrity check proves the file is not corrupt, **not** that it is complete. Both are needed |
| 6 | Copy the secrets file | `appsettings.Secrets.json` is gitignored, so it does not arrive with the repository. Copy it deliberately and separately from the store, or re-create it. Verify by running one stage that makes a vendor call rather than assuming |
| 7 | Recreate the schedule | Task Scheduler entries become launchd definitions |
| 8 | Repoint config paths | There should be none inside the database itself |
| 9 | Run one nightly job and look at it | Not "it did not throw". Open the watchlist, confirm the setups resemble the previous night's and that session boundaries landed correctly |
| 10 | Retire the source | Archive read-only |

**One machine is authoritative, permanently.** After the move the new machine owns the store. Running the nightly job on both produces two stores that diverge from the first night and cannot be merged, because setup ids are assigned independently and the same id will refer to different stocks. If both machines must run, one works against a read-only copy and never executes a job that writes.

**Rehearse this at checkpoint 1.11,** when the store holds a week of data and losing it costs nothing.

---

## Things that are expected and need no action

- A degraded night. Excluded from scoring automatically.
- A variant open for months without resolving. There is no timeout on purpose; a timeout is a decision made by the calendar rather than by evidence.
- The researcher abstaining. A valid outcome, recorded as such.
- Zero setups on a night. In a market where nothing holds its ladder, the correct output is nothing.
- Holdout windows running out. A designed dead end. The forward channel remains.
