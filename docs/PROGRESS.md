# PROGRESS.md

The log. Append only. Nothing here is struck through: a correction is a new dated entry that names what it corrects and states the right value.

One entry per checkpoint, plus an entry for anything measured, found or decided along the way.

## Entry format

```
## <checkpoint> — <date> — <commit>
Built:      what exists now that did not before
Measured:   numbers, with how they were obtained
Verified:   which command, test count, coverage
Findings:   observation and reading kept separate
Carried:    obligations, each naming the checkpoint it falls due at
```

**Observation and reading stay separate.** "The nightly count is 14" is an observation. "The daily-range floor is too strict" is a reading. Recording them as one sentence is how a reading survives into later sessions as though it were a measurement.

**A measurement records how it was obtained.** A figure that came from a conversation rather than from a run is labelled as such, or it will be cited later as though it had been measured.

**Green means nothing I ran failed.** A checkpoint entry states its coverage, not only its result.

---

(the corpus was authored and reviewed before `git init`, and the same reasoning applies here as in `CHANGELOG.md`: drafting history of text no session ever read is noise on day one. The log starts at checkpoint 1.1.)

## 1.1 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `PullbackStrategyLab.sln` and the six projects under `/src`, all namespaced in full.
            `Directory.Build.props` pins net10.0, `InvariantGlobalization=false`, nullable and warnings-as-errors in one place.
            Core: `IClock` with `UtcNow`, zone conversion and session-boundary resolution by IANA identifier, rejecting Windows identifiers rather than translating them; the configuration record; `PullbackStrategyLabPaths` composing every path from one data root.
            Data: `StoreConnectionFactory` setting the four pragmas at open, a hand-written SQL migration runner versioned on SQLite's own `user_version`, `StoreText` as the one named crossing between decimal-in-code and TEXT-in-storage, and `RunLogger` as sole writer of `run_log` on both operations.
            Worker: one CLI entrypoint per job, with `migrate` and `snapshot-db`. Api: read-only store access and `/health`. Web: a Razor Pages host that reads through the Api and has no reference to Data.
            `tools/ci.ps1`, `tools/ci.sh`, `tools/migrate`, `tools/snapshot-db`. The GitHub Actions matrix on `windows-latest` and `macos-latest`, from this commit.
            Eight checks as named CI steps: decision-resolves, no-superseded-citation, stated-counts, pinned-constants, path-casing, writer-ownership, api-isolation, ci-parity.

Measured:   `tools/ci.ps1` green on Windows, 13 steps, 22 tests. Obtained by running it; the macOS half of the matrix has not run yet, so the two-platform check is unconfirmed rather than passed.
            Check coverage, read from `artifacts/checks/*.json` after that run:
            decision-resolves examined 152 (55 decision names, 45 citations, 32 files), unexamined 0.
            no-superseded-citation examined 49, unexamined 0, and vacuous: nothing has been superseded yet.
            stated-counts examined 17 claims, unexamined 0 within the registry.
            pinned-constants examined 16, unexamined 29 authored parameters that have no code constant yet.
            path-casing examined 522 (408 string literals, 8 naming a repository path, 8 compared byte for byte), unexamined 0.
            writer-ownership examined 57 (34 stores declared, 1 created by a migration, 18 source files, 2 writes found, 2 declared writers of a live store), unexamined 46.
            api-isolation examined 12 libraries in the compiled dependency file, unexamined 0.
            ci-parity examined 26, being 13 steps in each script.

Verified:   `dotnet build PullbackStrategyLab.sln`, then `tools/ci.ps1` against a dropped store. Both config properties pinned: an environment variable overrides a value present in `appsettings.Secrets.json`, and a host starts cleanly with no secrets file on disk. A third test asserts the provider list is exactly the three sources in the stated order, because a host builder installs several of its own and one left in place would change which value wins.

Findings:   Observation. `docs/ARCHITECTURE.html` read "The that check filter excludes the names most likely to be hard to borrow". Reading: a casualty of the sweep that replaced check codes with names, which left the sentence pointing at nothing. Corrected to name the `tradable-shortable` check, prior text in `CHANGELOG.md`.
            Observation. `Microsoft.Data.Sqlite` 10.0.0 resolves `SQLitePCLRaw` 2.1.11 transitively, which carries a published advisory against the native library, and warnings are errors. Pinned to 2.1.13 explicitly in `PullbackStrategyLab.Data.csproj`.
            Observation. `dotnet new sln` produces `.slnx` by default on the .NET 10 SDK. The solution was created with `-f sln`, because the commands table and the build plan are written against `PullbackStrategyLab.sln`.
            Observation. `writer-ownership` can examine 2 of 48 declared writers today. Reading: that is the expected shape at 1.1 and the coverage line is what keeps it visible; the number rises as each checkpoint creates its stores.

Carried:    Expectations for 1.1 to 1.6 owed to the golden fixture with their tier, due at 1.7. Recorded in `BUILD_PLAN.md`'s carried obligations table when it was created.
            The two-platform check is unconfirmed until the macOS runner has run. Due at 1.2, which is the checkpoint whose deliverable is the clock proven on both platforms.
            The daily call budget counts against the UTC date. Whether the vendor's own quota resets on that boundary is assumed rather than confirmed, and is confirmed at 1.3 when the first real call is made.

## 1.2 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `ClockTests`, proving the clock on whichever platform the suite runs on. `America/New_York` resolves and sits behind UTC by more than nothing and at most a day, with both bounds read from the clock: reading one from the clock and the other from `DateTime` would prove the two agree rather than that the zone resolved, and it is the resolution that fails when IANA lookup is unavailable.
            Session boundaries asserted on both sides of the daylight transition, plus the hour the zone skips in March and the hour it repeats in November, since those are the two cases where a framework default would differ from what a reader assumed.
            The ban, as a named CI step: nothing outside `SystemClock.cs` reads the machine clock. `DateTime.Today` and `DateTimeOffset.Now` are banned alongside the three CLAUDE.md names, being the same mistake spelled differently.
            `CheckProofTests`, which feeds each scanner source it wrote itself. A test proving a check works has to be permanent rather than a break-and-revert done by hand once, so the violations live in the test and the repository is never broken to produce them.
            `CSharpSource.WithoutComments`, so a comment naming a banned construct in order to explain the ban is not read as the code doing it. Applied to the clock scanner, the store-write scanner and the path scanner.

Measured:   `tools/ci.ps1` green on Windows, 14 steps, 48 tests. Obtained by running it.
            clock-usage examined 19: 18 shipped source files scanned, 1 direct clock read inside the clock implementation. Unexamined 0.

Verified:   `tools/ci.ps1` against a dropped store. The macOS half of the matrix has still not run, so the two-platform check remains unconfirmed rather than passed.

Findings:   Observation. `TimeZoneInfo.TryConvertWindowsIdToIanaId("UTC")` succeeds, so `UTC` is a Windows timezone identifier as well as an everyday word and the guard rejects it. Reading: the code uses `Etc/UTC`, and the rejection is pinned by a test rather than left as a surprise. Accepting `UTC` would be the one exception that makes the rule unenforceable.
            Observation. `api-isolation`, `ci-parity` and `clock-usage` run as named CI steps but are not rows in CLAUDE.md's Checks table. Each is mandated by name elsewhere in CLAUDE.md: the isolation check under "Repository layout", the two-way script check under "Commands", the clock ban under "Hard rules". Reading: whether the table should list every executable check is a question the phase report answers better than a guess does, so it is carried rather than settled here.

