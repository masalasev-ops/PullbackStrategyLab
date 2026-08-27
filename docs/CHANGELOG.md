# CHANGELOG.md

Prior text of every clean edit to a spec document.

`CLAUDE.md`, `ARCHITECTURE.html`, `SCHEMA.md`, `BUILD_PLAN.md` and `RUNBOOK.md` are read to know the **current** state, so a removal there is a clean edit: the old text is deleted, the citation stays at the point of change, and what it said before is recorded here.

That makes this file the only pointer to what a spec used to say, so **the citation on each entry is load-bearing**. An entry that does not name the decision authorising the change is a hole in the record, not a formatting lapse.

`DECISIONS.md` and `PROGRESS.md` are records. They correct themselves in place, by moving an entry to "Previously decided" or by adding a dated correction, so they never appear here.

## Entry format

```
### <date> — <document> — cites <decision name>
Was:  <the exact prior text>
Now:  <the exact replacement, or "removed">
Why:  <one line, or the decision speaks for itself>
```

---

### Before the first commit

Every document here was written, reviewed and corrected several times before `git init`. Those changes are not recorded below, and the reason is worth stating rather than leaving to be inferred: a clean edit supersedes a **published** spec, and nothing was published until the first commit. Recording the drafting history of text no session ever read would fill this file with noise on day one.

The rule starts at the first commit. From that point every clean edit to a spec lands here with its prior text and the decision authorising it.

---

### 2026-08-25 — CLAUDE.md — cites The store contains no absolute paths
Was:  A checks table with no `store-portability` row.
Now:  `| store-portability | No row in a populated store carries an absolute path, so the store stays a directory that can be copied |`
Why:  The hard rule had been stated since the first day and nothing asserted it, which the migration rehearsal is the checkpoint for. A path in a row is invisible until the store arrives on the other machine, and it is a rule the table now lists because the table lists every check that runs.

### 2026-08-25 — RUNBOOK.md — cites The store contains no absolute paths
Was:  | 2 | Record source counts | `setup`, `setup_signal`, `forward_return`, `trade`, `variant`, plus max setup id. **Written down before anything is copied**, or there is nothing to compare against |
Now:  A row count for every table the store holds, derived from the schema rather than from a list, taken before anything is copied, and `tools/snapshot-db` doing it rather than a person.
Why:  Five of the named tables do not exist yet and the list would go stale at every migration that adds one. A count that silently omits a table is exactly the failure the step exists to catch: the copy opens cleanly, every counted table matches, and the one nobody counted is empty.

### 2026-08-25 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  One paragraph separating unexamined from out of scope, with out of scope defined as "the corpus places it in a later phase".
Now:  The same, with "later phase" replaced by "a checkpoint that has not landed", plus a paragraph requiring every out-of-scope claim to name the checkpoint that ends it, requiring that checkpoint to exist in BUILD_PLAN.md and to be one PROGRESS.md does not yet record, and stating that the report groups them by it.
Why:  The fourth verdict's failure mode is a claim resting there forever, indistinguishable from one nobody got to. A phase is too coarse to close a claim against, and a claim still deferred to a checkpoint that has landed is one that checkpoint shipped without coming back to. Naming the checkpoint makes the count fall as work lands rather than reading as a permanent sixty-four.

### 2026-08-25 — ARCHITECTURE.html — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A claim this document places in a later phase is listed as out of scope and counted separately, because collapsing the two would let a later phase's rows hide the one row nobody can check.
Now:  The same against a checkpoint rather than a phase, plus: each of those names the checkpoint that ends it, and the report groups them by it, so the count falls as checkpoints land rather than resting as a permanent number.
Why:  Same change, in the document that describes the report.

### 2026-08-25 — ARCHITECTURE.html — cites Components are named, not coded
Was:  A component catalogue of 51 rows with no chart page, and an ordering note reading "The 51 components are listed by layer".
Now:  A **Chart page** row above the watchlist page, and "The 52 components are listed by layer", with a sentence saying the chart page sits with the screens rather than with the ingest components it draws from.
Why:  1.10 built a screen the catalogue did not name. A component added without a catalogue row is a component with no description, and the conformance check now asserts every screen against the route that answers for it, so the omission would have read as a page nobody had to account for.

### 2026-08-25 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A checks table listing twelve properties, and a `/fixtures` layout entry naming only the captured inputs.
Now:  The table lists every check that runs, gaining `fixture-replay`, `architecture-conformance`, `api-isolation`, `bar-append-only`, `ci-parity`, `clock-usage` and `shell-executable`, with a paragraph saying that is the rule. `/fixtures` also names `expectations.json`.
Why:  Discharges the obligation raised at 1.2 and due here: five checks ran as named CI steps and were not rows in the table. The phase report enumerates checks by name, so a check that runs and is not declared is a property nobody wrote down and the two lists would disagree with nothing to reconcile them.

### 2026-08-25 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The `coverage-reported` paragraph ended at "Green means \"nothing I ran failed\", never \"nothing is wrong\"."
Now:  A second paragraph separating unexamined from out of scope, and stating that verify-phase is green only with zero unexamined.
Why:  The report needs the distinction to be green at all. Sixty-four of the eighty-one architecture claims are about components a later phase builds; counting them as unexamined would make every phase report red for reasons nobody can act on, and counting them as examined would let them hide the one claim nobody can check. The vocabulary now has both.

### 2026-08-25 — ARCHITECTURE.html — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  <tr><td>Coverage</td><td>... A claim the report could not examine is listed as <b>unexamined</b>, which is not a pass.</td></tr>
Now:  The same, plus: a claim this document places in a later phase is listed as out of scope and counted separately.
Why:  The document described three verdicts and the report produces four. The fourth is not a softening of the third: unexamined stays a defect and out of scope is counted where it cannot be mistaken for coverage.

### 2026-08-25 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  Insert SetupDetector, calibration mode only · Read by nobody
Now:  Insert LongSetupDetector / ShortSetupDetector in calibration mode, **disjoint by `direction`** · Read by nobody
Why:  There is no component named SetupDetector. The catalogue names two detectors and the calibration run is both of them, so the declaration named something that does not exist and `writer-ownership` could assert neither direction for it. Found by the conformance check on its first run, which is what it is for.

### 2026-08-25 — CLAUDE.md — cites Fixture inputs record where they came from, and a path a live run exercises needs a captured one
Was:  A repository layout with no `/fixtures`, and a checks table with no `fixture-inputs` row.
Now:  `/fixtures  captured  the golden fixture's inputs, verbatim vendor responses with a manifest naming the endpoint, query and instant of each`, and a `fixture-inputs` row in the checks table.
Why:  The decision requires a captured input per endpoint and nothing enforced it. A check nobody runs is not a check, and a directory the layout does not name reads as something somebody forgot about.

### 2026-08-25 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  ### `index_bar`
      Grain: symbol + date. SPY, QQQ, IWM. Same shape as `daily_bar`.
      Insert IndexIngestor
Now:  Grain: symbol + date + `observed_at`, the same terms as `daily_bar` stated rather than implied, `Insert IndexIngestor · PK (symbol, bar_date, observed_at)`, and a note headed **No foreign key to `security`**.
Why:  "Same shape as daily_bar" left the key and the append-only terms to be inferred, and the two places it could have been inferred wrong are the two that matter: whether a correction appends and whether a tracker has to be a listed security.

### 2026-08-25 — ARCHITECTURE.html — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  A data budget with no row for the index bars, totalling ~795.
Now:  A row reading Index bars, three trackers, 3 a night, 1 per request, nightly, and a total of ~798.
Why:  The trackers are a nightly cost and the budget did not carry them. Three calls rather than a hundred is also the endpoint split doing its job, and a table that did not state it would leave the next session free to reach for the bulk endpoint.

### 2026-08-25 — RUNBOOK.md — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  A nightly schedule with no index step, totalling ~795.
Now:  | 17:50 | `index-bars`, one call a tracker | 3 |, and a total of ~798.
Why:  Same edit in the document an operator follows. The position matters: after the bars and before the indicators, because the regime label at 2.5 reads what this writes.

### 2026-08-25 — ARCHITECTURE.html — cites An unprocessed corporate action of any kind blocks calculation, not only a split
Was:  <tr><td><b>ActionIngestor</b></td><td>Nightly / weekly</td><td>Splits and dividends, and forces an average rebuild when a split lands</td></tr>
      <tr><td><b>DailyBarIngestor</b></td><td>Nightly 17:30</td><td>Pulls the whole market's closing prices in one bulk request</td></tr>
      <tr><td><b>IndicatorEngine</b></td><td>Nightly 18:00</td><td>Moving averages, daily range, true range and dollar volume, computed locally</td></tr>
Now:  ActionIngestor is nightly rather than nightly-or-weekly and raises a demand for any action that moves the adjusted close, never satisfying one. DailyBarIngestor also owns the per-ticker refetch, and the catalogue says why: a second component issuing that insert would be a second writer of the same table. IndicatorEngine refuses for a stock with a demand outstanding and satisfies it once the history has been refetched.
Why:  Three rows describing components whose behaviour changed under them. The ActionIngestor row was the one that would mislead: it named splits and a cadence that no longer exists, and a reader would have taken both as current.

### 2026-08-25 — RUNBOOK.md — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  The only thing depending on N is whether the backfill runs in one day or two: steps 4 and 5 cost 2N together, so with the other steps the day fits comfortably while 2N stays under about 4,000. Above that, split steps 4 and 5 across two days. Nothing else in the design is sensitive to the count, which is why no figure for it is written down anywhere.
      Steps 1 to 3 are one day, steps 4 to 6 the next. Nothing downstream depends on doing them together, and splitting keeps each day well inside the ceiling.
Now:  **The backfill is not counted against the nightly ceiling.** ... **Size, measured rather than estimated.** N was 2,070 when this was first run, so steps 4 and 5 are 2,070 calls each and the whole procedure is about 7,145.
Why:  The two-day split existed only because a one-time operation was being charged against the guard the nightly job needs. It was never too large for the vendor. With the two separated the procedure states its own size instead of a rule conditioned on something it has nothing to do with, and the size is now a measurement rather than a bound.

### 2026-08-25 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  | `calls_used` | INTEGER | counted as the stage runs, against the daily ceiling |
Now:  | `calls_used` | INTEGER | counted as the stage runs |
      | `counts_against_ceiling` | INTEGER | 0 or 1. Whether the daily total sees this run's calls |
      followed by a note headed **`counts_against_ceiling` is how a one-time operation stays out of the nightly budget**.
Why:  Every run's calls were counted against one budget, so a one-time operation and the evening's job competed for the same allowance. The run now says which it is, in the store, rather than the query recognising a stage by name and carrying the exception where no reader of SCHEMA would find it.

### 2026-08-25 — SCHEMA.md — cites An unprocessed corporate action of any kind blocks calculation, not only a split
Was:  `indicator_daily` keyed (`ticker`, `as_of`), `ladder_grade` noted as "Written by TierClassifier", declared `Insert IndicatorEngine · Update TierClassifier (ladder_grade only)`.
Now:  Keyed (`ticker`, `as_of`, `computed_at`), `ladder_grade` noted as null until TierClassifier writes an observation carrying it, declared `Insert IndicatorEngine · Insert TierClassifier, **disjoint by computation**`, followed by three notes: **Append-only, on the same terms as `daily_bar`**, **A rerun that produces the same figures writes nothing**, and **Why two inserters rather than an inserter and an updater**.
Why:  It was the one computed store that was not append-only, and that is precisely why a rebuild could not reach the rows it invalidates: a row computed on a basis the vendor had since restated stood for ever, because the engine could insert and nothing could replace it. With the table append-only there is nothing to update, so TierClassifier's declared update becomes an insert of a later observation.

### 2026-08-25 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  Insert SignalVectorizer (nightly, new rows) · Insert SignalBackfiller (backfill, adds signals to old setups; may never touch a signal SignalVectorizer owns for that date)
Now:  Insert SignalVectorizer · Insert SignalBackfiller, **disjoint by date and signal**: the vectorizer writes new rows nightly and the backfiller adds signals to old setups, and may never touch a signal the vectorizer owns for that date
Why:  Both declarations said the same thing and only one said it in words a checker could read. `writer-ownership` carried `setup` and `setup_signal` as named exceptions, which is a fact about the checker rather than about the design. The declarations now state the disjointness and the check reads it, so a third store that legitimately needs two writers says so in SCHEMA instead of being added to a list in a test.

### 2026-08-25 — BUILD_PLAN.md — cites Fixture inputs record where they came from, and a path a live run exercises needs a captured one
Was:  ... the report states how many expectations changed since the last commit alongside how many passed.
Now:  ... **The fixture's bars are `CAPTURED` from real vendor responses**, which the backfill at 1.6 makes possible, and the synthetic split case from 1.5 is kept and marked `AUTHORED`. **The report breaks inputs down by tier beside expectations**, and a path with no captured input reads as unexamined however many authored cases pass.
Why:  The tier system gave expectations a provenance and said nothing about inputs. Twice in phase 1 a fixture passed while the live run failed, both times because the fixture encoded a false belief about the vendor. 1.7 makes the fixture the primary verification instrument, so this was the last cheap moment to fix it.

