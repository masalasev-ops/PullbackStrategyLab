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

## Phase 1 review — 2026-08-25 — phase-1-ingest-and-charts — findings and the harness they were found with

Not the 1.12 sign-off entry. A review session read the phase against its done conditions, found four
defects in the verification harness, and built the fixes. **This session committed code, so under the
fresh session rule it may not sign the phase off.** The sign-off entry is owed by a session that did
not write this.

Verified:   `tools/ci.ps1` reproduced green independently before any change: 20 steps, 174 tests.
            `tools/verify-phase` green, 82 claims, 18 pass, 0 fail, 64 out of scope, 0 unexamined.
            `c8c8fc6` is on `origin/phase-1-ingest-and-charts` and run 32913545848 is green on both
            `windows-latest` and `macos-latest`. The 1.11 rehearsal counts were re-read from the live
            store and match the record to the row on all ten tables.
            After the work below: `tools/ci.ps1` green, 22 steps, 187 tests. `tools/verify-phase`
            green, 115 claims, 46 pass, 0 fail, 69 out of scope, 0 unexamined.

Findings:   Observation. `coverage-reported` was a row in CLAUDE.md's Checks table, a paragraph
            calling it the check that matters most, and no code anywhere. Four of the twenty declared
            checks did not run, and the table said which of them ran nowhere.
            Reading: the obligation raised at 1.2, that the table should list every check that runs,
            was discharged at 1.7 by a sweep rather than by a check, and the table had since drifted
            the other way with nothing able to notice.
            Observation. A check that stops running leaves no trace. `dotnet test --filter` exits 0
            when the filter matches nothing, so the CI step passes; the phase report assembles its
            coverage section from whatever files sit in `artifacts/checks`, so the run is measured
            against itself. Reproduced before the fix: deleting one coverage record and re-running
            the report left the verdict GREEN and changed one summary number nobody compares.
            Observation. `architecture-conformance` read 5 of ARCHITECTURE.html's 17 tables. The
            other 12 produced no verdict at all, including "Running on Windows and macOS", whose six
            rows are live phase-1 properties with checks already standing behind most of them.
            Reading: absent is worse than unexamined, because unexamined is the verdict that blocks.
            Observation. ARCHITECTURE.html's move procedure still told an operator to count `setup`,
            `setup_signal`, `forward_return`, `trade` and `variant`, none of which exists. That is
            the defect 1.11 found and recorded as fixed; it was fixed in RUNBOOK.md only.
            Reading: `stated-counts` compared the two statements of the procedure by row count, ten
            against ten, which is what a count does when the disagreement is in the words.
            Observation. The hard rule that prices are TEXT in storage had no check, the same shape
            as the absolute-path rule 1.11 closed. No `REAL` column exists today. It compounds:
            `store-portability` scans TEXT columns and says in its own source that TEXT suffices
            because prices are TEXT, so a REAL price column would have narrowed that scan too.
            Observation. RUNBOOK's first-time setup says to put the data root outside the repository.
            The shipped default in every `appsettings.json` is `data`, and the store in use is at
            `data/live` inside it. Gitignored, so nothing leaks. Not fixed; a decision about which of
            the two is right, left for the sign-off session.

Built:      `coverage-reported`, reconciling CLAUDE.md's roster against the traits in the suite, the
            steps in `tools/ci.*` and the checkpoints BUILD_PLAN schedules, in four directions. The
            Checks table gained a Runs column so each row says whether it runs every CI run, runs as
            the matrix, or names the checkpoint that starts it, and a checkpoint row obeys the rule a
            deferred claim obeys.
            `PhaseReportStage` now requires a coverage record from every check the roster says runs,
            and treats the roster's own absence as a reason, which is what closes the loop rather
            than moving it. Both cases proved by hand: a deleted record and a missing roster each
            turn the report NOT GREEN with a named reason and exit 1.
            `price-storage-form`, asserting that no migration declares a column with REAL affinity,
            on SQLite's own rule rather than on the literal word, so `DOUBLE PRECISION` cannot pass.
            `architecture-conformance` now places every table in the document, asserts the six
            two-platform rows against the properties that hold, and compares the move procedure step
            by step against RUNBOOK's on the stores each step names rather than on its wording.
            Eleven permanent proofs in `CheckProofTests`, each breaking one of the new checks on
            purpose: a declared check nothing implements, a CI step naming a check that does not
            exist, a step whose name and filter disagree, a check that states no coverage, a check
            deferred to a checkpoint that has landed or that the plan does not have, and a procedure
            step naming stores the other document does not.

Measured:   17 tables in ARCHITECTURE.html, all now placed: 12 read for claims or exempt by name
            with the reason, 5 out of scope naming the checkpoint that ends them. Claims rose from
            82 to 115 and passes from 18 to 46 with unexamined still 0.
            21 checks on the roster: 17 every CI run, 1 the matrix, 3 named to 2.6, 2.10 and 4.6.
            10 migration files, 13 tables, 70 column declarations, 0 with REAL affinity.

Decided:    The obligation raised at 1.9 is **dropped**, on the terms it set itself. It could not be
            run: the vendor client has `GetBulkSplitsAsync`, per date at 100 calls, and
            `GetDailyHistoryAsync`, which returns bars. Nothing fetches one name's splits, so
            RUNBOOK's step 5 described work with no implementation and the obligation's own wording,
            that it "has not run", named a command that does not exist. What it would buy is the
            history of splits from before the lab started, which nothing reads: splits arrive nightly
            from the bulk endpoint from the first night onward, and the detector run that would look
            further back writes to `calibration_setup` (see: The evidence store holds only setups
            flagged forward, never setups reconstructed from history). Step 5 is gone from RUNBOOK
            and the row is gone from the carried-obligations table.

Carried:    Three obligations still fall due at 1.12 and none is discharged. The `CONFIRMED` values
            from 1.6, which need a person reading a charting platform; the second machine from 1.11;
            and the fixture's single market day from 1.7. All three need the operator rather than a
            build session, which is why they are still here.

## Phase 1 review — 2026-08-26 — phase-1-ingest-and-charts — the price basis, and three obligations narrowed

Not the 1.12 sign-off entry. Same review session as the entry above it, which committed code and so
may not sign this phase off.

Measured:   **The basis. IndicatorEngine reads `adj_close`, and so does the derivation.**
            `IndicatorEngine.Calculate` fills its price array from `bar.AdjustedClose` and puts high
            and low on the same basis through each bar's own `adj_close/close` factor;
            `tools/derive-indicators.py` selects `adj_close` and applies the same factor. Both
            asserted by reading the two sources, not inferred from the values.
            **IESC's values are current and are on the adjusted basis.** Recomputed from the live
            store after the rebuild: ema_9 352.9966, ema_21 353.2321, ema_50 343.3746, atr_14
            24.1364, adr_20 0.0670, range_avg_20 23.3959, dollar_volume_median_20 204,580,994.64.
            Identical to the figures recorded at 1.6, 0 disagreements at 4 decimal places over all
            three tickers.
            The same window computed on the raw close instead gives ema_9 542.6366, ema_21
            623.0237 and ema_50 648.7660. The stored figures match the adjusted basis exactly and
            are nowhere near the raw one.
            The rebuild had landed before those values were taken. The refetch at
            2026-08-25T18:39:05.228Z wrote 743 restated bars for IESC, the demand was marked
            satisfied at 18:43:31.935Z, and the indicator row was computed after that. Its
            `computed_at` reads 2026-08-25T00:00:00.000Z because migration 009 stamped every row
            that predated it at the first instant of its own session; 27 rows carry that stamp and
            1,989 carry a real one.

Findings:   Observation. The premise the basis check was raised on does not hold against any source
            in the repository. IESC closed 324.12 on 2026-08-24 and its adjusted close was also
            324.12, in the live store, in `fixtures/captured/history-IESC.json` and in
            `bulk-end-of-day.json`. There is no 353.97 and no 176.985 anywhere.
            Reading: the split is in the raw series between 2026-08-19 and 2026-08-20, where the
            raw close falls from 697.38 to 341.635. The doubling is on the bars before the split,
            which carry a raw close near 700 against an adjusted close near 350, and the two
            columns agree from 08-20 onward. So the averages near 353 are the adjusted basis: they
            sit at the level of the adjusted series through August, and the raw basis over the same
            window is 543 to 649. The cross-check from the range figures agrees and is independent
            of any column: range_avg_20 over adr_20 is 23.3959 over 0.0670, a price level of 349,
            and the last twenty adjusted closes average about that.
            Observation. Nothing in the suite could have settled that. Every other arithmetic test
            builds its own bars and sets both price columns from one number, so all of them would
            have passed unchanged against an engine reading the raw close. Reading: the basis was
            arguable from a progress entry precisely because no test asserted it against a series
            somebody else had adjusted.
            Observation. `UniverseBuilder.Median` was a byte-for-byte copy of `Averages.Median`.
            Reading: found while wiring the liquidity measurement to the one the screen uses.
            IndicatorEngine's own comment three files away says computing the median two ways in
            two components would make the screen and the indicator disagree, and it was computed
            two ways. The comment asserted the property; the code duplicated it.
            Observation. A regex in the new CONFIRMED provenance check was written with literal
            backspace characters where a word boundary was meant, so it required a control
            character either side of the date and matched nothing. Reading: it made the check
            accept every input while appearing to test one, and greps rendered it as the intended
            pattern. It was caught by the permanent proof, on its first run, which is the argument
            for the rule that a check is not finished until something breaks it on purpose.

Built:      A permanent test that the averages are on the adjusted basis, asserted against the
            vendor's own `adjusted_close` in the committed capture rather than against a stored
            figure, on a ticker whose window contains a split. It checks both directions: equal to
            the adjusted recursion, and at least 1.4 times below the raw one, so the case cannot
            decay into a comparison between two numbers that are near each other. Proved by
            pointing the engine at `close` and watching it fail, then restoring it.
            `UniverseBuilder.Median` now delegates to `Averages.Median`.
            The liquidity floor, measured over the twenty sessions it is defined on, for every
            fixture name. 65 new expectations, of which 30 are the medians and are `DERIVED`.
            A `rehearsal` job on `ubuntu-latest` running `tools/ci.sh` and then the store copy.
            `voidedBecause` on an expectation, and two rules on `CONFIRMED` rows: the producer must
            name the platform and the date it was read, and a confirmed `adr20` must either record
            the platform's definition or be declared void. Six more permanent proofs.

Verified:   `tools/ci.ps1` green, 22 steps, 194 tests. `tools/verify-phase` green: 115 claims, 46
            pass, 0 fail, 69 out of scope, 0 unexamined; 328 expectations, 66 DERIVED, 262 FROZEN,
            65 changed since the last commit, which is the 65 added.
            The liquidity half: 30 fixture names carry a full twenty-session window, 0 are short of
            it, and all 30 clear the floor. Every one of the 30 medians reproduces under
            `tools/derive-indicators.py` against the replay's own store, 0 disagreements at 4
            decimal places. The lowest is PAYO at 34,889,899.64 against a floor of 20,000,000, a
            margin of 1.7 times.
            The store copy, run locally against the replay store as the rehearsal job runs it: 10
            tables, 37,299 rows, integrity ok, every count matched. The ubuntu job itself has not
            run: it runs on push and nothing has been pushed.

Decided:    The obligation raised at 1.7 is **closed against both halves**. The half the fixture can
            support is now measured over the window the floor means. The half it cannot, the
            whole-market screen, is out of scope in `fixture-replay` with the condition that ends
            it written down: twenty bulk days, 1,900 calls and about 130 MB committed for ever, to
            close a gap the live run closes nightly. Recorded with it, because it is the weakness
            of the half that was done: no fixture name is below the floor, so the comparison
            exercises the passing side only.
            The obligation raised at 1.11 is **narrowed to step 6**, copying the secrets file, and
            is due at the actual move rather than at a checkpoint. Everything else it carried now
            runs on every push on a case-sensitive filesystem.

Carried:    **The `CONFIRMED` values, still owed, and now the only obligation left at 1.12.** The
            fixture reports 0 CONFIRMED. The machinery is ready and the values are not, because
            they cannot be produced here: they are a person reading a platform. What is owed is one
            value per ticker, against the window ending **2026-08-24, 150 sessions**, seeded on the
            simple mean of the first n:

              LITE  ema_9 862.2732  ema_21 841.9685  ema_50 826.9671  atr_14 81.0755  adr_20 0.0923
              PAYO  ema_9   7.1103  ema_21   7.0987  ema_50   6.8553  atr_14  0.0432  adr_20 0.0054
              IESC  ema_9 352.9966  ema_21 353.2321  ema_50 343.3746  atr_14 24.1364  adr_20 0.0670

            LITE and PAYO first: neither has an adjustment anywhere in three years, so a
            disagreement on them is a formula fault and nothing else. IESC can now be read too, on
            the basis settled above, and it is the only one of the three where a platform may
            legitimately differ depending on whether it has applied the two-for-one.
            Two things to expect rather than to be alarmed by. The nine and twenty-one day averages
            should agree closely; the fifty-day one may not, because a platform seeding over its
            whole available history seeds in a different place, and that difference is information
            about the seed rather than a fault. And `adr_20` here is the mean of (high-low)/close.
            A platform computing sma(high,20)/sma(low,20)-1 is reporting a different quantity under
            the same name, and that row is then recorded void rather than as agreement.
            Each value is written into `fixtures/expectations.json` with a `CONFIRMED` tier and a
            producer reading "read from PLATFORM on YYYY-MM-DD", which the check now requires.

## Phase 1 review — 2026-08-26 — phase-1-ingest-and-charts — corrects the attribution of the basis hypothesis above

Not a checkpoint entry. A correction to the entry above it, which recorded the basis investigation
without saying where the hypothesis came from, and to this session's own report of it, which framed
it as an instinct that turned out right. Both readings would mislead a later session, in opposite
directions.

Findings:   **Attribution, stated first because this will be read as history.** The hypothesis that
            IESC's averages sat on the raw basis was **this review's**, not the operator's. It was
            built on two figures, a raw close of 353.97 and an adjusted close of 176.985, taken
            from an earlier build report rather than from the repository, and it asserted a defect
            in shipped code without opening the captured history that would have settled it in one
            read. It is not recorded as the operator suspecting something, and it is not recorded
            as an instinct that turned out right. It was a claim made without reading the source,
            in a corpus whose first rule about verification is to read before asserting. The
            reasoning offered with it was internally sound and rested on a premise nobody checked,
            which is the failure mode worth naming: a chain of correct arithmetic over a number
            that is not in the store reads exactly like a finding.

            Observation. Neither figure appears anywhere in the repository. The live store,
            `fixtures/captured/history-IESC.json` and `fixtures/captured/bulk-end-of-day.json` all
            give IESC's 2026-08-24 close as 324.12 with the adjusted close equal to it. The split
            lands between 2026-08-19 and 2026-08-20, where the raw close falls from 697.38 to
            341.635, so the doubling is on the bars before it: raw near 700 against adjusted near
            350, with the two columns equal from 08-20 onward.
            Observation. Both implementations read `adj_close`, asserted by reading both sources
            rather than inferred from their output. `IndicatorEngine.Calculate` fills its price
            array from `bar.AdjustedClose`; `tools/derive-indicators.py` selects `adj_close`. Each
            puts high and low on the same basis through the bar's own `adj_close/close` factor.
            Observation. Recomputed from the live store after the rebuild had landed: identical to
            the figures recorded at 1.6, 0 disagreements at 4 decimal places across all three
            tickers. The same window on the raw close gives ema_9 542.6366, ema_21 623.0237 and
            ema_50 648.7660, which the stored figures are nowhere near.

            Reading, kept separate from the above. **The suite could not have settled it either
            way, and that gap was real even though the defect was not.** Every arithmetic test in
            `IndicatorEngineTests` builds its own bars and sets both price columns from one number,
            or sets the factor itself, so every one of them passes unchanged against an engine
            reading the raw close. A test that supplies both sides of a comparison cannot say which
            side the code took. That is why the basis was arguable from a progress entry at all,
            and it is the part of the episode that was worth acting on.

Built:      `A_split_inside_the_window_leaves_the_averages_on_the_vendors_adjusted_basis`, asserted
            against the vendor's own `adjusted_close` in the committed capture rather than against
            anything this lab stored, on the one fixture ticker whose window contains a split. It
            asserts both directions: equal to the adjusted recursion written out locally, and at
            least 1.4 times below the raw one, so it cannot decay into a comparison between two
            numbers that happen to sit near each other. Proved by pointing the engine at `close`,
            watching it fail, and restoring it.

            **Recorded explicitly, because the fix outlives the reason for it.** A later session
            reading only the test will find a case added for a defect that never existed and may
            conclude it can be removed. It cannot. The test was not written because the engine was
            wrong; it was written because nothing could show that the engine was right, and that
            is still true of every other test in the file. Removing it restores the position where
            a raw-column engine passes the whole suite.

## Phase 1 review — 2026-08-26 — phase-1-ingest-and-charts — the floor comparison, and the side nothing exercises

Not a checkpoint entry. Closes the second half of a gap the entry above opened: the liquidity floor
is now measured over the window it is defined on, and every expectation on it is an expectation on
it passing.