Carried:    Whether CLAUDE.md's Checks table should list every check that runs as a CI step, or only the properties it currently names. Due at 1.7, where the phase report enumerates checks and their coverage and makes the answer obvious.

## 1.3 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `UniverseBuilder`, following RUNBOOK's backfill order exactly: the exchange symbol list, then bulk end-of-day over the twenty-session window, then the floors. Screening on cheap bulk data before anything costs one call per name is what keeps the whole thing inside the ceiling.
            Migration 002 creates `security`, `universe_member` and `universe_snapshot`. Membership is state rather than a filter: a name that leaves keeps its row and gains a `removed_on`, so a setup recorded while it was a member still resolves to a security.
            `EodhdClient`, the vendor over HTTP, and `IMarketDataVendor` as the stages see it. Every method takes the day's budget, so a component cannot make a request the ceiling does not know about. The token never appears in an exception message, because it is in the URL.
            `ICallBudget` with per-request cost. A whole-market bulk request is priced at a hundred and a symbol list at five, and a budget counting requests rather than their cost would report a twentieth of what a night spends.

Measured:   **N = 2,070.** The survivor count, measured by running `universe-build` against live EODHD on 2026-08-25, not estimated. 17,996 common stock listed on the US exchanges, 17,988 of them with a price on the most recent session, 2,070 clearing the $5 price floor and the $20M median dollar volume floor over 20 sessions.
            2,105 calls for the screen: 5 for the symbol list and 2,000 for twenty market days, which is what RUNBOOK's backfill table budgets. 6,210 rows written, being 2,070 in each of the three stores.
            The whole day's usage was 2,415 against the 5,000 ceiling, the difference being two earlier runs that failed and are recorded as failed with the calls they spent.
            `tools/ci.ps1` green on Windows, 14 steps, 61 tests.
            writer-ownership now examines 7 declared writers of a live store against 4 tables, with 40 declared writers unexamined because their store does not exist yet and 1 because its component has not been built.

Verified:   `tools/ci.ps1` against a dropped store, then `universe-build` against live EODHD into a separate data root. The floors are asserted individually against a market the test states outright: an ETF that trades enormously, a $4.99 stock with a hundred million shares a day, a $950 stock with nineteen million dollars a day, and a name whose median clears nothing while its mean clears easily.

Findings:   Observation. 2N is 4,140. Reading: RUNBOOK's backfill says steps 4 and 5 fit in one day while 2N stays under about 4,000, so at this N the backfill splits across two days. That was the only thing in the design that depended on the count, and it is now answered.
            Observation. The vendor publishes `volume` as a JSON number with a fractional part on some rows. Reading: read as decimal and narrowed at the boundary, because a long would refuse the whole response over one field.
            Observation. `Host.CreateApplicationBuilder()` takes its content root from the current working directory. Reading: a configuration file found by the current directory is found on one machine and missed on the other, and scheduling lives outside the application where Task Scheduler and launchd each choose a working directory. All three hosts now set the content root to where the binary sits.
            Observation. The `writer-ownership` check rejected the first version of `UniverseBuilder`, which upserted `security` with an `ON CONFLICT DO UPDATE`. Reading: SCHEMA declares SectorResolver as the only updater of that table, on four named columns, so the upsert was an undeclared second writer. The insert is now insert-only. This is the check doing the job it exists for, on the first checkpoint that gave it something to catch.
            Observation. The vendor token is read from `Secrets:EodhdApiToken` rather than from the lab's own section, because the secrets file on this machine holds keys for more than this lab. It is still registered before environment variables, so `Secrets__EodhdApiToken` in the environment still wins.
            Observation. The same secrets file holds an `AnthropicApiKey`. Reading: checkpoint 6.5 requires that `ANTHROPIC_API_KEY` is absent from the **environment**, which a JSON file is not, so this is not that trap. It is close enough to it to be worth writing down before phase 6 rather than after.

Carried:    A partial universe run leaves membership as the last complete screen set it and writes tonight's snapshot from that membership. Whether a night screened over five sessions should instead write nothing is a judgement the count distribution at 2.11 will inform. Due at 2.11.
            The daily call budget counts against the UTC date, and the vendor's own reset boundary is still assumed rather than confirmed. Raised at 1.1, still open.

## 1.4 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `DailyBarIngestor`, one bulk request a night, storing bars for the names in the universe. Migration 003 creates `daily_bar` with PK (`ticker`, `bar_date`, `observed_at`) and a foreign key to `security`.
            `DailyBarReader`, the one way stored bars are read and the one place the point-in-time rule lives. Every read takes an as-of date and there is no overload that does not, because a read that could omit it would compile, run, and return a bar the lab could not have seen.
            `bar-append-only` as a named CI step: nothing in the shipped source deletes or updates a bar table. It carries a deliberate tripwire that fails when `intraday_bar` is created, so the one legitimate update SCHEMA declares, VwapEngine on `vwap_session`, has to be written into the check by name and by column rather than the check being widened until it passes.

Measured:   22,770 bar rows over 20,700 distinct ticker and date pairs, 2,070 tickers, 10 sessions from 2026-08-11 to 2026-08-24. Obtained by running `daily-bars` once per session date against live EODHD.
            The 2,070 rows above the distinct pairs are one redundant observation of 2026-08-24, written by the run that exposed the idempotence defect below. They are left in place: the table is append-only, and a read still returns the right figure because the latest observation carries the same values.
            100 calls per session, which is the whole market and the largest single line in the nightly budget. 3,915 calls used on 2026-08-25 against the 5,000 ceiling, across universe building, bar ingest and the reruns.
            `tools/ci.ps1` green on Windows, 15 steps, 73 tests.

Verified:   `tools/ci.ps1` against a dropped store, then `daily-bars` against live EODHD, then the same date again: 0 written, 2,070 already stored unchanged. A test asserts the write connection reports `journal_mode=wal` and `foreign_keys=1`, and a second asserts a bar for a ticker with no `security` row is refused, which is what foreign keys being on actually buys.

Findings:   Observation. The first version passed its idempotence test and was not idempotent. Re-running `daily-bars` for 2026-08-24 wrote 2,070 rows again with identical figures. Reading: the ingestor compared against observations made at or before the **bar date**, and a date being backfilled is observed today, so it found nothing and rewrote everything under a new observation. It looked idempotent for tonight's date and was not idempotent for any other, and the unit test only ever ran tonight's date. The bound is now the run's own instant, the parameter is a `DateTimeOffset` rather than a `DateOnly` so the two questions cannot be confused again, and a test backfills a date a fortnight old.
            Reading, worth keeping separate from the above: a same-day test cannot reach this defect at all. The live run is what found it, on the first checkpoint where a live run was possible.
            Observation. `daily-bars` for 2026-08-25 returned nothing at 13:00 ET. Reading: the bulk endpoint publishes after the close and RUNBOOK schedules this stage at 17:30 ET, so an empty response before the close is correct behaviour rather than a fault. It is reported as a clean run writing zero rows, which is the same shape a holiday produces.
            Observation. Bars are stored for universe members only, roughly 2,070 of 17,996 listed names. Reading: this keeps the store near 0.5 million rows a year rather than 4.5 million. A name joining the universe later has no history until the per-ticker endpoint backfills it, which is RUNBOOK's step 4 and is priced per ticker.

