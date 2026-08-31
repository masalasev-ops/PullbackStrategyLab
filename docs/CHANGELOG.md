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

### 2026-08-27 — CLAUDE.md — cites Decisions are named, not numbered
Was:  "Conventions" said nothing about commit subjects. The form `Phase {phase} / {checkpoint} — {what changed}` existed only in sixty commits of history.
Now:  The form is stated, with the rule that the checkpoint is never omitted even on a commit that builds nothing, and the note that this was broken once by a session inferring it from the log.
Why:  A convention that exists only in what previous sessions happened to do is a convention the next session will break, and this one was: a ruling commit dropped the checkpoint field on the reasoning that a ruling is not a checkpoint. `Phase 2 / 2.12 — the ruling the sign-off owed` is the counter-example that was already in the log. Not made a check, because the failure is loud and costs one amend; if it breaks twice more that reasoning is wrong and the check is owed, which is said in the text so a later session can hold it to that.

### 2026-08-27 — SCHEMA.md — cites Data ownership is declared once, in SCHEMA.md
Was:  `scoreboard` ended at `n_effective`.
Now:  It carries `population`, with a paragraph on why two panels on one page do not share one.
Why:  Band 1 is over every flagged setup and band 2 rank-decile curve is over the capped candidates, and at the calibrated thresholds those differ by three orders of magnitude. A stored panel that could not say which rows it used is a figure a later reader compares against the wrong one, which is the fifth defect shape.

### 2026-08-27 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The pre-3.6 sequence had six steps, the first being to settle what band 1 measures and the second to rule on the thresholds before accumulating.
Now:  Four steps, neither of those among them, with a paragraph saying why the threshold ruling is not on the path.
Why:  ARCHITECTURE settles what band 1 measures: its worked night is twenty-two flagged, fourteen passing every check, all twenty-two followed up. The evidence population is the flagged one, so band 1 fills whatever the thresholds do, and the ruling can be taken against three months of real outcomes instead of gating them.

### 2026-08-27 — ARCHITECTURE.html — cites The minimum sample is derived from a measured dispersion and counted in effective observations
Was:  Three statements of the selection-variant minimum sample. The KPI block read `Setup observations for the same question ~160` beside `Which takes about 13 days`; the pre-registration rule read "Selection variants settle at **160 paired setup observations**, which detects about a two-point difference in ten-day forward return"; the authored-parameters row read `Selection variant sample | 160 paired setup observations | Per proposal | Detects about a two-point difference in ten-day forward return`.
Now:  196 in all three, said as effective paired setup observations, with the power named and the reason effective is not rows. The KPI's "13 days" is replaced by "until band 1 says so".
Why:  160 read as a derived quantity and was not one. The dispersion the calculation turns on had never been measured, and the power it was sized for had never been stated. Measured over the fixture's own bars the paired dispersion is 0.099811, and at two points, 95% and 80% power the arithmetic gives 196. The old figure is the same arithmetic at about 72% power. The "13 days" was a rows-based calendar claim resting on about twelve setups a night; band 1 as built fills at about eighty-two, and how long it takes is now a thing the scoreboard reports rather than a thing the document predicts.

### 2026-08-27 — BUILD_PLAN.md — cites The minimum sample is derived from a measured dispersion and counted in effective observations
Was:  3.6's done condition read "Three months of accumulation, then read the scoreboard and decide whether to continue". Step 2 of the pre-3.6 sequence read "**Wait.** Three months, against the branch the phase lives on". The elapsed-time section read "phase 3 needs about three months of running before phase 5 has anything to score".
Now:  3.6 fires when band 1's effective sample reaches the pre-registered minimum on the tight control set, both directions counted separately, with the effective count shown beside the raw count and the minimum on every band 1 panel every night. Step 2 is "Accumulate", with a paragraph on why the calendar went. The elapsed-time section keeps its estimates and says outright that none of them is a trigger.
Why:  The three months was an estimate written before anything was measured and read as a derived quantity ever since. It rested on about twelve setups a night; band 1 as built fills at about eighty-two, and it is a paired comparison against same-night matched controls, so the market factor cancels rather than needing to be waited out. What remains to discount is the ten-day label overlap across nights, and how much that costs is a property of the realised series that no estimate could have known. A trigger a reader cannot see from the first night is a date in disguise, so the three numbers are a done condition on the panel rather than a figure in the store.

### 2026-08-27 — SCHEMA.md — cites The minimum sample is derived from a measured dispersion and counted in effective observations
Was:  `scoreboard` ended at `population`, and the note under it said the effective count falls below the row count because ten-day labels overlap and same-night setups share a market factor.
Now:  It carries `n_minimum`, and the notes say that `n_effective` starts from rows rather than nights because the paired difference removes the market factor by construction, with both discounts measured from the series.
Why:  Counting a night as one observation threw away exactly what the control draw was built to buy. The market factor is what makes forty unpaired names worth about one observation, and pairing removes it; what is left inside a night is each name's own move against its own controls. The discounts are now the label overlap and whatever clustering the matching failed to remove, both measured, with a night that cannot say how its own pairs dispersed counting as one so the pessimistic reading is the limiting case rather than the assumption.

### 2026-08-27 — ARCHITECTURE.html — cites The minimum sample is 262 effective observations, ratified at two points and 90% power
Was:  196 in all three places, at 80% power. The KPI read `Effective setup observations for the same question 196`; the pre-registration rule read "Selection variants settle at **196 effective paired setup observations** ... at 80% power against the measured dispersion of that return"; the authored-parameters row read `196 effective paired setup observations | Per proposal | Detects about a two-point difference in ten-day forward return, at 80% power against a dispersion measured rather than assumed`.
Now:  262 in all three, at 90% power.
Why:  Both judgement inputs were ratified by the operator on 2026-08-27. The dispersion and the arithmetic are unchanged; only the power moved, from a conventional 80% nobody had chosen to a 90% chosen for a stated reason. A false positive on band 1 is caught downstream by the forward paired test and the variant machinery; a false negative is caught by nothing, because band 1 reading flat means the project stops. At about eleven effective observations a night the extra power costs about six sessions.

### 2026-08-27 — BUILD_PLAN.md — cites The minimum sample is 262 effective observations, ratified at two points and 90% power
Was:  A carried-obligation row raised at 3.0, due at the operator, holding the ratification of the two judgement inputs behind the minimum sample.
Now:  Removed.
Why:  Ratified: two points at 90% power, giving 262. Both judgements are recorded with their reasoning in the decision, and the sensitivity table is asserted by a test so the choice stays visible as a choice rather than settling into a default.

### 2026-08-27 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  3.6 read "**The decision point** | Band 1's effective sample reaches the pre-registered minimum on the tight control set, both directions counted separately, then read the scoreboard and decide whether to continue", and sat in the phase table ahead of 3.7 as though the sign-off waited on it. The block below it was headed "What has to happen before 3.6, in order, because none of it is code", listed four steps ending "**3.7**, a fresh session", and said "Steps 1 and 3 are the operator's, and step 4 needs only a session that has not committed code".
Now:  3.6 is "**The decision point. Parked, and not a sign-off condition**", with both band 1 conditions named as its trigger and "when the panel reports both" as its due point. 3.7 says it signs off 3.0 through 3.5 and does not wait on 3.6. The block below states what parking does not change, and its step list no longer ends in the sign-off.
Why:  The two ask different questions and only one is answerable today. 3.7 signs off that 3.0 through 3.5 hold and meet their done conditions, which is a claim about code, documents and a fixture and is exactly what the phase report was built to answer. 3.6 decides whether the pattern works, which needs a sample that does not exist. Phase 1 is the precedent: it signed off with the `CONFIRMED` values outstanding, on the reasoning that an obligation whose due point moves at every sign-off is permanent while reading as pending. The same reasoning applies here from the other side, because a checkpoint nobody can reach for months left as a sign-off condition makes the sign-off permanently pending while every part of it that could be checked already passes.

### 2026-08-27 — BUILD_PLAN.md — cites The minimum sample is 262 effective observations, ratified at two points and 90% power
Was:  The carried-obligations table had eight rows, and scheduling the nightly job appeared only as step 1 of the pre-3.6 prose.
Now:  Ten rows. Scheduling the nightly job is a row due at the operator, and so is whether phase 3's accumulation runs from the branch or from `main`.
Why:  The corpus's own rule is that an obligation goes into this table in the commit that raises it, with a due point, or it does not exist. Scheduling is the item with the longest lead time in the project and it had never been a row, which is the shape that has already lost this corpus three obligations. The checkout question is new: parking 3.6 past 3.7 means the branch may merge before accumulation starts, so which checkout the nightly job runs from stopped being implied by the ordering.

### 2026-08-27 — RUNBOOK.md — cites The evidence store holds only setups flagged forward, never setups reconstructed from history
Was:  The nightly table began "| 17:20 | `actions`, splits bulk" and carried no `universe-build` row and no nightly snapshot row. Its total read "**~798 against a 5,000 ceiling**". There was no section describing an installed schedule.
Now:  A `universe-build` row at 17:15 and a `snapshot-db` row at 22:00, a total of ~803, a paragraph on why the universe row is the one that cannot be recovered, and a section describing the seventeen registered tasks and the logged-on limitation.
Why:  `UniverseBuilder` says the snapshot "is written every night without exception" and "cannot be reconstructed later", and `UniverseSnapshotReader.Members` matches the snapshot date exactly with no fallback, deliberately, so that a missing snapshot cannot be silently papered over with current membership. The stage was absent from the nightly table, so an operator following the RUNBOOK literally would have built a lab that flags nothing and reports every night clean. Found while scheduling the job.

### 2026-08-27 — ARCHITECTURE.html — cites Matched control populations are drawn nightly, loose and tight
Was:  <p>Each night, for every flagged setup, the sampler draws control stocks that were <b>not</b> flagged and records their forward returns identically. Two sets, both free because they come from daily bars already stored:</p>
Now:  The same sentence with the control-recording half moved into the Failure behaviour table, as a row conditioned on "A comparison has no control outcomes", and the prose repointed at it.
Why:  This was the only sentence in the corpus claiming control forward returns are recorded, and it sat in prose. `architecture-conformance` enumerates claims from tables, so it never saw it, never gave it a verdict, and reported zero unexamined while the claim was false: ForwardReturnFiller bound its subject kind to the literal `setup` and no control outcome was ever written. A claim only in prose is a claim nothing can fail on.

### 2026-08-27 — ARCHITECTURE.html — cites Matched control populations are drawn nightly, loose and tight
Was:  A Failure behaviour table with fifteen rows and no row for a comparison with no controls.
Now:  A sixteenth row, `A comparison has no control outcomes`, stating that ForwardReturnFiller records an outcome for every control drawn as well as every setup, over the control's own bars, from the flagging setup's session, signed by that setup's direction and expressed in the control's own range, and that a side with none is withheld naming a shortage of control outcomes rather than of time.
Why:  The claim needs a table to live in so it carries a verdict. It is asserted at 3.2 rather than deferred, because ForwardReturnFiller exists and a claim deferred to a landed checkpoint is a claim that checkpoint shipped without coming back to.

### 2026-08-27 — SCHEMA.md — cites The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
Was:  ...the ratio is a property of the realised series rather than of the design (see: The interval is a block bootstrap over paired differences, and the effective sample is measured).
Now:  The same sentence citing **The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured**.
Why:  The decision it cited was superseded. The effective-sample half it is about did not change; the citation is repointed so it does not resolve to an entry under "Previously decided".