Findings:   Observation. All 30 measured fixture names clear both floors. The closest to either is
            PAYO, at 34,889,899.64 against a liquidity floor of 20,000,000, a margin of 1.7 times,
            and at 7.11 against a price floor of 5. No captured name fails either floor.
            Reading: the screen's rejecting path carries no expectation anywhere, so it could stop
            rejecting without a single figure in the diff moving. A screen tested only where it
            admits is half a test, and the half that is missing is the half that keeps names out.

            Observation. The three index trackers are the only captured names the universe
            excludes, and they are not floor rejections. `exchange-symbol-list.json` types SPY,
            QQQ and IWM as `ETF`, so the screen drops them on security type before either floor is
            reached, and their twenty-session medians are 32,350,637,349, 23,907,112,602 and
            5,408,259,360 against a floor of 20,000,000, clearing it by between 270 and 1,600
            times. Each is far above the price floor too.
            Reading: they were the obvious candidate for the rejecting case and they are the wrong
            one. Recording them as floor rejections would file a type rejection under the wrong
            heading, leave the floor's rejecting side untested while the diff showed both sides
            covered, and that is worse than leaving it open and saying so.

Built:      The trackers measured against both floors and recorded under `tracker.*`, deliberately
            apart from the universe names. Ten expectations: three medians, independently derived
            outside the solution from the captured vendor histories and matching to four decimal
            places; three saying each clears both floors; three saying each is still not a universe
            member; and the count.
            What that pins is the security-type filter, against the strongest case it has: a name
            that passes every floor and is not admitted anyway. That is worth having on its own and
            it is not the floor's rejecting side.

Measured:   30 universe names measured, 0 short of the window, 30 clearing the floor, 0 below it.
            3 trackers measured, 3 clearing both floors, 0 admitted to the universe.
            `tools/verify-phase` green: 338 expectations, 69 DERIVED, 269 FROZEN, 0 unexamined.

Decided:    **The floor's rejecting side is out of scope, with the reason and the condition
            recorded in `fixture-replay` beside the whole-market screen.** No captured name fails
            either floor, and none of the 33 can be made to without capturing a different name.
            The condition that ends it is one per-ticker history call for a name chosen to fail, at
            the next capture.
            The price is stated because the two out-of-scope items in that check now cost very
            different things and would otherwise read as equivalent. The whole-market screen needs
            twenty bulk days, 1,900 calls and about 130 MB committed for ever. This one needs one
            call. It rests out of scope because nothing in the fixture fails a floor today, not
            because closing it is expensive, and a session that has a low-liquidity name to hand at
            the next capture should simply close it.

## 1.12 — 2026-08-26 — phase-1-ingest-and-charts — **the phase does not sign off in this pass**

The sign-off pass. A session that had committed nothing to this repository before it started, which
is what the fresh-session rule buys, reproduced the record rather than reading it and then went at
the machinery the two review entries above built. The verdict is at the end and it is not green.

The checkpoint and the phase are separate here and the entry is recorded on purpose. 1.12's own done
condition is a fresh session, findings batched into one pass, and `verify-phase` green with nothing
unexamined; all three hold, so the checkpoint ran and this is its record. What it produced is a
verdict of not signed off, and what blocks the phase is a done condition failed at 1.9 and two
checks that assert less than they claim, none of which is 1.12's to have caused.

Reproduced: `tools/ci.ps1` and `tools/ci.sh` both green at commit 535f9b3, 22 steps each, in the
            same order, 194 tests. `tools/verify-phase` green: 115 claims, 46 passed, 0 failed, 69
            out of scope, 0 unexamined; 338 expectations, 69 DERIVED, 269 FROZEN; coverage examined
            193,544; inputs 37 CAPTURED, 48 AUTHORED; 0 expectations changed since the last commit.
            Every figure below was read from `artifacts/replay.db` or from `fixtures/captured`, not
            from an entry above.

Verified:   The check roster reconciled by hand against the corpus rather than through
            `coverage-reported`: 21 rows in CLAUDE.md, 17 declared "every CI run" and all 17
            implemented as a trait, invoked as a named step by both scripts, and leaving a record
            in `artifacts/checks`. 4 rows deferred or matrix, none implemented. Nothing implemented
            that the roster does not declare, no record without a row, no step invoking a name no
            test carries. The reconciliation holds in all four directions.
            All 69 out-of-scope claims name a closing checkpoint, every one of those exists in
            `BUILD_PLAN.md`, and none of them appears in this file. The 149 out-of-scope coverage
            items all carry a reason, and the two in `fixture-replay` carry a price: 1,900 calls and
            about 130 MB for the whole-market screen, one per-ticker call for the floor's rejecting
            side.
            The three tracker medians recomputed from `fixtures/captured/history-{SPY,QQQ,IWM}.json`
            in a separate language, over the trailing twenty sessions to 2026-08-24, as the mean of
            the two middle values of close times volume: 32,350,637,349, 23,907,112,602 and
            5,408,259,360, matching the expectations exactly and matching `index_bar` in the replay
            store. Their DERIVED tier is earned. None of the three is a `universe_member`.
            30 universe names measured in the store, 30 clearing the liquidity floor, 0 below it, 0
            short of the window. `tools/derive-indicators.py` reads `daily_bar` and compares against
            `indicator_daily`, so it derives from the inputs rather than from the outputs.
            Both carried obligations are still carried with their due points: the CONFIRMED values
            at 1.12, step 6 of the move at the move. Neither was attempted.

Built:      Six proofs of the table-placement pass in `CheckProofTests`, which had 34 and none of
            them on the property the whole `architecture-conformance` widening rests on. Before
            writing them the property was tested the only way that settles it: a table on neither
            list was added to `ARCHITECTURE.html` and the check was run. It reported one unexamined
            claim naming the table, and `phase-report` exited 1 saying NOT GREEN. The partition is a
            partition. `TablePlacementClaims` is now callable so the proof is permanent rather than
            a break-and-revert done by hand once, and its unused `Schedule` parameter is gone: the
            placement claims already go through `OutOfScopeProblems` with every other claim, which
            is where a table exempted to a landed checkpoint is caught.

Measured:   `ci.ps1` and `ci.sh` green, 22 steps, 200 tests. `verify-phase` green, 115 claims, 0
            unexamined, 338 expectations, coverage examined 193,558.

Findings:   Finding. **A check can run, write a coverage record, and examine nothing, and neither
            `coverage-reported` nor the phase report will say so.** `coverage-reported` asserts that
            a check constructs `CheckCoverage` and calls `Report()`, which is a source scan for the
            shape of a statement, not an assertion about the number in it. `CheckCoverage.Report()`
            accepts zero examined without complaint. The phase report sums `Examined` across the
            checks and compares that sum to nothing.
            Reproduced twice. Breaking the production-source enumerator so nothing was scanned left
            `bar-append-only` passing with "0 source files scanned" in its own record; that break
            was caught, but by the floor assertions in `writer-ownership`, `clock-usage` and
            `architecture-conformance`, which happen to share the enumerator, not by
            `bar-append-only` and not by `coverage-reported`. Narrowing `BarTables` to the one table
            no migration has created was not caught by anything: the suite passed 194 of 194, the
            phase report said GREEN, and the only trace was the total examined moving from 193,544
            to 193,536.
            Reading: 193,544 is not an aggregate, it is `store-portability`'s 189,726 plus noise, so
            it is structurally incapable of showing any other check's scope collapsing. Twelve of
            the eighteen check files state a floor in advance and the rest do not, and which ones do
            is not a property anything holds. This is the corpus's own named failure mode, arrived
            at one level up: green means nothing I ran failed, and nothing was run.

            Finding. **Done condition seven is asserted over the whole fixture and the condition is
            per checkpoint.** `FixtureReplayCheck` says "Done condition seven, asserted rather than
            remembered" and then asserts that the fixture holds at least one DERIVED or CONFIRMED
            row anywhere. The clause reads "The checkpoint's expectations ... and at least one of
            them is DERIVED or CONFIRMED", and the comment's own next sentence names the right unit.
            Counted per checkpoint: 1.3 six FROZEN, 1.4 five FROZEN, 1.5 six FROZEN, 1.7 four
            FROZEN, 1.9 three FROZEN, none of them with a derived expectation of their own. 1.6,
            1.8, 1.10 and 1.12 clear it.
            1.3, 1.4 and 1.5 predate the fixture and were back-filled at 1.7 as the plan required,
            but that row promises only "back-fills expectations for 1.1 to 1.6 with their tiers",
            which is narrower than the clause it discharges. 1.9 landed after the fixture existed,
            carries three FROZEN expectations, and has no carried obligation recorded anywhere.
            Reading: 1.9 failed done condition seven and nothing said so at the time, which is the
            same shape as an out-of-scope claim resting at a checkpoint that has landed. It is also
            cheap to close: the index bar counts and date range derive from the three captured
            histories with no vendor call at all, exactly as the tracker medians just did.

            Finding. An out-of-scope **claim** must name a checkpoint that exists and has not
            landed, and three assertions enforce it. An out-of-scope **coverage item** carries free
            prose and nothing reads it. Seven checks record 149 of them. Four name a checkpoint only
            obliquely, as "the checkpoint that ingests it" or "the checkpoint that builds its
            component", which is the wording that cannot go stale because it never resolves to
            anything.
            Reading: the two halves of the same rule are enforced very differently, and the
            unenforced half is the larger one by count.

            Observation. `PROGRESS.md` states PAYO's twenty-session median dollar volume as
            34,889,899.64 at two places, in the entries of 2026-08-26. The fixture, the replay store
            and an independent computation over the captured history all give 34,889,899.60, which
            is also what the 1.6 entry records. A transcription, corrected below.
            Reading: it is a digit and it changes nothing, and it survived in the same entry that
            argues a figure must not be argued from arithmetic over a progress entry. Nothing checks
            a figure in a record against the fixture that holds it.

            Observation. 33 DERIVED expectations were added at 1.12 and 30 of them restate values
            the fixture already held under an `indicators.*` id, byte for byte, from the same script
            over the same bars. What they add is a second reader path, `UniverseBuilder`'s window
            selection rather than `IndicatorEngine`'s stored row. What they do not add is a second
            derivation. The count of new independent facts at 1.12 is three.

            Observation. `PhaseReplay.LiquidityFloorFigures` says it measures from the stored bars
            "because the floor is UniverseBuilder's and this has to fail if the two ever compute the
            median differently". The same commit made `UniverseBuilder.Median` a forwarder to
            `Averages.Median`, so there are no longer two implementations to differ. The comment
            describes a guard the change beside it removed the possibility of. The change itself is
            right and the production diff at 1.12 is behaviour-preserving: the removed body was byte
            for byte what it now calls.

            Observation. The bound in the basis test, `onRaw > onAdjusted * 1.4m`, is a magic number
            with a comment. What it is a bound on: the ratio of the raw-basis EMA to the
            adjusted-basis one over IESC's 150-session window, which holds a clean two-for-one with
            the last three sessions unadjusted. Measured, the ratios are 1.5372 at period 9, 1.7638
            at 21 and 1.8894 at 50. 1.4 sits under the tightest of the three, ema_9, with about nine
            percent of headroom, and ema_9 is the tightest because the shortest average carries the
            most post-split weight. The comment says "roughly twice", which is the factor at the
            start of the window rather than any of the three ratios the assertion compares.

Decided:    **Phase 1 does not sign off in this pass, and it is not blocked on the CONFIRMED
            values.** Those are correctly carried, cannot be discharged by any build session, and
            the basis test added at 1.12 buys part of what they were for by checking the averages
            against the vendor's own adjusted close, which is a third party's arithmetic if not a
            platform's readout. Holding the phase on an act no session can perform would make the
            due point permanent rather than pending.
            What blocks it is the first two findings, both of which are a check asserting less than
            its own label claims, which is the defect this corpus is built to catch and the one it
            has now shipped twice. Four things close it and none is expensive: a floor on examined
            for every check, or the equivalent read from the records by `coverage-reported`; done
            condition seven asserted per checkpoint, with proofs; the derived expectations owed at
            1.9, and at 1.3, 1.4 and 1.5 if the narrow BUILD_PLAN row is corrected rather than
            relied on; and the out-of-scope coverage reasons put under the rule the claims already
            obey.

Carried:    The six new proofs are this session's own code and are not covered by this pass. The
            next session reviews them along with whatever closes the four items above.

Stopping:   This pass found as much as the one before it, and two of its findings are the same
            shape as that pass's headline. By the stopping rule that means a third pass is owed
            rather than that the phase is done.

## Phase 1 corrections — 2026-08-26 — phase-1-ingest-and-charts — corrects PAYO's median in two entries

Not a checkpoint entry. Corrects a figure, changes nothing built.

Corrects:   The entry of 2026-08-26 titled "the price basis, and three obligations narrowed" and the
            entry of 2026-08-26 titled "the floor comparison, and the side nothing exercises" both
            state PAYO's twenty-session median dollar volume as 34,889,899.64. It is
            **34,889,899.60**. That is the value in `fixtures/expectations.json` under both
            `indicators.PAYO.medianDollarVolume` and `liquidity.PAYO.medianDollarVolume20`, the
            value in the 1.6 entry of 2026-08-25, the value derived from `artifacts/replay.db`, and
            the value derived from `fixtures/captured/history-PAYO.json` outside the solution.
            Nothing else in either entry depends on it: the margin over the liquidity floor is 1.7
            times either way, and PAYO is the closest name to the floor either way.

## 1.12 — 2026-08-26 — phase-1-ingest-and-charts — **the two blockers closed, the phase still not signed off**

The third pass, and scoped rather than open-ended. Two sessions had each found a check asserting
less than its own label claims; the pattern was established, so this pass existed to close it
rather than to look for a fourth instance. It found no third instance because it did not go
looking, and that is stated plainly so nobody later reads "no findings" as "looked and found
nothing".

Built:      `fixtures/checks-baseline.json`, committed: a floor on the examined count of every
            check the roster declares. `CheckCoverage.Report` writes its record first and then
            compares, so a check that narrowed still leaves the number it narrowed to where the
            report can read it, and then fails naming the check and printing both figures. It is a
            floor and not an equality, because counts differ between platforms and grow with the
            corpus, and a guard that goes red on a file being added gets suppressed, which is a
            slower way of deleting it. A check with no floor fails rather than being waved through,
            or the guard would cover whatever existed when it was written and nothing added since.
            The file sits beside the golden fixture and for the fixture's reason: it is a reference
            the run is measured against, never a result the run produces. `git ls-files artifacts`
            is still empty.

            Done condition seven asserted per checkpoint in `fixture-replay`, where it had been
            asserted over the whole fixture. A checkpoint with no `DERIVED` or `CONFIRMED`
            expectation either fails or names a carried obligation, and the naming is checked the
            way an out-of-scope architecture claim is checked: the obligation has to be a row of
            BUILD_PLAN's table and its due checkpoint has to be one this file does not yet record.
            Asserted in both directions, so a permit a checkpoint has outgrown is reported as spent
            rather than left to re-permit it silently if the expectation were ever removed.

            1.9 closed rather than carried. `tools/derive-indicators.py --index` reads the captured
            responses rather than the store, applies the ingestor's window from its own statement of
            it, and produces six figures per tracker; `PhaseReplay.IndexFigures` measures the same
            six back out of `index_bar`. Eighteen `DERIVED` expectations, no vendor call. The three
            totals 1.9 landed with stay, and they are what they always were.

            Eleven permanent proofs, five on the floor and six on the per-checkpoint condition,
            including the two directions of the baseline file against the roster.

            Two record corrections. `PhaseReplay.LiquidityFloorFigures` no longer claims to fail
            "if the two ever compute the median differently"; there is one implementation and the
            comment now says what the code does, which is to select the floor's window rather than
            the engine's through a second call to the point-in-time reader. The bound in the basis
            test is stated as a floor under the tightest of the three ratios it compares, with the
            three ratios written down, instead of being attributed to a split factor it never
            measures.

Measured:   `tools/ci.ps1` green, 22 steps, 211 tests. `tools/verify-phase` GREEN: 115 claims, 46
            passed, 0 failed, 69 out of scope, 0 unexamined; 356 expectations, 87 `DERIVED`, 269
            `FROZEN`, 0 differed, 0 missing; coverage examined 193,691 with 0 unexamined; inputs 37
            `CAPTURED`, 48 `AUTHORED`; 18 expectations changed since the last commit, which are the
            eighteen added here and no others.

            The falsification, run rather than argued. `BarAppendOnlyCheck.BarTables` narrowed to
            `daily_bar` alone: the check reported `bar-append-only examined 50 against a floor of
            54`, the step exited non-zero, and the run named the check. Reverted, and the same
            narrowing is now written into `CheckProofTests` so it never has to be done by hand
            again.

            The basis ratios re-derived in this session from `fixtures/captured/history-IESC.json`,
            outside the solution: over the 150-session window the raw-basis average divided by the
            adjusted-basis one is 1.5372 at period 9, 1.7638 at 21 and 1.8894 at 50. The assertion's
            1.4 clears the tightest of them by 9.8 percent. 147 of the 150 bars carry an adjustment,
            which is why period 9 is the tightest: the shortest average carries the most weight at
            the end of the window, where the two bases have converged.

            The trackers, derived from the captured responses: 251 bars each, 2025-08-25 to
            2026-08-24. SPY 642.4700 raw and 635.4292 adjusted at the first session, 763.4700 at the
            last; QQQ 570.3200 and 567.5839, 706.3200; IWM 232.3600 and 229.9714, 297.9700. The raw
            and adjusted close are taken at the first session because at the last they are equal for
            all three, where a pair read out of the wrong column would agree with itself.

Reviewed:   The six proofs the pass above added, which were its own code and outside its own scope.
            They hold. Each states one verdict of the four the placement partition produces, the
            orphan case asserts both the verdict and that the claim names the table, and the count
            proof is what catches a parser stopping early rather than failing. The signature change
            beside them, `TablePlacementClaims` losing its `schedule` parameter and becoming public,
            is behaviour-preserving: the parameter was unused, and the schedule rule those claims
            are subject to is applied at the call site, where every claim goes through
            `OutOfScopeProblems` after the placement claims are added. No finding.