Carried:    The per-ticker history backfill, RUNBOOK's steps 4 and 5, is not built. Ten sessions of bulk history is enough for nothing that needs a 50-day average, so 1.6's indicator values cannot be computed over a converged window until it exists. Due at 1.6.
            A name that joins the universe after go-live has no history until that backfill runs. Due at 1.6.

## 1.2 — 2026-08-25 — correction and completion of the two-platform obligation

Corrects the 1.2 entry above, which recorded the two-platform check as unconfirmed. It has now run, and it failed.

Measured:   The first CI run on the branch: `windows-latest` success, `macos-latest` failure. `./tools/ci.sh: Permission denied`. Obtained from the run's own log.

Findings:   Observation. `tools/ci.sh`, `tools/migrate` and `tools/snapshot-db` were recorded 100644 rather than 100755. Reading: Windows has no executable bit, so a shell script committed from here is recorded non-executable and works perfectly on the machine that wrote it. This is precisely the class of fault the matrix exists to catch, and it took the first macOS run to find it, on three files that had been run successfully dozens of times locally.
            Reading. `.gitattributes` already anticipated the sibling problem, line endings, and says so in a comment. The executable bit is the other half of the same thing and nothing covered it.

Built:      `shell-executable`, a check of its own with its own CI step, asserting the recorded mode of every shell entry point rather than the working tree's, because the working tree's mode is exactly what Windows does not have. It reports itself unexamined rather than passing if the mode cannot be read: a skip that reads as a pass is the failure the coverage line exists to catch.

Verified:   The check was demonstrated against a deliberate regression, `git update-index --chmod=-x tools/migrate`, and named the file and both modes. That demonstration is recorded here rather than as a test, because the property is git index state and a unit test asserting it would need a scratch repository to break; the check itself is permanent and runs on every CI run.
            `tools/ci.ps1` green on Windows, 16 steps, 74 tests.

## Phase 1 corrections — 2026-08-25 — phase-1-ingest-and-charts — amends 6.5, nothing built

Not a checkpoint entry. The format covers anything decided along the way, and this was decided during phase 1 against a checkpoint five phases out, so it is filed under the phase that did the work rather than the phase that will consume it. No code changed.

Built:      Nothing. `docs/DECISIONS.md` gains **The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface**, and four spec edits follow from it with their prior text in `CHANGELOG.md`.

Findings:   Observation. Five places treated one transport as the implementation rather than as one of two: BUILD_PLAN's 6.5 deliverable and its trap note, ARCHITECTURE's model budget and its migration note, and RUNBOOK's setup step 5. Reading: the trap note and the setup step are the two that would have caused harm, because each gave a reason for banning `ANTHROPIC_API_KEY` that applies to only one path, which reads as permission to set it on the other. The variable stays banned and both reasons are now stated.
            Observation. RUNBOOK was not among the three edits the change was scoped to. Reading: its stated done condition was that no document treats either transport as the only option, and that document did, so it was edited and the departure is named in its changelog entry rather than left for a sweep to find.

Verified:   `tools/ci.ps1` green on Windows, 16 steps, 74 tests. `decision-resolves` examines 56 decision names and 59 citations across 64 files, and the new name resolves from ARCHITECTURE and from BUILD_PLAN. `stated-counts` unaffected: no count in any document describes the number of decisions.

Carried:    The empty tool set is a property of configuration on the subscription path rather than of there being no mechanism, so it holds only as long as the test asserting it does. Due at 6.5, where that test is a done condition.

## 1.5 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `ActionIngestor`, one bulk request a night for splits and an optional second for dividends, storing actions for the names in the universe. Migration 004 creates `corporate_action` with PK (`ticker`, `effective_date`, `type`) and `indicator_rebuild` with PK (`ticker`, `effective_date`), both with a foreign key to `security`.
            The rebuild demand. A split that rescales history writes a row against that ticker and no other, and the row is stamped rather than cleared when it is honoured (see: A split records a rebuild demand that is stamped rather than cleared). `IndicatorRebuildReader` answers which tickers were blocked as of a given night, point in time like every other read, so a replay of a night the split was outstanding still says the stock was blocked after the rebuild has landed.
            `CorporateActionReader`, with the same shape as `DailyBarReader`: every read takes an as-of date and there is no overload that does not.
            `StoreText.RatioToStorageText`, named separately from the price crossing. Ratios are stored as fractions, and a crossing named for what it carries is what stops 6.8 being written where 0.068 was meant.
            Two more pins in `pinned-constants`: the whole-market bulk cost and the splits bulk cost, read from `EodhdClient` and compared against the data budget table.

Measured:   For 2026-08-24, live: 15 splits and 248 dividends published for the whole market, 20 of them in the 2,070-name universe, 20 rows written and one rebuild demanded. The one split was IESC, two for one. 19 of the 20 were dividends.
            200 calls for the pair of requests. The splits request is 100 and non-negotiable nightly; the dividends request is 100 and runs weekly.
            `tools/ci.ps1` green on Windows, 16 steps, 94 tests.

Verified:   The done condition against real data rather than only a synthetic fixture. One split landed in the universe on 2026-08-24 and exactly one ticker is blocked. The synthetic fixture holds the case the live night could not produce: a stock splitting, a second paying a dividend the same evening, a third doing nothing, and only the first blocked.
            Live rerun of the same date: 0 written, 20 already stored, 0 rows. That is the check 1.4 taught, run against the stage that could repeat the defect.

Findings:   Observation. The data budget states roughly 20 calls for the bulk dividend request. Reading: that is the nightly average of a weekly request rather than the price of one, and the request itself costs 100 like every other bulk call. It is the only row of that table whose figure is an average. Charging the budget 20 would under-count by 80 every time the stage ran, which is exactly the accounting error the ceiling exists to catch, so the constant is the cost and the schedule is what makes it weekly. The row is reported unexamined by `pinned-constants` with that reason, rather than pinned to a number that would be wrong.
            Observation. RUNBOOK's split-recovery row said to rerun the action ingest and then force an indicator rebuild. Reading: the second half describes an operator action that does not exist and now never will, because the rerun raises the demand by itself. Corrected as a clean edit.
            Observation. SCHEMA declared where a split is stored and nowhere for the rebuild it forces, while ARCHITECTURE stated both that ActionIngestor forces a rebuild and that an unprocessed split makes calculations refuse to run. Reading: a stated behaviour with no store behind it is one nothing can assert, which is how it would have reached 1.6 as a comment rather than as a mechanism. `indicator_rebuild` is that store and the decision naming it is new.
            Observation. A dividend also moves the adjusted close, and this build raises a demand only for a split. Reading: the corpus says split throughout, so the rule was followed rather than widened on a guess. Whether an accumulated dividend adjustment is large enough to matter to a 50-day average is a measurement, and 1.6 is where the average exists to measure it against.