### 2026-08-27 — ARCHITECTURE.html — cites Matched control populations are drawn nightly, loose and tight
Was:  <tr><td><b>ForwardReturnFiller</b></td><td>Nightly 21:30</td><td>Fills 1, 3, 5 and 10 day outcomes on every setup, traded or not</td></tr>
Now:  The same row ending "traded or not, and on every control drawn against one".
Why:  The catalogue's description was true of what the stage did and not of what it was for. Nothing asserts this cell, because the catalogue producer reads only the component name, so it is corrected for the reader rather than for a check.

### 2026-08-27 — BUILD_PLAN.md — cites Matched control populations are drawn nightly, loose and tight
Was:  A phase 3 section whose 3.5 row and following notes said nothing about the checkpoint having been reopened, and a carried-obligations table of ten rows.
Now:  A paragraph under the phase 3 table naming the three defects that reopened 3.5 on 2026-08-27, and twelve further obligation rows.
Why:  A checkpoint reopened after its PROGRESS entry is recorded is invisible in the plan otherwise, and the corpus's pointer to which checkpoint the build is on reads the plan and the record together. Eleven of the twelve rows are the sign-off review's non-blocking findings plus four raised while fixing the blocking ones; the twelfth is the nightly runner having only a Windows half.

### 2026-08-28 — SCHEMA.md — cites A setup row is corrected only where the correction uses no information the night did not have
Was:  Grain: date + ticker + direction. **Immutable after write.** The spine of the whole system.
Now:  Grain: date + ticker + direction. **Immutable after write, except by a correction that uses no information the night did not have.** The spine of the whole system (see: A setup row is corrected only where the correction uses no information the night did not have).
Why:  The rule as written forbade repairing a value that is wrong because an input stage died, which is not the thing immutability protects against. The writer line and two columns are added in the same edit, because the permission is worthless without the mark that makes a corrected row distinguishable.

### 2026-08-28 — BUILD_PLAN.md — cites A setup row is corrected only where the correction uses no information the night did not have
Was:  | 3.1 | SetupJournal finalised | Setup rows immutable after write. Asserted |
Now:  | 3.1 | SetupJournal finalised | Setup rows immutable after write, except by a correction meeting both of the correction rule's conditions, and asserted both ways: nothing rewrites a plan or a gating verdict, and a correction whose input is stamped after the setup's own date fails (see: A setup row is corrected only where the correction uses no information the night did not have) |
Why:  The done condition was the widest statement of the rule in the corpus and the only one a checkpoint was judged against. Amended rather than left standing beside a decision that contradicts it, and the amendment names the second condition, since a permission asserted in one direction only is the half that gets cited.

### 2026-08-28 — BUILD_PLAN.md — cites A reader's signature does not establish point-in-time; the query does
Was:  | 3.7 | **`PointInTimeCheck.Stamped` names seven tables and nothing asks the other direction.** The list is reconciled against the migrations one way only: `Every_stamped_table_the_check_names_carries_the_column_it_names` asserts that every table it names exists and carries its column, and no assertion anywhere asks whether every stamped table is named. Phase 3 added four observation stamps, `control_setup.drawn_at`, `forward_return.filled_at`, `ceiling_bound.computed_at` and `scoreboard.computed_at`, and none was added to the list; `detector_error.observed_at` has been outside it since 2.7. Adding the four was run at the 3.7 sign-off: the statement scope goes from 29 to 47 and **eight reads fail**, being four `control_setup` and two `ceiling_bound` in `ScoreboardBuilder` and two `scoreboard` in `LabScoreboard`. **None is a live wrong result today**, because `forward_return` is bounded everywhere, a control row is transitively bounded by the setup date its query already bounds, and the other two are latent until a scoreboard or a ceiling is rebuilt for a past date. That last condition is what a backfill is, so the latency is a schedule rather than a guarantee. The same fix carries a second item in the same file: `Statements()` matches each raw-string literal in both of its passes, so every such statement is yielded twice and the scope reads 29 over roughly fifteen, with a floor of 10 under it | 4.1 |
Now:  removed
Why:  Discharged at 3.8 rather than repointed. `PointInTimeCheck.Stamped` now names thirteen tables, the eight reads bound their stamps, and the reverse reconciliation the row asked for exists, so a table gaining a stamp fails rather than joining the corpus unnoticed. `Statements()` no longer yields each raw-string literal twice, which was the second item the row carried. It landed ahead of 4.1 because the repair in the same checkpoint is the first operation that rebuilds for a past date, which is the condition these reads were latent behind.

### 2026-08-28 — ARCHITECTURE.html — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Every value chosen rather than derived, with the basis for each and the point at which it should be revisited. Nothing here is left open. A value changes only by the same route as anything else: a proposal, a stated mechanism, and a paired test.
Now:  The same sentence with the completeness claim removed, followed by a paragraph explaining what an OPEN row is and why the claim went.
Why:  The table asserted a completeness it did not have while the exit rules carried five unauthored figures, a term defined nowhere in the corpus, a bar the store does not hold, and one of the two directions the project exists to compare. A current-state document claiming completeness tells a reader to stop looking, which is worse than saying nothing. Six rows are marked OPEN and a test fails if the claim returns while any of them is.

### 2026-08-28 — SCHEMA.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  Grain: date + ticker + direction. **Immutable after write, except by a correction that uses no information the night did not have.** The spine of the whole system (see: A setup row is corrected only where the correction uses no information the night did not have).
Now:  Grain: date + ticker + direction. **Rows are immutable after write, except by a correction the lateness bound admits.** The spine of the whole system (see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound).
Why:  One of four sites carrying the rule, all swept in the same commit against a count stated first, so the amendment could not become a fifth statement disagreeing with four.

### 2026-08-28 — BUILD_PLAN.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  | 3.1 | SetupJournal finalised | Setup rows immutable after write, except by a correction meeting both of the correction rule's conditions, and asserted both ways: nothing rewrites a plan or a gating verdict, and a correction whose input is stamped after the setup's own date fails (see: A setup row is corrected only where the correction uses no information the night did not have) |
Now:  | 3.1 | SetupJournal finalised | Setup rows immutable after write, except by a correction the lateness bound admits, and asserted in both directions: nothing rewrites a plan or a gating verdict, and a correction whose input arrived more than the bound after the setup's own session is refused (see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound) |
Why:  The second of the four sites. The done condition was the widest statement of the rule and the only one a checkpoint is judged against.

### 2026-08-28 — RUNBOOK.md — cites The vendor is EODHD, and the endpoint mix is what the call budget is built on
Was:  A schedule table and a recovery section that stated the nightly total against the ceiling without saying what unit the ceiling counts.
Now:  The same, plus a paragraph saying the ceiling's unit is the weighted cost rather than the request count, that the two differ by up to a hundredfold, and that every stage prints both.
Why:  Fifteen names is either fifteen or fifteen hundred against a 5,000 ceiling depending on the endpoint, and nothing said which. A reader comparing a printed figure to the ceiling had no way to know whether the two were in the same unit.

### 2026-08-28 — RUNBOOK.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  A Recovery section headed "The repair window, which closes at 19:59:59 ET on the session's own date", whose four paragraphs described the superseded rule: a morning read "is invisible to last night's session", the window having one edge, `recheck` refusing "any row whose input was stamped after the setup's own date", and "`recheck` refuses all fifteen and they keep that verdict permanently".
Now:  The same section headed "The repair window, which has two edges and closes 24 hours after the session's own end of day", stating the point-in-time bound and the lateness bound as separate edges, naming the session's end of day as the origin every lateness figure is measured from, and recording that the fifteen were admitted at 260 minutes and repaired.
Why:  The rule was superseded and its motivating case reversed, and this section still told an operator that the case had no repair. A runbook is what somebody reads at 07:00 with a broken night behind them, so a stale instruction here costs more than a stale sentence anywhere else in the corpus.

### 2026-08-28 — RUNBOOK.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  "**What the morning read cannot do is repair a stage that died part-way through its list**, and it is worth knowing that here rather than discovering it. A sector resolved this morning is invisible to last night's session, because every reader bounds on when the lookup was made."
Now:  The same paragraph opening "What the morning read can and cannot repair", saying a sector resolved this morning is late rather than invisible, and naming the two conditions under which it is admitted.
Why:  The second site in the file carrying the superseded rule, in the section an operator reads first. Swept in the same commit as the one above rather than left to be found later.

### 2026-08-28 — RUNBOOK.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  A `recheck` paragraph that named its refusals and its marks, and said nothing about the population the count it writes is taken over.
Now:  The same, plus a paragraph stating that the count is taken over the night's whole scan population rather than over the rows being repaired, and that a scan name with no setup row is counted.
Why:  A count taken over the repaired set would make every figure it produces an artefact of how many rows happened to be broken, and two of the fifteen came back failing at a cluster of one, which is the number that shape would produce. The property was true in the code and stated nowhere a reader would look.

### 2026-08-28 — RUNBOOK.md — cites Every line of code runs unmodified on Windows and on Apple Silicon macOS
Was:  "that bound is the session's own end of day: `2026-08-27T23:59:59.999Z` for the session of the 27th, which is 19:59:59 Eastern", and "a failure there leaves about an hour and three quarters before the first edge".
Now:  "that bound is the last instant of the session's own day **in Eastern time**: `2026-08-28T03:59:59.999Z` for the session of the 27th, which is 23:59:59 Eastern", and "a failure there leaves five hours forty-eight before the first edge and a further day before the second, and those figures no longer move with the clock change".
Why:  The bound was being built by appending `T23:59:59.999Z` to the session date, which closes an Eastern session at 19:59:59 Eastern in daylight time and 18:59:59 in standard time. The runbook stated the truncated figure as though it were the session's end. Both the instant and the window length change with the fix, and the window stops moving with the clock change.

### 2026-08-28 — RUNBOOK.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  "which is about six hours after the walk died and **260 minutes after that session's own end of day**", and "Had the rerun waited a further twenty hours".
Now:  "which is 00:19 Eastern: about six hours after the walk died and **20 minutes after that session's own end of day**", and "Had the rerun waited a further day".
Why:  260 minutes was measured from an end of day computed in UTC. Against the session's real end of day the same arrival is twenty minutes late. The figure was correct against the bound as it then stood and is corrected here in the same pass that corrected the bound, rather than left to disagree with the column.

### 2026-08-28 — CLAUDE.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  "CI green before merge, **and a phase branch does not merge until the whole phase has signed off.** Two conditions, and the second is the one that costs something." followed by three paragraphs stating what the sign-off gate bought, what it cost, and that a checkpoint still lands as its own commit.
Now:  "**CI green before merge. That is the only condition**", followed by the note that the rule has now moved twice, the cost that decided it, and the two things that do not move.
Why:  The sign-off gate keeps a correct pass unmerged for as long as the phase waits on something that is not code, and phase 3 waits three months for accumulation. The nightly job runs from the branch for the whole of that, so production runs from a branch to buy a property of the history. The decision names both halves of the trade and records that this is the second time the rule has moved, so a third change has to argue against both.

### 2026-08-28 — RUNBOOK.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  A schedule section describing `tools/nightly.ps1` and its log, saying nothing about which ref the job runs from beyond that the commit is recorded.
Now:  The same, plus a paragraph naming `main` as the ref as of 2026-08-28 and the merge commit that put the tree there, stating that nothing enforces it and that the night's own log is the check.
Why:  The job runs whatever the working tree is checked out to. That was `phase-3-corrections` for as long as the branch was open, which is the operational cost the merge rule was changed to stop, and the runbook never said which ref a night had run from. The obligation asking branch-or-main closes here, naming the commit.