Decided:    **The `CONFIRMED` values carry rather than block, as a judgement rather than by
            default.** Nothing reads them before phase 2's calibration, the basis test added at the
            pass above buys part of what they were for by checking the averages against the vendor's
            own adjusted close, and no build session can perform the act they need. Their obligation
            moves from 1.12 to 2.11, which is the first checkpoint that reads those figures; holding
            a phase on an act no session can perform would make the due point permanent rather than
            pending.

            **The phase does not sign off in this pass, and the reason is procedural rather than a
            finding.** This session committed code, and the fresh-session rule is that a session
            which has committed code must not sign that code off. What needs a session that did not
            write it is small and enumerable: `CheckCoverage.Shortfall` and the baseline it reads,
            `FixtureReplayCheck.DoneConditionSevenProblems` and the permit mechanism under it,
            `PhaseReplay.IndexFigures`, the `--index` mode of `tools/derive-indicators.py`, and the
            eleven proofs. Everything else in phase 1 has now been through three passes. That is a
            different state from the pass above, which was blocked on two defects; nothing here is a
            defect, and the next pass is a review rather than a repair.

Carried:    The out-of-scope coverage naming rule, raised by the pass above as one of four things
            closing the phase and left undone by this one, is now an obligation in `BUILD_PLAN.md`
            raised at 1.12 and due at 2.6. It is not a straight copy of the claim rule: two of
            `fixture-replay`'s exemptions close on a purchase nobody has scheduled rather than on a
            checkpoint, and what the rule says about those is the work.

            Done condition seven at 1.3, 1.4, 1.5 and 1.7, now an obligation raised at 1.1 and due
            at 2.1, with those four checkpoints named under `frozenOnly` in the expectations file
            and the naming asserted. The 1.7 row of `BUILD_PLAN.md` had claimed the obligation
            discharged; it discharged the tiers and not the condition.

            This session's own code, for the next session.

Stopping:   This pass closed what the pass above named and found nothing new, which is the third
            pass finding less than the one before it. The stopping rule points at a review rather
            than a fourth investigation, and the only thing standing between here and sign-off is
            one session that writes no code.

## Phase 1 sign-off — 2026-08-26 — phase-1-ingest-and-charts — **the phase signs off, with one finding and a rule**

The fourth pass and the first that could sign anything off. A session that had committed nothing to
this repository, so the fresh-session rule is satisfied; nothing it commits is code, so it stays
satisfied through the sign-off itself. Scope was the five things the pass above enumerated as its own
work and nothing else: `CheckCoverage.Shortfall` and the baseline it reads, `FixtureReplayCheck`'s
`DoneConditionSevenProblems` and the permit mechanism, `PhaseReplay.IndexFigures`, the `--index` mode
of `tools/derive-indicators.py`, and the eleven proofs.

Reproduced:  `tools/ci.ps1` green at 9c74c86, 22 steps, 211 tests. `tools/verify-phase` GREEN: 115
             claims, 46 passed, 0 failed, 69 out of scope, 0 unexamined; 356 expectations, 87
             `DERIVED`, 269 `FROZEN`, 0 differed, 0 missing, 0 void; coverage examined 193,691 with 0
             unexamined; inputs 37 `CAPTURED`, 48 `AUTHORED`. Every figure the pass above recorded.
             `git ls-files artifacts` is empty and `fixtures/checks-baseline.json` is tracked.
             The eighteen index figures re-derived here by running `tools/derive-indicators.py
             --index fixtures/captured 2026-08-24 SPY QQQ IWM`. All eighteen match
             `fixtures/expectations.json` exactly. The script reads the captured responses and nothing
             else, and its window is written out from the ingestor's statement rather than read from
             the code: at an as-of of 2026-07-24 it keeps 230 bars instead of 251, and with the
             three-year bound cut to zero it keeps 1, so the window is arithmetic rather than a
             constant that happens to agree.
             Eleven proofs counted, five on the floor and six on the per-checkpoint condition, in a
             `CheckProofTests` of 51. All are `[Fact]`s in the suite, so they run in `tools/ci.*` step
             22 rather than being a break-and-revert done by hand once.

Broken:      Five falsifications, run rather than argued, each reverted and the tree left clean.
             **A check narrowed below its floor.** `BarAppendOnlyCheck.BarTables` cut from three
             tables to one: the check failed with "bar-append-only examined 50 against a floor of 54",
             naming the check and both figures, the coverage record was still written showing the 50,
             and `tools/verify-phase` exited 1 with NOT GREEN. `path-casing` failed in the same run
             for an unrelated reason, recorded under Findings.
             **A baseline entry removed.** With `clock-usage` deleted from the baseline the check
             failed with "clock-usage has no floor ... add it to the checks object as clock-usage 48",
             and the proof `Every_check_the_roster_declares_has_a_floor_on_disk` failed naming it.
             Adding a floor for a check the roster does not declare failed the other proof by name.
             Both directions hold against the real file rather than only against a written one.
             **An obligation marked as discharged.** A `## 2.1` entry appended to this file made
             `hasLanded("2.1")` true, and `fixture-replay` failed with four problems, one per
             frozen-only checkpoint, each saying the obligation raised at 1.1 falls due at 2.1 and
             PROGRESS already records it. **A permit outgrown.** One of 1.4's expectations flipped to
             `DERIVED`: the check failed with "1.4 is listed as frozen-only and now carries 1
             independently produced expectation(s). The permit is spent". **A permit removed and a
             frozen-only checkpoint invented.** Deleting 1.7's permit and adding a `FROZEN`-only 2.3
             failed with both named and "nothing permits it" against each.
             **The `--index` derivation pointed elsewhere.** Over a mutated copy of the captured
             responses with SPY and QQQ swapped and one IWM bar deleted, seven of the eighteen figures
             move: the six SPY and QQQ closes swap with each other and `index.IWM.bars` falls to 250.
             The other eleven do not, because the three trackers share a session calendar. That is what
             the per-symbol closes exist to separate, and it is stated here rather than left to read as
             coverage the derivation does not have.

Verified:    The eighteen `DERIVED` index expectations bite on the production code, which is the
             direction that decides whether 1.9 is closed or decorated. `IndexIngestor` made to write
             `adjusted_close` into the raw close column: three expectations failed,
             `index.{SPY,QQQ,IWM}.firstClose`, and **all three of the `FROZEN` totals 1.9 landed with
             passed unchanged**. `IndexIngestor` made to drop the oldest bar per symbol: thirteen
             failed, twelve of them the new `DERIVED` ones and one the `inserted` total. So the three
             frozen totals detect a dropped row and detect nothing about which column was read, and
             the eighteen detect both. Done condition seven at 1.9 is met rather than paperworked.
             The judgement the pass above rested its `CONFIRMED` decision on, checked rather than
             taken: `A_split_inside_the_window_leaves_the_averages_on_the_vendors_adjusted_basis`
             asserts its fixture holds the case in counts stated in advance, writes out the recursion
             so it shares nothing with `Averages`, and bounds the raw basis in both directions. It
             buys the basis half of what a `CONFIRMED` reading is for and not the definition half,
             because its own recursion is the same author's reading of the definition. That is exactly
             what the entry above claimed for it and no more.
             All four carried obligations are carried with their due points and none was attempted:
             done condition seven at 1.3, 1.4, 1.5 and 1.7, raised at 1.1, due at 2.1; the `CONFIRMED`
             values, raised at 1.6, due at 2.11; out-of-scope coverage naming its closing checkpoint,
             raised at 1.12, due at 2.6; step 6 of the move, raised at 1.11, due at the move.

Findings:    Finding. **The examined floor is compared per check and the property it guards is per
             scope, so ordinary corpus growth pays for a narrowing.** `CheckCoverage.Report` sums every
             scope a check names and compares that one number. In five of the seventeen checks the sum
             is dominated by a size-of-corpus figure rather than by the property: `bar-append-only`
             reads 47 source files to hold 3 bar tables, `path-casing` reads 2,412 string literals to
             compare 27 paths, and `clock-usage`, `writer-ownership` and `store-portability` are the
             same shape.
             Run twice rather than reasoned. `BarAppendOnlyCheck.BarTables` cut to one table with five
             ordinary new files added to the Worker: the check passed at examined 55 against a floor of
             54, its own record reading "1 bar tables named by the check", `tools/ci.*` green, the
             phase report GREEN, and the coverage total **higher** than the committed run at 193,735
             against 193,691. `PathCasingCheck` gutted so it compared no paths at all, with forty
             ordinary new string literals added: the check passed at examined 2,479 against a floor of
             2,468, its record reading "0 paths compared against the on-disk name", and the phase
             report GREEN at 193,732. Five files and forty literals are less than one phase 2
             checkpoint's worth of code.
             The same sum misfires from the other side, and that half fires first. In the narrowing
             falsification above `path-casing` went red because removing two string literals from one
             test file dropped it below its floor, which has nothing to do with path casing. The
             corpus's own argument applies to itself here: a guard that cries wolf gets suppressed, and
             a suppressed guard is a dead one arrived at slowly.
             Reading: this is the third time the defect has shipped and each time it was inside the
             thing built to catch the last one. The pattern is not three unlucky checks, it is that the
             number which is easy to compute gets floored and the number carrying the property is the
             one nobody separated out. That is written into `CLAUDE.md` as a rule in this commit, on
             the terms this pass was given. The repair, a floor per scope with the corpus-size scopes
             marked as context, is code and is carried to 2.1.

             Observation. When a permit's obligation has fallen due the assertion fires and the
             coverage record beside it still reads "permitted by the obligation raised at 1.1, which
             falls due at 2.1". The line in `FixtureReplayCheck` that builds that reason resolves the
             obligation and does not re-ask `hasLanded`. The run is red either way, so what is affected
             is the report page on a red run, where four checkpoints read as legitimately permitted
             while the failure above them says they are not.

             Observation. A permit resolves its obligation by the checkpoint that raised it, with
             `FirstOrDefault` over the obligations table. Until this commit every row had a distinct
             `Raised`, so the lookup was unambiguous by accident. This commit adds a second row raised
             at 1.12, and a permit naming 1.12 would now resolve to whichever row `MarkdownTable`
             returns first. Nothing names 1.12 today and nothing checks that nothing does.

             Observation. `PathCasingCheck` records its no-work branch as `NotExamined(..., 0, ...)`.
             `TotalUnexamined` sums counts, so an unexamined item with a count of zero adds nothing and
             the phase report still reports zero unexamined. In the gutted run above the record carried
             an unexamined line and the report said "unexamined 0" on the same page. It is an admission
             that counts as silence.

             Observation. A `DERIVED` expectation carrying `voidedBecause` is void in the diff and
             still counts toward `Independent` in `ByCheckpoint`, so a voided derived row would satisfy
             done condition seven for its checkpoint while comparing nothing. No expectation in the
             fixture is void today, and the tier the void mechanism was written for is `CONFIRMED`.

Decided:     **Phase 1 signs off.** Every deliverable from 1.1 to 1.12 exists, runs and is verified;
             `tools/ci.*` and `tools/verify-phase` are green with nothing unexamined; four passes have
             found nothing wrong with what the lab produces. The finding above is real and it is
             forward-looking: no check in the committed tree examines less than it claims today, and
             every figure in the phase report is true as of this commit. What it defeats is the guard
             against a future narrowing, and the condition that activates it is phase 2 adding files.
             So it fails no done condition and breaks no check, which is the corpus's own test for
             whether a finding reopens a phase, and holding the phase open would put the repair in a
             session that then could not sign it off, which is another repair-and-review cycle for a
             defect that bites nothing yet. It goes into `CLAUDE.md` as a rule now and into the plan as
             an obligation due at 2.1, before phase 2 code starts growing the corpus.

             **The `CONFIRMED` values carry to 2.11 rather than block, confirming the judgement of the
             pass above rather than overturning it.** Checked rather than accepted: nothing in the
             solution reads those figures, no expectation is `CONFIRMED` today, and the basis test does
             buy what it was said to buy. The residual those readings close is a shared misreading of a
             definition, an exponential seed, Wilder's smoothing against a period-14 exponential, a
             range as a fraction against a percentage, and that first changes behaviour at 2.6 rather
             than at 2.11. It is still right at 2.11: that is where a wrong indicator stops being
             visible and starts being compensated for, because it is where a threshold is set against
             the distribution the indicator produces, and it is before the first forward night, so
             discovering it there costs a recalibration against an evidence store that is still empty.
             Any later due point would not be.

Carried:     Five obligations, four unchanged and one added here. Done condition seven at 1.3, 1.4,
             1.5 and 1.7, raised at 1.1, due at 2.1. The `CONFIRMED` values, raised at 1.6, due at
             2.11. Out-of-scope coverage naming its closing checkpoint, raised at 1.12, due at 2.6. The
             examined floor per scope rather than per check, raised at 1.12, due at 2.1. Step 6 of the
             move, raised at 1.11, due at the move.

Stopping:    The scope narrowed each pass and this one, scoped to five named things, returned one
             finding in the same family as the two before it. Under the terms this pass was given that
             is a rule in `CLAUDE.md` rather than a fifth pass, and the rule is written. Phase 2 starts
             at 2.1, whose first work is the obligation above.

## 2.1 — 2026-08-26 — phase-2-detection — the signal library, the floor per scope, and two obligations closed

The first checkpoint of phase 2, and the only one with no strategy in it. Four commits in a stated
order, because the spec pass authors what the code is written against and a session editing a spec
beside the code consuming it is the weakest review position available.

Built:      **(a) The malformed obligation row, and the parser that dropped it.**
            `docs/BUILD_PLAN.md`'s carried-obligations row for the per-scope floor carried two
            pipe-delimited cells where every other row carries three, its due point written into the
            obligation prose rather than into the `Due at` column. `Schedule.Read()` guarded with
            `if (row.Count >= 3)` and dropped it silently.
            `MarkdownTable` now asserts every body row against its own table's header width and
            throws naming the table, the row and both widths; the guard at the call site is gone.
            `FixtureReplayCheck` no longer resolves a permit's obligation with `FirstOrDefault` over
            `Raised`. Two rows raised at one checkpoint is legitimate, because the table is keyed by
            who raised an obligation rather than by the obligation; a permit naming such a checkpoint
            is not, because it uses `Raised` as a key. `MatchingObligations` returns every match and
            the caller reports none and more-than-one separately. `PermitReason` re-asks `hasLanded`
            rather than resolving the obligation and stopping there.

            **(b) The spec pass.** `docs/SCHEMA.md` gains a `## Signals` section: every signal with
            its formula, its source columns and a status of `active` or `candidate`, grouped by axis.
            Five decisions in `docs/DECISIONS.md`: the scans select a fixed count by rank rather than
            a threshold on the move; every scan magnitude is computed on the adjusted basis; the
            cluster grouping key is industry; a released cap slot goes to the side that still has
            candidates; a calibration run reconstructs against current membership and computes its
            indicators in memory.
            `mixed` defined as a grade rather than a gap, the squeeze test pinned to 20 sessions
            where the contraction test beside it already was, ThemeClusterer restated to count over
            scan hits so its stated clock and its stated dependency agree, SectorResolver added to a
            build-order phase for the first time, and two authored-parameter rows added for scan
            breadth and the month-mover lookback.

            **(c) The examined floor, per scope rather than per check.** A scope whose size is a fact
            about the corpus is recorded through `CheckCoverage.Context` rather than `Examined`: it
            is reported, carries no floor, and is never summed with the scope holding the property.
            `fixtures/checks-baseline.json` holds a floor per scope and the names of the context
            scopes. Four directions are asserted, not one: below its floor, no floor at all,
            reclassified from property to context without the baseline agreeing, and a floor naming a
            scope the run did not produce.
            `TotalUnexamined` counts admissions rather than their sizes.

            **(d) The four frozen-only checkpoints closed.** `tools/derive-indicators.py --universe`,
            on the pattern `--index` established at 1.12: it reads `fixtures/captured` and nothing
            else, restates the security-type filter and both floors from their own statements, and
            joins the captured bulk day and the captured actions against the surviving list. No
            vendor call. Fourteen expectations flip to `DERIVED` and all four permits are deleted.

Measured:   `tools/ci.ps1` green on Windows, 22 steps, 223 tests. `tools/verify-phase` GREEN: 115
            claims, 46 passed, 0 failed, 69 out of scope, 0 unexamined; 356 expectations, 101
            `DERIVED`, 255 `FROZEN`, 0 differed, 0 missing; inputs 37 `CAPTURED`, 48 `AUTHORED`; 14
            expectations changed since the last commit, which are the fourteen retiered here.
            Coverage examined 1,331 with 0 unexamined. That figure is not comparable to the 193,691
            recorded at the phase 1 sign-off, and the difference is the point: the old total summed
            every scope, so it was `store-portability`'s 189,726 stored values plus noise. It is now
            the property scopes only, with the corpus scopes reported beside it.
            The universe screen, derived outside the solution from the captured responses: 17,996
            common stock listed, 11,983 of them with a row on the captured bulk day, 7,202 clearing
            the $5 price floor, 2,002 clearing $20M of turnover as well, 5,200 rejected by the
            liquidity floor. 44,530 bars published, 15 splits, 248 dividends, 57 actions in the
            universe. 33 seeded histories, 3 of them trackers excluded by security type, 30 in the
            universe, 0 outside it, and IESC the only one acted on.
            Every figure equals what the replay records, and none was copied from it.