Carried:    Nothing reads `indicator_rebuild` to refuse a calculation yet. The reader exists and has no caller, because the thing that would refuse does not exist. Due at 1.6.
            A vendor publishing a different ratio for a split already stored is counted and printed and cannot reopen a rebuild already stamped, because `rebuilt_at` belongs to IndicatorEngine and this stage may not clear another component's column. Where the rebuild is still outstanding, which is the ordinary case, the stock is blocked regardless. Due at 1.6.
            The dividend question above. Due at 1.6.

## Phase 1 corrections — 2026-08-25 — phase-1-ingest-and-charts — amends 1.5

Not a checkpoint entry. Three defects in what 1.5 shipped, two of which were the same defect
reported as two carried obligations, and one resting state in the corpus that was wrong.

Built:      Migration 005. `corporate_action` and `indicator_rebuild` are rebuilt, both keyed on
            the action as observed. Actions are append-only on the same terms as bars: a
            restatement is a new row with a later `observed_at`, and reads take the latest
            observation at or before the as-of date (see: A rebuild demand is keyed on the action as observed, and a restated action raises a new one).
            The demand's key is the action's key, so a restated ratio raises a second demand
            beside the first rather than failing to reopen it.
            Any action that moves the adjusted close now raises a demand, not only a split
            (see: An unprocessed corporate action of any kind blocks calculation, not only a split).
            The data budget states cost per request and cadence in columns of their own. Both are
            pinned: the cost against `EodhdClient.BulkDividendCost`, the cadence against
            `ActionIngestor.RequestsDividendsByDefault`, which is false and load-bearing.
            `MigrationRowSurvivalTests`, which stands the store up at version 4, puts three
            actions and two demands in it, migrates, and asserts the counts and the rows.

Measured:   Live, after migrating the store and running `actions 2026-08-21 --with-dividends`:
            8 splits and 189 dividends published for the market, 24 in the universe, 24 written,
            24 demands raised over 24 tickers. The store now holds 44 actions and 25 open
            demands over 25 tickers, and 0 demands that do not name an action.
            `tools/ci.ps1` green on Windows, 16 steps, 98 tests.

Findings:   Observation. Under 004 a restated ratio could not be stored at all: the primary key
            was the action, so the second observation collided with the first and was dropped.
            Reading: the two carried obligations from 1.5 were one defect. A ticker rebuilt
            against a factor the vendor later changed stayed rebuilt, permanently, with the
            record showing a satisfied demand and the wrong number computed from it, and no
            reader anywhere to notice. Bars had already solved this and the discipline was not
            copied.
            Observation. `decision-resolves` failed the moment a decision was superseded, on a
            citation inside PROGRESS. Reading: it resolved citations against the current section
            of DECISIONS.md only, so it was answering "is this name in force" rather than "does
            this name exist", which is `no-superseded-citation`'s question. Folded together, a
            supersession breaks every record that ever cited the decision, and the only ways out
            are rewriting history or never superseding anything. `decision-resolves` now resolves
            against both sections and `no-superseded-citation` exempts the records, counting and
            reporting the exemption rather than applying it quietly.
            Observation. The 20 actions of 2026-08-24 already in the live store were observed
            before the rule widened, so the 19 dividends among them raised no demand and their
            tickers are not blocked. Reading: no indicator has ever been computed, so there is
            nothing invalidated today, and the per-ticker history refetch owed at 1.6 re-observes
            every member's history, which is what a rebuild is. Carried rather than repaired,
            because a repair would have a migration writing rows into a table SCHEMA declares one
            component as the writer of.

Carried:    The 2026-08-24 gap above. Due at 1.6.
            `indicator_rebuild` still has a reader and no caller. IndicatorEngine is what refuses,
            and it does not exist yet. Due at 1.6.
            The dividend pull is weekly, so a dividend effective on a Tuesday is unobserved until
            the weekly run and the stock is not blocked in between, computing on an adjusted
            series that has already moved. Making it nightly costs 80 more calls a night against
            a 5,000 ceiling. Raised here rather than decided here, because the cadence is stated
            in two specs and changing it is a decision. Due at 1.6.

## 1.6 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `IndicatorEngine`. EMA 9, 21 and 50 on the adjusted close, ATR 14 on Wilder's
            smoothing, ADR 20 as a fraction, the 20-session range average and the median dollar
            volume. Migration 006 creates `indicator_daily`.
            It refuses in two cases and writes no row in either: a window shorter than the
            150-session warm-up, and a ticker with a corporate action outstanding. A missing row
            is meaningful, which a number would not be.
            The per-ticker history refetch, as a mode of `DailyBarIngestor` rather than a
            component of its own, because SCHEMA declares one inserter of `daily_bar`. It serves
            both RUNBOOK's step 4 and the rebuild: the vendor returns a name's whole series
            adjusted as it adjusts it today, so the series arrives on one basis.
            Migration 007 creates `history_refetch` (see: A rebuild is satisfied by a recorded refetch, not by inferring one from what changed).
            `tools/derive-indicators.py`, a second implementation of every formula in another
            language, reading the store directly. Not run by CI and nothing imports it.

Measured:   Live, as of 2026-08-25. The full cycle, in order: `indicators` refused 1 ticker and
            computed 2; `backfill --rebuild` fetched 25 names for 25 calls, 18,425 bars
            published and 17,434 written; `indicators` then computed 25, satisfied 25 demands
            and blocked 0.
            2,043 of 2,070 members are short of the warm-up, because the full-universe backfill
            has not run. That is a carried obligation, not a defect.
            The three tickers, derived independently and diffed at 4 decimal places: 21 values,
            0 disagreements. Window 2026-01-20 to 2026-08-24, 150 sessions each.

              IESC  ema_9 352.9966  ema_21 353.2321  ema_50 343.3746  atr_14 24.1364
                    adr_20 0.0670  range_avg_20 23.3959  dollar_volume_median_20 204,580,994.64
              LITE  ema_9 862.2732  ema_21 841.9685  ema_50 826.9671  atr_14 81.0755
                    adr_20 0.0923  range_avg_20 75.4418  dollar_volume_median_20 3,872,506,949.00
              PAYO  ema_9   7.1103  ema_21   7.0987  ema_50   6.8553  atr_14  0.0432
                    adr_20 0.0054  range_avg_20  0.0385  dollar_volume_median_20  34,889,899.60

            `tools/ci.ps1` green on Windows, 16 steps, 111 tests.

            **Why these three, which matters more than which.** IESC is the only name in the
            store with a real corporate action inside the window: a two-for-one on 2026-08-24,
            and 748 of its 751 stored bars carry an adjusted close that differs from the raw
            one, so a disagreement on IESC alone would localise to the adjustment. The rebuild
            demand for it had already fired and been satisfied before these values were taken.
            LITE is the order-of-magnitude test. It closes near $830 with a 9.2 percent daily
            range, so `atr_14` lands at 81.08 and `adr_20` at 0.0923: a factor of roughly 880
            between two columns that a percentage-for-fraction slip would put within ten of each
            other. Every one of its 751 bars has an adjusted close equal to its raw close, so
            nothing about the adjustment is being tested by it.
            PAYO is the clean control near the price floor, at $7.11 with a 0.5 percent daily
            range and, like LITE, no bar in three years where the adjusted close differs from the
            raw one. It shares the recursion with the other two and shares no adjustment, so a
            disagreement on IESC that PAYO does not show is an adjustment fault rather than a
            formula fault. Its `atr_14` of 0.0432 against an `adr_20` of 0.0054 is also the
            smallest separation of the three, which is where a units error would hide best.

            **The formulas, stated because a value means nothing without one.** EMA(n) is seeded
            on the simple mean of the first n adjusted closes and then recursive with a
            multiplier of 2/(n+1). ATR(14) uses Wilder's smoothing, seeded on the simple mean of
            the first 14 true ranges, where true range is the greatest of the day's own range and
            the two gaps from the previous close, taken on the adjusted basis and undefined for
            the first bar. ADR(20) is the mean of (high-low)/close over the last 20 sessions, a
            fraction rather than a percentage. The range average is the mean of (high-low) over
            the same window on the adjusted basis. The median dollar volume is the median of
            raw close times raw volume, deliberately raw, because it is what changed hands and it
            is the figure the universe screen uses. High and low are put on the adjusted basis
            through each bar's own factor, adj_close/close.