### 2026-08-25 — CLAUDE.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  /tools            ci.ps1  ci.sh  verify-phase  snapshot-db  migrate
Now:  /tools            ci.ps1  ci.sh  verify-phase  snapshot-db  migrate
                        derive-indicators.py  one-time verification aid, not run by CI
Why:  The repository layout is a list a session reads to know what is there, and an unlisted file in `/tools` reads as something a check forgot about. The second line says what it is and, more usefully, what it is not: nothing imports it and no check runs it.

### 2026-08-25 — SCHEMA.md — cites A rebuild is satisfied by a recorded refetch, not by inferring one from what changed
Was:  (nothing. `history_refetch` did not exist)
Now:  A `### history_refetch` section under Market data, grain ticker plus the instant its whole series was re-observed, `Insert DailyBarIngestor`, and two notes: **The row is written even when nothing changed** and **This is what satisfies a rebuild demand**.
Why:  The rebuild demand had no way of being satisfied that survived contact with real data. Inferring it from bar observations fails in both directions, quietly, and the direction that fails leniently produces numbers. The event has a time, so it is stored as one.

### 2026-08-25 — ARCHITECTURE.html — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  A data budget with no row for the per-ticker history refetch, totalling ~690.
Now:  A row reading History refetch for the names carrying an action, 25 a night, 1 per request, nightly, and a total of ~715.
Why:  Honouring a corporate action costs one call per affected name and the budget did not say so, which made the nightly total wrong on any evening an action landed. Twenty-five is what the first live rebuild actually cost, on an evening the dividend pull ran.

### 2026-08-25 — RUNBOOK.md — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  A nightly schedule with no history refetch between the bulk bars and the indicators, totalling ~690.
Now:  | 17:45 | `backfill --rebuild`, one call per name carrying an open rebuild demand | ~25 |, and a total of ~715.
Why:  Same edit as the ARCHITECTURE one, in the document an operator follows. The position in the evening matters and is the reason it is a row rather than a note: it has to run after the actions are known and before the indicators are computed, or every affected name is refused for the night.

### 2026-08-25 — SCHEMA.md — cites A rebuild demand is keyed on the action as observed, and a restated action raises a new one
Was:  Insert ActionIngestor · PK (`ticker`, `effective_date`, `type`)
      **An action is an event, not a measurement.** Unlike a bar, the first observation of it stands: a rerun of the night finds the row and does nothing rather than writing a second observation, and there is no update declared. A vendor publishing a different ratio for a split already stored is therefore not absorbed. It is counted, printed, and demands the rebuild again, because the factor the history was rebuilt against may be the wrong one.
Now:  Insert ActionIngestor · PK (`ticker`, `effective_date`, `type`, `observed_at`)
      **Append-only. Never deleted, never updated,** on the same terms as `daily_bar` and for the same reason, followed by **A restatement raises a rebuild demand of its own**.
Why:  The prior text was wrong, and wrong in a way nothing could see. Vendors restate corporate actions, and under that key a restated ratio could not be stored at all: the ticker stayed rebuilt against a factor the vendor no longer publishes, the record showed a satisfied demand, and the wrong number was computed from it. The bar discipline is copied without variation instead.

### 2026-08-25 — SCHEMA.md — cites A rebuild demand is keyed on the action as observed, and a restated action raises a new one
Was:  ### `indicator_rebuild`
      Grain: ticker + the effective date of the split that demanded it. One row per split, and the row stays after it is honoured. Its PK was (`ticker`, `effective_date`) over columns `requested_at` and `rebuilt_at`, and its notes read **A row with a NULL `rebuilt_at` is a stock whose calculations must refuse to run**, **The demand is recorded, not queued** and **Two components, on purpose**.
Now:  Grain: the corporate action that raised the demand, as that action was observed. PK (`ticker`, `effective_date`, `type`, `observed_at`) over `rebuilt_at` alone, the three notes kept, and two added: **The key is the action as observed, not the ticker and the date** and **No foreign key to `corporate_action`, though the key is its key**.
Why:  `requested_at` and the action's `observed_at` were the same instant recorded twice, and keying on the ticker and the date meant a restated action collided with a demand that might already have been satisfied. Keyed on the observation, a restatement raises a new demand and nothing is mutated or cleared.

### 2026-08-25 — SCHEMA.md — cites An unprocessed corporate action of any kind blocks calculation, not only a split
Was:  A split rescales every adjusted close before it, so an average taken across the boundary is arithmetic on two different units and its answer is wrong by a factor while looking entirely reasonable.
Now:  Any corporate action moves every adjusted close before it, so an average taken across the boundary is arithmetic on two different units and its answer is wrong while looking entirely reasonable. Magnitude does not enter it.
Why:  The rule named splits and the reason it gave covered anything that moves the adjusted close, which a dividend does. A rule narrower than its own stated reason gets applied inconsistently by whoever reads it next.

### 2026-08-25 — ARCHITECTURE.html — cites An unprocessed corporate action of any kind blocks calculation, not only a split
Was:  <tr><td>Unprocessed split</td><td>Calculations refuse to run for that stock and no plan is published. Failing loudly matters: a split corrupts every moving average at once and produces plausible-looking nonsense.</td></tr>
Now:  <tr><td>Unprocessed corporate action</td><td>Calculations refuse to run for that stock and no plan is published. Failing loudly matters: any action that moves the adjusted close corrupts every moving average at once and produces plausible-looking nonsense. A split does it by a factor and a dividend by a smaller one, and magnitude does not enter it (see: An unprocessed corporate action of any kind blocks calculation, not only a split).</td></tr>
Why:  Same edit as the SCHEMA one above, in the document that states the behaviour rather than the store it is read from.

### 2026-08-25 — ARCHITECTURE.html — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  A three-column data budget of Nightly job, Calls and Note, in which the dividend row read `~20` under Calls and `Weekly rather than daily` under Note.
Now:  A five-column table of Nightly job, Calls a night, Cost per request, Cadence and Note, the dividend row reading 20, 100 and weekly, followed by a paragraph headed **Calls a night and cost per request are different numbers and the table now says which is which**.
Why:  One column was carrying two different quantities. On every nightly row a job's cost per request and its contribution to a night are the same number, and on the dividend row they are 100 and 20, because a weekly request amortises across five sessions. The row was therefore the one figure in the table that could not pin a constant, which made it permanently unexamined, which is a poor resting state for the one row known to carry a defect. Both numbers are now stated and both are checked. The daily total is unchanged, because the column it sums is still the nightly contribution.

### 2026-08-25 — RUNBOOK.md — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  | 17:20 | `actions --with-dividends`, weekly rather than daily. The 20 is a nightly average of a weekly request, not its price: it costs 100 like every other bulk call | ~20 |
Now:  | 17:20 | `actions --with-dividends`, weekly rather than nightly. 100 a week over five sessions is the 20, which is an amortised figure and was never the price of a call | 20 |
Why:  The tilde marked an estimate and the figure is exact: a weekly request of 100 over five sessions is 20 a night. Stating it as an estimate is what let it sit unchecked.

### 2026-08-25 — RUNBOOK.md — cites An unprocessed corporate action of any kind blocks calculation, not only a split
Was:  | A split was missed | Rerun `actions` for that date. It writes the split and raises the rebuild demand, and until that demand is stamped, calculations for that ticker refuse to run. No other ticker is touched |
Now:  | A corporate action was missed | Rerun `actions` for that date, with `--with-dividends` if a dividend is what was missed. It writes the action and raises the rebuild demand, and until that demand is satisfied, calculations for that ticker refuse to run. No other ticker is touched |
Why:  The row named splits, and a missed dividend now blocks the same way and is recovered by the same command with one more argument. An operator reading the old row would have found nothing telling them the dividend case existed.

### 2026-08-25 — SCHEMA.md — cites A split records a rebuild demand that is stamped rather than cleared
Was:  (nothing. `indicator_rebuild` did not exist)
Now:  A `### indicator_rebuild` section under Computed, declaring grain ticker plus effective date, the four columns, `Insert ActionIngestor · Update IndicatorEngine (rebuilt_at only)`, and three notes: a NULL `rebuilt_at` is a stock whose calculations must refuse to run, the demand is recorded rather than queued, and the two components are deliberate.
Why:  The architecture says ActionIngestor forces an average rebuild when a split lands and that an unprocessed split makes calculations refuse to run for that stock, and SCHEMA declared nowhere for either to be read from. A behaviour with no store behind it is a behaviour nothing can assert.

### 2026-08-25 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  | `ratio` | TEXT | |
Now:  | `ratio` | TEXT | on a split, new shares over old as a factor, so 4 for a four-for-one. On a dividend, cash per share |
Why:  The column carries a factor for one type and a money amount for the other, and nothing said so. A reader would have had to infer it from the vendor's response shape, which is the one place the answer was not written down.

### 2026-08-25 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  Insert ActionIngestor
Now:  Insert ActionIngestor · PK (`ticker`, `effective_date`, `type`), followed by a note on `ratio` and a paragraph headed **An action is an event, not a measurement**.
Why:  The grain was stated in prose and the key was not stated at all, which left open whether a re-observation appends like a bar does. It does not: the first observation of an action stands, there is no update declared, and a changed ratio is therefore counted and printed rather than absorbed. That consequence is worth stating where the table is declared rather than leaving it to be discovered from the code.

### 2026-08-25 — RUNBOOK.md — cites A split records a rebuild demand that is stamped rather than cleared
Was:  | 17:20 | splits, bulk | 100 |
      | 17:20 | dividends, bulk, weekly rather than daily | ~20 |
Now:  | 17:20 | `actions`, splits bulk | 100 |
      | 17:20 | `actions --with-dividends`, weekly rather than daily. The 20 is a nightly average of a weekly request, not its price: it costs 100 like every other bulk call | ~20 |
Why:  The stage exists now and the rows did not name it, so an operator following the runbook had nothing to type. The second half of the edit resolves a figure that reads as a price and is not: charging the budget 20 for a request that costs 100 would under-count by 80 every time it ran.

### 2026-08-25 — RUNBOOK.md — cites A split records a rebuild demand that is stamped rather than cleared
Was:  | A split was missed | Rerun the action ingest for that date, then force an indicator rebuild for that ticker. No other ticker is touched |
Now:  | A split was missed | Rerun `actions` for that date. It writes the split and raises the rebuild demand, and until that demand is stamped, calculations for that ticker refuse to run. No other ticker is touched |
Why:  "Force an indicator rebuild" described an operator action that does not exist and now never will, because the rerun raises the demand by itself. The recovery is one command rather than two, and the row said otherwise.

### 2026-08-25 — BUILD_PLAN.md — cites The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface
Was:  | 6.5 | ResearcherSeat via the Claude Agent SDK | Subscription auth, no `ANTHROPIC_API_KEY` present in the environment. Budget exhaustion queues the job and returns nothing |
Now:  | 6.5 | ResearcherSeat, built against the narrow interface, with both implementations present and selected by configuration | A test asserts the subscription implementation is constructed with an **empty tool set**. A test asserts `ANTHROPIC_API_KEY` is absent from the environment. A proposal row carries the **configured model**, the **transport** used and the **served model** string the response reported. Budget or plan-limit exhaustion queues the job and returns nothing, on both paths, and never a partial proposal (see: The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface) |
Why:  The deliverable named one transport as the implementation rather than as one of two, so the checkpoint could have been signed off with no way to reach the other. The done conditions now name the three things that are asserted rather than assumed: that the tool set is empty, that the environment variable is absent, and that a proposal records what actually served it as well as what was asked for.

### 2026-08-25 — BUILD_PLAN.md — cites The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface
Was:  **6.5 has a trap:** if `ANTHROPIC_API_KEY` is set anywhere in the environment it wins over subscription auth and you are billed at API rates. Assert its absence at startup and fail loudly.
Now:  **6.5 has a trap, and it survives the transport switch.** `ANTHROPIC_API_KEY` stays banned from the environment on both paths, for a reason that now applies to each of them: on the subscription path its presence silently defeats plan auth and bills API rates, and on the API path the key belongs in configuration like every other secret, so one in the environment means two places supply the same credential and nothing on the surface says which won. Assert its absence at startup and fail loudly.
Why:  The rationale was written against the subscription path alone, so a session adding the API path could reasonably have read the ban as no longer applying. The variable stays banned and the reason is replaced rather than deleted, because the two paths ban it for different reasons and both are worth stating.