Verified:   Falsified rather than argued, five times, each reverted and the tree left clean.
            The obligation row put back to two cells turned `architecture-conformance` red, naming
            the table, row 4 and both widths.
            `BarAppendOnlyCheck.BarTables` cut from three bar tables to one **with five ordinary new
            files added to the Worker** failed, naming three narrowed scopes with the corpus shown
            beside them at 52. That exact case passed at the phase 1 sign-off, at examined 55 against
            a floor of 54, with the phase report GREEN and the total higher than the committed run.
            `PathCasingCheck` gutted so it compared no paths **with forty ordinary new literals
            added** failed at examined 0 against a floor of 26, corpus 2,578. That case also passed
            at the sign-off, at examined 2,479 against a floor of 2,468.
            Two string literals removed from one test file no longer turns `path-casing` red. That
            case failed at the sign-off, for a reason that has nothing to do with path casing.
            The `--universe` derivation over a mutated copy of the captured responses, with 500
            common stock reclassified as ETF and one split row dropped: ten figures move and six do
            not. `listedCommonStock` falls by exactly 500; `screened`, `survivors`, both liquidity
            figures, `bars.inUniverse`, `splitsPublished`, `actions.inUniverse` and
            `tickersInUniverse` all move; AAPL appears among the excluded trackers.
            `sessionsScreened`, `bars.published`, `dividendsPublished`, `seededHistories`,
            `tickersOutsideUniverse` and `actionsObserved` hold.

Findings:   Finding. **A floor on `FROZEN expectations diffed` fails on an improvement.** Committed
            in (c) and found by (d) an hour later, when flipping fourteen expectations to `DERIVED`
            took `FROZEN` from 269 to 255 and turned the check red for having improved the fixture. A
            falling `FROZEN` count is ambiguous in a way a floor cannot resolve: it falls when an
            expectation is deleted, which is a defect, and equally when one is promoted, which is the
            direction this corpus wants. Reading: flooring it makes every future tier promotion a
            false alarm, and a guard that cries wolf gets suppressed. `FROZEN` is now context; the
            property is held by a new total, which a deletion moves and a promotion does not, and by
            the independent tiers, which only rise. Worth separating: this is the per-scope repair
            catching a mistake in its own first commit, which is what the mechanism is for.

            Finding. **An admission that covers nothing counted as silence.** `PathCasingCheck`
            records its no-work branch as `NotExamined(..., 0, ...)`, and `TotalUnexamined` summed
            the counts, so the record carried an unexamined line while the report read "unexamined 0"
            on the same page. Raised as an Observation at the phase 1 sign-off and closed here by
            counting admissions rather than their sizes.
            That immediately made one admission visible and forced it to be classified honestly:
            `stated-counts` exempting prose counts nobody registered is an exemption by name with a
            stated reason, which is CLAUDE.md's definition of out of scope rather than of unexamined,
            and it is the same shape as `no-superseded-citation` exempting citations inside a record.
            Its count stays 0 and now says what that 0 is: the check does not scan prose for numbers,
            so it is the number of exempted items the check can name, not a measurement of the hole.

            Finding. **`earnings_in_window` traces to no stored column.** The architecture lists it
            among the missing measurements, and 2.1's done condition says a signal that does not
            trace to named columns is a finding rather than an assumption. Nothing stored carries an
            earnings date: `corporate_action` holds splits and dividends and neither implies one, and
            the vendor's calendar endpoint is not among those the call budget is built on. Recorded
            in the `## Signals` section as a finding with what closing it would cost, rather than as
            a candidate with blank source columns, because a candidate with no source reads as work
            scheduled and is a purchase nobody has priced.

            Observation. `MarkdownTable` has eight call sites and seven of them index cells directly,
            so a ragged row would have thrown there rather than been dropped. The silent skip existed
            at exactly one. Reading: the blast radius of this defect was the permit mechanism, not
            every out-of-scope placement in the phase report. Checkpoint rows are read by a regex
            rather than by this parser, which an earlier reading of the defect had wrong.

            Observation. The plan for 2.1 called for asserting that `Raised` is unique across the
            obligations table. That would have failed on landing, because two rows are legitimately
            raised at 1.12 once the malformed row is restored. The property that holds is narrower
            and is what shipped: an obligation a permit names must resolve to exactly one row.

Carried:    Three obligations. Both of 2.1's are discharged and removed from `BUILD_PLAN.md`'s table.
            The `CONFIRMED` values, raised at 1.6, due at 2.11.
            The out-of-scope coverage naming rule, raised at 1.12, moved from 2.6 to 2.2, because 2.2
            creates `setup` with three of its four declared writers unbuilt and that is what makes
            `writer-ownership` record a run of out-of-scope items carrying free prose.
            Step 6 of the move, raised at 1.11, due at the move.

## 2.2 — 2026-08-26 — phase-2-detection — the frozen signal row, and out of scope made structured

Built:      Migration 011: `setup`, `calibration_setup` and `setup_signal`. `setup` constrains
            `direction` and carries a unique index on (as_of, ticker, direction), because two
            detectors writing one table makes a duplicate a real possibility rather than a
            theoretical one and a duplicated setup would be counted twice by everything downstream.
            `calibration_setup` has no foreign key to `security` where `setup` does, deliberately: a
            calibration run walks years of history and can reach a ticker listed then and absent
            now, and refusing that row would silently shrink the count the run exists to produce.
            `setup_signal` is keyed (setup_id, signal_name), which is what makes "written once"
            enforceable rather than intended.

            `SignalVectorizer`, verb `vectorize`, freezing sixteen of the library's thirty-three
            active signals. `SetupReader` and `SetupSignalReader` in Data, every overload taking an
            as-of with none that omits it, and `SetupReader.ReadCalibration` named separately rather
            than offered as a default so a caller has to say which store it means.

            **The library is larger than what can be frozen today, and the gap is declared rather
            than left.** A signal can only be frozen once something stores what it reads, and phase
            2 builds those producers over several checkpoints. `SignalVectorizer.AwaitingCheckpoint`
            names seventeen signals against the checkpoint that supplies each: the scans at 2.3, the
            ladder grade at 2.4, the market mood at 2.5, and the pullback geometry, the sector and
            the cluster count at 2.6. A test asserts the partition covers the library exactly in
            both directions, and a second asserts every awaiting checkpoint exists and has not
            landed.

            **The out-of-scope coverage naming rule**, the obligation raised at 1.12 and moved here
            from 2.6. `CheckCoverage.OutOfScope` takes a structured reason in one of three shapes
            rather than prose, and `DeferralProblems` asserts the checkpoint half exactly as an
            out-of-scope architecture claim is asserted. Twelve call sites converted across nine
            checks.

Measured:   `tools/ci.ps1` green on Windows, 22 steps, 236 tests. `tools/verify-phase` GREEN: 115
            claims, 0 unexamined; 375 expectations, 113 `DERIVED`; coverage examined 1,435 with 0
            unexamined; 19 expectations changed since the last commit, which are the nineteen added
            here.
            Deferral shapes across every check, counted after the conversion: 87 name a checkpoint,
            5 carry a price, 3 are permanent by design. Before it, one named a checkpoint.
            The two priced ones are the pair the obligation cited: the whole-market screen under the
            twenty-session liquidity floor at 1,900 vendor calls and about 130 MB, and the liquidity
            floor's rejecting side at one per-ticker call at the next capture.
            The signal freeze over the fixture: 1 setup, 16 signals frozen, 0 absent.

Verified:   The write-once property four ways, because no one of them reaches what the others do.
            A second run over the same night writes nothing and reports every signal already frozen.
            A bar restated underneath a frozen value leaves it alone: that is the only case that
            distinguishes write-once from a rerun recomputing the same numbers, because the value a
            rerun would produce is genuinely different. The store's own key refuses a second write,
            so a mistake in the stage's own check is a failure rather than a duplicate row. And no
            `UPDATE` against `setup_signal` exists anywhere in the shipped source, which is the
            direction a behavioural test cannot reach: a future stage could add one and every test
            above would still pass, because none of them runs it.

            Twelve of the nineteen new expectations are `DERIVED`, computed by
            `tools/derive-indicators.py --signals` over the fixture's own bars outside the solution.
            All three exponential distances are derived rather than one, because a sign error there
            is the invisible case: a stock below its average and one above it both produce a
            plausible small number.

            The deferral rule falsified rather than argued: `bar-append-only`'s deferral pointed at
            1.3, a checkpoint PROGRESS records, and the check failed naming the item, the checkpoint
            and the reason. Reverted.

Findings:   Finding. **The signal-library parser lost two rows to an escaped pipe and would have
            passed.** `SignalLibrary` read a table cell as "anything but a pipe", and two
            trade-geometry formulas write absolute value as `\|trigger − stop\|`. Both rows were
            dropped. Reading: the partition test caught it only because the vectorizer names those
            two signals, so they showed up as invented rather than as missing; had the vectorizer
            not named them, an empty-handed parser would have satisfied every assertion made against
            it, because a smaller library makes the partition easier to hold. This is the same shape
            as `MarkdownTable` dropping a short row at 2.1, one checkpoint later and in new code.
            `SignalLibrary` now states a floor of thirty in advance and throws below it.

            Finding. **The independent derivation disagreed with the engine on `atr_14`, and the
            derivation was wrong.** 24.1363 against 24.1364. The gap average needs the warm-up plus
            the gap window, so the derivation read 169 sessions and computed every figure over all
            of them; Wilder's smoothing is recursive and the seed is part of the answer. Reading:
            this is exactly what the chart page found at 1.10, where a fifty-day line drawn over 210
            sessions differed from the same average seeded at 150 and both looked like a moving
            average. The window is now chosen per figure rather than once, and the disagreement is
            recorded as a note on the expectation rather than only fixed.

            Observation. `signal.IESC-long.listing_age_sessions` is 1 for every fixture name.
            `first_seen` is the date a ticker first appears in the symbol list and the replay runs
            `universe-build` once on the as-of date, so listing age in the fixture is a fact about
            the fixture rather than about the security. Recorded as a note on the expectation: it
            detects the figure changing shape and says nothing about any stock.

            Observation. A third out-of-scope shape exists that the obligation did not name. It
            described a checkpoint and a purchase; three real exemptions are neither, and nothing
            could close them. Reading: `ByDesign` is added rather than forced, and the risk it
            carries is stated where it is declared. If everything drifts into it the naming rule is
            decoration, so the three counts are reported separately and by-design growing is visible.

Carried:    Seventeen active signals await their producer, each named against a checkpoint that
            exists and has not landed. Not an obligation in the table: it is asserted on every run
            in both directions, so it cannot be forgotten and it closes itself as the checkpoints
            land.
            The three obligations from 2.1 are unchanged and none was attempted: the `CONFIRMED`
            values at 2.11, and step 6 of the move. The out-of-scope naming rule, raised at 1.12 and
            due here, is discharged and removed from the table.

## 2.3 — 2026-08-26 — phase-2-detection — six scans, and the basis trap found where it actually sits

Built:      Migration 012 creates `scan_hit`, keyed on ticker + date + scan, with `magnitude` beside
            `rank`. The magnitude is stored rather than recomputed: it is what the thrust signals
            freeze, and deriving it later would put the same arithmetic in two places in the one
            situation where a disagreement is invisible, because a wrong magnitude still produces a
            plausible ranked list.

            `ScanEngine`, verb `scans`, six scans of fifty names each. `ScanMagnitudes` in Core, so
            the stage and the vectorizer share one implementation on the same terms as the averages.
            `ScanHitReader` in Data with the two reads the lab actually makes: one scan on one night
            in rank order, and one ticker across a window, which is the shape the thrust check wants.

            `UniverseSnapshotReader` in Data, and `IndicatorEngine` now calls it rather than holding
            a private copy. Two methods, named apart: `Members` reads the nightly snapshot and every
            nightly stage uses it; `CurrentMembers` reads membership as it stands today and exists
            for the calibration run at 2.11 and nothing else. Named rather than offered as a
            fallback, because a stage that silently fell back to current membership on a night with
            no snapshot would produce a reconstructed answer that looks exactly like a real one.

            The six thrust signals move from `AwaitingCheckpoint` to `Frozen`, which the partition
            test at 2.2 forced rather than reminded. `SignalVectorizer.Counts` declares which signals
            are counts rather than measurements.

Measured:   `tools/ci.ps1` green on Windows, 22 steps, 243 tests. `tools/verify-phase` GREEN: 115
            claims, 0 unexamined; 428 expectations, 140 `DERIVED`; coverage examined 1,540 with 0
            unexamined; 55 expectations changed since the last commit, being the 53 added here and
            the two amended below.
            The scans over the fixture: 7,202 members, 30 measured, 7,172 short of the
            twenty-two-session window, 180 hits across six scans. The 7,172 hold the one captured
            market day and nothing else, so they cannot be measured on a month magnitude at all.
            Ranks one to three of every scan, and their magnitudes, derived independently by
            `tools/derive-indicators.py --scans`: 24 figures, 0 disagreements. The derivation writes
            its own bar query, because the gap magnitude needs the open and the shared window helper
            does not select it.

            **IESC's twenty-session magnitude: +0.0746 on the adjusted basis, −0.4627 on the raw
            one.** Measured from the two rows the scan reads. Adjusted it sits tenth among the
            leaders; raw it would top the laggard scan ahead of NCLH at −0.1403.

Verified:   The basis property as a permanent test rather than as a fixture coincidence.
            `A_split_inside_the_month_window_does_not_make_a_riser_the_biggest_laggard` seeds two
            authored names, one rising seven percent through a two-for-one split and one genuinely
            falling fourteen percent, with the split written the way a vendor publishes one: the
            adjusted close behind the ex-date halved and the raw close left alone. The riser has to
            appear among the leaders with a positive magnitude and the faller has to top the
            laggards.
            The tiebreak, the six directions, the breadth as a count rather than a threshold, and a
            name short of the window measured on nothing.

Findings:   Finding. **The basis trap does not sit where the decision written at 2.1 said it did,
            and a guard placed there would have found nothing.** That entry said a two-for-one split
            reads raw as a fifty percent decline that would top the **decliner** scan on the day it
            happens. Measured: on IESC's split date the raw and adjusted one-day changes are both
            −0.0537, identical, because the vendor adjusts the history **behind** a split and leaves
            the sessions after it alone. The daily and gap scans cannot tell the two bases apart on
            the session the split occurs. It is the twenty-session scans that span the adjustment,
            where the same two rows give +7.46 percent and −46.27 percent.
            Reading: the decision stands and its reasoning was wrong in a way that decided where a
            test would go. Corrected in place in `DECISIONS.md`, on the precedent the holdout-window
            entry already set, and in `ARCHITECTURE.html` as a clean edit. The permanent test asserts
            the month window rather than the day, and the fixture expectation carrying the pair of
            figures is noted at `scan.leader.rank3` and `scan.laggard.rank1`.
            Worth separating: this was found by running the code and reading what came back, not by
            re-reading the decision. Three sessions had read that sentence and none had noticed.

            Observation. Two expectations from 2.2 changed and both are recorded on the row.
            `signals.frozen` 16 to 22, because the six thrust signals gained a store to read.
            `signal.IESC-long.listing_age_sessions` 1.0000 to 1: the figure is unchanged and its
            rendering is corrected, because the replay rounded every numeric signal to four places
            and a count is not a figure taken to four places. Which signals are counts is declared on
            the vectorizer rather than inferred from whether a value happens to be whole, since
            inference would read a price of 355.00 as a count.

            Observation. `scans.measured` is 30 of 7,202. Only the fixture's own tickers carry seeded
            histories. Reading: the scans are exercised over thirty names rather than over a market,
            and the counts say so. What the whole-market half would add is a ranking over seven
            thousand rather than thirty, and it closes on the same twenty-bulk-day purchase the
            liquidity floor's exemption is already priced at.

Carried:    Eleven active signals still await their producer: the ladder grade at 2.4, the market
            mood at 2.5, and the pullback geometry, sector, industry and cluster count at 2.6.
            Asserted on every run in both directions rather than carried as an obligation.
            The two obligations from 2.1 are unchanged and neither was attempted: the `CONFIRMED`
            values at 2.11, and step 6 of the move.

## 2.4 — 2026-08-26 — phase-2-detection — the ladder grade, and a stage that counted what it did not write

Built:      `TierClassifier`, verb `tiers`. No migration: `ladder_grade` has been on `indicator_daily`
            since 006 and the re-key at 009 is what makes this stage an inserter rather than an
            updater. It writes a later observation of the same session carrying the grade and copies
            the seven computed figures forward, so a reader taking the latest row gets an answer
            instead of assembling one from two.
            `StoredIndicators` now declares `IIndicatorFigures`, which it already satisfied member
            for member. Declared rather than duplicated, so the grading function takes the interface
            and the stored row and the freshly computed values go through one signature.
            `ladder_grade` moves from `AwaitingCheckpoint` to `Frozen` in the vectorizer, which the
            partition test forced rather than reminded.

Measured:   `tools/ci.ps1` green on Windows, 22 steps, 253 tests. `tools/verify-phase` GREEN: 115
            claims, 0 unexamined; 465 expectations, 170 `DERIVED`; coverage examined 1,594 with 0
            unexamined; 38 expectations changed since the last commit.
            The grades over the fixture: 7,202 members, 30 graded, 7,172 with no indicator row for
            the session. **9 rising, 16 mixed, 5 falling.**
            All 30 grades derived independently by `tools/derive-indicators.py --ladder`, which
            recomputes the three averages over the engine's warm-up rather than reading the stored
            row: 0 disagreements, and the 9/16/5 split reproduced.