Verified:   The derivation shares no code with the engine. It is a different language reading the
            store directly, with the window selection written out rather than borrowed, which
            rules out a transcription error, an off-by-one in a window and a seed taken from the
            wrong place. It does not rule out both implementations agreeing on a definition that
            is wrong, because the same session wrote both. That is what the charting-platform
            check is for and it is carried to 1.7.
            The restatement cycle end to end, as a test: a satisfied demand, a restated ratio, a
            second demand, the ticker blocked again, and satisfied again only after a second
            refetch.

Findings:   Observation. The first satisfaction rule inferred the rebuild from bar observations,
            taking the earliest observation in the window, and it blocked IESC for ever on the
            live store. Reading: a refetch rewrites the bars the action moved and leaves the
            recent ones alone, because those were already ingested on the post-action basis, so
            the window keeps an old earliest observation. The obvious repair, taking the latest
            observation instead, is worse: the nightly ingest writes a bar for every name every
            night, so every demand would clear itself by the following evening with nothing
            refetched, and that failure produces numbers. Both are now permanent tests.
            Reading, worth separating: the unit test passed against the first rule. It passed
            because the fixture restated every bar in the series, which a real vendor does not do
            for bars already on the new basis. The live run is what found it, on the first real
            split in the store, which is the second time in this phase that a live run has found
            what a fixture could not.
            Observation. A refetch done after midnight UTC is invisible to a read as of the
            previous session date. Reading: that is the point-in-time rule working, not an
            inconvenience. It means a rebuild honoured tonight takes effect from tonight's
            session forward, and the tests now say so explicitly by naming the session each step
            happens on.
            Observation. `indicator_daily` is keyed on ticker and date, and SCHEMA declares
            TierClassifier as its only updater. Reading: the engine can therefore insert and
            nothing else, so a night already written stands and rows computed on a pre-rebuild
            basis are never revisited. The run reports how many it left alone rather than
            overwriting them quietly. The fix is a decision about the store's shape rather than a
            code change, and it is carried.

Carried:    One `CONFIRMED` value per ticker against a charting platform's own readout. Due 1.7.
            Past indicator rows are not recomputed after a rebuild. Due 1.7.
            The full-universe backfill, 2N at 4,140 calls, is the two-day operation RUNBOOK
            describes and has not run. Due 1.7.
            The dividend pull is weekly, so a dividend effective on a Tuesday is unobserved until
            the weekly run and the stock is not blocked in between. Raised at 1.5, still open.

## Phase 1 corrections — 2026-08-25 — phase-1-ingest-and-charts — the backfill, the cadence, the computed store, and input provenance

Not a checkpoint entry. Four changes in one pass, three of which discharge obligations 1.6
carried and one of which is owed to 1.7.

Built:      Migration 008 adds `counts_against_ceiling` to `run_log`. The ceiling guards the
            evening's job and a one-time operation is not the evening's job; charging the two
            against each other is the whole of why the backfill looked like a two-day procedure.
            The calls are still recorded, and the run says which kind it is in the store rather
            than being recognised by its stage name.
            Dividends are pulled nightly. `ActionIngestor.RequestsDividendsByDefault` is true and
            `--splits-only` is the opt-out for an evening where the budget is short.
            Migration 009 rekeys `indicator_daily` on (`ticker`, `as_of`, `computed_at`) and makes
            it append-only. A rebuild now reaches the sessions it invalidates: each gains a later
            observation and the earlier one stands. `IndicatorDailyReader` takes the latest
            computation at or before its as-of date.
            `DailyBarReader.Read` gained an explicit observation bound. The as-of date says which
            bars are in the window and the instant says what was known when the answer was
            produced, and they only coincide by habit.
            `writer-ownership` reads declared disjointness instead of carrying a list of tables to
            forgive. `setup`, `setup_signal` and now `indicator_daily` state in SCHEMA how their
            two writers stay apart, and the check reads the declaration.

Measured:   The one-time backfill, RUNBOOK step 4, run live against EODHD on 2026-08-25.
            2,070 tickers selected, 2,070 fetched, 2,070 calls, from 2023-08-25 to 2026-08-24.
            1,480,032 bars published, 1,439,679 written, 40,353 already stored unchanged.
            Recorded outside the daily ceiling; the counted total for the day finished at 4,755
            against 5,000 with the backfill's 2,070 alongside it rather than inside it.
            The store now holds 1,482,108 bars over 2,070 tickers, three years deep, at 216.5 MB.
            2,016 indicator rows. 37 rebuild demands, 0 open.
            The first full indicator run: 1,989 computed, 27 unchanged, 54 short of the
            150-session warm-up, 0 blocked. The 54 are names listed for less than 150 sessions.
            The three tickers re-derived against the rebuilt store: 21 values, 0 disagreements at
            4 decimal places, unchanged from the values recorded at 1.6 despite every bar in the
            window having been re-observed by a different code path.
            `tools/ci.ps1` green on Windows, 16 steps, 113 tests.

Verified:   The nightly dividend cadence end to end, live, on today's session. `actions 2026-08-25`
            published 1 split and 50 dividends, 12 of them in the universe, and raised 12 demands
            over 12 tickers. `indicators` then blocked all 12 and computed none of them.
            `backfill --rebuild` fetched those 12 for 12 calls, counted against the ceiling because
            it is nightly work, and wrote **0 bars**: the vendor's series was already on the basis
            the store held. `indicators` then satisfied all 12 and blocked none.
            That last run is the strongest evidence produced this phase. Under the satisfaction
            rule this session replaced, a refetch that wrote no bars left the ticker blocked for
            ever, so all twelve would have been stuck on the first evening the corrected cadence
            ran. The two changes were made independently and the second one exercised the first.

Findings:   Observation. Migration 009 first stamped each existing row's `computed_at` as the end
            of its own session. Reading: that puts a migrated row in front of every recomputation
            made the same evening, which is exactly the wrongness the rekey exists to remove. It
            was caught on the live store before any recomputation depended on it. The stamp is now
            the first instant of the session: visible from that session onward, and behind any
            real computation. The live store was restored from the pre-migration snapshot and
            migrated again, which cost the run-log record of one `actions` run and its 200 calls.
            Those calls were genuinely spent and are not in the day's counted total.
            Observation. The first recompute returned nothing. Reading: it read the window through
            a bound derived from the session date, so a rebuild done today could not see bars
            refetched today when recomputing a session three weeks old. The session and the
            observation instant are two different bounds and the reader was conflating them.
            Observation. The 20 actions of 2026-08-24 still raised no demands, because they were
            already stored when the rule widened. Reading: the obligation carried from 1.5 said
            the full backfill would close this by re-observing every member's history, and it has.