### 2026-08-25 — ARCHITECTURE.html — cites The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface
Was:  <p>A practical consequence: running the seat through the Claude Agent SDK on an existing subscription costs nothing marginal, which is the strongest argument for it, but the pinning requirement still applies and a vendor retiring the pinned model is a forced fork either way.</p>
Now:  <p>A practical consequence: <b>the transport is a configuration switch</b>. The subscription path runs the seat through the Agent SDK against an existing Claude plan and costs nothing marginal; the API path calls the Messages API and costs the table above, which is a few dollars a year. Neither is the only option and the choice is reversible at any time, <b>provided the served model string does not change</b>. If it does, switching forks the record exactly as changing the model does. The pinning requirement is unaffected and now applies to the transport as well, and a vendor retiring the pinned model is a forced fork either way. (see: The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface)</p>
Why:  "The strongest argument for it" understated the trade by pricing one side and not the other, and by presenting a choice as a conclusion. Both costs are now stated, the choice is named as reversible, and the one condition under which it is not neutral, a change in the served model string, is stated with it. The pinning requirement and the forced-fork consequence are unchanged.

### 2026-08-25 — ARCHITECTURE.html — cites The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface
Was:  <p>One thing that does not migrate: the RTX 3090. Nothing here needs it, since the researcher runs through the Claude Agent SDK rather than locally, but it is worth knowing before any part of the design starts assuming local inference.</p>
Now:  <p>One thing that does not migrate: the RTX 3090. Nothing here needs it, since the researcher runs against a hosted model on either transport rather than locally, but it is worth knowing before any part of the design starts assuming local inference.</p>
Why:  The sentence named one transport as the way the researcher runs. Its actual point, that nothing in the design needs local inference, holds on both paths and is now written that way.

### 2026-08-25 — RUNBOOK.md — cites The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface
Was:  5. Confirm `ANTHROPIC_API_KEY` is **not** set anywhere in the environment. If it is, the researcher seat bills at API rates instead of using the subscription.
Now:  5. Confirm `ANTHROPIC_API_KEY` is **not** set anywhere in the environment. It stays out on both researcher transports: on the subscription path its presence silently defeats plan auth and bills API rates, and on the API path the key belongs in `appsettings.Secrets.json` with every other secret, so one in the environment means two places supply the same credential and nothing on the surface says which won.
Why:  Setup step 5 gave the operator a reason that applies to one transport, which reads as permission to set the variable on the other. Not among the three edits the change was scoped to, but "no document treats either transport as the only option" is the stated done condition, and this document did.

### 2026-08-25 — CLAUDE.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  7. The checkpoint's expectations are added to the golden fixture, so `tools/verify-phase` covers it from now on. A checkpoint that adds behaviour and no expectation has widened the unexamined set.
Now:  7. The checkpoint's expectations are added to the golden fixture **with their tier**, so `tools/verify-phase` covers it from now on, and at least one of them is `DERIVED` or `CONFIRMED` rather than `FROZEN`. A checkpoint that adds behaviour and no expectation has widened the unexamined set; one that adds only frozen expectations has added regression detection and called it verification. Expectations are owed at the checkpoint that produces them, **or carried to the checkpoint that first can** where the fixture does not exist yet, and a carried obligation is recorded in `BUILD_PLAN.md` when it is created rather than remembered. (see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything)
Why:  Done condition seven required an expectation and said nothing about how it was produced, so a checkpoint could satisfy it entirely with output copied from the engine it was meant to check. The carve-out is stated because the fixture does not exist until 1.7 and the condition is preceded by "All seven, or it is not done".

### 2026-08-25 — CLAUDE.md — cites Data ownership is declared once, in SCHEMA.md
Was:  The repository exists and the document corpus is committed. There is no `src/`, no solution file, and no code of any kind.

      The next thing to happen is checkpoint 1.1. Everything in "Conventions", "Definition of done for a checkpoint" and "Merge" is written against commits, and from the first commit onward those rules are live: a clean edit to a spec records its prior text in `CHANGELOG.md` with the decision authorising it, and a checkpoint ends in a `PROGRESS.md` entry.

      Keep this section current. A session that reads "Repository layout" and goes looking for `src/` has been misled by the document that was supposed to orient it.
Now:  The solution exists under `/src` with the six projects "Repository layout" describes, and the rules in "Conventions", "Definition of done for a checkpoint" and "Merge" are live: a clean edit to a spec records its prior text in `CHANGELOG.md` with the decision authorising it, and a checkpoint ends in a `PROGRESS.md` entry.

      **Which checkpoint the build is on is the last entry in `docs/PROGRESS.md`,** and the one to build next is the checkpoint after it in `docs/BUILD_PLAN.md`. That is stated as a pointer rather than as a number here on purpose: a number in this file is a second place the same fact lives, and it goes stale the moment a checkpoint lands without anyone noticing.

      Anything a checkpoint has not built yet does not exist, however completely `docs/ARCHITECTURE.html` describes it. The phase report at 1.7 is what says which of the two you are looking at.
Why:  The section said there was no `src/`, which stopped being true at 1.1. It is now written as a pointer at the record that already carries the answer, so it does not need editing again at every checkpoint. The cited decision is the general form of the rule being applied: a fact belongs in one document, and restating it elsewhere is how a corpus drifts.

### 2026-08-25 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  | 1.6 | IndicatorEngine: EMA 9/21/50, ADR20, ATR14, median dollar volume | Values for three hand-picked tickers match an independent calculation to 4 decimal places |
Now:  | 1.6 | IndicatorEngine: EMA 9/21/50, ADR20, ATR14, median dollar volume | Values for three hand-picked tickers match an independent calculation to 4 decimal places. **The independent calculation and its source are recorded in PROGRESS**, and those values carry forward as the fixture's first `DERIVED` expectations at 1.7 rather than being checked once and discarded (see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything) |
Why:  The hand-check at 1.6 is the strongest evidence phase 1 produces and nothing carried it forward, so it would have been performed once and discarded.

### 2026-08-25 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  | 1.7 | **Phase report harness** and the golden fixture: real bars for 30 tickers over 250 sessions, committed, with expected indicator values frozen beside them. `tools/verify-phase` runs the pipeline over the fixture, diffs against expectations, parses the architecture tables, located by heading text rather than by position, and asserts each claim against the code, then writes `artifacts/phase-report.html` and `.json` | **Openable, and machine-readable.** Report shows three sections: doc conformance, fixture diff, coverage. Every architecture claim is pass, fail, or **unexamined**, and unexamined is not a pass. From here on every checkpoint adds its expectations to the fixture (see: Every phase ends in a generated phase report, not in a page somebody looks at) |
Now:  | 1.7 | **Phase report harness** and the golden fixture: real bars for 30 tickers over 250 sessions, committed, with expected indicator values beside them, **each carrying its tier**. `tools/verify-phase` runs the pipeline over the fixture, diffs against expectations, parses the architecture tables, located by heading text rather than by position, and asserts each claim against the code, then writes `artifacts/phase-report.html` and `.json` | **Openable, and machine-readable.** Report shows three sections: doc conformance, fixture diff, coverage. Every architecture claim is pass, fail, or **unexamined**, and unexamined is not a pass. The fixture diff is broken down **by tier** rather than reported as one number, and the report states how many expectations changed since the last commit alongside how many passed. **1.7 back-fills expectations for 1.1 to 1.6 with their tiers**, discharging the obligation raised at 1.1, since those checkpoints predate the fixture and could not meet done condition seven when they landed. From here on every checkpoint adds its expectations to the fixture (see: Every phase ends in a generated phase report, not in a page somebody looks at) (see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything) |
Why:  "Frozen beside them" named the weakest tier as though it were the only one, and the back-fill closes the obligation raised at 1.1 against a stated requirement rather than against someone's memory.

### 2026-08-25 — ARCHITECTURE.html — cites Components are named, not coded
Was:  The design mitigates rather than solves this. The that check filter excludes the names most likely to be hard to borrow,
Now:  The design mitigates rather than solves this. The <code>tradable-shortable</code> check excludes the names most likely to be hard to borrow,
Why:  A casualty of the sweep that replaced check codes with names. "The that check filter" is what is left when an identifier is replaced by a placeholder rather than mapped to its replacement, and it left the sentence pointing at nothing. The check it means is the one named two tables above it, and the same sentence ends by naming what that check exists for.

### 2026-08-25 — ARCHITECTURE.html — cites Headings carry no numbers, and anchors are slugs
Was:  The harness is described in 18.1 and is built at the seventh checkpoint of phase 1, before most of what it checks exists.
Now:  The harness is described under "The phase report" and is built at the seventh checkpoint of phase 1, before most of what it checks exists.
Why:  A section number left over from before headings became slugs. It resolved to nothing.

### 2026-08-25 — ARCHITECTURE.html — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  <tr><td>Filesystem case sensitivity</td><td>macOS is insensitive by default, Linux is not</td><td>Lowercase file and directory names throughout, enforced by a check. A mismatch that works on both your machines will fail the first time it touches a Linux CI runner</td></tr>
Now:  <tr><td>Filesystem case sensitivity</td><td>macOS is insensitive by default, Linux is not</td><td>Lowercase names for every path the application composes or reads at runtime: the data root and everything under it, the golden fixture, <code>artifacts/</code> and <code>tools/</code>. .NET source and project directories keep the framework's PascalCase, which the repository layout mandates. Enforced by the <code>path-casing</code> check, which asserts each path literal against the on-disk path byte for byte rather than asserting lowercase, since exact match is the stronger property. A mismatch that works on both your machines will fail the first time it touches a Linux CI runner</td></tr>
Why:  "Lowercase throughout" contradicted the repository layout in `CLAUDE.md`, which mandates `PullbackStrategyLab.Core` and its siblings in PascalCase, and it also misdescribed `path-casing`, which asserts exact match rather than lowercase. Taken literally the architecture forbade the layout the same corpus requires. Folded into the same edit: the two `<h3>` headings under "The two patterns" gained the `id` attributes every other heading in the document already had.

### 2026-08-25 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A two-column checks table, `| Check | Asserts |`, with twenty rows and no statement of which of them run. Followed directly by "The table lists every check that runs, not only the properties this file argues for."
Now:  A three-column table, `| Check | Runs | Asserts |`, with twenty-one rows, each carrying `every CI run`, `the matrix`, or the checkpoint that starts it. Two paragraphs added: "The Runs column is what makes the table a roster rather than a wish", and "A check that stops running is the sharpest form of that". A `price-storage-form` row is added, and `coverage-reported` and `architecture-conformance` restate what they assert.
Why:  Found at the 1.12 review. The table read as the live set and was not: `coverage-reported` was declared and did not exist, and `point-in-time`, `check-completeness` and `order-provenance` were declared and scheduled at checkpoints the table did not name. The one-directional rule stated below the table, that a check running as a CI step must be declared, had been discharged at 1.7 by a sweep rather than by a check, and the table had since drifted the other way with nothing to notice. The Runs column makes the roster assertable in both directions.

### 2026-08-25 — ARCHITECTURE.html — cites The store contains no absolute paths
Was:  <tr><td>2</td><td>Record the source counts</td><td>Row counts for setup, setup_signal, forward_return, trade and variant, plus the maximum setup id. Written down before anything is copied, or you have nothing to compare against.</td></tr>
Now:  <tr><td>2</td><td>Record the source counts</td><td>A row count for every table the store holds, derived from the schema rather than from a list here, taken before anything is copied, or you have nothing to compare against. <code>tools/snapshot-db</code> does it: a list goes stale at the migration that adds a table, and a count that silently omits one is the failure this step exists to catch.</td></tr>
Why:  The same defect the 1.11 rehearsal found and recorded as fixed, still live in the other document that states the same procedure. RUNBOOK.md's step 2 was corrected at 1.11 and this copy was not, so the design source of truth still told an operator to count five tables that do not exist, get zero, and report success. `stated-counts` compared the two procedures by row count, ten against ten, so it passed over the divergence; the conformance check now compares the steps themselves.

### 2026-08-25 — RUNBOOK.md — cites The evidence store holds only setups flagged forward, never setups reconstructed from history
Was:  | 5 | Full split history for the survivors | N |
      | 6 | Minute bars for 200 names to calibrate the fill model | ~1,000 |
      | | **Total** | **~3,005 + 2N** |
      **Size, measured rather than estimated.** N was 2,070 when this was first run, so steps 4 and 5 are 2,070 calls each and the whole procedure is about 7,145. It is one operation and it runs in one sitting; the order within it is what matters, not the calendar.
Now:  The split-history row removed, minute bars renumbered to 5, the total `~3,005 + N`, the size paragraph reading step 4 alone at 2,070 and the procedure at about 5,075, and a paragraph saying there is no split-history step and there was never any code for one.
Why:  The obligation raised at 1.9 fell due at 1.12 and was to be run or dropped rather than carried. The 1.12 review found it could not be run: the vendor client has `GetBulkSplitsAsync`, which is per date and costs 100, and `GetDailyHistoryAsync`, which returns bars. Nothing fetches one name's splits, so the step named work with no implementation behind it, and the obligation's own wording, that it "has not run", described a command that does not exist. Dropped rather than built, because what it buys is the history of splits from before the lab started and nothing reads that: splits arrive nightly from the bulk endpoint from the first night onward, and the detector run that would look further back writes to `calibration_setup`, where survivorship bias already disqualifies the rows as evidence.

