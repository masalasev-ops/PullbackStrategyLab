# RUNBOOK.md

Operator procedures. How to set the lab up, run it, move it and recover it. Written for the person at the keyboard, not for the build session.

---

## First-time setup

1. Install the .NET SDK. Confirm `dotnet --info` reports the arm64 runtime on Apple Silicon.
2. Clone, then `dotnet restore` and `dotnet build`.
3. Create the data root outside the repository and outside any synced folder. Set `PullbackStrategyLab:DataRoot` to it. **Never place the store inside OneDrive, Dropbox or iCloud.** A sync client copying an open database mid-write is a real corruption risk, not a theoretical one.
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

---

## Daily operation

The nightly job is one CLI entrypoint per stage, invoked by Task Scheduler on Windows or launchd on macOS. The application holds no scheduling logic, which is what makes a failed 6pm stage easy to rerun by hand.

| Time (ET) | Stage | Calls |
|---|---|---|
| during session | spread snapshots, two passes | 120 |
| 17:15 | `universe-build`, the symbol list and the nightly snapshot of who was listed | ~5 |
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
| 18:30 | plans per variant | 0 |
| 18:40 | publish watchlist | 0 |
| 20:30 | minute bars for flagged setups | 300 |
| 21:00 | session replay, fills, positions | 0 |
| 21:30 | `forward-returns`, every flagged setup at 1, 3, 5 and 10 sessions | 0 |
| 21:35 | loss classification | 0 |
| 21:40 | variant scoring | 0 |
| 21:50 | `scoreboard`, the three bands, every panel with its own count | 0 |
| 22:00 | `snapshot-db`, the night's copy, which is the recovery path | 0 |
| **total** | | **~803 against a 5,000 ceiling** |

**`universe-build` was missing from this table until 2026-08-27 and it is the one row that cannot be recovered by rerunning tomorrow.** `UniverseSnapshotReader.Members` matches the snapshot date exactly and offers no fallback, deliberately: a stage that quietly read current membership on a night with no snapshot would produce a reconstructed answer indistinguishable from a real one. So a night without this stage flags nothing, and the run reports **clean** while recording it. Every other row here can be rerun for its date; a delisted name is simply absent from tomorrow's symbol list, so a missing snapshot is a permanent hole in the evidence (see: The evidence store holds only setups flagged forward, never setups reconstructed from history).

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

**The ref the job runs from is `main`, as of 2026-08-28.** It is not configured anywhere: the slot
script runs whatever the working tree is checked out to, and the tree is on `main` because
`6f27926` merged the correction pass and the post-pass. Nothing enforces this and nothing should
pretend to, so the check is the log: the first line of every night's entry names the branch and the
commit, and a night that ran from something else says so in its own record. Before the merge the job
ran from `phase-3-corrections`, which is what the merge rule was changed to stop
(see: A phase branch merges on CI green, and the sign-off reviews what is already on the default branch).
Anyone leaving the tree on a branch overnight is choosing which code runs the night, and the way to
undo it is `git checkout main` in the repository the tasks point at.

**They run only while the user is logged on, and that is a real limitation rather than a preference.**
Registering a task that runs whether or not anybody is logged on needs either an elevated shell, to
set an S4U principal, or a stored password. Neither was available when these were created, so a
logged-out evening is a lost night, and a lost night's universe snapshot is the one thing that cannot
be recovered by rerunning tomorrow. Raised as an obligation.

**To remove or re-create them:** `Get-ScheduledTask -TaskName 'PullbackStrategyLab-*'`, then
`Unregister-ScheduledTask`. On macOS they become launchd definitions, which is step 7 of the move.

### Every morning

Open the watchlist. Check the status band: run clean, calls within budget, positions and risk within caps. If the run is marked degraded, that night is excluded from variant scoring automatically and nothing needs doing.

**What the morning read can and cannot repair after a stage died part-way through its list** is worth knowing here rather than discovering it. A sector resolved this morning is *late* for last night's session, because every reader bounds on when the lookup was made. It is admitted only where the session itself asked for it and it arrived inside the lateness bound, and where it is admitted the row carries how late it was. The slot already retries within its own window, and "The repair window" under Recovery says what is left if that was not enough.

### Every week

Run `ceiling`, which recomputes the win-rate bound per direction over every setup whose tenth
session has closed. Weekly rather than nightly on purpose: the bound moves with the population
rather than with a session, and a figure recomputed every night over one more row than yesterday
invites reading noise as movement. It makes no vendor call and a week recomputed leaves earlier
weeks standing, because the gap narrowing over time is the thing worth looking at.

