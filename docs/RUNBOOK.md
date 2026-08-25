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

The only thing depending on N is whether the backfill runs in one day or two: steps 4 and 5 cost 2N together, so with the other steps the day fits comfortably while 2N stays under about 4,000. Above that, split steps 4 and 5 across two days. Nothing else in the design is sensitive to the count, which is why no figure for it is written down anywhere.

| Order | Job | Calls |
|---|---|---|
| 1 | Symbol list | ~5 |
| 2 | Bulk end-of-day, last 20 sessions, whole market | ~2,000 |
| 3 | Apply the price and liquidity floors. Survivor count **N**, measured not assumed | 0 |
| 4 | Full daily history for the survivors, one call each, any depth | N |
| 5 | Full split history for the survivors | N |
| 6 | Minute bars for 200 names to calibrate the fill model | ~1,000 |
| | **Total** | **~3,005 + 2N** |

Steps 1 to 3 are one day, steps 4 to 6 the next. Nothing downstream depends on doing them together, and splitting keeps each day well inside the ceiling.

---

## Daily operation

The nightly job is one CLI entrypoint per stage, invoked by Task Scheduler on Windows or launchd on macOS. The application holds no scheduling logic, which is what makes a failed 6pm stage easy to rerun by hand.

| Time (ET) | Stage | Calls |
|---|---|---|
| during session | spread snapshots, two passes | 120 |
| 17:20 | `actions`, splits bulk | 100 |
| 17:20 | `actions --with-dividends`, weekly rather than nightly. 100 a week over five sessions is the 20, which is an amortised figure and was never the price of a call | 20 |
| 17:30 | bulk daily bars | 100 |
| 17:45 | `backfill --rebuild`, one call per name carrying an open rebuild demand | ~25 |
| 18:00 | `indicators` | 0 |
| 18:10 | scans, ladder grade | 0 |
| 18:15 | cluster, regime | 0 |
| 18:20 | detectors, both directions | 0 |
| 18:25 | signal freeze, journal | 0 |
| 18:26 | control sampling | 0 |
| 18:28 | cap | 0 |
| 18:30 | plans per variant | 0 |
| 18:40 | publish watchlist | 0 |
| 19:00 | sector resolve for new names | ~50 |
| 20:30 | minute bars for flagged setups | 300 |
| 21:00 | session replay, fills, positions | 0 |
| 21:30 | forward returns | 0 |
| 21:35 | loss classification | 0 |
| 21:40 | variant scoring | 0 |
| 21:50 | scoreboard | 0 |
| **total** | | **~715 against a 5,000 ceiling** |

The job counts calls as it goes and stops rather than overrunning the ceiling. A stopped job writes a partial-run row and the affected setups are marked degraded.

### Every morning

Open the watchlist. Check the status band: run clean, calls within budget, positions and risk within caps. If the run is marked degraded, that night is excluded from variant scoring automatically and nothing needs doing.

### Every week

Open the scoreboard. Band 1 is the one that matters. If the tight-control comparison has been flat for a quarter, that is the project's answer and it is worth taking seriously rather than waiting for it to improve.

---

## Recovery

| Symptom | Do this |
|---|---|
| A nightly stage failed | Rerun that stage alone. Every stage is idempotent for its date |
| Vendor returned bad or partial data | Do not delete anything. Re-ingest; the later `observed_at` wins on read |
| A corporate action was missed | Rerun `actions` for that date, with `--with-dividends` if a dividend is what was missed. It writes the action and raises the rebuild demand, and until that demand is satisfied, calculations for that ticker refuse to run. No other ticker is touched |
| Database will not open | Restore the most recent snapshot from the data root, then re-run the nightly stages for the missing dates in order |
| `git` permission error mid-commit on Windows | Run `git fsck` before retrying. Usually a real-time scanner or file indexer holding a handle on a loose object. Add a scanner exclusion for the repository folder |
| Researcher produced nothing | Check whether the usage allowance is exhausted. The job queues rather than returning a degraded proposal, and this is expected behaviour |

Snapshots are taken before every migration and nightly. They are the recovery path; there is no other.

`VACUUM INTO` writes a full copy each time, so the backup total grows faster than the store: thirty nightlies in year three is around 70 GB against a 2.5 GB database. Keep a short rolling window of nightlies plus one monthly, and prune rather than accumulating.

---

## Moving the store to another machine

The SQLite file format is architecture and OS independent, so the database is portable with no conversion. All the care is in how it is copied.

**The failure this avoids.** Copying a live database gives a torn file, and write-ahead logging means the most recent writes live in a `-wal` sibling. Copying only the `.db` loses data silently and produces a store that opens cleanly and is missing several nights.

| # | Step | Detail |
|---|---|---|
| 1 | Stop the worker | Confirm no stage is mid-run. A partial nightly leaves setups without their signal rows |
| 2 | Record source counts | `setup`, `setup_signal`, `forward_return`, `trade`, `variant`, plus max setup id. **Written down before anything is copied**, or there is nothing to compare against |
| 3 | Write one clean file | `VACUUM INTO '/path/pullbackstrategylab-migrate.db';` Folds in the log, drops free pages, leaves no siblings to forget |
| 4 | Copy bar files too | If bars are outside the database, copy that directory alongside |
| 5 | Verify on arrival | `PRAGMA integrity_check;` then re-run the step 2 counts and compare. Integrity check proves the file is not corrupt, **not** that it is complete. Both are needed |
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