### 2026-08-25 — BUILD_PLAN.md — cites The evidence store holds only setups flagged forward, never setups reconstructed from history
Was:  | 1.9 | RUNBOOK's step 5, the split history for every survivor, is a second 2,070 calls and has not run. Nothing depends on it: splits arrive nightly from the bulk endpoint, so what is missing is only the history of splits from before the lab started. It is either run or dropped at sign-off, rather than carried indefinitely | 1.12 |
Now:  removed
Why:  Discharged at the 1.12 sign-off by being dropped, on the terms the obligation itself set: run or dropped rather than carried. The row leaves the table cleanly rather than being struck through or marked closed, because the table is the list of obligations that are still open and a spec is read to know the current state. The disposition and the reasoning are in RUNBOOK.md at the backfill table and in the entry above it here.

### 2026-08-26 — .github/workflows/ci.yml, CLAUDE.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  A workflow with one job, the windows and macos matrix. CLAUDE.md's `path-casing` paragraph ended at "if that turns out to be true after 1.1, drop it rather than carrying it."
Now:  A second job, `rehearsal`, on `ubuntu-latest`, running `tools/ci.sh` and then the store copy against the store the fixture replay builds. CLAUDE.md gains "And a runner now exists for it to fail on."
Why:  `path-casing` was written against a runner nobody had. Case sensitivity is a property of the filesystem and both development machines are insensitive, so the check could not fail on either however often it ran, and the 1.11 obligation wanted a second machine to find exactly that class of fault. A container reaches it and a Mac was not required. Kept out of the matrix deliberately: Linux is not a supported platform here, it is an instrument for one fault, and folding it into the matrix would quietly widen what `two-platform` claims.

### 2026-08-26 — BUILD_PLAN.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  | 1.11 | The migration rehearsal ran on one machine, not two. Everything that can fail on one ran and passed: the counts, the integrity check, the schema on arrival, a nightly job over the copy and the chart drawn from it. What one machine cannot exercise is what the checkpoint was written for, which is the paths and the secrets that do not travel: a Windows path in a row, a timezone identifier the other platform rejects, a secrets file nobody copied. The store-portability check now covers the first of those permanently, and the other two wait for a second machine | 1.12 |
Now:  The obligation narrowed to step 6 alone, copying the secrets file, and due at the actual move rather than at a checkpoint.
Why:  Everything else it was carrying now runs on every push in the `rehearsal` job. What is left is not a checkpoint's work: it is a person copying a gitignored file between two machines, and dating it to a checkpoint would put a due date on an event nobody has scheduled.

### 2026-08-26 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  | 1.7 | The fixture screens the universe over one market day, because one is what the fixture holds, and the liquidity floor is a median over twenty. The replay measures the screen's verdict under the real floors and then runs against the wider list, so nothing downstream is screened on a number the floor does not mean. A fixture holding twenty market days would remove the difference and costs 1,900 calls and about 130 MB | 1.12 |
Now:  removed
Why:  Closed against both halves rather than left open on the half that was never worth paying for. The half that can be tested is: the fixture holds 251 sessions for each of its own names, so the twenty-session median is computable over them, and the replay now measures the real floor against the real window for all thirty, with every median independently derived. The half that cannot is the whole-market screen, which needs twenty bulk days at 1,900 calls and about 130 MB committed for ever to close a gap the live run closes nightly; `fixture-replay` records it out of scope with that condition written down. One weakness is recorded with it rather than left to be found: all thirty names clear the floor, the closest at 1.7 times it, so the comparison exercises the passing side only.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The Checks section ran "A check that stops running is the sharpest form of that" straight into "Unexamined and out of scope are counted separately". The `fixture-replay` row read: The pipeline over the golden fixture matches every committed expectation, broken down by tier, with every figure it produces named by one
Now:  Two paragraphs inserted between them, "And a check that keeps running while looking at less is the quiet form of it" and "The floor is a floor and not an equality, and that is the part worth defending". The `fixture-replay` row gains ", and every checkpoint in the fixture carries an independently produced expectation or names an open obligation"
Why:  Found at the third sign-off pass of phase 1. `CheckCoverage.Report` accepted any examined count and compared it against nothing, so the mechanism written to catch silent narrowing was itself silently narrowable: cutting `bar-append-only` from three bar tables to one left the suite green, the phase report GREEN, and one summary number nobody compares eight lower. `fixtures/checks-baseline.json` closes it with a committed floor per check. The floor is stated as a floor rather than an equality in the same edit, because that is the property a later session would most reasonably reverse and the cost of reversing it, false alarms then suppression, would look like tightening.

### 2026-08-26 — CLAUDE.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  Done condition seven ended: Expectations are owed at the checkpoint that produces them, **or carried to the checkpoint that first can** where the fixture does not exist yet, and a carried obligation is recorded in `BUILD_PLAN.md` when it is created rather than remembered.
Now:  The same sentence, followed by "The carrying is asserted per checkpoint, not remembered either", naming the `frozenOnly` section of the expectations file and the three ways `fixture-replay` fails a permit, and the sentence saying what asserting it over the fixture as a whole allowed.
Why:  The condition was already written per checkpoint and the check was not. `FixtureReplayCheck` asserted one `DERIVED` expectation anywhere in the fixture under a comment reading "Done condition seven, asserted rather than remembered", which one checkpoint's derived expectation satisfied on behalf of every other, and it passed for the whole time five checkpoints were frozen-only. Same shape as the finding above it: a check whose label claims more than its assertion. The corpus already had the carrying rule, so what was missing was the assertion and the place to record which checkpoints are carrying.

### 2026-08-26 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  **1.7 back-fills expectations for 1.1 to 1.6 with their tiers**, discharging the obligation raised at 1.1, since those checkpoints predate the fixture and could not meet done condition seven when they landed.
Now:  The same clause, followed by: That discharges the obligation raised at 1.1 as far as tiers reach and no further, naming 1.3, 1.4, 1.5 and 1.7 itself as still frozen-only and pointing at the carried obligations table.
Why:  The row claimed a discharge wider than what happened. 1.7 gave every back-filled expectation a tier, which is the half of done condition seven that copying figures out of a run can satisfy; the half asking each checkpoint for one `DERIVED` or `CONFIRMED` expectation was not met at 1.3, 1.4, 1.5 or 1.7, and the row read as though it had been. A row saying an obligation is closed is the strongest possible way to stop anyone looking at it.

### 2026-08-26 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  The carried obligations table had two rows, raised at 1.6 and 1.11. The 1.6 row fell due at 1.12 and ended: which is the one step in this corpus a build session cannot do for itself | 1.12 |
Now:  Three rows. A row raised at 1.1 is added for done condition seven at 1.3, 1.4, 1.5 and 1.7, due at 2.1. The 1.6 row gains a sentence recording the move from 1.12 to 2.11 and why, and falls due at 2.11.
Why:  Two changes to one table, both from the 1.12 sign-off. The 1.1 row is the obligation the 1.7 row had been claiming was discharged, written down so a checkpoint can name it: `fixture-replay` now requires a frozen-only checkpoint to name an obligation that is a row of this table and whose due checkpoint `PROGRESS.md` does not yet record, which is the rule an out-of-scope architecture claim already obeys. 1.9 was the fifth frozen-only checkpoint and is not in the row, because it was closed at the same review from the captured responses for no vendor call. The 1.6 row moved because nothing else could happen to it: it needs a person reading a charting platform, no build session can perform that, and a phase held on it would have a permanent due point rather than a pending one. 2.11 is the first checkpoint that reads those figures.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The carried obligations table had no row raised at 1.12.
Now:  A row raised at 1.12, due at 2.6: an out-of-scope coverage item names the checkpoint that ends it, the way an out-of-scope architecture claim already has to.
Why:  Raised by the second sign-off pass as one of four things closing the phase, and left undone by the third, which was scoped to the two findings that blocked. Recorded rather than remembered, on the rule that a carried obligation is written into the plan when it is created. It is not a straight copy of the claim rule: two of `fixture-replay`'s exemptions close on a purchase nobody has scheduled rather than on a checkpoint, so what the rule says about those is the work rather than an afterthought. Due at 2.6, the next checkpoint that brings a check into being, so the rule is in place before phase 2 writes more out-of-scope coverage under the old one.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The Checks section ran "The floor is a floor and not an equality, and that is the part worth defending" straight into "Unexamined and out of scope are counted separately". The paragraph above it ended: At or above the floor passes; below it fails, names the check and prints both figures.
Now:  That sentence gains a clause saying what the floor does not close, and two paragraphs are inserted after the floor-not-equality one: "A floor holds a scope, not a total, and on the day it is recorded the two are indistinguishable" and "So a check states a floor under each scope it names, and a run is measured scope by scope".
Why:  Found at the phase 1 sign-off, run rather than argued. `CheckCoverage.Report` compares one number per check against the baseline, and that number is the sum of every scope the check names. In five of the seventeen checks the sum is dominated by a size-of-corpus figure rather than by the property: `bar-append-only` reads 47 source files to hold 3 bar tables, `path-casing` reads 2,412 string literals to compare 27 paths, and `clock-usage`, `writer-ownership` and `store-portability` have the same shape. Narrowing `bar-append-only` to one bar table and adding five ordinary files left `tools/ci.*` green and the phase report GREEN with the examined total higher than before; gutting `path-casing` so it compared no paths at all and adding forty ordinary literals did the same. The reverse fires more easily still: removing two string literals from one test file turned `path-casing` red. The repair is a floor per scope rather than per check and it is code, so it is carried to 2.1 rather than made here; the rule is written now because the defect is in how these checks get written and phase 2 writes more of them.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | 1.12 | **The examined floor is compared per check and the property it guards is per scope.** ... and it is code rather than a document, which is why it is carried here. Due at 2.1, the first checkpoint of phase 2 and the last one before phase 2 code starts growing the corpus |
Now:  The same row with its due point in the table's own third column: ... which is why it is carried here. The first checkpoint of phase 2 and the last one before phase 2 code starts growing the corpus | 2.1 |
Why:  The row carried two cells where every other row in the table carries three, with its due point written into the obligation prose rather than into the `Due at` column. `Schedule.Read()` guarded with `if (row.Count >= 3)` and dropped it, so the obligation driving checkpoint 2.1 was absent from `Schedule.Obligations` entirely and no permit could resolve against it. The row is the symptom and the parser is the fix: `MarkdownTable` now asserts every body row against its own table's header width and fails naming the table, the row and both widths, and the guard at the call site is gone. Found by reading the table while planning phase 2, not by any check.

### 2026-08-26 — SCHEMA.md — cites Proposals come in two kinds, rule changes over existing signals and requests for a new signal
Was:  No signal library anywhere in the corpus. `signal_definition` declared three columns, `signal_name`, `formula` and `source_columns`, and nothing said what went in them.
Now:  A `## Signals` section between `## Setups` and `## Trading — phase 4`: every signal with its formula, its source columns and a status of `active` or `candidate`, grouped by axis, plus the one signal that traces to nothing recorded as a finding.
Why:  Checkpoint 2.1's done condition is that every signal traces to named stored columns and any that does not is a finding rather than an assumption. That is unanswerable against a library nobody wrote down. Active means SignalVectorizer freezes it, and is what "copies every number the decision depended on" resolves to; candidate means the formula and the columns are settled and nothing computes it yet, so SignalBackfiller at 6.1 computes a specified formula rather than inventing one. The architecture's verdict that the library "is almost entirely price path, and volume appears nowhere except buried inside a scan definition" stays true of the active set and is stated as such: the three volume signals are candidates, because no phase 2 decision depends on them and freezing them would widen the library on a guess rather than through the admission route. `earnings_in_window` is the one that traces to nothing: no stored column carries an earnings date, `corporate_action` holds splits and dividends and neither implies one, and the vendor's calendar endpoint is not among those the call budget is built on. Recorded as a finding with what it would cost, because a candidate with blank source columns reads as work scheduled and is actually a purchase nobody has priced. It is a section rather than the `SIGNALS.md` the plan named, because the corpus is eight documents and a ninth requires retiring one.

### 2026-08-26 — ARCHITECTURE.html — cites The scans select a fixed count by rank, not a threshold on the move
Was:  ScanEngine's entry named the six scans and stopped. No selection rule, no threshold, no lookback for "the past month", and no statement of which price basis the magnitudes read.
Now:  The same six, each taking the top 50 universe names by its own magnitude ranked 1 to 50, with the magnitudes stated as the one-day change, the gap from the previous close to the open, and the change over 20 sessions, all on the adjusted basis with the open put there through its own bar's `adj_close / close` factor.
Why:  2.3 cannot be built against a name. Rank rather than a percentage because phase 2 calibrates against nightly counts with no forward return in the store, and only a rank cut can be calibrated that way; a percentage floor is a claim about market volatility over the sample. Rank also makes the six comparable, which no percentage does. The basis was the more dangerous half of the gap: read raw, a two-for-one split is a 50% decline and tops the decliner scan every time one happens, then feeds the thrust check as a real event. That is a plausible ranked list rather than an error, and it is the same basis trap the averages closed at 1.12 with nothing closing it for the scans.