Carried:    One `CONFIRMED` value per ticker against a charting platform's own readout. Due 1.7.
            The split-history backfill, RUNBOOK step 5, is a second 2,070 calls and has not run.
            Nothing depends on it yet: splits are picked up nightly from the bulk endpoint, so what
            is missing is only the history of splits before the lab started. Due 1.7.
            54 universe members are short of the warm-up and get no indicator row. They are names
            listed for less than 150 sessions, which is a fact about them rather than a defect.

## 1.9 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `IndexIngestor` and migration 010 creating `index_bar`, on the same terms as
            `daily_bar`: append-only, keyed with `observed_at`, reads taking the latest
            observation at or before the as-of date. `IndexBarReader` beside it.
            Three calls, not one bulk request. The bulk endpoint carries all three symbols inside
            the whole market's response and is priced per market day, so asking it for three
            symbols would be a hundred calls to learn three numbers. The per-ticker endpoint is
            one call a symbol at any depth, which makes the nightly update and the backfill the
            same request.
            `FixtureCapture`, a one-time stage that stores one real vendor response per endpoint
            verbatim, with the endpoint, the query and the instant beside it, and `fixture-inputs`
            as a named CI step asserting every endpoint a live run exercises has one.

Measured:   `index-bars 2026-08-25` against live EODHD: 3 symbols, 2,253 bars over three years,
            3 calls, 2,253 rows written.
            `capture-fixture 2026-08-24`: 37 responses, 33 symbols, 15,368,544 bytes, 338 calls,
            recorded outside the daily ceiling. Two files are most of it: the exchange symbol list
            at 7.6 MB and the whole-market bulk response at 6.7 MB. The 33 histories are about
            1 MB together.
            `tools/ci.ps1` green on Windows, 17 steps, 118 tests.

Verified:   The captured responses are diffed against the manifest by `fixture-inputs`, which also
            asserts no response and no recorded query carries an `api_token`. The token is in the
            URL on every request, so a capture that stored the URL would have put the credential
            in the repository, and the check exists because that would have been silent.

Findings:   Observation. The two bulk responses are 14.3 of the 15.4 MB. Reading: they were kept
            whole rather than trimmed. Trimming would make them a hand-edited derivative of a
            captured response, which is neither tier and is exactly the muddle the two tiers
            exist to prevent. Git packs them well and the fixture is captured once.
            Observation. The fixture holds 33 histories for 30 tickers. Reading: the three
            trackers are captured alongside, because the index path is a path a live run
            exercises and the decision asks for a captured input for each of those, not for each
            table.

Carried:    RUNBOOK's step 5, the split history for every survivor, is a second 2,070 calls and
            has not run. Nothing depends on it: splits arrive nightly from the bulk endpoint, so
            what is missing is only the history of splits from before the lab started. Due 1.7.

## 1.7 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `tools/verify-phase`, and the harness behind it. One command builds, runs the suite so
            every check writes its part under `artifacts/`, then assembles the report and states one
            verdict. It does not stop when the suite fails: a red suite is exactly when the report
            is worth reading, and the suite's exit code is handed to the assembler so a red suite
            cannot leave a green report either.
            `FixtureVendorHandler`, which serves the captured fixture at the transport rather than
            at the interface. A fake implementing `IMarketDataVendor` would hand the stages objects
            a test author built, so the replay would exercise the stages and skip the parsing, the
            field names, the number formats and the URL the client actually asks for. Handing back
            the captured bytes runs the real client over the real response and replaces only the
            network.
            `PhaseReplay`, the nightly pipeline end to end over the fixture in RUNBOOK's order:
            universe, history seed, actions, bulk bars, the rebuild refetch, index bars, indicators.
            The order is itself under test, because an action observed tonight has to block the
            averages until a refetch made after that observation lands.
            `fixtures/expectations.json`: 245 expectations, each carrying its tier, the checkpoint
            that owes it and how it was produced. 21 are `DERIVED`, being the three hand-picked
            tickers' seven figures each, re-derived by `tools/derive-indicators.py` over the
            fixture's own bars rather than carried across from the live store.
            `fixture-replay` and `architecture-conformance` as named CI steps, and
            `PhaseReportStage`, which asserts nothing and only assembles: a second implementation of
            a claim inside the reporter would be a second place to keep right.
            `CheckCoverage.OutOfScope`, separating a claim placed in a later phase from a claim this
            phase should have asserted and could not.

Measured:   `tools/ci.ps1` green on Windows, 19 steps, 120 tests. `tools/verify-phase` green.
            81 architecture claims: 17 pass, 0 fail, 64 out of
            scope, 0 unexamined. 245 expectations, all matched. Inputs `CAPTURED` 37, `AUTHORED` 47.
            Coverage across all checks: 2,676 examined, 0 unexamined.
            The replay over the fixture: 17,996 common stock listed, 11,983 screened, 2,002
            surviving the real floors on that one market day, 7,530 bars seeded over 30 tickers,
            15 splits and 248 dividends published, 57 demands raised, 753 index bars, 30 tickers
            computed and 1 demand satisfied. Two seconds.
            `fixture.actionsObserved` and `fixture.rebuildsStamped` both read `IESC`: the whole
            rebuild loop, on captured data, end to end.

Verified:   The 21 `DERIVED` values against `tools/derive-indicators.py` run over the replay store:
            0 disagreements at 4 decimal places. That is a second implementation in a second
            language from the textbook definitions, so it catches a transcription error, an
            off-by-one in a window or a seed taken from the wrong place. It cannot catch the two
            implementations agreeing on a definition that is wrong, which is what the `CONFIRMED`
            tier is for and which is still owed.

Findings:   Observation. SCHEMA declared `Insert SetupDetector` on `calibration_setup` and no such
            component exists. Reading: the catalogue names two detectors and the calibration run is
            both of them. `writer-ownership` had been counting it as one unresolved name for six
            checkpoints; what changed is that the phase report makes an unexamined item block rather
            than appear in a log nobody reads. Corrected as a clean edit.
            Observation. The fixture's control ticker fails the liquidity floor on the captured day.
            Reading: PAYO's dollar volume was $11.4M on 2026-08-24 and its twenty-session median is
            $34.9M, against a $20M floor. The floor is a median over twenty sessions and the fixture
            holds one market day, so applying it to one session screens on a number that is not the
            number the floor means. The replay measures the screen's verdict under the real floors,
            then runs against the list the floor did not cut, and both numbers are expectations. The
            alternative was a fixture that quietly loses a name it was built to check.
            Observation. Sixty-four of eighty-one architecture claims are about components a later
            phase builds. Reading: that is the expected shape at 1.7 and it is why the fourth verdict
            exists. Counting them as unexamined would make every report red for reasons nobody can
            act on; counting them as examined would let them hide the one claim nobody can check.

            The obligation raised at 1.6, discharged. Both phase 1 defects that passed a unit test
            and failed live, re-read against the input tiers:
            The idempotence defect at 1.4 exercised `eod-bulk-last-day`, which had no `CAPTURED`
            input at the time, so `fixture-inputs` would have listed that endpoint as resting on
            authored evidence alone and the report would have read unexamined rather than green.
            The satisfaction-rule defect at 1.6 exercised `eod/{ticker}`, likewise uncaptured,
            likewise unexamined. Neither would have read green, so the tier definition holds.
            Worth separating, because it is the stronger claim: the fixture now detects the first of
            them outright. `bars.unchanged` is 30 because the seeded histories already hold the
            captured session, and a bound that regressed to the bar date would make it 0 and write
            7,202 rows instead of 7,172. The second is detected in one direction only: the earliest
            observation rule leaves IESC blocked and the replay would say so, while the latest
            observation rule needs two nights to fail and the fixture holds one. That case stays
            with the permanent unit tests, which is where it belongs.