### 2026-08-28 — BUILD_PLAN.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  | 3.6 | **Whether phase 3's own accumulation runs from the branch or from `main`.** A phase branch does not merge until the phase signs off, and 3.6 is now parked past 3.7, so the ordering that used to be implied no longer holds: if 3.7 signs off and PR #4 merges, the nightly job should run from `main`; if it does not, the job runs from the phase branch for as long as accumulation takes. Both work and they are not the same operational fact, and a job pointed at a checkout nobody is maintaining is the kind of thing discovered months later through a gap in the record. Raised at the handover rather than assumed, because the parking is what created the question. Due at the operator, with the scheduling above | the operator |
Now:  removed
Why:  Answered at 3.9(b) rather than moved again. The premise it rested on, that a phase branch does not merge until the phase signs off, is the rule that changed; PR #5 merged on CI green at `6f27926` and the working tree is on `main`, so the job runs from `main`. RUNBOOK names the ref, says nothing enforces it, and points at the night's own log as the check. The row's second copy in the operator table goes with it.

### 2026-08-28 — BUILD_PLAN.md — cites A reader's signature does not establish point-in-time; the query does
Was:  | 3.8 | **`scan_hit` carries no observation stamp, so a hit inserted for a past session after the fact is invisible to every bound the lab has.** Found while asserting that the lateness bound admits exactly one stamped column. It does, and the assertion is only about stamped columns: `scan_hit` has no stamp to bound, so a rerun of `scans` for a past date writes rows that no point-in-time read can tell from the originals, and the cluster count a repair derives would silently include them. Nothing is wrong today, because nothing has rerun `scans` for a past date and the primary key makes a rerun of the same session idempotent in what it writes rather than in when it wrote it. It is raised here rather than fixed because the fix is a migration plus a backfill of 300 rows with an instant nobody recorded, and inventing one would be worse than the gap: a stamp asserting a night it was not observed on is a wrong answer where a null is an honest one. Due at 4.1 with the other stamp work | 4.1 |
Now:  removed
Why:  Discharged at 3.9(d) rather than repointed, and the row's own reasoning is what was wrong. It said the fix needed "a migration plus a backfill of 300 rows with an instant nobody recorded, and inventing one would be worse than the gap". The instant was recorded: `run_log` holds the `scans` run of 2026-08-27 with `started_at` 22:10:03.506Z, `ended_at` 22:10:03.959Z, outcome clean and `rows_written` 300, which is exactly the number of hits for that date. Reading an instant across from another table is not the same act as choosing one. All 300 rows now carry `ended_at`, every read bounds the stamp, a null is refused by any session other than the row's own, and `scan_hit` joins the stamped list as its fourteenth table.

### 2026-08-28 — CLAUDE.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  | `ci-parity` | every CI run | `tools/ci.ps1` and `tools/ci.sh` run the same steps in the same order |
Now:  The same row, plus "and a step that fails fails the script it runs in, proved by running each shipped step function against a command that fails".
Why:  `tools/ci.sh` could not report a failure and had not been able to for the whole of phase 3. `if ! "$@"; then local code=$?` captures the status of the negated pipeline, which is 0 exactly when the command failed, so every one of the twenty-seven steps aborted the run and then exited 0. The macOS half of the matrix and the Linux rehearsal job both enter through that script, so neither could go red, and the merge rule makes CI green the only condition. Parity as previously stated could not see it: the two scripts declare identical step names in identical order and disagreed only in what they did with a non-zero status, so the property being added is the one the old row's wording left out.

### 2026-08-28 — BUILD_PLAN.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  A Phase 3 table ending at 3.9.
Now:  A 3.10 row, being the verification repair in eight lettered parts, with its done condition.
Why:  A full code review after 3.9 found the CI script above, three checks asserting less than their names, four claims that pass by comparing nothing, and the shipped defects those gaps were covering. None of it fits inside an existing checkpoint: 3.8 and 3.9 are closed and recorded, and the work is not a deliverable either one owed. The parts are ordered by dependency rather than by severity because every part after the first is verified by a script that could not fail.

### 2026-08-28 — BUILD_PLAN.md — cites Nothing in the corpus is struck through
Was:  A carried-obligations table with no row for the empty tool set on the researcher's subscription path.
Now:  A `| 1.5 | ... | 6.5 |` row for it, naming what the obligation is and that 6.5's own done condition already carries the test.
Why:  `carried-obligations` found it on the first run it ever reconciled anything. The obligation was written into the phase 1 corrections entry on 2026-08-25 and never given a row, so for three phases it was scheduled in substance, by 6.5's done condition, and absent from the one place the corpus says obligations live. That is the failure the check's own docstring says has happened four times, and this is the fifth.

### 2026-08-28 — ARCHITECTURE.html — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | 5 | Verify on arrival | `PRAGMA integrity_check;` then re-run the step 2 counts and compare. Integrity check alone proves the file is not corrupt, **not** that it is complete. Both are needed. |
Now:  The same, with "`tools/snapshot-db` does both against the copy and exits non-zero on either." after the first sentence.
Why:  Found by `architecture-conformance` on the first run in which its procedure comparator had anything to compare. RUNBOOK's step 5 names the tool that performs the step and the design source of truth did not, so an operator reading the architecture would do by hand what a committed script does and exits non-zero on. The two statements of one procedure had drifted, which is the exact defect the claim exists to catch and could not, because both sides of the comparison had been empty since 1.12.

### 2026-08-28 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | `bar-append-only` | every CI run | Nothing in the shipped source deletes or updates a bar table |
Now:  The same row, plus "and no migration deletes, updates or drops one".
Why:  The hard rule says "CI greps for delete and update statements against bar tables". The grep read `RepositoryLayout.SourceFiles`, which is `*.cs`, so it had never read a migration, and every table rebuild in this project is a migration. Migrations 028, 029 and 030 issue `UPDATE indicator_daily`, `UPDATE scan_hit` and `DELETE FROM scoreboard` unseen, and `DROP TABLE` plus re-`INSERT` is the established rebuild idiom in 005 and 009. None of them touches a bar table, so the property held and nothing was checking that it held.

### 2026-08-28 — RUNBOOK.md — cites Averages are computed locally, never through the vendor's technical endpoint
Was:  | 17:15 | `universe-build`, the symbol list and the nightly snapshot of who was listed | ~5 | and a stated total of **~803 against a 5,000 ceiling**.
Now:  | 17:15 | `universe-build`, the symbol list and then the screening window, being one bulk end-of-day request per session until twenty sessions have been screened | ~2,005 |, a total of **~2,803**, and a paragraph giving the 2,005 to 3,205 range for the stage and 2,803 to 4,003 for the night.
Why:  The row named the symbol list and stopped there, and the stage does the symbol list and then the screening window at `BulkEndOfDayCost` 100 times `LiquidityWindowSessions` 20. `UniverseBuilderTests` has asserted `Assert.Equal(2005, result.CallsUsed)` since it was written, so the document and the suite disagreed by two thousand calls in the one table an operator reads to know whether a night fits. The headroom is under twice expected usage rather than seven times, and `daily-bars` runs after `universe`, so the stage that stops short on a full day is the one that stores the bars.

### 2026-08-28 — SCHEMA.md — cites A gate handed an absent or degenerate quantity fails rather than passing
Was:  | `trigger_price`, `stop_price` | TEXT | raw prices | and | `stop_distance_ranges` | TEXT | the number check nine turns on |, with the three signal rows describing the quantities without saying they can be absent.
Now:  The same columns as `TEXT NULL` with "null where the geometry is absent", the four signal rows marked **Absent where the setup has none**, and a paragraph stating that nought is not the same answer as none, what the flattening cost, and that rows written before migration 031 keep it.
Why:  The three columns were `NOT NULL`, so a setup whose geometry the detector could not compute had nowhere to record that. The detector wrote nought, `SignalVectorizer` froze the nought into `setup_signal`, which is written once and never updated, and the gallery rendered a trade whose give-up was nothing. The golden fixture already held the case: `2026-08-24-INTC-short` records `exit-tight` as failed with value null on the same row whose frozen signal said `stop_distance_ranges = 0.0000`, and the fixture's own expectation file had frozen that 0.0000 as a value to be preserved.

### 2026-08-28 — SCHEMA.md — cites Averages are computed locally, never through the vendor's technical endpoint
Was:  A `setup` column list with no `degraded_because`.
Now:  `| degraded_because | TEXT NULL | which stages of this setup's own session had already ended other than cleanly when the row was written, comma separated. Null on an ordinary night. The third clause of the vendor-ceiling rule, which had no column until migration 032 |`
Why:  The hard rule reads "A stage stops rather than overrunning, writes a partial run entry, **and marks the affected setups degraded**." The first two clauses held from 1.4. The third had no column anywhere in the store, no entry here, and nothing in the source but a doc comment on `RunOutcome.Partial`. A night where `sectors` stops at the ceiling leaves setups whose cluster verdict failed for want of an input, and setup rows are immutable, so nothing on them said the night was short and nothing could improve them afterwards. That is the same need the correction mark was added for at 3.8.

### 2026-08-28 — RUNBOOK.md — cites The lab keeps one store per purpose under one data root, and CI never opens the operator's
Was:  A recovery table naming "Restore the most recent snapshot from the data root" with nothing said about how many snapshots exist or how far back one reaches.
Now:  The same, plus three paragraphs: the lab keeps the last 7 and removes the rest, removal happens only after the new snapshot passes both its checks and is named in the night's log, and a snapshot renamed out of the generated form is kept indefinitely.
Why:  There was no retention at all. Twenty-four snapshots had accumulated in four days, 4.6 GB against a store holding one session of setups, growing about 290 MB a night. RUNBOOK is where an operator reads how far a restore can reach back, so the bound belongs where the restore instruction is, and the 7 is pinned against `PullbackStrategyLabOptions.SnapshotsKept` so the promise cannot drift from the code that keeps it.

### 2026-08-28 — BUILD_PLAN.md — cites The averages are one implementation, computed nightly and drawn on demand
Was:  | 3.11 | **`held-floor` and `no-reclaim` compare every pullback bar against the as-of session's average rather than each bar's own.** ... Not repaired here because it changes what the detectors flag, which moves every count phase 3 has recorded. | 4.1 |
Now:  removed
Why:  Discharged at 3.11(f) rather than repointed. The comparison is per bar, the two definitions are pinned apart in both directions by `FloorSeriesTests`, and the golden fixture reports no flip in either direction over its three setups. The 44 rows of 2026-08-27 stay as they were flagged and the seam is stated in PROGRESS with the date the definition changed.

### 2026-08-28 — SCHEMA.md — cites The averages are one implementation, computed nightly and drawn on demand
Was:  | `closes_beyond_floor` | sessions in the pullback closing below `ema_21`, long; above `ema_50`, short | `daily_bar.adj_close`, `indicator_daily.ema_21`, `indicator_daily.ema_50` | active |
Now:  | `closes_beyond_floor` | sessions in the pullback closing below the 21-day average **as at that session**, long; above the 50-day, short. The average is a series over the window, not the value at the as-of date | `daily_bar.adj_close` | active |
Why:  Found by the sweep the 3.11(f) change owed. The description read as a single value and the provenance column was factually wrong after the change: the signal no longer reads `indicator_daily` at all, because the floor is computed from the bars through `Averages.ExponentialSeries`. Naming a column the signal does not read is the failure `writer-ownership` and the point-in-time reads both depend on this table being right about.

### 2026-08-28 — DECISIONS.md — cites The averages are one implementation, computed nightly and drawn on demand
Was:  "The arithmetic lives in Core and is called by two components. IndicatorEngine computes the value at the as-of date and is the sole writer of `indicator_daily`. The read surface computes the same average at every session in a window, which is the shape a chart needs, and writes nothing."
Now:  The same, restated as two components in three shapes, naming the series IndicatorEngine now computes for the checks that read a span, plus a paragraph recording that the third shape arrived at 3.11 and that the defect it fixed is this decision's own failure mode reached from inside the lab rather than from the read surface.
Why:  The decision described IndicatorEngine as computing "the value at the as-of date". That was complete when it was written and stopped being so when `held-floor` began reading a series. It never fixed the floor comparison as scalar, so nothing here supersedes it and the 3.11(f) change contradicts no authored decision; what it did was leave the decision's account of IndicatorEngine one shape short.