### 2026-08-26 — ARCHITECTURE.html — cites A released cap slot goes to the side that still has candidates
Was:  SetupCapper's entry said unused slots on either side are released to the other, and stated no order in which a freed slot is allocated.
Now:  The same, plus: each side takes the lesser of its candidate count and its allocation, whatever either leaves unfilled is offered to the other by rank within that other side, and no priority order is needed because a side that released a slot is not also asking for one.
Why:  The obvious reading of "unused slots released" is that two sides compete for a freed slot and something breaks the tie. They cannot: a slot is released only by a side that ran out of candidates, and that side is not also short. Stating the property is what stops a later session inventing a tiebreak for a case that cannot arise, which would read as though it can.

### 2026-08-26 — ARCHITECTURE.html — cites The cluster grouping key is industry, not sector
Was:  | ThemeClusterer | Nightly 18:15 | Counts same-sector names flagged together |
Now:  | ThemeClusterer | Nightly 18:15 | Counts same-industry names among that night's scan hits |
Why:  Two corrections in one row. The key was "sector" here and "industry" in both cluster checks and in the authored parameter, and the two are different columns giving different answers on the same night. And "flagged together" put ThemeClusterer at 18:15 downstream of detectors that run at 18:20; SCHEMA settles it by putting `cluster_count` on `scan_hit` rather than on `setup`, so the count is over scan hits, which run at 18:10. The stated clock and the stated data dependency now agree.

### 2026-08-26 — ARCHITECTURE.html — cites Headings carry no numbers, and anchors are slugs
Was:  | The ladder | 9-day above 21-day above 50-day means a steady rise. The reverse order means a steady fall. This lab uses those as its definitions of uptrend and downtrend. | and the squeeze check reading "narrower than its own recent average".
Now:  The ladder entry names all three grades, defines `mixed` as anything that is neither and states that the three are a partition; the squeeze check reads "narrower than its own average over the last 20 sessions".
Why:  `mixed` appeared in the catalogue and in Figure 3 as a grade and was defined nowhere, so TierClassifier at 2.4 had two of its three outputs specified. And the squeeze check stated no window at all where the contraction check beside it is pinned to 20 days, which is the difference between a check and a description of one.

### 2026-08-26 — ARCHITECTURE.html — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "Four thresholds are marked "phase 2 count check"." over three rows carrying the marker. The authored-parameters table had no row for scan breadth or the month-mover lookback, and the P2 build-order row did not list SectorResolver.
Now:  "Five rows of the authored-parameters table are marked "phase 2 count check"", derived from the table and asserted by `stated-counts`, with rows added for scan breadth and the month-mover lookback and a row for the squeeze test's window. SectorResolver added to the P2 build-order row.
Why:  The stated count was of thresholds and the table is of rows, and the pullback-shape row carries two numbers, so the two units differed and nothing derived either. It is a row count now, on the rule every other count in this corpus obeys: a number a spec states about its own contents is derived and checked, or it is not written. SectorResolver appeared in no phase row at all while phase 2 depends on it for the cluster key and the short side's market-cap floor.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The Checks section stated the per-scope floor as a rule and described no implementation, and the paragraph on unexamined and out of scope said nothing about how unexamined is counted.
Now:  Two paragraphs added: one stating the shape the rule took in `fixtures/checks-baseline.json` and the four directions the comparison runs in, and one stating that unexamined counts admissions rather than the things they cover.
Why:  The rule was written forward-looking at the phase 1 sign-off and the code landed at 2.1, so the spec now describes what exists rather than what was intended. The second paragraph is a defect the first one exposed: `PathCasingCheck` records its no-work branch with a count of zero, zero adds nothing to a sum, and the report said "unexamined 0" on the same page as the admission. Counting admissions makes an admission visible whatever its size.

### 2026-08-26 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  Five rows in the carried obligations table, two of them falling due at 2.1: the row raised at 1.1 for done condition seven at 1.3, 1.4, 1.5 and 1.7, and the row raised at 1.12 for the examined floor being per check where the property is per scope.
Now:  Three rows. Both 2.1 rows are removed.
Why:  Both were discharged at 2.1 and a carried obligation that has been met is a row saying a checkpoint still owes what it has done. The discharges are recorded in `PROGRESS.md` with what was built and how it was verified, which is where a completed obligation belongs; this table carries what is still open. The three that remain are the `CONFIRMED` values at 2.11, the out-of-scope coverage naming rule now at 2.2, and step 6 of the move at the move.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The Checks section stated the naming rule for an out-of-scope claim and said nothing about an out-of-scope coverage item.
Now:  A paragraph naming the three shapes a coverage item may take, and why the third is counted separately.
Why:  The obligation raised at 1.12 and due at 2.2. A claim has always had to name the checkpoint that ends it; a coverage item carried free prose and nothing read it, and seven checks recorded 149 of them. The rule does not transfer unmodified, which the obligation said and which converting the call sites confirmed twice over. Two of `fixture-replay`'s exemptions close on a purchase rather than a checkpoint and differ by three orders of magnitude in cost, which prose loses. And several close on nothing at all: a citation inside a dated record, a runner set asserted against the workflow, a column exempted by name. Forcing those into a checkpoint would invent one and forcing them into a price would lie about the shape, so there are three and the third is counted separately, because a by-design exemption growing unnoticed is how this rule would be lost.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | 2.2 | SignalVectorizer and the frozen signal row shape, `setup_signal` | Written once, never updated by the vectorizer. Asserted |
Now:  The same row naming migration 011 and the out-of-scope coverage rule moved here from 2.6, with the four ways the write-once property is asserted and the two conditions on the signal library.
Why:  "Asserted" named no assertion, and the four that shipped are not interchangeable: a rerun writing nothing does not prove a restated bar leaves a frozen value alone, and neither reaches an `UPDATE` a later stage might add. The obligation moved from 2.6 because 2.2 creates `setup` with three of its four declared writers unbuilt, which is what makes `writer-ownership` record a run of deferred items; 2.6 was already the heaviest checkpoint in the phase and would have met the rule a checkpoint after it was needed.

### 2026-08-26 — RUNBOOK.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  | 18:25 | signal freeze, journal | 0 |
Now:  | 18:25 | `vectorize`, the signal freeze, then journal | 0 |
Why:  The stage now exists and the nightly order names entrypoints by the verb an operator types. Zero calls is unchanged and correct: the vectorizer reads the store and makes no vendor request.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The carried obligations table held a row raised at 1.12 for the out-of-scope coverage naming rule, due at 2.2.
Now:  Removed.
Why:  Discharged at 2.2. The discharge is recorded in `PROGRESS.md` with what was built and how it was falsified; this table carries what is still open. Two rows remain: the `CONFIRMED` values at 2.11 and step 6 of the move.

### 2026-08-26 — SCHEMA.md — cites The scans select a fixed count by rank, not a threshold on the move
Was:  `scan_hit` declared `ticker`, `as_of`, `scan`, `rank` and `cluster_count`, with `rank` unannotated and `cluster_count` noted as same-sector hits.
Now:  A `magnitude` column is added, `rank` states the breadth and cites the decision, `cluster_count` reads same-industry, the primary key is declared, and two notes are added.
Why:  The magnitude the rank was taken on is what the thrust signals freeze. Deriving it later from bars would put the same arithmetic in two places in the one situation where a disagreement is invisible, since a wrong magnitude still produces a plausible ranked list; storing it also makes the ordering auditable against the number it was taken on. `cluster_count` said sector where both cluster checks and the authored parameter say industry, which the 2.1 spec pass settled everywhere else and missed here.

### 2026-08-26 — ARCHITECTURE.html — cites Every scan magnitude is computed on the adjusted basis
Was:  "Read raw, a two-for-one split reads as a 50% decline and tops the decliner list every time it happens."
Now:  The same point without naming the wrong scan, plus: the vendor adjusts the history behind a split and leaves the sessions after it alone, so the one-day and gap magnitudes agree on both bases on the split date itself and only the month magnitudes span the adjustment.
Why:  Measured at 2.3 and the original was wrong about where the trap sits. On IESC's split date the raw and adjusted one-day changes are both -0.0537, because the adjustment lands on the prior history. The twenty-session magnitude is +0.0746 adjusted and -0.4627 raw. A guard placed on the daily scan would have found nothing.

### 2026-08-26 — BUILD_PLAN.md — cites The scans select a fixed count by rank, not a threshold on the move
Was:  | 2.3 | ScanEngine, six scans, three per direction | Hit counts per scan per night recorded |
Now:  The same row naming migration 012, the shared magnitudes, the thrust signals moving to frozen, and three properties the hit count does not state.
Why:  A hit count is the same number whatever the scan ranked on and whichever way it ordered. The three conditions added are the ones a count cannot carry: the breadth is a count rather than a threshold, the tiebreak is stated so the boundary does not depend on row order, and the basis is adjusted.

### 2026-08-26 — RUNBOOK.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  | 18:10 | scans, ladder grade | 0 |
Now:  | 18:10 | `scans`, then the ladder grade | 0 |
Why:  The stage exists and the nightly order names entrypoints by the verb an operator types. Zero calls is unchanged: the scans are a function of stored bars.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | 2.4 | TierClassifier | Ladder grade on every universe member per night |
Now:  The same row naming the later-observation write and the ladder grade moving to frozen, with the partition swept rather than sampled and a refused grade counted rather than absorbed.
Why:  "A grade on every member" is satisfied by a stage that grades nothing and says it graded everything, which is exactly what happened on the first run: the write collided with the engine's own row on the primary key, the insert said DO NOTHING, and the stage reported thirty grades over zero rows. The two conditions added are the ones the original could not carry.

### 2026-08-26 — RUNBOOK.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  | 18:10 | `scans`, then the ladder grade | 0 |
Now:  | 18:10 | `scans`, then `tiers` for the ladder grade | 0 |
Why:  The stage exists and the nightly order names entrypoints by the verb an operator types.

### 2026-08-26 — BUILD_PLAN.md — cites The market-mood label is recorded on every setup and filters nothing in the baseline
Was:  | 2.5 | RegimeLabeler, two scores summed | Both raw scores stored alongside the label. Label filters nothing |
Now:  The same row naming migration 013 and the mood signals moving to frozen, with "filters nothing" asserted against the shipped source and two boundary conditions stated.
Why:  "Label filters nothing" is the one condition here that no figure can show, and a stage that quietly began branching on it would produce identical numbers. It is now a source scan with comments stripped and three files exempt by name, on the same pattern the clock ban uses. The two conditions added are the boundaries that decide a mood: neither extreme is reachable without both scores agreeing, which is what makes the three states buffer themselves, and an unmeasurable tracker scores zero rather than minus one, because reading a missing feed as a falling market turns an outage into a signal.

### 2026-08-26 — RUNBOOK.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  | 18:15 | cluster, regime | 0 |
Now:  | 18:15 | the cluster count, then `regime` | 0 |
Why:  The stage exists and the nightly order names entrypoints by the verb an operator types. The cluster count arrives at 2.6 and stays prose until then.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | `check-completeness` | 2.6 | Every setup row has a result recorded for every check defined at its date |
Now:  The same row with `every CI run` in the Runs column, and the reconciliation stated as running in both directions against ARCHITECTURE's own gate lists.
Why:  The check exists as of 2.6 and runs as a named CI step, so a checkpoint row would now name a checkpoint `PROGRESS.md` records, which `coverage-reported` fails on. Both directions matter and reading one would catch half the divergences: a gate the detector does not run is a rule the document states and the lab does not apply, and a check the detector runs that no gate names is a rule the lab applies and the document does not state.

### 2026-08-26 — SCHEMA.md — cites The scans select a fixed count by rank, not a threshold on the move
Was:  `scan_hit.cluster_count` noted as "same-sector hits that night".
Now:  "same-industry hits that night", with a note stating why and citing the decision.
Why:  Missed by the 2.1 spec pass, which settled the key everywhere else. Sector and industry are different columns giving different answers on the same night.

### 2026-08-26 — BUILD_PLAN.md — cites Failed checks are recorded rather than discarded
Was:  | 2.6 | LongSetupDetector, ten checks, all results recorded, with SectorResolver and ThemeClusterer behind the cluster check | `check-completeness` passes: every recorded setup has a result for every check |
Now:  The same row naming the shared Core rules and the last seven signals moving to frozen, with the reconciliation stated in both directions and the one-sidedness measurement added as a condition.
Why:  "Every recorded setup has a result for every check" is satisfied by a fixture where every check has only ever returned one answer, and 300 results diffed then reads as full coverage while the branch nobody reached is asserted by nothing. The report now names one-sided checks individually, because the useful sentence is which checks rather than how many, and the decision on the remedy sits at this checkpoint rather than at sign-off, where the session that would have to take it cannot.

### 2026-08-26 — RUNBOOK.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  Three nightly rows named their stages in prose: the cluster count, the detectors, and the sector resolve.
Now:  `clusters`, `detect-long` and `sectors` named by the verb an operator types.
Why:  The stages exist. The sector row keeps its ~50 calls: the lookup is lazy and cached, so the steady-state cost is names newly surfaced by a scan rather than the universe.

