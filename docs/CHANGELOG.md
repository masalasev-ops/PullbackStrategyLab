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