### 2026-08-29 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Six defect shapes, the sixth ending "Where a property is asserted to be stated, recorded on every row, or shown, the assertion names the surface a person reads it on and checks that surface."
Now:  The same, plus a seventh: every check in this corpus takes its subject from the source, the documents, the golden fixture, or a store the check builds, and the running lab is in none of those. A green report is a statement about the build and never about the lab, and a property about the running system is asserted against it by a guard in the code and a figure a person reads, or it is not asserted.
Why:  The phase 3 sign-off found the lab had lost the night of 2026-08-28 entirely. Migrations 031 and 032 had landed, the live store was never migrated, four stages died on a missing column, and no setup was flagged. On that tree that night `tools/ci.*` was green at 27 steps and 516 tests and the phase report was GREEN with zero unexamined. Neither instrument was wrong and neither could have seen it. The six recorded shapes are all faults in something the corpus wrote; this one is a fault in what it points at, so it does not fit under any of them.

### 2026-08-29 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A phase 3 table ending at 3.11, and a carried obligations table with no row for the status band's tie-break or for the direction `surface-claims` does not reconcile.
Now:  A 3.12 row for the sign-off's findings in six parts with its done condition, and two obligation rows due at 4.1.
Why:  The sign-off review of phase 3 found three blocking defects and four smaller ones. Three of the blocking ones are repairs to shipped code and one is a night to be recovered, so they are a checkpoint rather than a set of obligations; the two that are questions rather than repairs are obligations, on the rule that a finding which does not block a checkpoint waits and is written down when it is raised.

### 2026-08-29 — ARCHITECTURE.html — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A failure behaviour table with no row for a store at a version other than the build's.
Now:  A row stating that every stage but `migrate`, `snapshot-db` and `list-stages` refuses before opening the store, naming both versions, that a store ahead of the build is refused on the same footing, and that the status band states the mismatch rather than printing two numbers to be compared.
Why:  There was no such behaviour and no claim about one. A store behind its migrations failed at the first statement needing a new column, mid-night, with a raw SQLite error naming the column, which says what broke and not why. Every behaviour change owes a claim, and this is the claim for the guard 3.12 adds.

### 2026-08-29 — RUNBOOK.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A recovery table whose first row read "A nightly stage failed | Rerun that stage alone. Every stage is idempotent for its date", with no row for a store behind its migrations; and a morning check reading "Check the status band: run clean, calls within budget, positions and risk within caps."
Now:  A second recovery row for a stage failing on a column the store has not got, naming `tools/migrate` and the rerun in slot order; and a morning check that reads the schema pair first, with a paragraph on why it is first.
Why:  On 2026-08-28 the store sat two migrations behind, four stages died on `no such column: degraded_because`, and the night flagged nothing. The RUNBOOK's morning routine sent the operator to the status band and the band had no way to say so. The recovery table's existing first row would have had them rerun the stage, which failed again the same way with no next step written down.

### 2026-08-29 — RUNBOOK.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  **The ref the job runs from is `main`, as of 2026-08-28.** It is not configured anywhere: the slot script runs whatever the working tree is checked out to, and the tree is on `main` because `6f27926` merged the correction pass and the post-pass. Nothing enforces this and nothing should pretend to, so the check is the log: the first line of every night's entry names the branch and the commit, and a night that ran from something else says so in its own record. Before the merge the job ran from `phase-3-corrections`, which is what the merge rule was changed to stop (see: A phase branch merges on CI green, and the sign-off reviews what is already on the default branch). Anyone leaving the tree on a branch overnight is choosing which code runs the night, and the way to undo it is `git checkout main` in the repository the tasks point at.
Now:  The same paragraph with the date and the commit moved to 2026-08-29 and `743a98a`, and a second paragraph recording that the tree has now been left on a branch twice, the second time across every slot of 2026-08-28 at six different commits, which is the night the lab flagged nothing.
Why:  The first version read as though the branch had been a one-off before a merge that fixed it. It happened again, on the branch this entry is written from, and it cost the night. A sentence naming one instance of a recurring fault reads as history rather than as a live hazard, and the second instance is what the obligation raised at 3.12 rests on.

### 2026-08-29 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Every clause of a corpus sentence a claim names is asserted by that claim, or the claim says which clause it does not reach.
Now:  The vendor-ceiling claim asserting every clause of the sentence it names, with the measurement that decided the narrowing and a pointer to the PROGRESS entry that names the amendment as one.
Why:  The clause was universal where the clause beside it is scoped to one checkpoint's statements, and the deliverable it belongs to names one claim. Measured before amending rather than after: 40 of the 76 live claims name a corpus sentence of more than one clause, across five tables and about 160 clauses. Each needs its clauses classified as assertable or as rationale before anything can be asserted, and that classification is judgement no check can make. Amending a done condition is legitimate and hiding the amendment is not, so it is named as an amendment in the entry and the sweep is a carried obligation due at 4.1.

### 2026-08-29 — CLAUDE.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  A Merge section stating that CI green is the only condition, that the rule has moved twice, what decided it, and that a checkpoint still lands as its own commit. Nothing anywhere about how a change reaches `main`.
Now:  The same, plus the rule that every change reaches `main` through a branch and a pull request and none is committed to `main` directly, with the branch deleted after the merge and the working tree returned to `main`; and a paragraph recording that this was undocumented until the 3.12 sign-off and where it broke.
Why:  The existing section says when a merge may happen and never how a change arrives, so the branch-and-PR convention lived only in two records naming a PR number: `PR #4` in the 3.7 sign-off and `PR #5` in an answered question. Neither is a rule. It then broke in exactly the way "Conventions" predicts: `ecf5a3b`, `3e88a35` and `2b5316c` were committed straight to `main`, and the sign-off session reviewing them was one command from doing the same. Second instance in this corpus of a convention lost because it existed only in what previous sessions happened to do, after the commit subject at 3.7, which is the point that paragraph names as the one where a check becomes owed.

### 2026-08-29 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A carried obligations table with no row for finding six of the phase 3 sign-off and none for the two quantities sharing one truncation expression; and a row on the degraded mark's window naming `RunLogger.IncompleteStagesOf` alone.
Now:  Two rows due at 4.1, and the degraded mark's row extended to name the status band, whose `LatestRun` now bounds on the same session calendar day.
Why:  The 3.12 sign-off found finding six closed on paper and not in fact, its done condition having been authored about the seam dating rather than about the missing entry, which leaves one sentence in the record right today by an accident of ordering. It found one further question, being that `LabStatus` and `RunLogger.CallsUsedOn` compute a session day and a vendor quota day with the same expression, which no guard can separate. Neither blocks a checkpoint or breaks a check, and a finding that does not block waits and is written down when it is raised. The third finding of that pass, a doc comment separated from the method it documents, is repaired in this commit rather than carried, so it takes no row. The degraded mark's row assumed one reader of what a night is where the repair at 3.12 gave that question a second.

### 2026-08-29 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A Commands table whose "Verify a phase" row read "`tools/verify-phase`" for Windows and "same" for macOS, and a paragraph on what the phase report asserts with nothing about how the command is invoked.
Now:  The row reads `tools/verify-phase.ps1` on Windows and `tools/verify-phase` on macOS, with a paragraph above the existing one saying why the two cells differ for this command alone, what the silent no-op was, and that the report now carries the commit that produced it.
Why:  `tools/verify-phase` is a bash script with no extension. Called by name from a PowerShell session it does not execute, returns 0, and leaves the previous run's artifacts on disk reading as current; the script's own rm block is the guard for that and sits inside the thing that did not run. The table told a Windows reader to run the form that no-ops. Found at the 3.12 sign-off by a session that ran it that way and quoted an earlier run's figures before catching it, which is the same shape as a check that stops running: the exit code says nothing is wrong because nothing ran.

### 2026-08-29 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  | 3.12 | **Finding six of the phase 3 sign-off closed on paper and not in fact, because the done condition was authored about a different subject.** The finding was that `97b3a2a` left no PROGRESS entry, having changed DECISIONS, SCHEMA and two gallery-visible check notes and replaced an assertion the previous commit's entry describes. 3.12's done condition (f) reads "the seam 3.11(f) dated to a session on which nothing was flagged, corrected", which is the Carried block's dating: a real defect, cleanly corrected by the entry of 2026-08-29, and not the one finding six named. So the condition was met and the finding was not, and nothing between them said so. **The live consequence is one sentence.** The entry of 2026-08-28 states that period and warm-up are "asserted equal to what the chart builds". That was false when it was written, because the assertion then called `Averages.ExponentialSeries` with `IndicatorEngine`'s own constants and proved only that `FloorSeries` delegates, saying nothing about the page. It became true three commits later, at `97b3a2a`, which replaced the assertion with one reading the rendered line out of `LabChart.Read`. **The record is therefore right today by an accident of ordering**, and a reader has no way to see that: `97b3a2a` appears nowhere in the corpus except inside the finding that named it. What is owed is a dated entry naming what that commit changed, and the reason this is carried as a narrowing rather than as a missing paragraph is that a done condition coming out narrower than the finding it was written from is the defect CLAUDE.md names as the most common in this corpus | 4.1 |
Now:  The row is removed. The obligation it carried was a dated entry naming what `97b3a2a` changed, and that entry is written: "3.11(f) — 2026-08-29 — the entry `97b3a2a` never wrote, and what it changed".
Why:  Discharged rather than moved. The entry records the SCHEMA provenance row, the decision restated as three shapes, the two gallery-visible check notes and the replaced assertion, and it says in terms that the sentence of 2026-08-28 was false when written and became true three commits later, so the record is right on the record rather than by an accident of ordering. The narrowing the row's subject named needs no row of its own: "a done condition narrower than its clause is the most common defect in this corpus" is already in CLAUDE.md, and the instance is on the record in the 3.12 sign-off entry. Removed rather than kept with a discharge note because the obligations table is what is owed, and a table that keeps what is done reads the same as one nobody works through.

### 2026-08-29 — RUNBOOK.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  **The ref the job runs from is `main`, as of 2026-08-29.** ... the tree is on `main` because `743a98a` was fast-forwarded onto it once `tools/ci.ps1` was green at that commit. ... **It has now been on a branch twice, and the second time cost a night.**
Now:  The same two paragraphs with the commit moved to PR #8's merge at `6661f2d`, and the count moved to three, the third recorded as having happened inside the pass that closed the second, with the window it sat in and how it was found.
Why:  Third instance. The 3.12 sign-off closed "production running from a branch" by returning the tree to `main`, then created `phase-3-signoff` and committed the closure onto it; the tree was on a branch from 13:32Z until PR #8 merged at 14:26Z. It crossed no slot and cost no night, and it was found by a review running `git status` rather than by anything the corpus runs. A paragraph saying "twice" while the third instance is live is the same fault the second version of this paragraph was written to fix.

### 2026-08-29 — BUILD_PLAN.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  A row whose subject was the recurrence and whose owed work was "the decision between leaving it to the log and giving the slot a ref it expects, taken once rather than re-argued at each sign-off", naming two instances.
Now:  The same row with the guard as its subject, being `tools/nightly.ps1` refusing to dispatch when the tree is not on `main`; the reason no instrument can catch this, which is that the ref is a property of the working tree and every check reads source, documents or a store it builds; the third instance; and the override the 3.6 attempt lacked.
Why:  A row stating a recurrence and offering two options is a row nobody can act on, and it had already been re-argued at two sign-offs. The third instance settles the choice: it happened inside the pass closing the second, which is not a fault more remembering fixes. The guard belongs in the slot script because that is the only thing in the project that already knows which ref it is running from, and the reason the first attempt was removed is recorded so the second does not repeat it.