### 2026-08-26 — ARCHITECTURE.html — cites A gate handed an absent or degenerate quantity fails rather than passing
Was:  The long check list ran straight into the plain box headed "Why the exit-tight check is the interesting one", with nothing between them.
Now:  A rule box headed "A gate handed nothing fails, on both lists", stating that a gate whose quantity is absent, zero or undefined fails and records what was missing, and naming the vacuous `exit-tight` pass as the failure it closes.
Why:  The document defined ten thresholds and never said what a gate does when handed no number at all. The long detector's first fixture run passed `exit-tight` on a name whose thrust was the session itself: entry and give-up point at the same price, distance zero, and zero clears every threshold expressed as a maximum. The answer decides verdicts, so it belongs where the strategy is stated rather than only in the code that happened to get it right.

### 2026-08-26 — ARCHITECTURE.html — cites Two directions are tested, with separate detectors, separate management and separate scoring
Was:  `reached-ceiling`'s note read: The level is where the bounce is expected to stall. On the long side the equivalent level is where the dip is expected to hold.
Now:  The same, plus a paragraph saying the third clause does not run until 4.4, that the check is narrower than the line describes until then, and why approximating an anchored average price from daily bars is worse than not running the clause.
Why:  The clause needs a volume-weighted average anchored at the last swing high, computed from minute bars by VwapEngine at 4.4. A daily-bar approximation would put a number that looks like the real thing inside the check that decides whether a bounce reached its ceiling, which is the shape of the vacuous `exit-tight` pass one gate list up: plausible, wrong and silent. A later session reading a passing `reached-ceiling` needs to know which clauses ran.

### 2026-08-26 — SCHEMA.md — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  The `counts_against_ceiling` note named the history backfill as the one-time operation the flag exists for.
Now:  The same note, followed by a paragraph naming fixture capture on the same grounds and stating the scope rather than a second exemption: what decides the flag is whether the run is the evening's job, so a third one-time operation inherits the answer without a third entry.
Why:  Second time the distinction surfaced and it was nowhere in the corpus. Adding an endpoint to the fixture cost 30 live calls on 2026-08-26; charged against the evening's allowance they would have competed with the night's work for no reason. Written as a scope statement because two exemptions listed one after another read as a growing list of special cases rather than as one rule.

### 2026-08-26 — BUILD_PLAN.md — cites Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
Was:  | 2.7 | ShortSetupDetector, ten checks | Same. Plus: no setup row carries a direction its detector does not own |
Now:  The same row naming the degeneracy proof and the authored boundary suite, with the done condition stating both, the one-sidedness condition, the `AUTHORED` marking, and `reached-ceiling`'s third clause out of scope naming 4.4.
Why:  2.7 is the first checkpoint at which all twenty gates exist, so it is the first that can assert anything over the gate list as a list. It is also where the one-sidedness raised at 2.6 is closable: forty authored cases give every gate a pass and a fail at no vendor call, where the remedy priced at 2.6 would have left eight gates with three results each instead of two.

### 2026-08-26 — RUNBOOK.md — cites Two directions are tested, with separate detectors, separate management and separate scoring
Was:  | 18:20 | `detect-long`, then the short detector | 0 |
Now:  | 18:20 | `detect-long`, then `detect-short` | 0 |
Why:  The stage exists and has a verb. An operator following this file at 18:20 needed the name they would type, and "the short detector" was the placeholder standing in until there was one.

### 2026-08-26 — RUNBOOK.md — cites The cluster grouping key is industry, not sector
Was:  | 19:00 | `sectors`, resolved once per name and cached | ~50 |, sitting between the watchlist at 18:40 and the minute bars at 20:30.
Now:  The same stage at 18:12, between `tiers` and `clusters`, with a line saying it was moved and why.
Why:  Three stages read what it writes and all three ran before it. `clusters` at 18:15 counts same-industry names and `tradable-shortable` at 18:20 reads the market capitalisation, so on a live night a name newly surfaced by a scan had neither when they ran. Neither consumer errors on a missing sector: the cluster count reads nought and the short check fails for want of a figure, and both look like an ordinary quiet night. The fixture replay could never have shown it, because the replay ran the lookup first. The stage order there is now asserted against this table rather than kept in step by hand.

### 2026-08-26 — RUNBOOK.md — cites Components are named, not coded
Was:  | 17:30 | bulk daily bars | 100 |
Now:  | 17:30 | `daily-bars`, the whole market in one bulk request | 100 |
Why:  The row described the work and not the verb an operator types at 17:30, which is the same gap the short detector's row had. Found by the check that asserts the replay's stage order against this table: a row naming no verb is a row the schedule cannot be read from.

### 2026-08-26 — SCHEMA.md — cites Failed checks are recorded rather than discarded
Was:  No `detector_error` section. `setup`, `calibration_setup` and `setup_signal` ran straight on to the trading stores.
Now:  A `detector_error` section between `calibration_setup` and `setup_signal`, grain date plus ticker plus direction, with both detectors declared as writers disjoint by direction and a note on why each issues its own insert.
Why:  ARCHITECTURE's failure table has said since before any code existed that a detector erroring on one stock writes an error row for that stock and date, and the corpus placed the claim at 2.7. Nothing wrote one. A silent skip shrinks the recorded universe without anyone noticing: every count downstream is over the setups that were recorded, so a name the detector could not read is simply absent and the night looks lighter rather than wrong.

### 2026-08-26 — BUILD_PLAN.md — cites Failed checks are recorded rather than discarded
Was:  2.7's done condition ended at "No setup row carries a direction its detector does not own."
Now:  The same, plus a name a detector cannot read getting an error row and the run being recorded partial rather than skipped into a night that merely looks lighter.
Why:  The behaviour is owed at this checkpoint by ARCHITECTURE's failure table, and a done condition that does not state it leaves the phase report to be the only thing that noticed.

### 2026-08-26 — RUNBOOK.md — cites The nightly cap is 60, split forty long and twenty short, unused slots released
Was:  | 18:28 | cap | 0 |
Now:  | 18:28 | `cap`, the night truncated to sixty by rank | 0 |
Why:  The stage exists and has a verb. The same gap `daily-bars` and the short detector had: a row describing the work rather than naming what an operator types, which the check asserting the replay's stage order against this table cannot read.

### 2026-08-26 — SCHEMA.md — cites Versions select from one shared nightly candidate list rather than each re-scanning
Was:  The `setup` section ended at the note on the two detectors writing disjoint rows.
Now:  Two further notes: that `rank` and `capped_out` are the night's rather than a version's and that there is deliberately no column that could make them a version's, and that both are null on a setup that failed a gating check.
Why:  A cap applied per version leaves the disagreements between versions unscoreable, and the property is unassertable once versions exist: by then the record it destroyed cannot be reconstructed. The schema is where it can be held now, so the absence is stated and a test reads it.

### 2026-08-26 — BUILD_PLAN.md — cites A released cap slot goes to the side that still has candidates
Was:  | 2.8 | SetupCapper, 60 a night, 40 long 20 short, unused slots released | Truncation recorded with the pre-cap count |
Now:  The same row naming the verb and the arithmetic's home in Core, with the done condition stating the sweep over every arrangement of the two counts, the order-independence of the release, the ranking within a direction, and the shared candidate list asserted against the schema.
Why:  "Truncation recorded with the pre-cap count" is satisfied by a run over an empty candidate list, which is exactly what the fixture produces: two setups, neither clearing every gating check. The release rule's whole claim is about every arrangement of the two counts, so the condition asks for every arrangement.

### 2026-08-26 — SCHEMA.md — cites The agreement a person records is written through the read surface, and it is the only write it makes
Was:  **One writer, one connection.** The Worker is the sole writer by design ... The Api opens the file read-only. A second writing connection produces intermittent lock failures that look like load problems and are not.
      And: Update Setup inspector (`agreement`, `agreement_note`) on `setup`.
Now:  The same note narrowed to "the sole writer of everything the nightly job produces", followed by a paragraph naming the one exception and its scope, and the writer declared as `LabSetups`, the type that issues the statement.
Why:  The three rules around the agreement column left nowhere to put it. The Web project may not open the store, the Worker has no channel a browser can reach, and one writer per table per operation means it cannot be split. What makes this the right exception rather than the first crack in the rule is that it is not the same kind of write: a person saying what they thought of one row, on two columns no computation reads. The writer is declared by type so `writer-ownership` holds the scope rather than the prose.

### 2026-08-26 — BUILD_PLAN.md — cites The Web project reads through the Api and never opens the store
Was:  | 2.9 | Setup inspector, the gallery page: prev/next, filter by failed check, agreement capture | **Openable.** You page through a night's setups by keyboard and record agree or disagree per setup |
Now:  The same row naming `LabSetups`, with the done condition stating the two lists, every check on every card, the shared component, the form post working without the script, and a filter that hid everything saying so.
Why:  "Openable" is a condition only a person can discharge, and everything around it can be held without them. The additions are the properties a build session can assert before that person opens it, so the review they do is a review of a page that already holds them rather than a first pass over whether it renders.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | `point-in-time` | 2.10 | No signal definition reads a column whose observed date can exceed the setup date |
Now:  The same row with `every CI run` in the Runs column and the property stated as its three halves: the readers' signatures, the hand-written statements beside them, and a row observed after the as-of being invisible until the as-of moves past it.
Why:  The check exists as of 2.10 and runs as a named CI step, so a checkpoint row would now name a checkpoint `PROGRESS.md` records, which `coverage-reported` fails on. The three halves are stated because the first alone is what the corpus had been relying on, and it is the half that proves nothing about a query somebody wrote beside a reader: four such queries were in the shipped source when the check was written.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | 2.10 | Point-in-time test | A deliberately future-dated column causes a loud failure. The test is permanent, not a manual break-and-revert |
Now:  The same row naming the check and its three halves, with the done condition stating where the loud failure happens, the two named exemption lists, and the two-sided read.
Why:  "A loud failure" left unsaid where. Append-only point-in-time storage does not throw on a future-dated row; it declines to see it, silently and correctly. The failure that has to be loud is in CI, and saying so is what stops a later session looking for a runtime exception that was never going to arrive.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Verification ended at the paragraph on a mechanical sweep destroying the meaning it was carrying.
Now:  Three further paragraphs: that an assertion must fail when the thing it guards is removed and the proof of that is permanent, that this is the fourth instance and therefore a rule, and how it is held, with the three shapes a backing can take and the rule that an unbacked scan is reported rather than failing the run.
Why:  The failure table's detector-error claim passed with the catch clause deleted, because the private method issuing the insert was still in the file with nothing calling it. Three earlier instances had the same shape and each was closed one at a time; a fourth is the signal to write the property down and hold it mechanically rather than to close it again.

### 2026-08-26 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | `coverage-reported` | every CI run | Every check the roster says runs is implemented, is invoked by `tools/ci.*`, states its own scope in numbers, and left a coverage record in the run the phase report reads |
Now:  The same row, plus: every file in the suite that reads the shipped source belongs to a check, or is listed by name as a scan whose backing nothing records.
Why:  Each check declares its source-scan assertions in its own coverage record and `CheckCoverage.Report` fails a check that declares neither. A scan written in an ordinary test has no coverage record to declare in, so nothing would say whether a behavioural test backs it and the sweep would report a clean list while missing them. Three such scans exist today, and the check names them rather than counting them.

### 2026-08-26 — BUILD_PLAN.md — cites A calibration run reconstructs against current membership and computes its indicators in memory
Was:  | 2.11 | One-time calibration | Detector run over the full stored history **into `calibration_setup`, never into `setup`** ... Nightly count distribution inspected. If the median falls outside 5 to 60 per side, thresholds adjusted **once**, before phase 3 ... |
Now:  The run covers the golden fixture's seeded histories, replaying the nightly pipeline session by session into a scratch store so each detector reads the rows the stages that own them wrote. The market-cap clause of `tradable-shortable` is exempted by name. The distribution is recorded per side as a raw count and as a rate per name per session, and the band is applied to the scaled rate with the scaling named as an assumption.
Why:  The row rested on the plan's premise that calibration mode computes what it needs in memory. It computes the averages that way and not the rest: the detectors read `indicator_daily` and `scan_hit`, and the live store holds one session of each, so a full-history run was three stages of second implementation whose only consumer is a table nothing downstream reads. Over the fixture the same run needs no path that does not exist. What the narrowing costs is population, so the row says how the band survives thirty names instead of pretending it does.

### 2026-08-26 — BUILD_PLAN.md — cites A calibration run reconstructs against current membership and computes its indicators in memory
Was:  The 2.7 obligation ended: "Three answers are available and none is obviously right ... The choice belongs at 2.11, where the distribution is what a threshold is set against."
Now:  The answer taken, with what was rejected and why, and a new row raised at 2.11 carrying the full-history run over the live universe to 3.2 with its price.
Why:  The choice was taken before 2.11 rather than at it, so the row stating three open answers was no longer true. An obligation that still poses a question somebody has answered reads as open work and gets re-answered.