Verified:   The partition swept rather than sampled. Every arrangement of four values drawn from a
            set of four, 256 in all, produces exactly one of the three grades, and all three appear.
            A sweep that only ever returned "mixed" would satisfy "produces one of the three", so
            the count of distinct grades is asserted alongside the count of arrangements.
            Equality grades mixed. Two averages exactly equal is a real state on a flat series and
            is neither a rise nor a fall, so the comparisons are strict on purpose.
            The later-observation write end to end: two rows for the session, the latest carrying the
            grade, the engine's figures unchanged in both.

Findings:   Finding. **The stage counted thirty grades and wrote no rows, and reported success.**
            `indicator_daily` is keyed (ticker, as_of, computed_at) and the insert says
            `ON CONFLICT DO NOTHING`. The stage took its instant from `RunScope.StartedAt`, the
            replay's clock is fixed, so every stage in a replay run shares one instant: the grade
            row collided with the engine's own row and vanished. The grade counters had already been
            incremented before the insert, so the run printed 9 rising, 16 mixed, 5 falling over
            `graded 0`, and every ladder figure read `ungraded`.
            Reading: the fixed clock made this certain and a real clock makes it merely unlikely,
            which is the worse of the two. In production the wall clock happens to move between
            stages, so this would have sat undiscovered until the first night two stages ran inside
            the same millisecond, and it would have failed the same silent way. The repair is not a
            fixture workaround: a later observation must carry a later instant, so the stage now
            writes at `max(run start, the observation it copies + 1ms)`. The counters moved after the
            insert and a refused row is counted and reported rather than absorbed.
            Worth separating: the replay found this because a fixed clock is a harsher environment
            than production, not a laxer one. That is the second time in this phase that the fixture
            has been the harsher case.

            Observation. `signals.frozen` has now changed twice, 16 to 22 to 23, and both changes are
            recorded on the row. It is a count of the library's covered half and it rises each time a
            checkpoint gives a signal a store to read. Reading: this expectation is a progress
            indicator rather than a property, and it will move again at 2.5 and 2.6. That is fine and
            it is worth naming, because an expectation that changes at most checkpoints is one a
            reader should not treat as a regression signal.

Carried:    Ten active signals still await their producer: the market mood at 2.5, and the pullback
            geometry, sector, industry and cluster count at 2.6.
            The two obligations from 2.1 are unchanged and neither was attempted: the `CONFIRMED`
            values at 2.11, and step 6 of the move.

## 2.5 — 2026-08-26 — phase-2-detection — the market mood, and the label that filters nothing

Built:      Migration 013 creates `regime_daily`, one row a night. Both raw scores beside the label
            and both raw ladder counts beside them, plus how many trackers were above their average,
            so a later proposal wanting the continuous form does not have to recompute it from bars
            that may since have been restated.
            `RegimeLabeler`, verb `regime`. `RegimeReader` in Data. The three mood signals move from
            `AwaitingCheckpoint` to `Frozen`, which the partition test forced.
            The tracker averages are computed over the engine's warm-up rather than over 21
            sessions, through the shared `Averages` in Core. An average seeded 21 sessions back is
            not the one seeded 150 sessions back and both look like an average, which the chart page
            found at 1.10 and the signals derivation found again at 2.2.

Measured:   `tools/ci.ps1` green on Windows, 22 steps, 266 tests. `tools/verify-phase` GREEN: 115
            claims, 0 unexamined; 475 expectations, 177 `DERIVED`; coverage examined 1,700 with 0
            unexamined; 11 expectations changed since the last commit.
            The mood over the fixture: 3 trackers measured, **1 above its 21-day average**, 9 rising
            against 5 falling, index score 0, breadth score +1, label **mixed**.
            All seven figures derived independently by `tools/derive-indicators.py --regime`: 0
            disagreements.
            **The closest tracker decides the index score.** SPY closes at 763.47 against a 21-day
            average of 763.3055, sixteen hundredths above; QQQ and IWM are both below. A seed taken
            21 sessions back rather than over the warm-up moves an average by more than that, so
            `regime.indexesAbove` is the figure that would catch it.

Verified:   The label filters nothing, asserted against the shipped source rather than stated. No
            file outside the labeller, its reader and the vectorizer names either extreme, read with
            comments stripped so a comment explaining the rule is not read as the code breaking it.
            The vectorizer is exempt by name because freezing the label onto a setup is recording
            it, which is what the decision asks for.
            This is the condition no figure can show. A stage that began branching on the mood would
            produce identical numbers, and the assumption would be baked into the baseline where the
            design says it must be a version instead.

            The two boundaries the scores turn on. Neither extreme is reachable without both scores
            agreeing, swept over every pair. The breadth thresholds are exclusive at both ends, so a
            ratio of exactly 1.5 scores 0 and 1.55 scores +1; a boundary written with the wrong
            comparison is the commonest way a threshold is off by one case and never noticed.

Findings:   Observation. Two undefined ratios mean different things and are scored differently. No
            falling names at all scores +1, because every name that laddered laddered upward, which
            is the strongest reading the score has. Nothing laddering either way scores 0, because
            there is no reading. Collapsing them into one guard against dividing by zero would have
            made an empty market read as a strong one.

            Observation. An unmeasurable tracker is not counted as below. Reading: counting it as
            below would move the score toward risk-off on exactly the nights the data is thin, which
            is a bias rather than a missing value, and it would turn a feed outage into a market
            signal. `IndexScore` therefore takes how many were measured rather than assuming three,
            and returns 0 when none was.

Carried:    Seven active signals still await their producer, all at 2.6: the pullback geometry, the
            sector, the industry and the cluster count.
            The two obligations from 2.1 are unchanged and neither was attempted: the `CONFIRMED`
            values at 2.11, and step 6 of the move.

## 2.6 — 2026-08-26 — phase-2-detection — the long detector, and eight of ten checks one-sided

Built:      `PullbackGeometry` and `LongPullbackRules` in Core, pure and shared. The nightly run, the
            calibration run and a test all evaluate the same rules, because a calibration count
            produced by a second implementation would be a fact about the calibration code rather
            than about the thresholds, which is the one thing that run is for.
            The mirror is a parameter rather than a second class: long and short are the same
            geometry read in opposite directions, and what is genuinely not a sign flip stays in the
            detectors where the corpus says the three differences are.

            `LongSetupDetector`, verb `detect-long`, with `--calibrate from to` writing to
            `calibration_setup` off current membership. Ten checks, every result recorded, nothing
            short-circuiting. A recording floor of the four cheap filters, so a recorded setup is one
            where the pattern test had something to say rather than two thousand rows a night of
            names that moved one percent.

            `SectorResolver`, verb `sectors`, lazy and cached, with `fundamentals` added to the
            vendor client and to the captured fixture. `ThemeClusterer`, verb `clusters`, counting
            same-industry names **on the same scan**: two names in one industry, one gaining and one
            declining, are the industry splitting rather than shifting.

            `check-completeness` as a named CI step, reading ARCHITECTURE's own gate ids and
            reconciling them against the detectors in both directions.

            `capture-fixture` is now idempotent: a response already in the manifest is reused
            verbatim, instant and all. Re-running to add one endpoint cost 30 calls rather than 338,
            and re-stamping a kept response would have said it was captured tonight, which is exactly
            the provenance the tier records.

            The last seven signals move from `AwaitingCheckpoint` to `Frozen`. **The awaiting list is
            now empty and all thirty-three active signals have a producer**, which the partition test
            written at 2.2 has forced one checkpoint at a time.

Measured:   `tools/ci.ps1` green on Windows, **23 steps**, 267 tests. `tools/verify-phase` GREEN: 115
            claims, 0 unexamined; 568 expectations, 197 `DERIVED`; coverage examined 1,908 with 0
            unexamined; inputs **67 `CAPTURED`**, up from 37, being the 30 fundamentals responses.
            30 fundamentals calls spent live against EODHD, recorded outside the daily ceiling.
            Over the fixture: 7,202 members examined, 7,201 below the recording floor, **1 setup
            recorded** and 0 passing every gating check. 180 scan hits, all 30 names resolved to an
            industry, 114 hits in an industry group of two or more.
            All twenty check verdicts across the two setups derived independently by
            `tools/derive-indicators.py --checks`, which restates each gate from ARCHITECTURE's own
            wording: 0 disagreements.

Verified:   The detector's one real setup, HOOD, is a name whose thrust is the session itself. Its
            checks read: tradable, moves-enough, uptrend and thrust pass; dip-shape, contraction,
            trigger-near, exit-tight and cluster fail. IESC, the authored case, fails uptrend because
            it split on this session and sits below all three averages.
            The authored setup's check results come from the shipped rules over the evidence the
            detector would have assembled, not from a literal. What is authored is the trigger and
            the stop, which is the part a detector cannot supply for a name that has not pulled back.

Findings:   Finding. **Eight of the ten checks are one-sided over the fixture, and the cause is the
            sample rather than the checks.** Measured rather than observed, which is what this
            checkpoint added: per check, how many setups passed and how many failed, with the
            one-sided ones named individually.
            Only two setups are recorded, so a check with two results is one-sided unless those two
            happen to disagree. Two do: `uptrend` and `contraction`. The other eight have returned
            one answer each.
            Reading, and it matters for the remedy: the priced remedy the plan carried was a second
            as-of date at 33 per-ticker calls, and it does not fix this. A second session over the
            same thirty names would record perhaps one or two more setups, and eight checks would
            still have two or three results each. What would fix it is a fixture with enough
            sessions or enough names to record tens of setups, which is a different and much larger
            purchase than the one that was priced.
            **The decision is held rather than taken.** The instruction under which this phase is
            being built says more than two one-sided checks are declined with a written paragraph,
            and more than half is a different case worth waiting on. Eight of ten is more than half,
            so this is recorded and carried rather than decided here.

            Finding. **The detector counted vacuous passes on a name that had not pulled back.**
            HOOD's thrust is the session itself, so the extreme is the last bar, the trigger and the
            stop are the same price, and the give-up distance is zero. `exit-tight` and
            `trigger-near` both passed: the tightest possible stop, on a trade that does not exist.
            Reading: a vacuous pass is worse than a fail here, because the research loop reads these
            results to find which checks carry the strategy and a check that passes on nothing looks
            like a check that is easy to clear. `exit-tight` is the one the corpus calls the most
            informative in the system, so a false pass on it is the most expensive one available.
            Both distances are now absent where there is no pullback, and both checks fail with the
            reason recorded.

            Finding. **`writer-ownership` could not see the detector's writes at all.** The insert
            named its table through an interpolated string, because one method serves both `setup`
            and `calibration_setup`, and the scanner reads literals. It reported the detector as
            declaring two inserts and issuing none.
            Reading: a write nothing can attribute is a write nobody owns, and the check was right to
            refuse it. The statement is now written out twice, once per table, and the duplication is
            the price of the write being visible to the thing that audits ownership.

            Observation. `check-completeness` first read the store `fixture-replay` leaves behind and
            failed intermittently on a file lock, because the two run in the same assembly. Reading:
            a shared artefact between two checks is a coupling neither declares and it fails on
            timing rather than on the property. It runs its own replay now.

            Observation. The captured fundamentals response returns keys named `General::Sector`
            rather than a nested object, so the convention-named record deserialized to a row of
            nulls without erroring. Reading: that would have left every name unresolved and looked
            exactly like a vendor with nothing on any of them. Found by capturing the real response
            rather than by reading the vendor's documentation, which is the third time this phase
            that running the thing found what reading it did not.

Carried:    **The one-sidedness decision, held for a human.** Eight of ten checks one-sided over two
            recorded setups. The remedy the plan priced does not close it, and what would is a
            materially larger fixture. Due before 2.12.
            The two obligations from 2.1 are unchanged and neither was attempted: the `CONFIRMED`
            values at 2.11, and step 6 of the move.

## 2.7 — 2026-08-26 — phase-2-detection — the short detector, and a lookup that ran after its readers

Built:      `ShortPullbackRules` in Core and `ShortSetupDetector` in Worker, verb `detect-short`, with
            `--calibrate from to` on the long side's terms. The mirror is a parameter: what is a sign
            flip is read out of the shared geometry with `isLong: false`, and what is not lives here.
            `tradable-shortable` carries four floors where the long side has two, `averages-squeezing`
            has no long-side counterpart, `reached-ceiling` asks whether a bounce arrived at a level
            rather than whether a dip held one, and `no-reclaim` reads the 50-day average where
            `held-floor` reads the 21-day. The recording floor is the premise rather than the first
            four rows of the list, because `averages-squeezing` sits fourth and belongs to the pattern
            test the way `contraction` does on the long side.

            **`reached-ceiling` runs two of its three clauses and the record says which.** The third
            compares the price against the average price anchored to the last swing high, which is a
            volume-weighted average over minute bars and is what VwapEngine computes at 4.4. It is out
            of scope naming that checkpoint, recorded through `check-completeness` rather than in this
            entry alone, and every one of the check's verdicts carries the note. Not approximated from
            daily bars: a stand-in would put a number that looks like the real thing inside the check
            deciding whether a bounce reached its ceiling.

            **Forty authored boundary cases**, in `fixtures/gate-cases.json`, two per gate on both
            lists, one just inside its threshold and one just outside. Evaluated through the shipped
            rules over a baseline built to clear everything, so the difference between the two sides
            is the one field the case moved. `tools/derive-indicators.py --gates` restates all twenty
            gates from ARCHITECTURE's own wording and decides the same forty independently.

            **The degeneracy proof, over the gate list rather than per gate.** A gate handed an
            absent quantity fails; a gate whose own quantity is absent fails while the rest of the
            evidence stands, with the mapping from gate to quantity derived from the boundary cases
            rather than written down a second time. A gate with no boundary case fails the proof, so a
            check admitted in phase 6 inherits both properties without anyone remembering to.

            `AverageGap` in Core, the 21-to-50 distance session by session, shared by the squeeze
            check and the signal that freezes it. `DailyBarReader.SessionsStored`, shared by the
            listing-age floor and the signal that freezes it. `SecurityReader`, which reads the
            resolved attributes bounded on `sector_resolved_at`.

            `NightlyOrderTests`, which asserts the replay's stage order against RUNBOOK's nightly
            table rather than leaving the two to be kept in step by hand.

            Migration `014` creates `detector_error`, and both detectors catch per name: a stock a
            detector cannot read gets a row of its own and the run is recorded `partial`. Written
            because the phase report went red the moment this checkpoint landed, which is what the
            failure table's placement at 2.7 was for.

Measured:   `tools/ci.ps1` green on Windows, **23 steps**, 283 tests. `tools/verify-phase` GREEN: 115
            claims, **55 passed**, 0 unexamined, 60 out of scope; 677 expectations, **259 `DERIVED`**;
            coverage examined 2,161 with 0 unexamined; inputs 67 `CAPTURED` and 88 `AUTHORED`, the
            second being 48 synthetic vendor responses and the 40 gate cases now counted beside them.
            Over the fixture: 7,202 members examined, 7,201 below the recording floor, **1 short setup
            recorded**, INTC, and 0 passing every gating check. All ten of its verdicts derived
            independently by `tools/derive-indicators.py --checks --short`: **0 disagreements**. All
            forty boundary cases derived by `--gates`: **0 disagreements**.
            **`check.long.oneSided` and `check.short.oneSided` both read `none`.**
            Thirteen more authored parameters pinned, so every threshold marked "phase 2 count check"
            is now held against the code before 2.11 is allowed to move one.

Verified:   The degeneracy proof was falsified twice and reverted both times. An eleventh gate added
            to `SetupChecks.Long` with no boundary cases turned three assertions red, naming it. The
            `exit-tight` regression that shipped at 2.6, restored by hand, was named by three:
            "long exit-tight passed with stopDistanceRanges absent", "these long gates still passed:
            exit-tight", and the geometry case.
            The order test was falsified by putting `sectors` back at 19:00, which reported that the
            replay runs `clusters` after it and RUNBOOK schedules it before.

Findings:   Finding. **The sector lookup ran after the three stages that read what it writes.**
            RUNBOOK scheduled `sectors` at 19:00 while `clusters` at 18:15 counts same-industry names
            and both detectors at 18:20 read the market capitalisation. Observation: on a live night a
            name newly surfaced by a scan has no industry when its cluster is counted and no cap when
            `tradable-shortable` decides. Reading: neither consumer errors on a missing sector. The
            cluster count reads nought and the short check fails for want of a figure, so the night
            looks quiet rather than wrong, and the first name of a new theme is exactly the one this
            loses. The stage moves to 18:12. What let it survive 2.6 is that this replay ran the
            lookup first, against its own comment saying the sequence is itself under test, so the
            fixture could never have shown it; the order is now asserted against RUNBOOK.

            Finding. **`listing_age_sessions` measured the age of the lab's record rather than of the
            name.** Observation: it counted sessions since `security.first_seen`, which is when the
            universe build first saw a ticker, and read **1** for every name on the fixture's only
            night while `tradable-shortable` had cleared a floor of ninety. Reading: the check that
            decides and the signal that freezes the decision were two different numbers, which is the
            thing the frozen row exists to prevent. On a live lab it would have read 1 on the first
            night for all 2,070 names and climbed by one a night, so the floor would have rejected
            every short until the lab had been running ninety sessions, had anything read it. Both now
            count stored sessions through `DailyBarReader.SessionsStored`.

            Finding. **The squeeze test compared signed gaps.** Observation: the frozen signal is
            `(ema21 − ema50) / ema50`, and on the one side this check runs the 21-day sits below the
            50-day, so both today's gap and its average are negative. Reading: compared signed,
            "narrower" reads as "further below", which is the opposite rule. A squeeze would fail and
            a widening decline would pass, and every verdict would look reasonable in the record. The
            series stays signed, because a proposal may want to know which way round they sat, and the
            check takes absolutes and says so.

            Finding. **A detector that could not read a stock skipped it silently.** Observation: the
            failure table has said since before the code existed that an error row is written for that
            stock and date, and the corpus placed the claim at 2.7; the phase report turned it from
            out of scope to unexamined the moment this checkpoint landed, and neither detector wrote
            anything. Reading: every count downstream is over the setups that were recorded, so a lost
            name is simply absent. The night looks lighter, the counts stay plausible, and the first
            name of a new theme is exactly the kind this loses. `detector_error` now takes a row per
            stock per night per direction and the run is `partial` rather than `clean`.

            Finding, in the thing built to catch the last one. **The first assertion of that behaviour
            passed with the behaviour deleted.** Observation: it scanned both detectors for the insert
            statement and for the partial outcome. With the catch removed from the short detector, the
            private method issuing the insert was still in the file with nothing calling it, and the
            claim read `pass`. Reading: a scan for text present is not a scan for a property held,
            which is the fourth time this corpus has met that shape from a new direction. The property
            is now held by a behavioural test that damages one name and runs **both** detectors over
            it, and the scan asks for the call site inside the catch rather than for the statement.
            Falsified in that order: the tightened scan was written after the test caught what it did
            not.

            Observation. The short detector's `tradable-shortable` cannot pass until SectorResolver
            has seen the name, and `SecurityReader` bounds the read on `sector_resolved_at`, so a
            reconstructed historical session sees no cap at all. Reading: the calibration run at 2.11
            will record no short rows unless something changes, which is a question about what a
            reconstructed run is entitled to read rather than a defect here. Carried to 2.11, which is
            the checkpoint that needs the answer.