### 2026-08-29 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A phase 3 table ending at 3.12.
Now:  A 3.13 row for the review's findings in three parts, with its done condition.
Why:  The review after the 3.12 sign-off found five things. Two are rows, and three are repairs to shipped code: the report writing one file of two, a restore that validates a check argument and then discards it, and a positional date argument that a flag's value is eligible to be taken as. Repairs to shipped code are a checkpoint rather than obligations, on the same reading that made 3.8 through 3.12 checkpoints. Two of the three sit in code the 3.12 sign-off session committed and then signed off, so the checkpoint is also where the read that code never had is discharged.

### 2026-08-29 — SCHEMA.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  A `setup` column table ending at `corrected_from`, and a writer line reading "Update CheckRecomputer (`check_results`, `corrected_at`, `corrected_because`, `correction_lateness_minutes`, `corrected_from`, and only for a check the baseline records without requiring)".
Now:  A `corrected_check` row, and the same writer line with that column in it.
Why:  025 through 027 recorded that a row was corrected, how late its input was and what it said before, and none of them recorded what was corrected. The check's name reached the row only inside `corrected_because`, so the one value a scoped restore has to select on was the one that was prose. `recheck --restore --check <name>` therefore validated the check and then discarded it, restoring every corrected row of the date. Harmless while `cluster` is the only admitted check and silently destructive once a second is, so the column comes before the query that needs it.

### 2026-08-29 — RUNBOOK.md — cites A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
Was:  "**If a check verdict was recorded with no value anyway**, `recheck <date> --check cluster` reports what it would correct and writes nothing", with the corrected row recording `corrected_at`, `corrected_because`, the lateness and the prior results, and nothing about argument order.
Now:  The same paragraph with `--as-of` as the documented form and `corrected_check` among the recorded columns, plus a paragraph saying the arguments go in any order, that a bare date still parses, and what was false before 3.13.
Why:  The date was whatever argument was neither a flag nor the check's own name, so three of the four orderings anybody would write read `--expect`'s value as the date and exited on the format. The ordering this line documented was the one that happened to work, and nothing said so. It is written down rather than left implicit because a command with one working ordering and no statement of it is a command that fails for the next reader in a way that looks like their mistake.

### 2026-08-29 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A carried obligations table with no row for the three small things the review found together, and a 3.5 row on `CeilingCalculator`'s insert comment ending "which of the two is intended is a decision rather than a typo, because the bound is stated as recomputed weekly".
Now:  A row raised at 3.13 naming the schema-mismatch label, the stacked summaries and the doubled query, with the label's consequence stated; and the 3.5 row extended with the general form 3.9(e) implemented in one place only.
Why:  `LabStatusView.SchemaBehind` is a not-equal named as "behind", so a build older than its store reads on the band as a build newer than its store, and the guard's own message distinguishes the two because they need different acts. That is a surface losing a correct answer, which is the sixth failure shape, and it belongs beside two cosmetic findings from the same read rather than in a row of its own. The 3.5 row is extended rather than joined by a second because the comment and the asymmetry are one statement read two ways: `CeilingCalculator.Insert` returns void and counts no skip, so recomputing a week that already has a row is the no-op wearing a clean run that 3.9(e) was written about. What is owed is the general form written down and applied, not another instance recorded.

### 2026-08-29 — BUILD_PLAN.md — cites The corpus is eight documents plus one artefact, and a ninth requires retiring one
Was:  A carried obligations table with thirty of its forty-five rows falling due at 4.1 and nothing saying what they are.
Now:  A section, "What the thirty due at 4.1 are, before any of them is moved", classifying them into one that blocks a phase-4 done condition, five that belong to a later checkpoint's subject and twenty-four independent of phase 4 entirely, with the three counts derived and checked by `stated-counts`.
Why:  A due point that moves at every sign-off is permanent while reading as pending, and the table had reached that state at aggregate scale one legitimate local decision at a time. The ordering is produced without moving anything, so the decision about what to move is taken once with the groups in front of whoever takes it rather than thirty times by whoever is closest to each row. It is a section rather than a ninth document because the corpus is eight plus one artefact, and a classification of BUILD_PLAN's own table belongs inside BUILD_PLAN, where phase 4's plan cannot fail to read it.

### 2026-08-29 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  "**The one that blocks is the fixture permit, and it blocks mechanically rather than by judgement.** `fixtures/expectations.json` names seven frozen-only checkpoints under `frozenOnly` ... So the first CI run after 4.1's PROGRESS entry lands turns red seven times over", over a group table reading 1, 5 and 25, and a paragraph naming `price-storage-form` as the fifth row belonging to a later checkpoint's subject.
Now:  Two paragraphs naming both rows that collect themselves at 4.1, the count read as eight from the fixture rather than stated in prose, a group table reading 2, 5 and 24, and `price-storage-form` moved out of the five and into the two while `writer-ownership`'s attribution row moves into the five at 4.6.
Why:  Three things were wrong in one paragraph. The count went stale by one in the commit that added the eighth permit. `price-storage-form` defers eighteen columns to 4.1 and `CheckCoverage.DeferralProblems` fails a deferral naming a landed checkpoint, so it collects itself at 4.1 exactly as the permits do and the paragraph said there was only one such row. And the mechanism the paragraph rests on did not exist: `fixture-replay` asked its permit question in two loops and the due-point clause was in the one no live permit takes, so nothing would have turned red at all. The classification is the artefact the phase-4 planner is told to decide from, and all three make it wrong in the direction of understating what has to happen first.

### 2026-08-29 — BUILD_PLAN.md — cites A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
Was:  A phase 3 table ending at 3.13, whose only sign-off row is 3.7, scoped by its own done condition to "3.0 through 3.5".
Now:  The same table with 3.14, the completeness review's findings, and 3.15, a phase sign-off covering 3.8 through 3.14.
Why:  Sign-off is owed on the phase as a whole before the next phase's plan, and every other phase's table ends in its sign-off row. Phase 3's did not: six checkpoints landed after 3.7 with nothing scheduling a pass over them, and 3.13's own record parks its `tools/verify-phase` figures in "the sign-off that follows", which the plan did not have. 3.8 through 3.11 were read by the sign-off of 2026-08-29 and 3.12 by the one after it, so the row covers the range rather than the remainder: a sign-off that names a subset is how this gap opened.

### 2026-08-29 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  "Some are plausibly expectation-free ... and for six of the eight none of that has been established."
Now:  The same sentence reading "seven of the eight".
Why:  Eight checkpoints are named in the row and exactly one, 3.13, is named as the exception, so seven are unestablished. The fixture agrees: seven permits carry the boilerplate saying nothing has been established and 3.13's states its reason.

### 2026-08-29 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "`tools/verify-phase.ps1` finds a bash and hands the work to the one script rather than reimplementing it, and exits non-zero with a named message when the machine has none."
Now:  The same sentence with the exit code named, and what it did before 3.14: it took whatever `Get-Command bash` returned, which on a stock Windows 11 is the Windows Subsystem for Linux launcher in System32, ahead of Git for Windows on the path.
Why:  The guard built at the 3.12 sign-off to stop the Windows invocation no-opping did not work on the machine it was written for. With no WSL distribution installed it exited 1, which is the code a red phase report exits with, so the documented Windows command reported a failing gate and ran nothing; the fallback list naming Git for Windows was never reached because the path lookup had already succeeded.

### 2026-08-29 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Seven defect shapes under "Verification", the last being a subject the corpus does not point at.
Now:  Eight, the new one placed before the seventh: a clause that exists, is correct, is exercised by a passing proof test, and governs a population that goes down the other branch.
Why:  `fixture-replay` asked done condition seven in two loops and the due-point clause was written in only one of them. Every permit in the fixture is in the other, so the guard the plan calls the one thing that collects itself at 4.1 reached nothing, and every proof test used the overload that empties the population it could have been seen in. None of the seven shapes covers it: the assertion is present, correct, and applied to the wrong set.

### 2026-08-29 — BUILD_PLAN.md — cites Targets and minimum samples are written at creation and are immutable
Was:  "Thirty-one of the fifty-eight rows above fall due at 4.1", over a carried-obligations table of fifty-eight rows.
Now:  The same sentence reading "fifty-nine", over a table of fifty-nine, the new row being the 160-observation minimum sample raised at 3.0(f) and due at 5.1. The total is now derived from the table by `stated-counts` rather than stated in prose.
Why:  The obligation existed only in the record. 3.0(f) established that the 160 paired observations `VariantAdmitter` freezes into a version's pre-registration were counted as though they were independent, wrote its `Carried` block as "due before 5.1", and no row was ever added; nothing reads the record for work, so a figure that is too small was on course to be frozen into every version admitted under it. `carried-obligations` exists to catch exactly that and could not see the phrase: its due-point pattern read a literal "due" optionally followed by "at", so "due before" matched nothing, and 6 of the 71 due points the record names were invisible to it. The prose total is edited in the same pass because adding the row is what made it wrong, and it is derived from here on for the reason this document exists.

### 2026-08-29 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  The 2.11 threshold row ending at "**Due at the operator**, on the same terms as the other three: it is a ruling rather than work, no build session can take it, and a due point that moves to the next checkpoint at every sign-off is permanent while reading as pending."
Now:  The same row recording that the operator ruled on 2026-08-29, that the once-only adjustment stays unspent, that the second wrong quantity is what the row now waits on, that the 5 to 60 band is not re-derived either, and what the ruling settles for phase 4.
Why:  The row had been due at the operator since the 2.12 sign-off and BUILD_PLAN names it the one open question that stalls a phase, but nobody had ever put it to them, so it read as pending for want of an answer rather than for want of work. The four readings were stated as alternatives and the second was taken, which is the one this row already argued for. Recording it changes what the row waits on: not a decision, but the identification of a second wrong quantity, the geometry having been the first and having moved the retrace medians without moving the count. It also settles that phase 4's plan is written against flagged setups rather than passing ones, which is what 4.1 renders in any case.

### 2026-08-29 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "Greps over markdown must be whitespace-tolerant."
Now:  The same rule extended to markup over the span the pattern matches, with the instance that earned it.
Why:  The rule was already written and `carried-obligations` broke it in a way the wording did not reach. Its due-point pattern used a literal space and no tolerance for emphasis, so it read four of the six forms the record writes a due point in, missing "Due **4.1**", "due **at 3.6**", "Due at **the operator**", "due before 5.1" and any phrase a long table cell wrapped across a line. Six of the seventy-one due points in the record were invisible, and one of the six was the 160-observation minimum sample, which was in no obligation row at all. Whitespace was half the rule and emphasis is the other half, and both fail silently: a match that never happens is not a match that broke, so the count never stood higher and no floor under it could show the gap.

### 2026-08-29 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "**Which checkpoint the build is on is the last entry in `docs/PROGRESS.md`,** and the one to build next is the checkpoint after it in `docs/BUILD_PLAN.md`."
Now:  The same pointer reading "the furthest checkpoint `docs/PROGRESS.md` records", with the rule it collided with and the instance that showed it.
Why:  Two rules in this file could not both be read literally. This one said the build is on the last entry; "Nothing in the corpus is struck through" says a record is corrected by a new dated entry naming what it corrects. So correcting an old checkpoint appends an entry naming that checkpoint and moves the pointer backwards. It happened the day it was exercised: a ruling recorded against 2.11 on 2026-08-29, with 3.14 landed, made `ArchitectureConformanceCheck.Schedule` read `LastLanded` as 2.11 and the phase report title itself "Phase 2 report". The proxy gives way rather than the correction rule, because appending a dated correction is what the corpus requires everywhere and "last" was standing in for "furthest" only while every entry happened to be a new checkpoint.

