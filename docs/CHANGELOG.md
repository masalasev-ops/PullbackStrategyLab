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