Carried:    **The market capitalisation a calibration run may read.** Due at 2.11. See above.
            The two obligations from 2.1 are unchanged and neither was attempted: the `CONFIRMED`
            values at 2.11, and step 6 of the move.
            **Retired at this checkpoint:** the one-sidedness raised at 2.6 and due at 2.12. Closed by
            the authored boundary suite rather than by a purchase, and the reason the purchase was
            rejected is recorded with it: the remedy priced at 2.6 was a second as-of date at 33
            per-ticker calls, and it would have left those eight gates with three results each instead
            of two. Both directions now report no check one-sided, and the cases are marked `AUTHORED`
            and written nowhere near `setup`, so nothing reads them as evidence about the market.

## 2.8 — 2026-08-26 — phase-2-detection — the nightly cap, and a rule with nothing to run on

Built:      `NightlyCap` in Core and `SetupCapper` in Worker, verb `cap`, updating `setup.rank` and
            `setup.capped_out` and nothing else. Forty long, twenty short, unused slots released.
            Candidates are the setups that cleared every gating check; a recorded setup that failed
            one keeps a null rank rather than a rank among names it was never ranked against.

            The arithmetic is in Core so it can be swept rather than sampled. The release rule's
            whole claim is about every arrangement of the two counts, and a stage-shaped test would
            have asserted it over whichever arrangements a fixture produced.

            **Truncated candidates keep their rank.** A night that recorded only what it kept could
            never say whether the cap bound, and how far past sixty a night went is what decides
            whether sixty is the right number.

            `fixtures/cap-cases.json`, eight authored arrangements and one ordering case, with
            `tools/derive-indicators.py --cap` restating the release rule and the ranking from the
            decision's own wording.

Measured:   `tools/ci.ps1` green on Windows, **23 steps**, 291 tests. `tools/verify-phase` GREEN: 115
            claims, 55 passed, 0 unexamined; 702 expectations, **277 `DERIVED`**; coverage examined
            2,233 with 0 unexamined.
            Over the fixture: 3 setups, **0 candidates**, so the live cap ranked nothing and every one
            of its figures is nought. The eighteen authored figures carry the rule instead, all
            derived independently: 0 disagreements.
            The release rule swept over **3,721 arrangements**, every pair of counts from nought to
            twice the cap, on four properties: no side takes more than it has, the total never
            exceeds sixty, a slot goes unused only when neither side has a candidate left for it, and
            neither side is ever cut below its own allocation to make room for the other.

Verified:   **The absent tiebreak is asserted rather than argued.** The corpus says no priority order
            is needed when a slot is released, because a side that ran out of candidates is not also
            asking for more. That is a claim about every arrangement, so the sweep runs the rule twice
            over each one, offering the spare to the long side first and to the short side first, and
            compares. They agree on all 3,721.

            The ordering case carries a tie on give-up distance broken by ticker, and both sides start
            at rank one. A pooled ranking would have the short side start at five, which is what
            reading the two together looks like when it is wrong.

Findings:   Finding. **The rule the checkpoint exists for had nothing to run on.** Observation: the
            fixture records three setups and none clears every gating check, so the live cap sees zero
            candidates and its done condition, "truncation recorded with the pre-cap count", is
            satisfied by a run that truncated nothing. Reading: an untested release rule that reports
            a clean run is worse than one that fails, because the report says the checkpoint is
            covered. The arrangements that matter, both release directions and both sides
            overflowing, are exactly the ones thirty names on one session cannot reach, which is the
            same shape as the one-sidedness at 2.6 and gets the same answer: authored cases, on the
            tier that says what they are. The live figures are kept and expected at nought, because
            that is what says why the authored ones are needed.

            Observation. `setup` carries no column that could make a rank belong to one version, and
            that is now asserted rather than intended. Reading: the property is unassertable once
            versions exist. A cap applied per version leaves two versions' disagreements
            indistinguishable from names the cap removed, and by the time that shows up the record it
            destroyed cannot be reconstructed. The assertion is on the schema and on the reader's
            signature, both of which survive a rewrite of the stage.

            Observation. The nightly row for this stage read "cap" rather than the verb, the third
            row of RUNBOOK's nightly table this phase to describe the work instead of naming what an
            operator types. Reading: the check that asserts the replay's stage order against that
            table found it, which is the second thing that check has caught since it was written
            yesterday.

Carried:    Unchanged: the market capitalisation a calibration run may read, due at 2.11; the
            `CONFIRMED` values at 2.11; and step 6 of the move.

## 2.9 — 2026-08-26 — phase-2-detection — the gallery, and the one write the read surface makes

Built:      `LabSetups` in the Api: a night's setups, both directions as two lists on the wire, each
            with every check's verdict and a forty-session window to read it against. `/setups/{as-of}`
            with an optional failed-check filter, and `/setups/{setup-id}/agreement` for the recording.

            The gallery page at `/setups`. Thumbnails from the one shared candlestick component, whose
            own documentation named this checkpoint as its second consumer; the plan, the checks and
            the agreement control on each card; the two sides in two blocks under two headings.
            Filtering by a failed check is a GET, so a filtered night is a URL a person can keep.

            The recording is a form post with an antiforgery token and works with the script removed.
            The keyboard paging is a local block that moves the selection and presses a button that
            already exists, which is what makes it a convenience rather than the mechanism.

            `SetupDirection` in Core. The two direction strings were constants on the detectors and the
            read surface may not reference the Worker, so a page separating a night by direction would
            have carried the literals on the other side of that boundary.

Measured:   `tools/ci.ps1` green on Windows, **23 steps**, 300 tests. `tools/verify-phase` GREEN: 116
            claims, 57 passed, 0 unexamined, 59 out of scope; 712 expectations, **279 `DERIVED`**;
            coverage examined 2,282 with 0 unexamined.
            Over the fixture: 3 setups shown, 2 long and 1 short, 16 distinct check names offered by
            the filter, **0 looked at** and therefore no agreement rate. The thumbnail's geometry
            derived independently at the gallery's own window and box, forty sessions in 260 by 110:
            last centre 201.45 against 201.45, forty candles against forty.

Verified:   Every state of the page is a test through the host: a night with both sides, a night with
            nothing flagged, a filter that hid everything, an agreement recorded, and an agreement the
            read surface refused. The last one matters most: the page says so above the night and
            renders the rest, rather than losing a review in progress to an exception.

            The `voidedBecause` fix the plan placed here landed with a proof over hand-written rows: a
            voided expectation counts toward a checkpoint's total and not toward its independent
            count, so a checkpoint cannot satisfy done condition seven with a `DERIVED` row that
            compares nothing. Theoretical until now, which is why the plan put it here: `CONFIRMED` is
            the tier the void mechanism was written for.

Findings:   Finding. **The agreement column had nowhere to be written from.** Observation: SCHEMA
            declared "Setup inspector" as its writer; the Web project may not open the store, the
            Worker has no channel a browser can reach, and one writer per table per operation forbids
            splitting it. Reading: three rules that are each right left one column unreachable, and
            the answer is a stated exception rather than a quiet one. The read surface writes those
            two columns and nothing else, ever. What makes it the right exception is that it is not
            the same kind of write: a person saying what they thought of one row, on two columns no
            computation reads, where every other write in the lab is the evening's job producing
            evidence on a schedule. Named as a decision, with the scope stated as the whole of the
            guarantee, and the writer declared by the type that issues the statement so
            `writer-ownership` holds the scope rather than the prose.

            Observation. `LabSetups` had to become a registered instance rather than a static class,
            which the other two read-surface types are. Reading: a declared writer is resolved against
            the component catalogue, and a catalogue component has to be something the container can
            build. That is a real constraint and not an accident: a writer nothing can name is a write
            nobody owns, which is the same finding the detectors produced at 2.6 arriving from the
            direction of the catalogue instead of the SQL.

            Observation. The shell test asserting no screen carries a script had to narrow to no
            screen *fetches* one. Reading: the rule was always about fetching, and the broader
            assertion was correct only while no page had landed that needed a local block. It gained a
            counterpart at the same time: a screen whose checkpoint has landed no longer says it is
            waiting, so an empty state left in place after its checkpoint would fail rather than read
            as a page nobody built.

            Observation. `architecture-conformance` held the catalogue's row count as an equality
            against a literal 52. Reading: that is a third copy of a number the document already
            states about itself and `stated-counts` already checks in both directions, so an ordinary
            component addition turned it red for a reason that has nothing to do with conformance. It
            is a floor now, guarding the thing it was for, which is the parser still finding rows.

Carried:    **The `CONFIRMED` gallery expectations.** Due at 2.12, where the gallery review is part of
            the sign-off. The tier the corpus defines as what the chart page and the setup gallery
            produce has no entries yet, and a build session cannot make one: it is a person looking at
            a screen and writing down what they saw. The page is built and every property a session
            can assert about it is asserted; what is left is the looking.
            Unchanged: the market capitalisation a calibration run may read, due at 2.11; the
            `CONFIRMED` indicator values at 2.11; and step 6 of the move.

## 2.10 — 2026-08-26 — phase-2-detection — point in time, and four reads that were not

Built:      `point-in-time` as a named CI step, the twenty-fourth, and the named check the roster has
            carried since 1.1. Three halves, and the third is the one a convention could not hold.

            **The readers.** Every public read in `PullbackStrategyLab.Data` takes a date, asserted
            over the reader types by reflection rather than by reading their source. One exemption,
            by name and with its reason: `UniverseSnapshotReader.CurrentMembers`, which calibration
            mode uses to read membership as it stands today on purpose.

            **The statements written by hand.** Every SQL statement in the shipped source outside the
            readers that selects from a table carrying an observation stamp has to bound that stamp.
            Two exemptions, by file and with their reasons.

            **The behaviour.** A permanent case, not a break-and-revert: a session observed twice,
            read from both sides of the second observation's own instant.

Measured:   `tools/ci.ps1` green on Windows, **24 steps**, 306 tests. `tools/verify-phase` GREEN: 116
            claims, 59 passed, 0 unexamined; 715 expectations, **282 `DERIVED`**; coverage examined
            2,357 with 0 unexamined.
            The check examines 28 public reads on the readers, 10 statements selecting from a stamped
            table across 7 stamped tables, 3 named exemptions and 2 directions of the future-dated
            case, over a corpus of 23 statements read.
            Over the fixture: IESC's session reads **324.1200** on the night and **999.0000** from
            past the correction, over 2 observations of one session, all three derived independently
            by `tools/derive-indicators.py --point-in-time`.

Verified:   Falsified twice and reverted both times. Replacing the vectorizer's bounded name lookup
            with a raw `SELECT industry FROM security` named the file, the table and the column.
            Dropping the observation bound from `DailyBarReader`'s window read reported that a bar
            observed after the as-of instant was visible to a read as of that date.

Findings:   Finding. **Four hand-written reads were unbounded, and one of them fed a frozen signal.**
            Observation: `SignalVectorizer` read `industry` and `market_cap` straight from `security`
            with no bound on `sector_resolved_at`; `ThemeClusterer` joined the same table for the
            same column; both detectors enumerated calibration sessions from `daily_bar` with no
            bound on `observed_at`. Reading: the vectorizer's is the serious one. Everything else the
            lab computes can be recomputed, and a frozen signal is the one row nobody recomputes: it
            exists to say what was knowable on the night, and it was freezing two attributes resolved
            afterwards. All four are bounded now, the first two through `SecurityReader`, which
            already held the bound for the short detector's `tradable-shortable`.

            Reading, on why a convention was never going to hold this. Every reader in
            `PullbackStrategyLab.Data` has taken a date since 1.4, and the corpus took that as the
            property. It is not: a reader's signature says nothing about a query written beside it,
            and all four of these were queries written beside one. That is the difference between a
            rule and a check, on the property the corpus calls the most important in the system.

            Observation. The fixture's figures did not move when the four were fixed. Reading: in the
            replay the sector lookup runs before the vectorizer on the same session, so the bound
            changes nothing there and no expectation shifted. A test over the fixture would have
            passed on all four, which is exactly why the property needed a check that reads the
            source rather than a case that runs it.

Carried:    Unchanged: the market capitalisation a calibration run may read, due at 2.11; the
            `CONFIRMED` indicator values at 2.11; the `CONFIRMED` gallery expectations at 2.12; and
            step 6 of the move.

## 2.11 — 2026-08-26 — phase-2-detection — the one-time calibration, and a band two checks cannot reach

Built:      Calibration mode, in the shape the decision always described and the plan never had.
            `ISessionFigures` is where a forward night and a reconstructed one differ and the only
            place they do: the nightly path reads each figure from the store through the reader that
            owns it, and `CalibrationFigures` computes the same figures from the bar window the walk
            is already reading. The rules see one evidence shape either way.

            It is assembly rather than a second implementation. The figures come from
            `IndicatorEngine.Calculate`, the ladder from `TierClassifier.Grade`, the six rankings
            from `ScanMagnitudes` and `ScanEngine.Top`. All four are the nightly stages' own and all
            four were made public at 2.6 so this run would not need copies of them.

            `NightlyCounts` in Core: the quartiles a threshold is read against, with the quantile
            convention written out rather than borrowed, and the rate-per-name scaling that lets a
            count over one universe be compared against a band stated for another.

            The market-cap clause of `tradable-shortable` is exempt in calibration mode and every
            short verdict says which clauses ran.

Measured:   `tools/ci.ps1` green on Windows, 24 steps, **324 tests**. `tools/verify-phase` GREEN:
            116 claims, 59 passed, 0 unexamined; **741 expectations, 287 independent**; coverage
            examined 2,444 with 0 unexamined.

            **Over the golden fixture**, which is a diff and not a measurement: 102 sessions from
            2026-03-30, 30 members with a warm-up behind them out of 7,202 listed, 237 long rows and
            88 short, **nought passing on either side on every night**. Twenty-six expectations, five
            of them `DERIVED` by `tools/derive-indicators.py --calibration`, which restates the
            session count, the range and the member count from the captured responses and agrees
            exactly on all five.

            **Over the live universe**, 2,016 names with a warm-up behind them, 631 sessions from
            2024-03-21 to 2026-08-24, no vendor call:

            | | long | short |
            |---|---|---|
            | rows recorded | 32,533 | 16,917 |
            | recorded a night, median | 44.0 | 13.0 |
            | recorded a night, quartiles | 24.5 to 74.0 | 7.0 to 41.0 |
            | candidates a night, median | 0 | 0 |
            | candidates a night, highest | 1 | 0 |
            | candidates in total | 7 | 0 |
            | nights with no candidate | 624 of 631 | 631 of 631 |

            Per check, over the rows that cleared the recording floor:

            | long | passes | short | passes |
            |---|---|---|---|
            | `tradable` | 100% | `tradable-shortable` | 100% |
            | `moves-enough` | 100% | `moves-enough` | 100% |
            | `uptrend` | 100% | `downtrend` | 100% |
            | `thrust` | 100% | `thrust` | 100% |
            | `held-floor` | 96.1% | `no-reclaim` | 99.2% |
            | `contraction` | 46.6% | `averages-squeezing` | 29.1% |
            | `trigger-near` | 31.2% | | |
            | `exit-tight` | 1.0% | `exit-tight` | 1.1% |
            | `dip-shape` | 0.7% | `bounce-shape` | 1.1% |
            | `cluster` | 0% | `cluster` | 0% |

            The quantities behind the three that bind, as distributions rather than as pass rates:
            the retrace among moves of the right length has a median of **1.088** long and **1.006**
            short against a cap of 0.40; the stop distance has a median of **1.157** ranges long and
            **1.191** short against a cap of 0.5; `reached-ceiling`'s distance has a median of
            **1.802** ranges against a cap of 0.5. Against those, `trigger-near`'s cap of 1.5 ranges
            sits above a median of 0.513 and 96.2% of measurable rows clear it.