### 2026-08-29 — CLAUDE.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The Verification section running fourth, fifth, sixth, **eighth**, seventh: the eighth failure shape and its two following paragraphs sat between the sixth and the seventh, and the eighth opened "The four early shapes are an assertion whose subject went away. The fifth is a figure over the wrong population, the sixth a correct answer dropped by a surface, the seventh a subject the corpus never points at."
Now:  The same paragraphs in ordinal order, the eighth and its two moved below the seventh and the rule that closes it. No word of either shape changed.
Why:  A numbered sequence out of order in the file every session is told to read first, and it made two sentences false about their own document: the eighth forward-referenced a seventh the reader had not met, and the seventh's "The six above are all faults in something the corpus wrote" described seven paragraphs, one of which was the eighth it does not mean. Found by the 3.15 sign-off. Nothing guards it: `stated-counts` reads this file for the seven done conditions and the lifecycle table's five, three and one, and nothing reads the shapes. An ordinal sequence a spec states about its own contents is the same kind of number as those, and a claim asserting the shape headers appear in order with no gaps would have caught this on the commit that made it. Recorded as owed rather than added here, because adding it is code and a sign-off session that commits code may not sign it off.

### 2026-08-29 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  "`fixtures/expectations.json` names nine frozen-only checkpoints", "turns red nine times over", and a 3.10 obligation row reading "The nine landed checkpoints that contributed no fixture expectation at all ... and 3.10, 3.13 and 3.14 do the same ... for seven of the nine none of that has been established. **3.13 and 3.14 are the exceptions**".
Now:  The same three sentences at ten, naming 3.15 among the checkpoints that contributed none and among the exceptions that state a reason rather than record that one is owed.
Why:  The sign-off is a checkpoint and `fixture-replay` now asks done condition seven of every landed one, which is 3.14's own repair. A sign-off adds no stage to the replayed pipeline and no behaviour to freeze, so it takes a permit under the obligation raised at 3.10 on the footing 3.13 and 3.14 established. The two pinned sentences move with the permit because `stated-counts` derives that figure from the fixture as of 3.14, which is the guard doing what it was added for: the count and the sentence naming it cannot part again.

### 2026-08-29 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  Three carried obligation rows ending at "return before the first `UPDATE`" (3.14, due 4.6), "in the one place nothing scans" (3.12, due 4.1) and "a table nobody works through" (3.13, due 4.1).
Now:  The same three rows, each extended with a finding of the 3.15 sign-off that belongs to the subject it already names: two more of `recheck`'s command line, a third doc comment asserting the opposite of its code, and the second shipping of the stacked `<summary>` block.
Why:  Extended rather than joined by three new rows, on the grounds the 3.5 row was extended at 3.13: a row per instance of a shape already recorded is a table nobody works through. It is also the only form available here. `stated-counts` pins the obligations table's own total and the count due at 4.1 as literals in the test project, where the permit claims six lines below read theirs from this document, so adding or repointing a row is a source edit rather than a document one. A sign-off session that made it would have committed code and could not then sign the phase off. That collision is itself a finding of this sign-off and is recorded in PROGRESS with the ruling it blocked.

### 2026-08-30 — BUILD_PLAN.md — cites The minimum sample is derived from a measured dispersion and counted in effective observations
Was:  The `ForwardDispersion` row raised at 3.11 ending at "A ruling rather than a repair, because it moves the number 3.6 turns on.", due at 4.1.
Now:  The same row recording that the 3.15 sign-off repointed it to the operator and that the repoint was executed on 2026-08-30, due at the operator. It also appears as a tenth row in the operator's table, with what it blocks and what stalls without it.
Why:  3.15 took the ruling and could not execute it. Its reasoning is that a due point at 4.1 sits behind the checkpoint that spends the figure, which is the shape the classification section spends its length arguing against, and that the twin raised at 3.5 says the same thing about the same quantity from the other side and was already the operator's. Both are rulings on one number rather than repairs, so they are answered together or not at all. What stopped 3.15 executing it is the entry below.

### 2026-08-30 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  The `price-storage-form` row raised at 3.7 ending at "the statement form a later phase is most likely to add a column with", due at 4.1, and the classification section naming it as the second of two obligations that block 4.1 mechanically.
Now:  The same row due at 4.6, recording the repoint and that `PriceStorageFormCheck`'s own deferral moved with it, and the classification section recording that it is settled and no longer a member of that group.
Why:  The check deferred its eighteen `ALTER TABLE` columns to 4.1 while the classification sent the row to 4.6 as the cheapest place to write the parse, and `CheckCoverage.DeferralProblems` fails a deferral naming a checkpoint `PROGRESS.md` records. The first CI run after 4.1's entry would have turned that step red for a disagreement between a row and a check rather than for a defect. BUILD_PLAN already said what was owed before 4.1 was that the two agree, not that the parse be written, and named 4.6 as the checkpoint: 4.6 is where the tables carrying orders arrive, which is where money columns start to matter. Nothing about the parse changed and it is still owed.

### 2026-08-30 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "### What the thirty-one due at 4.1 are, before any of them is moved", opening "Thirty-one of the fifty-nine rows above fall due at 4.1", followed by "**Nothing is moved here and no row's due point changes.**"; a group table reading 2, 5 and 24; "**Two block, and both block mechanically rather than by judgement.**"; and "twenty-four of thirty-one".
Now:  The heading at twenty-nine with the promise dropped, the group table at 1, 5 and 23, one paragraph recording which two rows have since moved and under which ruling, and the counts through the section moved with them.
Why:  Two rows left 4.1 under rulings that named them, and a section that says nothing is moved while two rows have moved is a spec that has to be broken to do what a ruling asked for. The heading carries the count rather than a promise about it. What has not changed is the part that matters: the twenty-three independent rows are still where they landed by default, and choosing their due points is still the decision this section does not take and hands to whoever plans phase 4.

### 2026-08-30 — BUILD_PLAN.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  "### The nine that are the operator's", introduced by "Nine sessions have each moved one of these to the next checkpoint", with "the operator's eight below" in the section above it, "It is the largest of the eight" inside the 2.11 row, and closing "**The one to answer first is 2.11**, and it is the only one of the nine that stalls a phase. The other eight cost...". The 2.11 question read "The threshold ruling: the thresholds are wrong, or the quantities they apply to are".
Now:  The same section at ten, with the stale eights corrected, the 2.11 question restated as ruled on 2026-08-29 and still open on what it now waits on, and a paragraph recording that two of its counts read eight while the table held nine from 3.14 until this pass.
Why:  Two figures in this section went stale when 3.14 added the ninth row and nothing could see it. `stated-counts` named the obligations table's total, the count due at 4.1, the classification's three groups and the permits, and never this table, and a registry cannot catch a figure nobody registered. The 2.11 row was worse than stale: the ruling was taken on 2026-08-29 and recorded in the obligations row, while this section, which is where whoever plans phase 4 is sent to read, still pointed them at the question as unanswered. The heading's count is now derived from the table below it and the table is reconciled against the obligation rows due at the operator, so the two cannot part again.

### 2026-08-30 — BUILD_PLAN.md — cites The tight control set draws from any session sharing the market mood, and the loose set stays within the night
Was:  The 3.3 row ending at "It sits with the threshold ruling for the same reason: no build session can take it", due at the operator, and a row in the operator's table reading "May the tight control set draw from neighbouring sessions". The section heading read "### The ten that are the operator's", with "the operator's ten below", "Six of the ten block nothing at all today", "It is the largest of the ten", "the only one of the ten that stalls a phase" and "**The three that would move a number are 3.3, 3.5 and 3.11**".
Now:  The same row recording the ruling of 2026-08-30 and what it leaves owed, due at 3.6; the operator's table at nine without it; and every count in that section moved with it.
Why:  The operator ruled it. The tight set is declared to match on the trend ladder and the market mood, the second half had never been implemented because within one night it excludes nothing, and the choice was to make the dimension real or drop it. It is kept: the tight set draws from any session carrying the same mood label. That closes the judgement and leaves a draw, which is a build session's work, so the row moves to 3.6 where the other two instruments 3.6 needs already sit. It could only be ruled now: a tight set whose definition changes after a series has accumulated spends that accumulation twice, and at the ruling no setup had closed its ten-session horizon, so nothing is discarded.

### 2026-08-30 — CHANGELOG.md — correction to the entry of 2026-08-30 on the `ForwardDispersion` repoint
Was:  That entry cites "The minimum sample is derived from a measured dispersion and counted in effective observations", which sits under "Previously decided" in `DECISIONS.md`.
Now:  It should cite "The minimum sample is 262 effective observations, ratified at two points and 90% power", which is the live decision on the same quantity and the one the 3.15 sign-off cited when it took the ruling.
Why:  A citation on a CHANGELOG entry is load-bearing, and one resolving to a superseded decision points at reasoning that no longer holds, which is worse than a missing citation because it reads as authorised. `no-superseded-citation` did not catch it and was right not to: it reports citations inside a record as out of scope by design, twenty-five of them, because a dated record cites what was live when it was written. This one was not, on the day it was written. Recorded as a dated correction rather than by editing the entry, because this file is a record.

### 2026-08-30 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  A carried obligation row raised at 3.14 and due at 3.6, "The interval fixture cannot express a series whose nights differ in pair count, so the `DERIVED` tier asserts only the case where two different quantities agree", over a table of fifty-nine rows; "`fixtures/expectations.json` names ten frozen-only checkpoints"; "turns red ten times over"; and a 3.10 row reading "The ten landed checkpoints that contributed no fixture expectation at all ... 3.10, 3.13, 3.14 and 3.15 do the same ... for seven of the ten none of that has been established. **3.13, 3.14 and 3.15 are the exceptions**".
Now:  The row removed, the table at fifty-eight, the permit figures at nine, and the 3.10 row naming 3.10, 3.13 and 3.15 with 3.13 and 3.15 as the exceptions, plus a sentence recording that 3.14's permit was spent on 2026-08-30.
Why:  The obligation is discharged. `fixtures/interval-cases.json` now carries an optional `pairsByNight` and two scenarios that use it, both reusing an even scenario's series verbatim so the pair counts are the only thing that differs. `tools/derive-indicators.py --interval` reads the same field and carries the harmonic-mean arithmetic the shipped code has held since 3.14, which it did not: it computed `rows / design * serial` and agreed with the shipped code only because every scenario in the file had uniform pair counts. The two now agree to every place over uneven series, so the tier verifies the quantity rather than the corner where two different quantities coincide. 3.14's permit falls with it, because the expectation was owed at 3.14 and carried to the first pass that could produce it.

### 2026-08-30 — BUILD_PLAN.md — cites Phase 2 thresholds are calibrated once against nightly counts, before phase 3
Was:  The 2.11 row ending at "nothing in phase 4 may assume a trade will ever fire until the second quantity is found. Recorded at 3.14".
Now:  The same row carrying the result of the hunt of 2026-08-30: that there is no second wrong quantity in the sense the row assumes, that the long side has two and the short side none that any single relaxation reaches, and that the premise the two sides fail the same way is what breaks.
Why:  The 2.11 ruling of 2026-08-29 left the row waiting on the identification of a second wrong quantity. The hunt was run over the 602 calibration sessions and the answer is that the framing was wrong rather than that a quantity was found. Recorded on the row because the row is where the next reader looks, with the figures in the PROGRESS entry of the same date. No threshold moves and the once stays unspent.