### 2026-08-26 — BUILD_PLAN.md — cites A calibration run reconstructs against current membership and computes its indicators in memory
Was:  | 2.11 | One-time calibration, over the golden fixture's seeded histories rather than the live universe. The nightly pipeline replayed session by session into a scratch store, so each detector reads the `indicator_daily` and `scan_hit` rows the stages that own them wrote for that session ... |
Now:  The row names the mechanism that works: calibration mode carries the session in memory through `IndicatorEngine.Calculate`, `TierClassifier.Grade`, `ScanMagnitudes` and `ScanEngine.Top`. The fixture run becomes the fixture's expectations and the distribution comes from the live universe, with the done condition stating why thirty names cannot answer for a threshold and adding the clause that holds when the band is out of the five thresholds' reach.
Why:  Replaying the pipeline session by session does not work and could not have: `IndicatorEngine`, `ScanEngine` and `TierClassifier` all read the night's universe snapshot, and a night the lab was not running has none. Giving those three a current-membership mode would write reconstructed `indicator_daily` rows, which the calibration decision forbids. The in-memory session turned out to be assembly rather than a second implementation, because all four pieces of arithmetic were made public at 2.6 for exactly this. And the fixture cannot answer for a threshold at all: thirty names against a scan breadth of fifty puts every name inside every scan on every session.

### 2026-08-26 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  | 2.11 | **The calibration run over the full stored history, on the live universe.** ... So the full-history run has a price ... a calibration mode that carries indicators, ladder grades and scan hits in memory, which is a second implementation of three stages ... | 3.2 |
Now:  The row says the path exists as of 2.11 and what is left is population and running time, with the assumption the scaling rests on named as the thing a live run would replace.
Why:  The price was wrong by an order of magnitude and the run it was deferring happened at 2.11 instead. An obligation that prices work already done reads as work outstanding.

### 2026-08-26 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  | 2.11 | **The calibration run over the live universe, which 2.11 ran over thirty names.** The path exists as of 2.11 and is the same mode: what is left is population and running time ... | 3.2 |
Now:  removed, and a row raised at 2.11 takes its place: the threshold adjustment the distribution calls for, which the five thresholds cannot deliver, due at 3.1.
Why:  2.11 ran over the live universe rather than over thirty names, so the obligation was discharged on the day it was written. What the run found instead is that reaching the band needs two checks removed rather than five thresholds moved, and that is a decision rather than a build step.

### 2026-08-26 — BUILD_PLAN.md — cites A calibration run reconstructs against current membership and computes its indicators in memory
Was:  | 2.7 | **What market capitalisation a calibration run is entitled to read.** ... What is left is building it, and recording on every calibration verdict that the short distribution was measured against a nine-clause detector | 2.11 |
Now:  removed.
Why:  Built at 2.11 and asserted: `CalibrationFigures` reports the clause exempt, `ShortPullbackRules` runs the other three, and a test reads every short row a calibration run wrote and finds the note on each. An obligation whose work has shipped is a row that reads as outstanding.

### 2026-08-26 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  The 1.6 obligation fell due at 2.11, because "2.11 is the first thing that reads these figures, since calibration is where a threshold is set against them".
Now:  The same row, plus the move to 2.12 and its reason, and a due checkpoint of 2.12.
Why:  The calibration ran and set no threshold, because the distribution showed the band is out of the five thresholds' reach. Nothing yet rests on the three confirmed figures, and 2.12 is where a person is already reading a screen. Left at 2.11 the row would name a checkpoint `PROGRESS.md` records, which is a checkpoint that shipped without coming back to it.

### 2026-08-26 — ARCHITECTURE.html — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  "The one-time calibration, and why it is not tuning" ended at the paragraph on the adjustment being calibration against a population count rather than tuning against results.
Now:  A further paragraph: the count has to come from a population the scans can discriminate within, which the golden fixture is not, with the arithmetic of why thirty names against a breadth of fifty makes every geometry check fail.
Why:  Nothing in the document said where the distribution comes from, and the obvious reading is the fixture, because that is where every other figure in this corpus is diffed. Over the fixture the count is nought by construction and a session reading it would take that for a fact about the gates.

### 2026-08-26 — SCHEMA.md — cites A calibration run reconstructs against current membership and computes its indicators in memory
Was:  The `calibration_setup` section named one reconstruction: membership as it stands today.
Now:  Three, each recorded rather than assumed: membership, the exempt market-cap clause, and the bar series read as the store knows it now rather than as it stood on the night.
Why:  The third is not a choice between two readings. A backfill takes a name's whole history in one evening, so every historical bar was observed later than its own session, and a read bounded on the session's own instant returns nothing at all. Both narrower bounds were tried on the way to this and both reported a run of nought sessions over a store of one and a half million bars.

### 2026-08-26 — SCHEMA.md — cites The agreement a person records is written through the read surface, and it is the only write it makes
Was:  Update LabSetups (`agreement`, `agreement_note`)
      And: **The one exception is the agreement a person records, and its scope is the whole guarantee.** The read surface opens a writing connection for `setup.agreement` and `setup.agreement_note` and for nothing else, ever ... The writer is declared above by the type that issues the statement rather than by the screen that asks for it, so `writer-ownership` holds the scope rather than the prose.
Now:  The declaration names the reason beside the columns, and the paragraph states the property first: a person's judgement is captured on the page that asks for it, and the Worker never writes those two columns because it has no judgement to record. The mechanical half is separated into its own paragraph.
Why:  "The read surface writes these two columns and nothing else, ever" is right about the columns and reads as a general licence for the Api to write where writing is convenient. The property says why those two and not others, which is the half a later session citing this needs, and it is the half a writer declaration naming only the writer cannot carry.

### 2026-08-26 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  The 2.11 threshold obligation ended: "The second has a specific candidate, which is that the geometry measures the thrust from the session before the scan hit, the right origin for a one-day scan and the wrong one for a twenty-session scan. Due before 3.1 ..."
Now:  The same row, plus the candidate now being a row of its own with its prediction, and the ordering: the two are worked in order rather than together, because a pass that moves the geometry and the thresholds at once can say nothing about either.
Why:  The candidate was a sentence inside the obligation it might dissolve, which reads as an aside rather than as work. Separated, one row carries a once-only adjustment and the other carries a falsifiable correction, and the ordering between them is the thing that makes either result mean anything. The adjustment cannot be spent twice, so a pass that moved both would leave no second attempt to attribute the outcome to.

### 2026-08-26 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  | 2.12 | Phase sign-off | Fresh session. The gallery review is part of it, and `tools/verify-phase` green with nothing unexamined |
Now:  The same row, plus a reading pass over what the fixture's composition cannot be evidence about, with `thrust` as the worked example and the question of which other checks stand where it does.
Why:  Twice now the fixture's composition has bounded what a check could show, and both times it was found by an accident in a different checkpoint: eight of ten gates one-sided, measured at 2.6 only because that checkpoint added per-check pass and fail counts, and `thrust` unreachable over thirty names, found at 2.11 by a run over a different population. Nothing looks for the shape, so the count of checks in that position is unknown rather than nought, and a sign-off is where a reading pass belongs.

### 2026-08-26 — CLAUDE.md — cites Long and short are never pooled into one figure
Was:  "Verification" ended with the rule that each check names, per source-scan assertion, whether a behavioural test backs it.
Now:  Two further paragraphs: a fifth defect shape that is not an absent subject, and the rule it earns, being that a figure states the population it was computed over and a one-side figure says which side.
Why:  The four instances the section already records are one shape, where the subject an assertion guarded went away and the assertion kept saying what it always said. Every guard the corpus has is pointed at that shape. The 2.12 sign-off found three instances of a different one in a single pass, and in all three the counts were correct, the check was live and the subject was present: an `AUTHORED` setup row counted into the captured population of the fixture's one-sidedness figures, a median taken over dips of two bars or more under a phrase naming the gate's own two to seven, and a long-side rate offered for a reading asserted of both sides. Nothing in the corpus guards which rows a stated figure was computed over, and the stopping rule at the sign-off said a fifth shape earns a rule rather than a fourth pass.

### 2026-08-26 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  The 2.11 threshold obligation gave "a median give-back of 1.088 long and 1.006 short among moves of the right length", and "With the two shapes set to pass always the remaining conjunction yields about 6 a night, the bottom of the band, so the band is reachable only by removing two checks."
Now:  1.060 long over 5,849 dips of 2 to 7 bars beside the unchanged 1.006 short over 3,448 bounces of the same length, each naming its population, and a recount rather than a product: a median of 7 rows a night long and nought short.
Why:  Reproduced against `calibration_setup` at the 2.12 sign-off. The long median was taken over dips of two bars or more with no upper bound, which is a different population from the one the phrase beside it names and from the one the short figure used, and the two sides were therefore not comparable. "About 6" is the long side's pass rates multiplied out under an independence assumption, and the row asserts the shape of the failure holds on both sides; recounted per side the short figure is nought, which is not about 6. Both corrections make the obligation's own reading stronger rather than weaker, and 3.1 judges a prediction against these numbers, so the numbers have to be the ones the prediction is about.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The obligation for the `CONFIRMED` indicator values fell due at 2.12, and so did the one for the `CONFIRMED` gallery expectations.
Now:  Both fall due at the operator, and both say at the sign-off that moved them why the due point is no longer a checkpoint.
Why:  Neither can be discharged by any session. The indicator obligation has now been moved three times, 1.12 to 2.11 to 2.12, each time for a sound local reason, and a due point that moves at every sign-off is permanent while reading as pending, which is the fault the checkpoint-naming rule exists to prevent one level up. The gallery review is the same: a sign-off session cannot page through a gallery either, so leaving it at 2.12 would have moved it to 3.7. Two rows already carry a due point that is an act rather than a checkpoint, step 6 of the move and the vendor's reset boundary, and these are the same shape.

### 2026-08-26 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  The carried obligations table had no row for `PullbackGeometry.Of`.
Now:  A row raised at 2.12 and due at 3.1, ahead of the thrust-window correction rather than after it: `DERIVED` expectations over the fixture's own bars for the origin, the extreme, the two shape quantities and the two raw prices, restated by `tools/derive-indicators.py` rather than by the method itself.
Why:  The gate table produced at the 2.12 sign-off named the gap and 3.1 walks into it. The authored gate cases hand-build the pullback record and never call the method, and over the captured fixture the method returns the degenerate shape on every row, so the quantity the correction moves is exercised nowhere on a real input. The original defect produced plausible numbers for 631 sessions with nothing noticing, and that is a property of the method rather than of that defect: every figure it returns is a small plausible number whichever way it was computed. Without the instrument the 3.1 prediction is judged by a median moving in the expected direction, which a wrong-but-plausible correction would also produce.

### 2026-08-26 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  One row raised at 2.12 carrying two verification-quality items, the `check-completeness` crash and the gate-id count in a source comment, described as neither letting anything through.
Now:  Two rows, the first raised from an observation to a finding on the distinction between a crash and a named failure.
Why:  The two are not the same size and bundling them priced the first as an aside. `check-completeness` does fail when a gate's implementation is removed, so the property holds; it fails on an unhandled `Single` where it has a reconciliation message written for that exact case, and the message never runs because the replay dies before the comparison. A crash and a named failure are different artefacts: one tells a later session which gate went missing, the other tells it the check threw. That is worth a row of its own in the corpus whose four recorded defects are all about what an assertion says when its subject is gone.

### 2026-08-27 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "Definition of done for a checkpoint" ended: Done conditions are written against **what the file will say after the edit**, not as statements of intent. A done condition narrower than its clause is the most common defect in this corpus.
Now:  The same, plus the rule that a checkpoint amending its own done condition says so in its PROGRESS entry in those words.
Why:  2.11 added the clause that let it decline the once-only threshold adjustment, in the same commit as the measurement that would otherwise have failed the condition as written. The 2.12 sign-off ruled the clause stands, on three checkable grounds, so amending is not the defect. The defect is that nothing outside that session's own prose marked the amendment, and a later reader sees a done condition and a run that met it. The item reached the sign-off only because it had been written into the phase's gitignored build prompt and someone read that file the next day, which is not a mechanism. Naming the amendment costs a line and gives the sign-off something to rule on.

### 2026-08-27 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The carried obligations table had no row for reconciling PROGRESS's `Carried` blocks against the table itself.
Now:  A row raised at 2.12 and due at 3.1, carrying the narrow form: reconcile the set of due points rather than the sentences, failing only on a `Carried` block naming a checkpoint no row does.
Why:  Three obligations have now been stated in a `Carried` block and never in the table, so nothing read them: the 1.3 screening question, the 1.1 vendor reset boundary, and the question of this reconciliation itself, which lived only in the build prompt. The objection that kept it open is sound, being that prose-to-prose matching false-alarms and a suppressed guard is a dead one, and the narrow form escapes it because a due point is structured on one side and only has to be present on the other.