Verified:   Three behavioural tests, because the fixture's figures cannot carry these. A run over
            history leaves the evidence store byte for byte as it found it, compared row by row
            rather than by asking whether `setup` is empty, and a rerun of the same range writes
            nothing new either. Every short row a calibration run writes carries the clause note. And
            a forward night still fails a name with no resolved capitalisation, which is the
            direction a leaked default would be silent in.

            The distribution arithmetic is swept rather than sampled: the median of an even number of
            nights, a night of no candidates counted rather than dropped, one night as its own every
            quartile, the quantile convention pinned at five points, both ends of the band inclusive,
            and a rate scaled back to its own universe returning the count it came from. A
            distribution over no nights is refused rather than reported as noughts.

Findings:   Finding. **The band is out of the five thresholds' reach, and the adjustment is not
            made.** Observation: the median is nought candidates a night on both sides against a band
            of 5 to 60, so the condition fires; the recording floors are healthy at 44 and 13 rows a
            night; the pattern test then admits 7 long rows in 631 sessions and no short row at all;
            with the retrace cap and the give-up cap set to pass always, the remaining conjunction
            yields about 6 a night. Reading: 6 is the bottom of the band, so the band is reachable
            only by removing two checks from each list, and removing a check is a change to what the
            strategy is rather than a calibration of it. The once-only adjustment is not spent, and
            `BUILD_PLAN.md`'s done condition now carries the clause that fires here, because spending
            it on a threshold set that cannot reach the band spends it for nothing.

            Reading, on which of two things is wrong. The thresholds are wrong, or the quantities
            they are applied to are, and the second has evidence rather than only a possibility. A
            stop at the extreme of a two-to-seven bar move spans more than one daily range by
            construction, so a cap of half a range is asking for a shape the geometry cannot make. A
            retrace whose median is above 1.0 means the typical give-back exceeds the whole thrust it
            is measured against, which is not a description of a pullback. Both hold on both sides
            with the same numbers, which is what rules out a long-side accident. `trigger-near` is
            the counter-example that rules out "everything is too tight": same population, same
            geometry, cap well above the median, 96% clearing it.

            Reading, offered as a hypothesis and not as a measurement. `PullbackGeometry` measures
            the thrust from the close before the session the scan flagged. That is the right origin
            for `gainer` and `gapper`, which flag a one-day move, and the wrong one for `leader` and
            `laggard`, which flag a twenty-session move and would have their thrust measured as a
            single day while the give-back is measured over several. Nothing here proves it: the scan
            that produced a thrust is not recorded on the setup row, which is itself a gap worth
            naming, and recording it would move the `thrust` expectations at 2.6 and 2.7 for a change
            that belongs to whoever decides the question.

            Finding. **`cluster` is unmeasured across the whole calibration record.** Observation: it
            passes on nought rows of 49,450. Reading: `ThemeClusterer` counts same-industry names
            among a night's scan hits, industry is resolved lazily on first sighting, and a
            reconstructed session has the industry the lab learned in 2026 or none at all. The
            calibration hands the check no cluster count, so it is unknown and fails. It gates
            nothing, so the counts above are unaffected, but no calibration row says anything about
            clustering and none can.

            Finding. **A reconstructed session cannot be read on its own observation instant, and
            both narrower bounds fail silently.** Observation: bounding the calibration's reads on
            each session's own instant returned no bars at all; bounding them on the end of the range
            returned no sessions at all. Both produced a run that completed clean over a store of one
            and a half million bars and reported nought. Reading: a backfill takes a name's whole
            history in one evening, so every historical bar was observed later than its own session
            and later than the last session in any range that ends at the last close. The run reads
            as of now, which is the bound a rebuild uses and is named in `DailyBarReader` for the same
            reason. It is recorded on `calibration_setup` as one of three reconstructions those rows
            carry rather than left as a property of a query.

            Finding. **The golden fixture cannot produce a candidate and never could.** Observation:
            nought passing on every night of both directions over 102 sessions. Reading: a scan takes
            the top fifty by its own magnitude and the fixture holds thirty names with a history, so
            every one of them is inside every scan on every session; the most recent thrust is
            therefore always the session itself, no pullback has any bars, and every geometry check
            fails. This is why the checkpoint was narrowed to the fixture and then run live anyway:
            the narrowing rested on the in-memory path costing three checkpoints, it cost one file,
            and the population it narrowed to was the one that could not answer.

            Observation, on a mechanism the plan named and the code cannot support. Replaying the
            nightly pipeline session by session was recorded as 2.11's mechanism before the
            checkpoint began. `IndicatorEngine`, `ScanEngine` and `TierClassifier` all read
            `UniverseSnapshotReader.Members` for the night they compute, and a night the lab was not
            running has no snapshot. Giving those three a current-membership mode would write
            reconstructed `indicator_daily` rows, which the calibration decision forbids in the
            paragraph directly above the one that would have authorised it.

Carried:    The threshold adjustment, due before 3.1 with the numbers above attached, because phase 3
            must not start recording against thresholds nothing has calibrated.
            The `CONFIRMED` indicator values for IESC, LITE and PAYO, still due and still not a build
            session's to discharge. 2.11 was named as the point they were needed because it is where
            a threshold is set against them; no threshold was set, so nothing rests on them yet, and
            the obligation moves to 2.12 where a person is already reading a screen.
            The source-scan assertions nothing exercises, raised at 2.11 and due at 3.1.
            Step 6 of the move procedure, due at the move.

## 2.11 — 2026-08-26 — phase-2-detection — correction: two obligations that were never in the table

Corrects:   the 1.3 entry above, and the 1.1 entry through it.

Findings:   Finding. **Two carried obligations were recorded in a progress entry and never written
            into `BUILD_PLAN.md`'s table, and one of them fell due at a checkpoint that has since
            landed.** Observation: the 1.3 entry carries "whether a night screened over five sessions
            should instead write nothing", due at 2.11, and "the daily call budget counts against the
            UTC date, and the vendor's own reset boundary is still assumed rather than confirmed",
            raised at 1.1 and carrying no due point at all. Neither appears in the obligations table.
            Every later entry that lists what is outstanding lists the table's rows, so both dropped
            out of the record's own summary from 1.4 onward, and 2.11 landed without addressing the
            one that fell due there.

            Reading. `CLAUDE.md` already says a carried obligation is recorded in `BUILD_PLAN.md`
            when it is created rather than remembered. The rule was written and nothing reads it, so
            two obligations were remembered and then forgotten, which is the failure the rule names
            happening to the rule itself. This is the same shape as the malformed obligation row at
            2.1 with the mechanism removed rather than broken: there, a row existed and the parser
            dropped it; here, no row was ever written and nothing looks for one.

            Reading, on why no check was added for it here. Matching a prose obligation in an entry
            against a prose row in a table is fuzzy, and a guard that raises false alarms is a
            suppressed guard, which is a dead one arrived at slowly. What would work is the other
            direction, a fixed form for the sentence that records an obligation, and choosing one at
            the door of a sign-off widens the phase for a decision that is not urgent. Both rows are
            in the table now and the question of a mechanism goes to 2.12.

Decided:    The five-session obligation is **answered on its merits and scheduled for the part that
            needs code.** The count distribution informs it as the 1.3 entry expected: a night flags
            a median of 44 long names and 13 short out of 2,016, and membership drifts by a handful a
            month, so a five-session-old membership misstates a night by far less than skipping the
            night removes from it. Phase 3 measures forward returns over a series of nights and a
            missing night is a sample nothing recovers. So the behaviour stands: carry the last
            complete screen's membership. What is owed is that the snapshot say it was carried and
            over how many sessions, so nothing downstream reads a carried night as a freshly screened
            one, and that needs a column. Due at 3.1.

Carried:    The two rows above, now in the table. Everything else unchanged from the entry above.

## 2.11 — 2026-08-26 — phase-2-detection — the thrust window as a supported finding, and what the fixture cannot be evidence about

Corrects:   the entry above, which offers the thrust-window reading "as a hypothesis and not as a
            measurement". The data in hand supports it, and the support is written out here rather
            than left implied, because 3.1 acts on it and a reading filed as a hypothesis is one a
            later session is entitled to set aside without arguing with it.

Findings:   Finding. **The two checks that bind are the two whose quantities depend on where the
            thrust is located, and the one that does not is healthy on the same rows.**

            Observation, three rows over the live calibration run and nothing beyond them:

            | check | its quantity | cap | median | multiple of its cap |
            |---|---|---|---|---|
            | `dip-shape` | the retrace | 0.40 | 1.088 long, 1.006 short | 2.72 and 2.52 |
            | `exit-tight` | the give-up distance | 0.5 ranges | 1.157 long, 1.191 short | 2.31 and 2.38 |
            | `trigger-near` | the trigger distance | 1.5 ranges | 0.513 long, no short check | 0.34 |

            Reading, and it is one quantity rather than three. The retrace's denominator is the
            thrust itself, measured from the close before the session the scan flagged, so a median
            above 1.0 says the median setup gave back more than the whole move it is measured
            against. That is not a description of a pullback; it is what a one-session denominator
            inside a twenty-session move produces. The give-up distance is the width of the pullback
            swing, and the pullback is everything after the extreme, which `PullbackGeometry.Of`
            searches forward from that same flagged session: a move whose real high sits before the
            flag has its extreme found at the flag, and the swing is then measured over the whole
            drift rather than over the dip. Both are readings of one thing, where the thrust is, and
            both hold on both sides with the same numbers, which is what rules out a long-side
            accident.

            `trigger-near` is what turns that from a possibility into the supported reading, because
            it is the same population, the same rows and the same geometry. Its quantity is the
            distance from the last close to the pullback's own high, anchored at the last session
            rather than at the thrust, and it is the only one of the three that reads neither the
            origin nor the width of the drift. It sits at a third of its cap and 96.2% of measurable
            rows clear it. So "the pattern test is uniformly too tight" does not survive its own
            evidence: the two checks that read where the thrust is are two and a half times over,
            and the one that does not is comfortably inside.

            Finding. **The fixture cannot be evidence about `thrust`, and the authored gate cases
            have carried that check alone.** Observation: the fixture holds thirty names with a
            history against a scan breadth of fifty, so every one of them is inside every scan on
            every session and the sessions-since-thrust figure is nought on every fixture row of both
            directions. The only place the ten-session window is exercised on either side of itself
            is `fixtures/gate-cases.json`, whose long and short cases sit at ten and eleven. Over the
            live run the check reads 100%, and that figure is a tautology rather than a measurement:
            `thrust` is one of the four checks in the recording floor, so every recorded row passed
            it by definition of having been recorded.

            Reading: three instruments and no overlap on this one property. The captured fixture
            answers whether the arithmetic still does what it did on a real day and cannot reach this
            check at all. The live calibration run has the population and cannot fail a floor check
            by construction. The authored cases carry the threshold and say of themselves that they
            answer branch coverage and nothing about the market. None of the three is wrong, and the
            state is invisible from inside any one of them, which is why it is worth a line rather
            than a fix.

            Reading, on the shape rather than on this check. This is the second time the fixture's
            composition has silently bounded what a check could show, and the two were found by
            different accidents. The first was measured at 2.6, when the per-check pass and fail
            counts were added and eight of ten long gates came back one-sided over two recorded
            setups; `fixtures/gate-cases.json` records it under `whyNotAWiderFixture` and was built
            to answer it. The second is this one, found at 2.11 by a run in a different checkpoint
            over a different population. Neither was found by anything looking for the shape, so how
            many other checks stand where `thrust` does is unknown rather than nought. That is a
            reading pass rather than a build step, and it is now a done condition of 2.12.

Decided:    **The correction is written as a prediction before it is attempted, and 3.1 is judged
            against it.** Take the thrust's span from the scan that produced the hit rather than from
            the session it landed on: one session for `gainer` and `gapper`, twenty for `leader` and
            `laggard`, which is the lookback each of those scans already ranks on. The prediction is
            that the median retrace falls below 1.0 and the nightly count moves into single or low
            double digits, **with no threshold moved**. If it holds, the once-only adjustment is
            still unspent and the geometry was the fault. If it does not, the thresholds are the
            fault and the once is spent then, against the corrected geometry rather than against
            this one.

            The ordering is the point rather than an aside. 3.1 must not move the geometry and the
            thresholds in one pass, because then neither result means anything and a once-only
            adjustment has been spent against a population nothing described. Both rows are in
            BUILD_PLAN's table and each names the other.

            It is written now because a prediction written after the attempt is a description. What
            it needs first is the gap the entry above named: the scan that produced a thrust is not
            recorded on the setup row, so nothing today can tell a one-session hit from a
            twenty-session one after the fact. Recording it moves the `thrust` expectations at 2.6
            and 2.7, which belongs to the pass that makes the change rather than to this one.

Carried:    The thrust-span correction and its prediction, now a row of its own, due at 3.1 and
            worked before the threshold row rather than with it.
            The reading pass over what the fixture's composition cannot show, due at 2.12 as part of
            the sign-off.
            Everything else unchanged from the two entries above.

## 2.11 — 2026-08-26 — phase-2-detection — corrections: three figures and the population each was taken over

Corrects the 2.11 entry "the one-time calibration, and a band two checks cannot reach". Every figure
below was recounted at the 2.12 sign-off against `calibration_setup` in the live store, 49,450 rows
over 631 sessions, using the same run the entry reports. Nothing that was measured has changed. What
changed is which rows three of the figures were taken over.

Measured:   **The long retrace median is 1.060, not 1.088.** The entry gives "the retrace among moves
            of the right length has a median of 1.088 long and 1.006 short". The gate's own definition
            of the right length is 2 to 7 bars, being `MinimumPullbackBars` and `MaximumPullbackBars`.
            Over `dip-shape` values on rows whose note says 2 to 7 bars, n is 5,849 and the median is
            1.0597. The figure 1.088 is the median over dips of 2 bars or more with no upper bound,
            n 6,225, which admits 376 dips the gate rejects for length. The quantile convention is not
            the cause and was checked: the long count is odd, so every convention returns the same
            value, and the entry's other even-count figures reproduce on the upper of the two middle
            values, which is the convention the short retrace and the short stop distance both use. The short figure is right as
            printed: over `bounce-shape` on rows of 2 to 7 bars, n is 3,448 and the median is 1.0064
            taking the upper of the two middle values. The two sides were therefore taken over two
            different populations under one phrase, which is what makes the pair not comparable rather
            than the long figure merely being 0.028 out.

            **The relaxed conjunction is 7 a night long and nought short, not "about 6".** The entry's
            finding reads "with the retrace cap and the give-up cap set to pass always, the remaining
            conjunction yields about 6 a night". Recounted per side by forcing `dip-shape` and
            `exit-tight` to pass on the long list and `bounce-shape` and `exit-tight` on the short, and
            counting rows where every remaining gating check passed: 5,833 long rows over the range, a
            median of 7.0 a night and a mean of 9.2; 507 short rows, a median of 0.0 a night and a mean
            of 0.8. "About 6" is 44 nightly rows multiplied by the long side's own pass rates for
            `held-floor`, `contraction` and `trigger-near`, which is 6.15 and is the long side alone.
            The same arithmetic on the short side is 0.19.

            **`reached-ceiling` is missing from the per-check table.** The table under "Per check, over
            the rows that cleared the recording floor" carries ten long checks and nine short. The
            absent row is `reached-ceiling`, which passes on 859 of 16,917 short rows, being 5.08%. It
            is the third most binding gate on the short list, ahead of `averages-squeezing` and behind
            only `exit-tight` and `bounce-shape`. Its quantity does appear in the entry, as a median
            distance of 1.802 ranges against a cap of 0.5, so the check is discussed and its pass rate
            is not printed. The table pairs the two lists by position and `trigger-near` has no short
            counterpart; `reached-ceiling` has no long one and got no row of its own.

Verified:   Every other figure in the entry reproduced exactly. 32,533 long rows and 16,917 short;
            medians of 44.0 and 13.0 a night and quartiles of 24.5 to 74.0 and 7.0 to 41.0, under
            `NightlyCounts.Quantile` over 631 nights with the 29 long and 30 short empty nights counted
            rather than dropped; 7 long candidates on 7 separate nights and none short, so 624 of 631
            nights and 631 of 631; 2,016 names with a warm-up; the nineteen printed pass rates; stop
            distance medians of 1.157 and 1.191, the second on the same upper-middle convention the
            short retrace uses; `trigger-near` at a median of 0.513 with 10,153 of
            10,553 measurable rows clearing it, being 96.2%; `reached-ceiling`'s 1.802; and every short
            row carrying the cap-clause exemption, 16,917 of 16,917. `setup` is empty, which is the
            done condition.

Findings:   Reading. The three corrections all move the entry's own argument the same way, which is
            towards the finding it already reached. A long median of 1.060 is still above 1.0, so the
            typical give-back still exceeds the thrust. A short side of nought a night with both shapes
            relaxed is further out of the band than "about 6", not nearer it. And a `reached-ceiling`
            that passes 5.08% of the time is a third binding short gate the entry did not count, which
            makes the short side's failure structural in one more place. Nothing here reopens the
            decision not to spend the once-only adjustment.

            Reading, on why these three and not others. All three are correct arithmetic over a
            population other than the one the sentence names. That shape now has a rule, recorded in
            `CLAUDE.md` under "Verification" at this sign-off, and a sweep of the record's other stated
            figures is carried to 3.1.

Carried:    The corrected figures are carried into `BUILD_PLAN.md`'s 2.11 threshold obligation, which
            repeated two of them and is what 3.1 reads.

## 2.12 — 2026-08-26 — phase-2-detection — the phase signs off, with a fifth defect shape and two acts nobody in this corpus can perform

Fresh session, no commits of code to this repository before or during the pass. Its only commits are
documents, which the narrowed fresh-session rule permits.