### 2026-08-30 — ARCHITECTURE.html — cites The tight control set draws from any session sharing the market mood, and the loose set stays within the night
Was:  **Loose controls** match on liquidity and daily-range decile only. Comparing against these measures the whole funnel, thrust scan included.<br>**Tight controls** also match on the trend ladder and market mood. Comparing against these isolates the pullback checks, and answers the sharper question: is this pattern worth anything beyond simply owning stocks in uptrends?
Now:  The same two sentences with the session each set draws from named, followed by a paragraph stating that the tight set reaches across sessions, that the loose set does not, and what the asymmetry costs.
Why:  The document described a dimension the code did not have and could not have had: within one night the mood is a property of the session, so every candidate carries the same one and matching on it excludes nothing. The operator ruled the dimension is kept and made real, so the description now says which sessions each set draws from, which is the thing that changed.

### 2026-08-30 — ARCHITECTURE.html — cites The tight control set draws from any session sharing the market mood, and the loose set stays within the night
Was:  Draws matched control stocks nightly, loose and tight, five per set by deterministic nearest neighbour, before the cap so they answer for the flagged population rather than the kept sixty
Now:  The same, plus: The loose set draws from the setup's own session; the tight set draws from any session at or before it sharing the market mood, and each drawn row records the session it came from
Why:  The catalogue entry described a nightly draw with no session dimension, which is no longer what the stage does.

### 2026-08-30 — ARCHITECTURE.html — cites The tight control set draws from any session sharing the market mood, and the loose set stays within the night
Was:  ForwardReturnFiller records an outcome for every control drawn as well as every setup, over the control's own bars, from the flagging setup's session, signed by that setup's direction and expressed in the control's own range.
Now:  ForwardReturnFiller records an outcome for every control drawn as well as every setup, over the control's own bars, from the control's own session, signed by the flagging setup's direction and expressed in the control's own range.
Why:  "From the flagging setup's session" was true while every draw was within the night and became false the moment the tight set could reach an earlier one. Left standing it would have described a ten-day return measured from the wrong fortnight, which is not a shape any figure downstream could have shown.

### 2026-08-30 — SCHEMA.md — cites The tight control set draws from any session sharing the market mood, and the loose set stays within the night
Was:  Grain: setup + control ticker + set. Matched controls, drawn nightly, no API cost.
Now:  The same, plus a sentence saying the grain is unchanged after the tight set was allowed to reach across sessions, because where a name qualifies on several sessions the nearest is drawn and the others are not.
Why:  The grain reads as obvious and stopped being so. A tight pool holds a name once per session, so without the one-row-per-name rule a set of five could have been one name five times, which would have inherited exactly the idiosyncratic move five per set exists to avoid.

### 2026-08-30 — SCHEMA.md — cites The minimum sample is 262 effective observations, ratified at two points and 90% power
Was:  A `scoreboard` column table with no `n_sessions` and no `n_minimum_sessions` row.
Now:  Both rows, and a paragraph saying why the session count is stored beside the effective count rather than derived on the page.
Why:  Checkpoint 3.6 fires on two conditions and the store held one of them. The session count was computed on every night, discarded by the builder, and reached a reader only inside `withheld_because`, which is null the moment an interval exists.

### 2026-08-30 — RUNBOOK.md — cites Every phase ends in a generated phase report, not in a page somebody looks at
Was:  A weekly section ending at the scoreboard paragraph, with no step for a merge that carries a migration.
Now:  A paragraph stating both halves of the decision point's trigger as band 1 now renders them, and a new section "After a merge that carries a migration" giving the order: merge, then migrate, the same day, before the next slot.
Why:  The one fault nothing in the harness can catch, because every check takes its subject from the source, the documents, the fixture, or a store the check builds, and the running lab is in none of those. It cost the night of 2026-08-28 and this branch carries two more migrations.

### 2026-08-30 — BUILD_PLAN.md — cites Every fixture expectation records how it was produced, and only the independently derived ones verify anything
Was:  The fixture-permit paragraph ended at "So the first CI run after 4.1's PROGRESS entry lands turns red nine times over, and 4.1's own done condition 2 is `tools/ci.*` green." It named the block and stopped, saying nothing about how a permit is discharged and citing no precedent.
Now:  Three paragraphs after it: that the permits are discharged before 4.1's entry and nothing about 4.1 discharges them, with the by-construction reading named and refuted on its own terms; that 2.1(d) is the exact precedent, having discharged four permits by writing the expectations 1.3, 1.4, 1.5 and 1.7 owed; and that which of the two routes applies to each of the nine is the obligation's own work.
Why:  A paragraph that names a block and not its remedy leaves the remedy to be inferred, and it was inferred wrongly: that a permit is spent by phase-4 behaviour, so the permits close by construction when 4.1 lands. A permit is spent by an expectation over its own checkpoint's behaviour, and all nine landed in phases 1 to 3, so nothing about 4.1 bears on them. The section exists to put the decision in front of whoever plans phase 4, and it could not do that while the one row it calls mechanical had no stated way to be paid.

### 2026-08-30 — BUILD_PLAN.md — cites Long and short are never pooled into one figure
Was:  | 2.11 | **Ruled on 2026-08-29 and still open, on a different question.** The reading taken was that the once-only adjustment stays unspent and the second wrong quantity is what gets hunted; the 5 to 60 band is not re-derived. What is left for the operator is spending the once when a second quantity is identified, which is the half no build session can take | ... | **Phase 4 is buildable and untestable against live rows.** It is the largest of the nine, and since the ruling it is no longer waiting on an answer. The funnel at 3.9(i) names `exit-tight` as the gate the numbers point at, passing 1.29 per cent of 32,533 flagged long rows and 1.37 per cent of 16,917 short, an order of magnitude tighter than the next check on either side |
Now:  A row saying the question it waited on has been answered and answered past, that the decision left is whether the once is spent against a funnel with a known deferred clause or held until the gate runs as documented, and that the ruling of 2026-08-30 is **held until 4.4**. The figures are cited rather than copied. The middle column is unchanged.
Why:  The row waited on the identification of a second wrong quantity and that identification happened, so it described a question nobody was still asking. What the hunt found is not a quantity: the binding constraint is neither rarity nor the universe, and the gate that empties the short funnel runs two of its three disjuncts with the third deferred to 4.4. Spending the once against a gate about to change would calibrate a threshold to a definition that is going to move, and the once cannot be re-spent. The old third column quoted the unconditional exit-tight rates as the gate the numbers point at, which the conditional measurement supersedes on the short side, so it is not restated.

### 2026-08-30 — BUILD_PLAN.md — cites Long and short are never pooled into one figure
Was:  | 2.11 | **The short side's tightest gate runs two of its own three clauses until 4.4, and the long detector has no deferred clause anywhere.** `reached-ceiling` asks whether price is within half a daily range of the 21-day average, **or** the 50-day, **or** the declining average price anchored to the last swing high; the third is a volume-weighted average over minute bars and `VwapEngine` arrives at 4.4, so the check runs two disjuncts and records that it does. A disjunction missing a disjunct is strictly harder to pass, and this is the gate that takes the short funnel from 432 rows to 9 over 602 sessions, at 2.08%. Measured on 2026-08-30: short carries three hard gates in series where long carries one, and no single gate or pair lifts its median off nought, though one triple containing this gate reaches the band floor exactly. **What is owed is not a threshold**, which is the operator's, but the reading: 3.6 decides per direction and never pooled, and whoever reads the short side before 4.4 is reading a detector the corpus itself calls narrower than its specification, while the long side beside it is not. Either the short funnel is re-measured once the clause runs, or 3.6's short-side reading records that it was taken against two clauses of three. **Due at 3.6 rather than 4.4**, because 3.6 comes first and is where the number is read | 3.6 |
Now:  The same finding with the direction asymmetry as its stated subject rather than as its second half, the missing disjunct named, the threshold explicitly excluded as not owed here, a re-measurement of the short funnel with all three clauses running as the discharge condition, and the due point moved from 3.6 to 4.4.
Why:  Written due at 3.6 on the reasoning that 3.6 reads the short-side number first. That is a reason for 3.6 to read the row and not a point at which anything in it can be done: nothing discharges it until VwapEngine exists and the third clause runs, which is 4.4. A due point at which the work is impossible is the shape the operator's nine exists to name, one checkpoint earlier. The subject was also stated as the reading rather than as the asymmetry, which put a fact about the detector behind a fact about who reads it.

### 2026-08-30 — BUILD_PLAN.md — cites A frozen-only permit names an open obligation or the settled reason nothing could close it
Was:  **The one that blocks is the fixture permit.** `fixtures/expectations.json` names nine frozen-only checkpoints under `frozenOnly`, each resting on the obligation raised at 3.10, which falls due at 4.1. `fixture-replay` fails a permit whose due checkpoint `PROGRESS.md` already records, in those words: "that checkpoint shipped without discharging it and nothing said so at the time". So the first CI run after 4.1's PROGRESS entry lands turns red nine times over, and 4.1's own done condition 2 is `tools/ci.*` green.
Now:  The same paragraph with "each resting on the obligation" replaced by "of which three still rest on an open obligation", the red count moved from nine to three, and a paragraph after it recording that six were settled on 2026-08-30, naming them, and stating that the two counts are reported apart in the phase report.
Why:  A permit had one shape and rested on an obligation. Six of the nine are checkpoints no replayed market day could ever produce a figure for, being two spec-and-harness passes and four phase sign-offs, so resting them on an obligation gave each a due point that moves at every sign-off, which is permanent while reading as pending. The paragraph stated a red count of nine that was a count of permits rather than of permits that would fail, and after the shape changed those are different quantities.

### 2026-08-30 — BUILD_PLAN.md — cites A frozen-only permit names an open obligation or the settled reason nothing could close it
Was:  **The one that blocks is the fixture permit.** `fixtures/expectations.json` names nine frozen-only checkpoints under `frozenOnly`, of which three still rest on an open obligation, being the one raised at 3.10 which falls due at 4.1. ... So the first CI run after 4.1's PROGRESS entry lands turns red three times over, and 4.1's own done condition 2 is `tools/ci.*` green. Followed by a paragraph recording that six of nine had been settled that day.
Now:  The same paragraph saying the block is discharged, seven permits held and nought resting on an open obligation, with the red count nought against the nine it would have been before 2026-08-30; a paragraph naming which route each of the nine took and why; and the shape paragraph rewritten to describe a state rather than a change in progress.
Why:  The intermediate text was written when six of nine were settled and three were still being read. All nine are now discharged, two by contributing the expectations they owed and seven by establishing that no replayed market day could produce a figure. A paragraph that says a thing blocks, when it no longer does, is the failure this section exists to prevent one level up.

### 2026-08-31 — SCHEMA.md — cites A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
Was:  The calibration section held `calibration_setup` alone, whose note says a historical run's rows are not evidence and that nothing downstream reads them.
Now:  Two tables after it, `calibration_control_setup` and `calibration_forward_return`, with their columns, their writers, and the reason the excursion columns are nullable there and NOT NULL on the evidence side.
Why:  A reconstructed read needs matched controls and forward outcomes, and both had to go somewhere. A `source` column on the evidence tables was the alternative and is worse: it puts reconstructed rows one predicate away from every read the scoreboard makes, and the rows are real returns of real names, so nothing about their shape says which population they came from. Separate tables make the mixing impossible to write rather than merely wrong.

### 2026-08-31 — BUILD_PLAN.md — cites The tight control set draws from any session sharing the market mood, and the loose set stays within the night
Was:  The carried obligations table held fifty-seven rows, nine of them due at the operator. The classification's opening sentence read "Twenty-eight of the fifty-seven rows above fall due at 4.1" and "named for the operator's nine below"; the section heading read "### The nine that are the operator's"; the sentence under it read "Six of the nine block nothing at all today and are cheap to leave open; three sit in front of work that is already scheduled"; and the closing paragraph read "which leaves it the only one of the nine that stalls a phase and no longer the one that stalls for want of a ruling" and "3.3 was a third until the operator ruled it on 2026-08-30".
Now:  Fifty-eight and ten, with each of those figures moved, six blocking nothing against four in front of scheduled work, 3.3 named as a third row that would move something and the one that decides whether 3.6's tight half is reachable at all, and the closing sentence separating a row that stalls a phase from one that stalls a checkpoint.
Why:  A row was added to the obligations table and to the operator's own list. `stated-counts` derives the total, the count due at 4.1 and the operator's heading count from the tables themselves, so those three could not have gone stale; the sentences carrying the same figures in prose are not registered and had to move by hand, which is the instance the section's own paragraph about unregistered figures describes.