Carried:    One `CONFIRMED` value per ticker against a charting platform's readout, moved to 1.10.
            The comparison is made against a chart on a screen, which is what 1.10 builds, and
            describing it a checkpoint earlier does not make it happen.
            Whether the fixture should hold twenty market days so the universe screen runs under the
            floor it means. 1,900 calls and about 130 MB. Due 1.12.
            RUNBOOK's step 5, the split history for every survivor, still not run. Due 1.12, where it
            is either run or dropped rather than carried further.

## 1.8 — 2026-08-25 — phase-1-ingest-and-charts

Built:      The web shell. A layout carrying the mark, the five-item nav and the status band,
            rendered from `Navigation.Items` rather than written out in the markup, so a page
            without a nav entry is a page nobody can reach and a nav entry without a page is a
            link that 404s, and one test asserts both against one list.
            Five screens, each rendering an empty state that names what fills it and at which
            checkpoint. Nothing is stubbed: a test asserts no page carries any of the mockup's
            tickers, because a page of invented rows reads as a working screen and is the one
            thing that cannot be told apart from the real one later.
            The status band, and `/status` on the read surface behind it. It reports what the
            store holds and returns null for market mood, open positions and risk at stake,
            which phases 2 and 4 build; the band renders those as the checkpoint that fills
            them rather than as a zero, because a zero reads as "none open" where the truth is
            "positions are not a thing yet".
            The shared candlestick component, as geometry in C# and a partial that positions
            nothing it was not handed. Every chart in the lab is this one: the gallery at 2.9
            draws hundreds small and the chart page at 1.10 draws one large, and a second
            implementation would eventually disagree about which basis the prices are on, which
            is a disagreement nobody can see in a picture.
            One local stylesheet. No script anywhere, asserted per page.

Measured:   `tools/ci.ps1` green on Windows, 19 steps, 157 tests. `tools/verify-phase` green:
            81 architecture claims unchanged, 257 expectations, 0 unexamined.
            Twelve new expectations, all `DERIVED`: the chart's layout over the fixture's IESC
            window, derived by `tools/derive-indicators.py --chart` from a written statement of
            what the layout should be. 0 disagreements.
            Both hosts started against the live store: the band reads session 2026-08-25,
            2,070 names, 1,482,108 bars, 4,758 of 5,000 calls. Every route answers 200.

Verified:   The pages are asserted by asking the host for them rather than by reading the
            .cshtml, because a route that does not resolve and a view that does not compile
            both look perfectly fine in the source. The read surface is answered from a stub, so
            both states of the band, up and down, are tests rather than one being a state nobody
            exercises.

Findings:   Observation. `/lab.css` returned 404 from the real host while every page returned
            200. Reading: the host sets its content root to where the binary sits, for the same
            reason the Worker's does, and `wwwroot` was not copied there. Every page rendered,
            every route resolved, and every page was unstyled. The done condition as written,
            every page reachable and rendering its empty state, was satisfied by a build with a
            missing stylesheet. Now copied by the project file and asserted by a test that reads
            the sheet back.
            Observation. The price axis labelled 100.36, 104.22, 108.08. Reading: `rough /
            magnitude switch { ... }` parses as `rough / (magnitude switch { ... })`, so the
            step was never rounded to a round number. It looked like a chart with an odd scale
            rather than like a defect, which is the whole reason the geometry is separated from
            the drawing and frozen as numbers.
            Observation. The shell's tests took 51 seconds. Reading: every page load reads the
            status band and the read surface was not running, so each request waited out the
            client timeout. Answering from a stub took it to one second and gained the reachable
            case, which no test had. The timeout is now three seconds rather than the client's
            hundred-second default, which is a page rendering without its figures rather than a
            page that hangs.
            Observation. `path-casing` examined 21 paths this checkpoint, having had no work at
            all before 1.7. Reading: CLAUDE.md says to drop the check if nothing reads a file by
            a path built from a string, and the fixture is read that way, so it stays.

Carried:    Nothing new.

## 1.10 — 2026-08-25 — phase-1-ingest-and-charts

Built:      The chart page. Any ticker the store holds bars for, its candles on the adjusted
            basis, the three exponential averages drawn across them, and the figures the lab
            stored for that session beside them. A GET form, so a chart is a URL you can keep,
            and no script anywhere.
            `/chart/{ticker}` on the read surface, which reads the window plus its 150-session
            warm-up and draws only the window. A fifty-day average over sixty sessions is a line
            still climbing out of its own seed for most of the picture.
            `PullbackStrategyLab.Core.Indicators.Averages`. The arithmetic moved out of
            IndicatorEngine into Core, so the component that writes the numbers and the surface
            that draws them are one implementation. IndicatorEngine is still the only writer of
            `indicator_daily`; the read surface writes nothing.
            The decision authorising it, **The averages are one implementation, computed nightly
            and drawn on demand**, which also states why drawing a past session's average is not
            the reconstruction the evidence rule forbids.
            **Chart page** added to the component catalogue, and `architecture-conformance`
            taught to assert a screen against the route that answers for it rather than against
            a class name a screen does not have.

Measured:   `tools/ci.ps1` green on Windows, 19 steps, 168 tests. `tools/verify-phase` green:
            82 architecture claims, 18 pass, 0 fail, 64 out of scope, 0 unexamined. 263
            expectations, 36 of them `DERIVED`.
            Six new expectations at 1.10, three `DERIVED`: the last point of each drawn line,
            which is the same value `tools/derive-indicators.py` checks the engine against, plus
            the agreement itself as a word rather than only as a comparison.
            Live against the store: `/chart/IESC` draws 60 sessions from 210 read, and the
            readout reads EMA 9 353.00, EMA 21 353.23, EMA 50 343.37, ADR 20 6.70%, ATR 14 24.14,
            median dollar volume $204.6M.

Verified:   The property the page exists for is asserted three ways: on the arithmetic, where the
            last value of the series equals the single value over the same window; through the
            store, where every drawn line ends on the number the engine wrote; and in the
            fixture, where all three appear as expectations.