### 2026-08-27 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "Verification" ended with the fifth defect shape and the rule that a figure states the population it was computed over.
Now:  The same, plus a sixth shape: an instrument that is correct and a reader that discards its answer, and the rule that a claim something is visible is a claim about a surface and is checked against that surface.
Why:  The first four shapes were a check asserting less than its label and the fifth was a figure over the wrong population. This one is neither. `reached-ceiling` recorded that it ran two of its three clauses, `check-completeness` confirmed the result was present, and ARCHITECTURE said the narrowing is stated outright rather than left to be inferred from a passing verdict. Every one of those was true of the store. The gallery dropped the note whenever a value sat beside it, so the sentence was false of the screen, which is the only place it was ever about. Nothing upstream was wrong, so nothing upstream could have caught it, and the sweep the rule schedules is the only reason anyone would look.

### 2026-08-27 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The carried obligations table had no row for claims of visibility being checked against the surface that carries them.
Now:  A row raised at 2.9 and due at 3.1, naming three to start: the `reached-ceiling` narrowing, the calibration market-cap exemption, and the borrow assumption.
Why:  Every check in this corpus asserts over source, over the store, or over a document, and none reads a rendered page, so a claim about what a person can see is currently verified against what a machine can read. The two found by the gallery review are fixed and held by nothing but the fix having happened. The borrow assumption is the same shape unlooked at: the claim is that a 1.0% annualised cost and the availability caveat are recorded as unmodelled assumptions on every short trade, and no trade row exists until 4.7 nor a journal until 4.11, so it rests on a surface nobody has built. Kept narrow on purpose, because the general form is UI testing and the useful form is one question about one sentence.

### 2026-08-27 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Phase 3's table began at 3.1, SetupJournal finalised.
Now:  The same table with a 3.0 row before it, seven lettered parts in a stated order: the geometry instrument, the thrust scan on the setup row, the correction alone, the surfaces sweep, the remaining hygiene obligations, the spec pass, and a check result carrying a value per clause.
Why:  Twelve obligations fell due at 3.1 and 3.1's deliverable is SetupJournal, so every one of them named a checkpoint whose deliverable was not its work. That is the shape this corpus has already lost three obligations to. A row of their own gives them a due point that describes them, and inserting it ahead of 3.1 rather than renumbering keeps 3.6 the decision point and 3.7 the sign-off, which 57 out-of-scope claims and every coverage deferral in the suite name by number. Modelled on 2.1, which is the precedent for a lettered multi-part checkpoint whose first part authors what the rest is written against.

### 2026-08-27 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Twelve rows of the carried obligations table named 3.1 as their due point.
Now:  The same twelve rows, unchanged in every other respect, due at 3.0.
Why:  Repointed in the same commit that creates 3.0, because a row due at a checkpoint that does not yet exist fails the deferral guard exactly as surely as one due at a checkpoint that has landed, and the two edits are one change. The sweep run before the commit found the cost of the alternative: nothing outside BUILD_PLAN, PROGRESS and CHANGELOG references a phase 3 checkpoint by number except four places in the suite, and none of them names 3.6 or 3.7, so insertion is cheap and renumbering would not have been.

### 2026-08-27 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  `setup`'s column list ended at `agreement_note`, and `calibration_setup` was declared as having the same shape.
Now:  Both carry `thrust_scan` and `thrust_session`, nullable, with a paragraph saying why the signal of the same name does not cover it.
Why:  Four gates read a quantity computed from where the thrust is measured from, and `gainer` and `gapper` flag a one-session move where `leader` and `laggard` flag a twenty-session one. A row that does not say which scan flagged it cannot be told from a row measured over a different span, which is the whole of the 3.0(c) correction. `thrust_scan` already exists as a signal and cannot serve: `setup_signal` has a foreign key to `setup`, calibration writes to `calibration_setup`, so the 49,450-row population a threshold is counted over is exactly the population the signal cannot reach.

### 2026-08-27 — ARCHITECTURE.html — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Ten claims of visibility written in the present tense about surfaces that do not exist: the borrow assumption "recorded as an unmodelled assumption on every short trade" in two places, the watchlist's give-up units and short columns, the trade journal's two sections, the research ledger's separate scores, a refuted variant staying visible, the difference series and the holdout register, and the scoreboard's four bands.
Now:  Each names the checkpoint that builds its surface: 4.1 for the watchlist, 4.7 for the trade row, 4.11 for the journal, 5.5 for the ledger, 3.5 for scoreboard bands 0 to 2 and 6.8 for band 3.
Why:  A claim that something is visible is a claim about a surface, and this corpus verifies those claims against the store. Written in the present tense, "recorded on every short trade" reads as a property the lab has when no trade row exists until 4.7 and no journal until 4.11, which is the same shape as the gallery defect one step earlier: nothing upstream is wrong, so nothing upstream can catch it. Naming the checkpoint puts these on the same footing as an out-of-scope claim, which has always had to say what would end it.

### 2026-08-27 — DECISIONS.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  `The short borrow problem is mitigated by a filter, not solved` ended "Recorded as an unmodelled assumption on every short trade", and `Equity is a fixed $100,000 notional that never compounds` said the realised risk is recorded beside the intended risk so the gap is visible.
Now:  Both say what is owed and from which checkpoint, 4.7 for the row and 4.11 for the surface that shows it.
Why:  Same reason as the ARCHITECTURE edit above. A decision stating a property in the present tense is the strongest form of the claim in the corpus, and these two rested on rows nobody has built.

### 2026-08-27 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The carried obligations table had no row for an instrument that reads a rendered surface.
Now:  A row raised at 3.0 and due at 3.7, scoped to the sentences the 3.0(d) sweep produced rather than to UI testing in general.
Why:  The sweep named the surface behind every claim of visibility and said whether it holds today. It could not assert one, because no check in this corpus reads a rendered page. The eight claims found true today are true because a person looked, and because two of them happen to be covered by tests written for another reason. Due at 3.7 rather than inside 3.0, because building the instrument in the pass that found the gap is how a sweep turns into a checkpoint, and the pages phase 3 adds are the ones it would first cover.

### 2026-08-27 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The checks roster had twenty-one rows and no row for reconciling `Carried` blocks against the obligations table.
Now:  Twenty-two rows, with `carried-obligations` running on every CI run.
Why:  The narrow form the 2.12 obligation argued for. It compares the set of due points rather than the sentences, because prose against prose false-alarms on every rewording and a suppressed guard is a dead one. Four obligations have now been written into a `Carried` block and never into the table.

### 2026-08-27 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  `universe_snapshot` carried `as_of` and `ticker` and nothing else.
Now:  Two more columns, `screened_over_sessions` and `screen_carried`, with a paragraph on why a night that cannot screen carries rather than skips.
Why:  The question was raised at 1.3, recorded only in that entry's `Carried` block, and read by nothing until 3.0. The count distribution answers it: a night flags a median of 44 long names and 13 short out of 2,016 while membership drifts by a handful a month, so carrying misstates a night by far less than skipping removes from it. What it may not do is look like a screened night, which is why this is a migration rather than a document edit.

### 2026-08-27 — ARCHITECTURE.html — cites Components are named, not coded
Was:  Figure 8 labelled the "Signal request" box `ForwardReturnFiller`.
Now:  `ProposalRegistry`.
Why:  ForwardReturnFiller fills forward returns and has nothing to do with signal requests. A proposal asking for a new signal is ProposalRegistry's, which is what the two-kinds-of-proposal decision says. Found while reading for the surfaces sweep rather than by any check, because the conformance check reads the catalogue table and not the figures.

### 2026-08-27 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  `control_setup` and `forward_return` declared no primary key, `forward_return.subject_id` was documented as "a `setup_id` or a control row" with no column on `control_setup` for a control row to be, and `ceiling_bound` and `scoreboard` were declared at store level only, under "Research — phases 5 and 6".
Now:  Both phase-3 tables carry a key, `control_setup` gains `control_id` for `subject_id` to point at and a `rank`, `forward_return` gains `filled_at`, and `ceiling_bound` and `scoreboard` are declared in full in the section their writers land in.
Why:  Every sibling table declares a key and these two did not, so the grain was implied rather than enforced. The subject of an outcome row had nothing to name a control by, and the alternative to a surrogate was a three-column subject on every row. And the file's own preamble claims completeness for phases 1 to 3 while two phase-3 stores sat in the phases 5 and 6 table, so `writer-ownership` would have resolved them at 3.4 and 3.5 with no columns to check.

### 2026-08-27 — ARCHITECTURE.html — cites Components are named, not coded
Was:  The catalogue said SetupJournal "Makes the setup row immutable once written".
Now:  It says what the stage does: seals the night, every row complete, its evidence frozen, no column written that belongs to a later stage or to a person, and writes nothing.
Why:  The old line described an outcome nothing could perform. A component cannot make a row immutable; it can check the invariants that would be false if something had written where it should not, and it can do that without owning a table. The distinction matters because the first reading invites a component that enforces immutability by writing, which would be the second writer of the thing it protects.

### 2026-08-27 — RUNBOOK.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The 18:25 row read "`vectorize`, the signal freeze, then journal".
Now:  "`vectorize`, the signal freeze, then `journal`, which seals the night".
Why:  The nightly order test reads backticked verbs out of that table and asserts the replay runs them in the same order. Written as prose, the journal was a stage the schedule mentioned and the test could not see.

### 2026-08-27 — RUNBOOK.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The 21:30 row read "forward returns".
Now:  "`forward-returns`, every flagged setup at 1, 3, 5 and 10 sessions".
Why:  The nightly order test reads backticked verbs out of that table. Written as prose the stage was scheduled and invisible to the assertion that the replay runs the night in the order an operator follows.

### 2026-08-27 — ARCHITECTURE.html — cites Components are named, not coded
Was:  The catalogue said ControlSampler "Draws matched control stocks nightly, loose and tight".
Now:  The same, plus five per set by deterministic nearest neighbour, and before the cap so they answer for the flagged population rather than the kept sixty.
Why:  The count, the method and the position in the night are all things a later session would otherwise have to read the code to learn, and all three change what a comparison means. The ordering matters most: drawing after the cap would compare the kept setups against controls for a different question.

### 2026-08-27 — RUNBOOK.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The 18:26 row read "control sampling".
Now:  "`controls`, loose and tight per flagged setup, before the cap".
Why:  The nightly order test reads backticked verbs, and the position relative to the cap is the half of the schedule that carries meaning rather than convenience.

### 2026-08-27 — ARCHITECTURE.html — cites Components are named, not coded
Was:  The catalogue said CeilingCalculator "Computes the win-rate ceiling from the outcome distribution".
Now:  The same, plus per direction and from the path rather than the terminal return, so a setup that ended ahead having first been stopped out does not count toward a bound no rule could reach.
Why:  "From the outcome distribution" admits the reading that produced the first draft of this stage, in which the bound and the achieved rate were the same expression and the gap was nought by construction. The path is what makes the two differ, and per direction is what stops a pooled bound inheriting the short side borrow assumption.

### 2026-08-27 — RUNBOOK.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "Every week" said only to open the scoreboard.
Now:  It runs `ceiling` first, with why it is weekly rather than nightly.
Why:  The stage had no scheduled home. Weekly is deliberate: the bound moves with the population rather than with a session, and recomputing it nightly over one more row than yesterday invites reading noise as movement.

### 2026-08-27 — ARCHITECTURE.html — cites Components are named, not coded
Was:  The catalogue said ScoreboardBuilder "Computes the scoreboard panels".
Now:  The same, plus that it stores what each panel showed so it can be read back as it stood, and that every panel carries its row count and, where it has an interval, the effective observations that interval was really built on.
Why:  The two counts are different quantities and storing only the first is how a minimum sample stated in observations gets satisfied by rows. Saying so in the catalogue is what makes the column list in SCHEMA read as deliberate rather than as belt and braces.

### 2026-08-27 — RUNBOOK.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The 21:50 row read "scoreboard".
Now:  "`scoreboard`, the three bands, every panel with its own count".
Why:  The nightly order test reads backticked verbs, and the count is the half of the panel that stops a figure being read without its denominator.

### 2026-08-27 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "CI green before merge. That is the only condition. Sign-off is a separate activity with its own record and does not gate the merge."
Now:  Two conditions: CI green, and the whole phase signed off before its branch merges. With what the second buys, what it costs, and the note that a checkpoint still lands as its own commit.
Why:  Ruled by the operator at phase 3, against a standing draft PR carrying 3.0 to 3.5. The old rule was written to stop a sign-off holding finished work hostage; the cost it did not price is that a phase merged in pieces has no commit where the phase is what it says it is, and the sign-off then reviews code already on the default branch, where declining it costs a revert rather than a conversation. The price of the new rule is a branch open for as long as the phase waits on something that is not code, which for phase 3 is a quarter of accumulation with the nightly job running off that checkout. Priced here rather than discovered later.

### 2026-08-27 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The checks roster had twenty-two rows and no row for asserting a claim of visibility against the page that carries it.
Now:  Twenty-three rows, with `surface-claims` running on every CI run.
Why:  It is the only check in this corpus that reads a rendered surface. Every other one asserts over source, the store, or a document, which is exactly why the gallery defect survived every check the suite had: the note was in the store, check-completeness confirmed it, and the screen dropped it.