Open the scoreboard. Band 1 is the one that matters. If the tight-control comparison has been flat for a quarter, that is the project's answer and it is worth taking seriously rather than waiting for it to improve.

---

## Recovery

| Symptom | Do this |
|---|---|
| A nightly stage failed | Rerun that stage alone. Every stage is idempotent for its date |
| A stage walked part of its list and stopped | Read the slot's log. A stage that stops names why on its own line, and the run entry says `partial` or `failed` with `skipped` counting the names it passed over. Rerun the stage for its date; a name left unstamped is asked again. **Then read the paragraph below about the window**, because rerunning tomorrow does not repair tonight |
| Vendor returned bad or partial data | Do not delete anything. Re-ingest; the later `observed_at` wins on read |
| A corporate action was missed | Rerun `actions` for that date, with `--with-dividends` if a dividend is what was missed. It writes the action and raises the rebuild demand, and until that demand is satisfied, calculations for that ticker refuse to run. No other ticker is touched |
| Database will not open | Restore the most recent snapshot from the data root, then re-run the nightly stages for the missing dates in order |
| `git` permission error mid-commit on Windows | Run `git fsck` before retrying. Usually a real-time scanner or file indexer holding a handle on a loose object. Add a scanner exclusion for the repository folder |
| Researcher produced nothing | Check whether the usage allowance is exhausted. The job queues rather than returning a degraded proposal, and this is expected behaviour |

Snapshots are taken before every migration and nightly. They are the recovery path; there is no other.

### The repair window, which has two edges and closes 24 hours after the session's own end of day

A stage that dies part-way through its list leaves the stages after it reading a store it half filled, and one of those consequences cannot be repaired the next morning.

**Why there is a window at all, and where its two edges are.** Every reader in the lab bounds a lookup on when it was made, and that bound is the last instant of the session's own day **in Eastern time**: `2026-08-28T03:59:59.999Z` for the session of the 27th, which is 23:59:59 Eastern. **That instant is the origin every lateness figure in this lab is measured from**, and it is not the instant the stage that failed ran at; the two differ by the length of the evening, which is how one event comes to have two plausible-looking numbers. Inside the first edge a repair needs no mark at all. Past it, an answer the session itself asked for is still admitted for a further 24 hours and the row records how late it was, in minutes; past that it is refused (see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound). The `sectors` slot runs at 18:12, so a failure there leaves five hours forty-eight before the first edge and a further day before the second, and those figures no longer move with the clock change.

**So the first line of defence is not a person.** The `sectors` slot runs the stage twice. A name the vendor refused or answered unreadably is counted and left unstamped, so the second pass asks exactly those and costs one call each; where the first pass finished, the second finds nothing and costs nothing. That happens at 18:12, inside the window, without anybody watching.

**If a check verdict was recorded with no value anyway**, `recheck <date> --check cluster` reports what it would correct and writes nothing, and `--apply` writes it. It refuses any gating check outright, refuses a verdict that already carries a number, refuses a row already corrected, and refuses any row whose input arrived more than the lateness bound after the session's own end of day, naming both instants and exiting non-zero. A corrected row records `corrected_at`, `corrected_because`, the lateness in minutes and the check results exactly as they stood before, so a later reader can exclude corrected rows, sum how much of a figure rests on late answers, and put any row back the way it was.

**The count it recomputes is taken over the night's whole scan population**, not over the rows being repaired. That matters because a count over the repaired set would make every figure it produces an artefact of how many rows happened to be broken, and two of the fifteen came back failing at a cluster of one, which is exactly the number that shape would produce. A scan name with no setup row at all is counted, which is the form of the property no reading of "the repaired set" can produce.

**And when the window has closed, it has closed.** On 2026-08-27 the sector walk died on its 149th name and fifteen setups recorded a cluster verdict of failed with no value. Fifteen of **forty-four**, and forty-four is every setup that night's detectors flagged, forty long and four short; it is the denominator for the fifteen and for the `passed_all` count that did not move. The names were resolved at `2026-08-28T04:19:33.201Z`, which is 00:19 Eastern: about six hours after the walk died and **20 minutes after that session's own end of day**. Lateness is the second of those, so all fifteen were inside the 24-hour bound, were repaired, and carry the mark. Had the rerun waited a further day, `recheck` would have refused all fifteen and they would have kept that verdict permanently. There is no command for that case and there should not be one: the alternative is reading a value back into a night that did not have it, which is the thing the whole point-in-time rule exists to stop. What is owed instead is a decision about whether a lazily-resolved attribute carries a validity date separate from its lookup instant, and that is carried as an obligation.

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