Findings:   Observation. On the first live run the nine and twenty-one day lines matched the
            stored figures to four decimals and the fifty-day line did not: 343.2979 drawn
            against 343.3746 stored. Reading: the page read 210 sessions and the engine reads
            150, and an exponential average seeded 210 sessions back is not the one seeded 150
            sessions back. The shorter periods had converged and the longest had not, which is
            the shape this always takes. Both lines looked like a moving average. The series is
            now recomputed per session over the engine's own window, which makes the last point
            and the stored number the same number by construction rather than by luck.
            Reading, worth separating: the decision written before the code said this would
            happen, in those words, and it still happened. Writing the reason down is what made
            it a five-minute diagnosis instead of a puzzle.
            Observation. Running the engine over past sessions to fill the chart's averages
            returns nothing: `universe-build` has only ever run on 2026-08-25, so no earlier
            session has a snapshot and the engine has no members to compute for. Reading: that
            is the point-in-time design refusing exactly what it should. The lab has no record of
            who was listed on a night it was not running, and writing those rows would be the
            reconstruction 2.11 sends to `calibration_setup`. The chart computes its lines and
            stores nothing instead.
            Observation. The catalogue named no chart page. Reading: found because the
            conformance check has to place every catalogue row in a phase, and a screen this
            build produced had no row to place. Added, and the check now asserts screens against
            routes, which makes the other six screens assertable when their phases arrive rather
            than permanently unexamined.

Carried:    The `CONFIRMED` values, moved to 1.12. The page that makes the comparison possible
            now exists and shows the three averages in the two decimals a platform shows; what
            is left is a person reading a platform and writing three figures down, which is the
            one step in this corpus a build session cannot do for itself.

## 1.11 — 2026-08-25 — phase-1-ingest-and-charts — **partial, one machine**

Built:      `tools/snapshot-db` now does RUNBOOK's steps 2 and 5 as well as step 3. It counts
            every table in the source before the copy, runs `PRAGMA integrity_check` against the
            copy, counts every table there, compares, and exits non-zero on any difference or on
            an integrity result that is not `ok`. The counts come from the schema rather than
            from a list, because a list goes stale at the migration that adds a table and a count
            that silently omits one is exactly the failure the step exists to catch.
            `store-portability`, a named CI step asserting the hard rule that had been stated
            since the first day and asserted by nothing: no row carries an absolute path. It runs
            over the store the fixture replay builds rather than over the empty one a migration
            leaves, because a scan of an empty database examines nothing and passes forever.

Measured:   The rehearsal, executed on this machine against the live store.
            Source: 10 tables, 1,494,822 rows. `daily_bar` 1,482,108, `history_refetch` 2,107,
            `index_bar` 2,253, `indicator_daily` 2,016, `security` 2,070, `universe_member`
            2,070, `universe_snapshot` 2,070, `corporate_action` 56, `indicator_rebuild` 37,
            `run_log` 35. Snapshot 227,328,000 bytes.
            On arrival, after copying the snapshot into a data root of its own: `migrate` reported
            already at version 10 with nothing outstanding, `integrity_check` ok, and all ten
            table counts matched to the row.
            `indicators 2026-08-25` over the copy: 2,070 universe members, 0 computed, **2,016
            unchanged**, 54 short of the warm-up, 0 blocked. Every stored figure recomputed from
            the migrated bars to the same value, which is a stronger statement than a row count:
            the bars arrived, and they arrived on the same basis.
            The chart page against the copy reads EMA 50 343.37 and ADR 20 6.70% for IESC, the
            same figures the source shows.
            `store-portability`: 10 tables, 60 text columns, 189,656 stored values read, 0
            absolute paths.

Verified:   Steps 2, 3, 5, 8 and 9 of the ten, by running them. Step 6 in the negative only: the
            secrets file is present on this machine and gitignored, so it is confirmed not to
            travel with a clone rather than confirmed to have been copied.

Findings:   Observation. Step 2 named five tables, `setup`, `setup_signal`, `forward_return`,
            `trade` and `variant`, and none of them exists yet. Reading: a rehearsal at 1.11
            following that step literally would have counted nothing at all and reported success.
            The step now derives its list from the schema, which is the same rule `stated-counts`
            applies to prose counts, applied to a runbook.
            Observation. The hard rule about absolute paths had no check. Reading: found by
            asking what the rehearsal was actually verifying, which is the value of doing the
            rehearsal rather than reading it.

Carried:    **This is partial and is recorded as partial.** One machine, not two. Everything that
            can fail on one machine ran and passed. What one machine cannot exercise is what the
            checkpoint was written for: a Windows path in a row, an identifier the other platform
            rejects, a secrets file nobody copied. The first of those is now covered permanently
            by `store-portability`; the other two wait for a second machine. Due 1.12.

## Phase 1 corrections — 2026-08-25 — phase-1-ingest-and-charts — out of scope names the checkpoint that ends it

Not a checkpoint entry. A change to the phase report raised before 1.12 and made before it.

Built:      Every out-of-scope claim now carries the checkpoint that brings it into scope, and
            the report groups them by it. Three assertions guard the rule: the checkpoint has to
            be there at all, it has to be one BUILD_PLAN.md actually has, and it has to be one
            PROGRESS.md does not yet record. The third is the one that matters most, because a
            claim still deferred to a landed checkpoint is one that checkpoint shipped without
            coming back to, and nothing said so at the time.
            Placement now reads BUILD_PLAN.md's checkpoint rows rather than the build order
            table and the phase sections. The plan names every component in the row of the
            checkpoint that builds it, so the checkpoint comes from the document that schedules
            the work; the rows inside the carried-obligations table are excluded, because that
            table is keyed by the checkpoint that raised an obligation rather than the one that
            builds the thing.
            Four permanent proofs in `CheckProofTests`, one per way a claim can rest out of
            scope forever, fed claims written by hand rather than whatever the corpus says today.

Measured:   All 52 catalogue components place at exactly one checkpoint, with no ties and no
            gaps. The verdict split is unchanged at 82 claims, 18 pass, 0 fail, 64 out of scope,
            0 unexamined, and the 64 now spread across 37 checkpoints: 4.6 closes 8 of them,
            5.1 closes 5, 2.6 closes 3, and 28 checkpoints close one each.
            `tools/ci.ps1` green, 20 steps, 174 tests. `tools/verify-phase` green.

Findings:   Observation. The naive parser for checkpoint rows also matched the carried-obligations
            table, whose rows are keyed `| 1.6 |` and so on. Reading: that would have placed a
            component against the checkpoint that complained about it. Restricted to rows inside
            a phase section, and the reason is written at the parser rather than left for the
            next reader to rediscover.

## Phase 1 corrections — 2026-08-25 — phase-1-ingest-and-charts — a reported push that did not happen

Not a checkpoint entry. A correction to what this session reported, recorded because a build
session's report is the record a later reader trusts.

Findings:   Observation. Four times between 1.7 and 1.11 the session reported "pushed" having run
            `git push origin main`. The working branch is `phase-1-ingest-and-charts` and `main`
            was unchanged, so each command was a no-op that printed nothing under `-q` and each
            report was wrong. Nothing was lost: the four commits were local throughout and CI had
            not run on any of them, so a green CI reported at the time would also have been
            wrong, and was not claimed.
            Corrected the same session. `b829279` is on `origin/phase-1-ingest-and-charts` and the
            run over all four commits is green on `windows-latest` and `macos-latest`.
            Reading: this is the second time a report has stated something the machine did not do.
            The first was 1.2's two-platform check, reported as unconfirmed rather than as failed
            and then found failing on the first macOS run. Both are the same shape: the session
            reporting what it expected rather than what it read back. What is cheap and covers it
            is reading the command's own output before writing the sentence that describes it.