### 2026-08-31 — BUILD_PLAN.md — cites The tight control set draws from any session sharing the market mood, and the loose set stays within the night
Was:  "**It was nine until 3.9(b), eight until 3.14, nine again, ten on 2026-08-30 and nine by the end of that day.**", followed by a paragraph recording that 3.3 left because the operator ruled it.
Now:  The same sentence ending "nine by the end of that day, and ten again on 2026-08-31", and a paragraph after the departures one recording that 3.3 returned on the other side of the same ruling: the ruling stated what the reach costs, the reconstructed read of 2026-08-31 measured it, and the measured cost is a different judgement from the one that closed.
Why:  A list that silently grows reads the same as one nobody is working through, which is the reason the departures were named in the first place. A row that leaves on a ruling and comes back on that ruling's measured cost is the case a reader would otherwise take for a reversal, and it is not one.

### 2026-08-31 — ARCHITECTURE.html — cites The tight control set draws within the night, because a within-night draw controls the market mood exactly
Was:  "**Tight controls** also match on the trend ladder and market mood, and are drawn from *any* session at or before the setup's that carries the same mood label", above a paragraph headed "**The tight set reaches across sessions and the loose set does not, and the asymmetry is the point.**" That paragraph argued that matching on the mood inside a night is a comparison true by construction, that the operator ruled on 2026-08-30 to keep the dimension and make it real, and that what it costs is a setup and its tight controls coming from different sessions so the market factor common to one night no longer cancels. It called that trade a matched dimension bought with a comparison across time, taken because the alternative is a tight set differing from the loose one by the trend ladder alone.
Now:  Both sets drawn from the setup's own session, above a paragraph headed "**Both sets stay within the night, and the trend ladder is the only thing separating them.**" It states that the mood clause holds on every row and excludes nobody, that this is a dimension controlled exactly rather than one left unchecked, that the trend ladder is a property of the name and does exclude, and that the reach existed for one day and its cost was measured: 0.3718 and 3.40 for 428 effective observations on the loose panel against 0.1108 and 6.71 for 65 on the tight one, over identical rows and nights, putting band 1's minimum sample out of reach on every schedule the plan had, and that the same tight panel drawn within the night reads 0.2463 and 3.51 for 275.
Why:  The reach was reversed by a decision on 2026-08-31 and this document described it as current. The old paragraph was not wrong about the cost — it named it in the sentence beginning "what it costs is stated rather than left to be discovered" — but it stated the cost as a trade accepted rather than as a quantity, and once measured the trade is the wrong side of itself. The figures are carried here rather than left in the record because this is the page that explains what a control set is, and the reason one shape was chosen over the other is part of that.

### 2026-08-31 — SCHEMA.md — cites The tight control set draws within the night, because a within-night draw controls the market mood exactly
Was:  The `control_as_of` column read "the control's **own** session, which is the setup's for a loose draw and may be an earlier one for a tight draw", under a note beginning "`control_as_of` is the session a control's outcome is measured from, and it stopped being derivable on 2026-08-30", which explained the reach, why `ForwardReturnFiller` could no longer read a control's session off its setup, and that migration 035 backfilled every existing row.
Now:  The column reads that the session is the setup's on every row of both sets and could be an earlier one for one day only, under a note headed "**The reach it was added for was tried, measured and reversed, and the column stays.**" The note keeps the whole account of why the column was needed, records the reversal and what it cost, and gives two reasons the column and the migration are kept: the equality is a fact worth stating rather than inferring from a join, asserted by `ControlSamplerTests.A_tight_control_is_drawn_from_the_subjects_own_session`; and a reach that was tried and reversed argues for keeping the instrument that measures it.
Why:  A schema note saying a value "stopped being derivable" describes a state that lasted one day. Removing the column instead would delete the record of why it exists and the only thing that now asserts the invariant, and it would have to be added back by a second migration if the reach ever returns. The reasoning for the reach is kept intact rather than deleted, on the same grounds a superseded decision keeps its reasoning.

### 2026-08-31 — BUILD_PLAN.md — cites The tight control set draws within the night, because a within-night draw controls the market mood exactly
Was:  The carried obligations table held fifty-eight rows, ten of them due at the operator, with a 3.3 row asking whether the tight set keeps its across-session reach and a matching row in the operator's own list. The counts read fifty-eight and ten in the classification's opening sentence, in "named for the operator's ten below", in the section heading, in "Six of the ten block nothing at all today ... four sit in front of work that is already scheduled", and in the closing paragraph, which named 3.3 as a row that stalls a checkpoint rather than a phase. A paragraph headed "**3.3 returned the next day, on the other side of the same ruling**" recorded the row reopening.
Now:  Fifty-seven and nine, both rows gone, every count moved back, and the departures paragraph replaced by one headed "**3.3 left, returned and left again, and the three movements are one argument working itself out.**" It records the ruling of 2026-08-30, the measurement of 2026-08-31 that reopened the question with its figures, and the ruling of 2026-08-31 that closed it, and states that a row arriving with a measurement and leaving with the ruling that measurement asked for is not churn.
Why:  The row was raised so the operator would have to answer it and the operator answered it the same day. Removing it silently would leave the table correct and the record blank for a day on which the project's central comparison was redefined, which is the failure the departures paragraph exists to prevent one level up. The full arc is written out rather than netted, because the net is that nothing happened.

### 2026-08-31 — BUILD_PLAN.md — cites 3.6 gates what may be admitted, not what may be built
Was:  "- **3.6 still has to happen.** It is the project's own question and phase 4 should not start without its answer." 3.6's row read "**The decision point. Parked, and not a sign-off condition**", and its done condition ended "Then a person reads the scoreboard and decides whether to continue."
Now:  The bullet says 3.6 gates admission rather than construction, that building the trading layer, the variant machinery and the research loop does not wait on it because none of that apparatus encodes the baseline's thresholds, and that admitting a variant, changing a rule and spending a holdout window do wait on it for the direction concerned. The row and the done condition carry the same distinction and cite the decision.
Why:  The sentence was written when 3.6's timing was unknown and deliberately not estimated. It now has a measured range of five to twenty-two months, and holding twenty-nine checkpoints behind it for that long is not what the sentence was defending against. What it was defending against is tuning a rule that has no edge, and that is admission rather than construction: VariantAdmitter, ReplayHarness, HoldoutRegistry and the phase 6 loop are machinery for testing any rule, so if the baseline fails they are the instrument that finds a replacement. Corrected in place rather than deleted, because the thing it protects is real and is now named precisely.

### 2026-08-31 — BUILD_PLAN.md — cites 3.6 gates what may be admitted, not what may be built
Was:  Phase 4's table opened 4.1, 4.2, 4.3, and 4.1's done condition read "**Openable, and first in this phase on purpose.** Plans do not exist yet, so it renders the flagged setups with their computed trigger, stop and distance."
Now:  The table opens 4.2, 4.3, 4.1. 4.2's done condition carries the reason: it is the only checkpoint of the twenty-nine remaining whose value depends on when it starts, because minute bars exist only from the night capture begins, both directions enter intraday, and no historical route substitutes. 4.3 carries the same argument for spreads. 4.1 reads "**Openable, and third rather than first**", records that it led the phase until 2026-08-31, and keeps everything else it said.
Why:  A page loses nothing by waiting and a capture loses a night every night it does not run. The vendor sells daily history, so a minute bar not captured on its own evening cannot be bought later, which is the argument the universe snapshot already carries in this file. Nothing else in the phase moved: 4.1 is still the surface band 1 reports on and still the surface every later checkpoint shows up on, and being third costs it nothing.

### 2026-08-31 — BUILD_PLAN.md — cites An approved proposal creates a new version from zero, and a running version is never edited
Was:  Twenty-eight rows of the carried obligations table named 4.1 as their due point. The classification section was headed "### What the twenty-eight due at 4.1 are", opened "Twenty-eight of the fifty-seven rows above fall due at 4.1", grouped them 0 / 5 / 23, said "**What has not moved is the twenty-three below**, and choosing their due points is still the decision this section does not take", and closed "choosing it is the decision, and it belongs to whoever plans phase 4 with these three groups in front of them".
Now:  Two rows name 4.1, both of them 4.1's own subject. The heading, the opening sentence and the group counts read two and 0 / 2 / 0. The section records the decision as taken rather than handing it over, with a table of the eight destinations and the reason for each, and a paragraph naming the ten that fall before the baseline is frozen as the finding.
Why:  A due point chosen as "the next one" is a due point nobody chose, which is the section's own sentence, and it stayed true for twenty-eight rows across three phases. The rule applied is that a due point names the checkpoint whose own work the row bears on, so discharging it is part of that checkpoint rather than an errand attached to it; no destination was invented, every one already existed with its own deliverable. Ten of the twenty-three turned out to share a hard deadline nobody had noticed: each changes a stored figure, and a stored figure repaired after V0 is registered closes every open version as unresolved and starts a new generation.

### 2026-08-31 — BUILD_PLAN.md — cites Replay screens proposals and the forward paired test admits them
Was:  "| 5.3 | ReplayHarness | Re-filtering the stored history with a new rule completes in seconds and reproduces the baseline's own selections exactly |", and the paragraph below the table read "**5.3's acceptance test is the important one:** running the baseline's own rule through the harness must reproduce the baseline's historical selections exactly. If it does not, the harness and the live detector disagree and every replay result is worthless."
Now:  The deliverable names the limit, the done condition scopes the acceptance test to selection and states its cause, and both say that nothing screens an execution variant instead: it is admitted on forward evidence alone. The paragraph is scoped to the selection half in its opening sentence.
Why:  The row read as covering the harness generally while its acceptance test covered selection, which is the sixth failure shape one level up: a correct assertion whose scope is wider in the label than in the thing asserted. The cause is a hole in the stored history rather than a choice, so it does not expire: an execution variant is scored on R, R needs fills, fills need minute bars, and minute bars exist only from the night 4.2 starts capturing them. The nights that do have them are the forward-collected ones, so replaying an execution rule over them is the forward paired test arriving slowly rather than a screen run before it, which leaves the screening half of the cited decision empty for one of the two families.

### 2026-08-31 — BUILD_PLAN.md — cites An approved proposal creates a new version from zero, and a running version is never edited
Was:  The ten obligations repointed to 5.1 earlier the same day each carried the reason that obligation bears on the baseline, and none carried the freeze mechanic itself; that was stated once, in the classification section. 5.1's done condition read "Baseline V0 registered and frozen".
Now:  Each of the ten carries the freeze reason in its own cell, in the same words. 5.1's done condition puts the discharge of the ten first and says the order is the condition rather than a preference. A paragraph above 5.3's acceptance test states the ordering consequence: an obligation due at 5.1 is due before 5.1's done condition rather than alongside it, so the ten sit between 4.13 and 5.1 and are phase 4's real tail.
Why:  A row is read alone when someone picks it up, and the reason ten unrelated repairs share a deadline is not visible from any one of them. Stating it only in the classification section put it where a reader planning the work would not be looking. The ordering half was worse than absent: "due at 5.1" reads as work done during 5.1, and 5.1's own deliverable is the freeze, so the natural reading schedules the ten after the moment that makes them uncorrectable.