Verified:   Reproduced before reading the record, in this order. `tools/ci.ps1` green on Windows, 24
            steps, **324 tests**. `tools/verify-phase` **GREEN**: 116 claims, 59 passed, 0 failed, 57
            out of scope, **0 unexamined**; coverage examined 2,444 with **0 unexamined**; 741
            expectations of which 287 independent, 287 `DERIVED` and 454 `FROZEN` all matching, 0
            changed since the last commit; inputs 67 `CAPTURED` and 88 `AUTHORED`. Both runners and
            the rehearsal job green on the head commit in GitHub Actions, run 33035606747:
            windows-latest, macos-latest and the case-sensitive rehearsal.

            Run again after this entry and the document edits beside it, so the sign-off is against the
            state being committed rather than against the state it was reproduced from: `tools/ci.ps1`
            green, 24 steps, 324 tests, and `tools/verify-phase` GREEN with the same 116 claims, 59
            passed, 0 failed, 57 out of scope and **0 unexamined**. Coverage examined reads 2,450 rather
            than 2,444, the six being citations and scopes this entry and the corpus edits added.

            Nothing in the report is out of scope until 2.12, so signing this checkpoint off leaves no
            claim resting on a checkpoint that has just landed. The 57 out-of-scope claims and the 180
            out-of-scope coverage items close at 3.1 or later, or are priced, or are exempt by design.

            The 2.11 figures were recounted against `calibration_setup` rather than read from
            `PROGRESS.md`. Every figure in the entry reproduced exactly except three, which are
            corrected in the dated entry directly above this one. The three that moved were
            correct arithmetic over a population other than the one the sentence named.

Broke:      Five things were removed and the run watched, because reading has not been sufficient here.

            **A gate's implementation deleted.** `Contraction` taken out of `LongPullbackRules.Evaluate`
            and its method deleted. `check-completeness` fails. It does not name the gate: it dies on an
            unhandled `Single` inside `PhaseReplay.CheckSidednessFigures`, where the authored gate case
            for `contraction` finds no result of that name. Loud, and not the message the check has
            written for exactly this. A second break isolated the half that does name it: an extra
            `made-up` result added to the same list produced "2026-08-24-HOOD-long records a check no
            gate names: made-up", over two rows, so the per-row reconciliation is live.

            **The gid list emptied.** The `gid` span class renamed throughout `ARCHITECTURE.html`, all
            twenty. `check-completeness` fails with the count it states in advance: 0 gates under the
            long check list, not ten. Then, to reach the set comparison rather than the count, one gid
            renamed from `contraction` to `gone-quiet` so the list stays ten. Both directions fire in
            the same run: the document defines a gate the detector does not run, and the detector runs
            a check no gate names.

            **A check narrowed below one scope's floor while ordinary files were added.** `BarTables`
            cut from three to one, and eight ordinary new files added under
            `PullbackStrategyLab.Core/Indicators`. `bar-append-only` fails with three named shortfalls,
            one per scope, while the corpus scope it used to be summed with grew to 82 source files and
            was reported beside the property rather than added to it. This is the narrowing that passed
            at the phase 1 sign-off, and it now fails for the reason it should.

            **A backing test removed.** The vendor-correction test named by `bar-append-only` renamed
            away. The failure lands on `bar-append-only`, the check whose declaration names it, not on
            `coverage-reported`: "and no test by that name exists. A backing that has gone stale is
            worse than none, because it reads as covered." The 77 proofs in `CheckProofTests` still
            pass, which is right, because they prove the mechanism over hand-written inputs.

            **A two-cell row put into the obligations table.** `MarkdownTable` refuses it by position and
            content, naming a header three cells wide, a body row two cells wide, the row number and
            the text it begins with. It does not vanish.

            The working tree was restored after each and `git status` is clean.

Measured:   **For each of the twenty gate slots, which instrument exercises both sides.** Asked as the
            general question rather than as the three known instances. Two-sided means the instrument
            produced at least one pass and at least one fail for that gate on that side of the list;
            the two lists are never pooled, and four gate ids appear on both, being `cluster`,
            `exit-tight`, `moves-enough` and `thrust`, so there are sixteen ids across twenty slots.

            | gate | gate-cases.json | captured fixture | live calibration | second instrument |
            |---|---|---|---|---|
            | long `tradable` | both | pass only | pass only, 32,533 of 32,533 | none |
            | long `moves-enough` | both | pass only | pass only, 32,533 of 32,533 | none |
            | long `uptrend` | both | pass only | pass only, 32,533 of 32,533 | none |
            | long `thrust` | both | pass only | pass only, 32,533 of 32,533 | none |
            | long `dip-shape` | both | fail only | both, 227 pass | live calibration |
            | long `held-floor` | both | pass only | both, 31,258 pass | live calibration |
            | long `contraction` | both | fail only | both, 15,169 pass | live calibration |
            | long `trigger-near` | both | absent, never scored | both, 10,153 pass | live calibration |
            | long `exit-tight` | both | absent, never scored | both, 330 pass | live calibration |
            | long `cluster` | both | fail only | fail only, 0 of 32,533 | none |
            | short `tradable-shortable` | both | pass only | pass only, 16,917 of 16,917 | none |
            | short `moves-enough` | both | pass only | pass only, 16,917 of 16,917 | none |
            | short `downtrend` | both | pass only | pass only, 16,917 of 16,917 | none |
            | short `averages-squeezing` | both | fail only | both, 4,927 pass | live calibration |
            | short `thrust` | both | pass only | pass only, 16,917 of 16,917 | none |
            | short `bounce-shape` | both | fail only | both, 179 pass | live calibration |
            | short `reached-ceiling` | both | fail only | both, 859 pass | live calibration |
            | short `no-reclaim` | both | pass only | both, 16,783 pass | live calibration |
            | short `exit-tight` | both | absent, never scored | both, 185 pass | live calibration |
            | short `cluster` | both | pass only | fail only, 0 of 16,917 | none |

            The captured-fixture column is over the rows a detector wrote, being one long and one short.
            The live-calibration column is over `calibration_setup`, 32,533 long and 16,917 short rows.

Findings:   Finding. **`gate-cases.json` is the only instrument in CI that exercises both sides of any
            gate, and it carries all twenty alone.** The question asked how many checks it carries
            alone. The answer is every one of them, and the reason is not that the other instruments
            are weak but that only one other instrument has ever produced a two-sided result and it is
            not committed. Over the captured fixture a detector writes one row per direction, so
            twenty slots have one answer each. The live calibration run does exercise both sides of ten
            of the twenty, and it lives in `data/live`, which is gitignored, produced by a once-only
            command, and re-derived by nothing. If that store is deleted, the pass rates in the record
            become the only trace and the ten become gate-cases-only too.

            That is not a defect. It is what the decision already chose, and it is worth having the
            number: (see: Gate boundaries are exercised by authored cases and the captured fixture is
            not asked to do it) is carrying twenty gates by itself, and nobody had counted.

            Finding. **Ten of the twenty are structurally one-sided against any run's rows, and
            `thrust` is one of ten rather than a special case.** The 2.12 clause names `thrust` as the
            worked example. The general shape is the recording floor: `RecordingFloor` is `tradable`,
            `moves-enough`, `uptrend` and `thrust`, and a row exists only if all four passed, so all
            four are 100% by construction on every side of every run, forever, not only over this
            fixture. Eight slots. The other two are `cluster` on both lists, which is unmeasured for a
            different reason already recorded at 2.11: a reconstructed session has no industry to
            count. So the answer to "which other checks stand where `thrust` does" is: the other three
            in the recording floor, on both sides, permanently, and `cluster` on both sides for as
            long as calibration is the only population.

            Finding. **Five gates have their branch covered and the arithmetic behind the number
            covered by nothing.** `GateCases.Evaluate` builds a `LongEvidence` or `ShortEvidence` and
            constructs the pullback record by hand from the retrace depth and the bar count in the case
            file. It never calls `PullbackGeometry.Of`. The gates whose quantity comes from that method
            are `dip-shape` and `bounce-shape`, and `trigger-near` and `exit-tight`. Over the captured
            fixture `PullbackGeometry.Of` runs and returns the degenerate shape on every row:
            `pullback_bars` is 0 and `retrace_depth` is 0.0000 on all three setups, and `trigger-near`
            and `exit-tight` are not scored at all but recorded absent, because there is no trigger and
            no stop. So the geometry that the 3.1 thrust-window correction is about has no instrument
            exercising it on a non-degenerate input, anywhere, except the uncommitted live run. The
            prediction 3.1 is judged against, that the median retrace falls below 1.0 with no threshold
            moved, can be evaluated only by re-running that command over the live store.

            Finding, and the fifth defect shape. **The fixture's one-sidedness figures count an
            `AUTHORED` row into the captured population.** `PhaseReplay.CheckSidednessFigures` counts
            passes and fails from every row of `setup`. The fixture's `setup` table holds three rows,
            not two: `2026-08-24-HOOD-long` and `2026-08-24-INTC-short` from the detectors, and
            `IESC-long` inserted by `VectorizeAuthoredSetup` to give the vectorizer a subject. That row
            bypasses the recording floor, which is why it carries `uptrend` failed on a grade of
            `mixed` and is in the store at all. `uptrend` and `contraction` are the only two long gates
            the report calls two-sided, and each is two-sided only because the authored row disagrees
            with the detector row. Over detector-written rows every one of the twenty slots is
            one-sided, so the measured figure behind (see: Gate boundaries are exercised by authored
            cases and the captured fixture is not asked to do it) is ten of ten long rather than eight
            of ten. The same function keeps the authored gate cases out of these counters deliberately
            and says so in its own comment; the authored setup row arrives through the store and is
            kept out of nothing. The decision's conclusion is unaffected and its evidence is stronger
            than recorded.

            Reading. That finding and the three corrected figures in the entry above are one shape, and
            it is not the shape this corpus has four instances of. In all four of those the subject an
            assertion guarded went away and the assertion kept saying what it always said. In all four
            of these the counts are correct, the check is live, the subject is present, and the
            sentence is still false, because the figure was computed over a population other than the
            one named beside it. Tiers guard which rows may be believed and scope floors guard how many
            rows were looked at. Nothing guards which rows a stated figure was computed over. Per the
            stopping rule, a fifth shape is a rule rather than a fourth pass, and it is written into
            `CLAUDE.md` under "Verification" in this commit with its prior text in `CHANGELOG.md`.

            Observation. Two smaller things, neither of which lets anything through. `check-completeness`
            fails on a crash rather than on its own message when a gate implementation is removed. And
            `PhaseReplay.CheckSidednessFigures` says in a comment that five of the twenty gate ids
            appear on both lists; there are four. Both are carried to 3.1 rather than fixed here,
            because fixing them is a code commit and a session that commits code cannot sign it off.

            Observation, on the thrust-scan gap 2.11 named. The obligation says the scan that produced
            a thrust is not recorded on the setup row. `thrust_scan` is a signal and is recorded, with
            expectations at 2.3, 2.6 and 2.7, but `setup_signal` has a foreign key to `setup` and
            calibration writes to `calibration_setup`, so no calibration row can carry it and
            `setup_signal` is empty in the live store. The claim is true of the population the finding
            is about, and the work at 3.1 is a column or a re-run on the calibration side rather than a
            new signal, which is worth knowing before the estimate is made.

Carried:    Confirmed present with their due points, none attempted. The threshold adjustment the count
            distribution calls for, due 3.1, now carrying the corrected figures. Where the thrust is
            measured from and the prediction it is judged against, due 3.1. The unbacked source scans
            and the three scans without coverage records, due 3.1, reconfirmed against this run as nine
            backed by a test, one by the rehearsal job, three by nothing, and fifteen source-reading
            files of which twelve belong to a check. Whether a night screened over five sessions writes
            nothing or carries the last membership, due 3.1. Step 6 of the move, due at the move. The
            vendor's reset boundary, due at the operator.

            Three new, all due 3.1: the sidedness counters split by tier, the sweep of the record's
            other stated figures for the population each was taken over, and the two verification-quality
            items above.

            Two moved to the operator rather than to a checkpoint. The `CONFIRMED` indicator values for
            IESC, LITE and PAYO, and the `CONFIRMED` gallery expectations.

Verdict:    **Phase 2 signs off**, with the gallery review and the three `CONFIRMED` indicator values
            outstanding, and that is a judgement rather than a default.

            The reason. Everything a session can verify is verified and green, on both runners and on a
            case-sensitive filesystem, with nothing unexamined and nothing out of scope resting on this
            checkpoint. What is left is two acts a person performs at a screen, and no session of any
            kind can perform either. The indicator obligation has already been moved three times, 1.12
            to 2.11 to 2.12, each move sound on its own terms, and a due point that moves at every
            sign-off is permanent while reading as pending, which is precisely the fault the
            checkpoint-naming rule exists to prevent one level up. Holding phase 2 open for them would
            move them again at 3.7 and buy nothing, because phase 3 does not read them: 2.11 was named
            as the point a threshold would be set against those figures, no threshold was set, and the
            gallery review changes the detector's rules rather than any measurement phase 3 records.

            What signing off does not mean. The gallery review clause of 2.12's done condition is not
            met by this pass and this entry does not claim it. It is stated here rather than left to be
            inferred from a green report, and the operator can overturn the judgement by doing the
            review before phase 3 begins, which is the better outcome and is why both rows stay in the
            table rather than being closed.

            One caution carried into phase 3, not a blocker. Ten of the twenty gates are exercised on
            both sides by one committed instrument and that instrument is authored. Phase 3 measures
            outcomes for setups these gates select, and a gate whose only two-sided evidence is a
            constructed number is a gate whose behaviour on real data is asserted by a store that is
            gitignored. That is the shape of the 3.1 work already scheduled, and it is worth carrying
            the sentence rather than the impression.

## Phase 2 sign-off — 2026-08-26 — phase-2-detection — two obligations the gate table earned, added before 3.1 rather than after

Adds to the 2.12 entry above. Nothing measured here that was not measured there; what changes is that
two things the gate table named as observations are now scheduled work with a due point.

Findings:   Finding, raised from the gate table rather than newly found. **`PullbackGeometry.Of` is
            exercised on no non-degenerate input anywhere, and 3.1 is the checkpoint that changes what
            it computes.** The 2.12 table recorded this as the third of its findings and left it as a
            caution. It is now an obligation, due at 3.1 **before** the correction rather than after.

            The reasoning that moved it. `GateCases` constructs a `PullbackGeometry.Pullback` by hand
            from `pullback.retraceDepth` and `pullback.pullbackBars`, so the authored cases answer
            whether each gate's two branches work and never touch the method that produces the numbers
            those branches compare. Over the captured fixture the method does run, and returns the
            degenerate shape on every row: `PullbackBars` nought and `RetraceDepth` nought on all
            three setups, because thirty names against a scan breadth of fifty puts the thrust at the
            last bar every time. So the quantity the thrust-window correction is about is computed by
            a method that nothing checks on an input where its answer could be wrong.

            Why before rather than after. The defect the correction addresses produced plausible
            numbers for 631 sessions and nothing noticed, and that is a property of this method rather
            than of that defect: every figure it returns is a small plausible number whichever way it
            was computed, which the method's own comment says. A corrected version is wrong in exactly
            the same silent way if it is wrong. Judged after the fact, the 3.1 prediction is a median
            moving in the direction it was predicted to move, and a wrong-but-plausible correction
            produces that too. Judged against expectations written first, the correction either
            reproduces figures a second implementation derived or it does not.

            What is owed, stated so the obligation cannot be met narrowly. `DERIVED` expectations over
            the fixture's own bars for `ThrustOrigin`, for `ExtremeIndex` and `ThrustExtreme`, for
            `PullbackBars` and `RetraceDepth`, and for the raw-basis `Trigger` and `Stop` that
            `trigger-near` and `exit-tight` read, restated independently by
            `tools/derive-indicators.py` rather than by the method itself. Both bases are pinned on
            purpose: the shape quantities are adjusted, the two prices are raw, and reading one where
            the other was meant is the error this method carries a warning about.

            It costs nothing to acquire. `Of` takes the thrust index as a parameter and the fixture
            holds 250 sessions for each of thirty names, so a non-degenerate case is a thrust index
            inside the stored window rather than at its end. No capture, no vendor call, no committed
            megabyte. That is the same argument the authored gate cases won at 2.6, applied one layer
            down to the thing those cases skip over.

            And it has to survive the correction it is written for. The expectations pin what `Of`
            computes over a given window, and the correction changes which window and which index the
            caller hands it, so the correction should move inputs and leave the pinned arithmetic
            where it is. If it moves a pinned expectation anyway, that is a fixture change with a
            recorded reason rather than a number that quietly became different, which is the whole
            visibility this buys.

            Finding, raised from an observation in the entry above. **`check-completeness` fails on a
            crash where it has a named failure written for the same case.** Deleting a gate's
            implementation at the sign-off failed the check, so the property holds and nothing passed
            over the absent subject. It failed on an unhandled `Single` inside
            `PhaseReplay.CheckSidednessFigures`, where the authored gate case for that gate finds no
            result of its name, and what a reader gets is "Sequence contains no matching element" and
            a stack trace. The check's own reconciliation message never runs, because the replay it
            reads dies before the comparison. Reading: a crash and a named failure are not the same
            artefact. One tells a later session which gate went missing; the other tells it the check
            threw, and a session that has to work out which of twenty gates is absent from a stack
            trace is doing the work the check was written to do for it. Recorded as an observation
            above and raised here on that distinction, which is why it now has a row of its own rather
            than sharing one.

Carried:    Both due at 3.1, and the second is now separate from the gate-id count in a source comment
            it was bundled with, which keeps its own row. The obligations table now carries five rows
            raised at 2.12.
