# BUILD_PLAN.md

Six phases. Each checkpoint states its deliverable, its done condition, and how it is verified. The general definition of done is in `CLAUDE.md` under "Definition of done for a checkpoint" and applies on top of everything here.

Cross-document references cite a heading, never a section number, for the same reason decisions are named rather than numbered: a misremembered number resolves to the wrong place silently.

Every phase produces two things: a screen you can open, and a generated report saying whether what was built matches the architecture. Neither replaces the other. The screen is how you notice something looks wrong; the report is how you know, and how a build session checks its own work without asking anyone.

The harness is checkpoint 1.7, built before most of what it checks exists. From there every checkpoint adds its expectations to the golden fixture, and every phase sign-off requires `tools/verify-phase` green with nothing listed as unexamined.

---

## Phase 1 — Ingest and charts

Nothing here depends on any open question. Start immediately.

**`git init` before 1.1.** There is no repository yet, and the done conditions, the progress entries and the merge rule are all written against commits.

| # | Deliverable | Done when |
|---|---|---|
| 1.1 | Solution `PullbackStrategyLab.sln` under `/src`: Core, Data, Worker, Api, Web, Tests, all namespaced `PullbackStrategyLab.*`. Clock abstraction, config binding via one shared extension, store pragmas set in that same extension, `RunLogger` as sole writer of `run_log` | `git init` done first. `tools/ci.*` green. Api has no transitive reference to Worker, asserted against compiled deps, with `PullbackStrategyLab.Tests` the one declared exemption. **The CI matrix runs windows and macos from this commit onward**. Config is wired by one shared extension used by Worker, Api and Tests alike, pinned by two tests: an environment variable overrides a value present in `appsettings.Secrets.json`, and a project starts cleanly with no secrets file on disk (see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables) |
| 1.2 | Clock abstraction proven on both platforms | A test resolves `America/New_York` and asserts it is behind UTC by at most a day, with both bounds read from the clock. The matrix already exists from 1.1, so this checkpoint adds the test rather than the runners (see: Every line of code runs unmodified on Windows and on Apple Silicon macOS) |
| 1.3 | UniverseBuilder from the exchange symbol list, common stock only, $5 floor and the liquidity floor | `universe_member` populated. **The survivor count is measured and recorded in PROGRESS**, since the backfill call budget is one call per survivor and no figure for it exists yet. `universe_snapshot` written per night |
| 1.4 | DailyBarIngestor, one bulk request per night, idempotent | Re-running the same date changes no row. Bar count and date range recorded. A test asserts the connection reports `journal_mode=wal` and `foreign_keys=1`, since both are silently off by default |
| 1.5 | ActionIngestor, and the EMA rebuild-on-split path | A synthetic split fixture forces a full recompute for that ticker and only that ticker |
| 1.6 | IndicatorEngine: EMA 9/21/50, ADR20, ATR14, median dollar volume | Values for three hand-picked tickers match an independent calculation to 4 decimal places. **The independent calculation and its source are recorded in PROGRESS**, and those values carry forward as the fixture's first `DERIVED` expectations at 1.7 rather than being checked once and discarded (see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything) |
| 1.7 | **Phase report harness** and the golden fixture: real bars for 30 tickers over 250 sessions, committed, with expected indicator values beside them, **each carrying its tier**. `tools/verify-phase` runs the pipeline over the fixture, diffs against expectations, parses the architecture tables, located by heading text rather than by position, and asserts each claim against the code, then writes `artifacts/phase-report.html` and `.json` | **Openable, and machine-readable.** Report shows three sections: doc conformance, fixture diff, coverage. Every architecture claim is pass, fail, or **unexamined**, and unexamined is not a pass. The fixture diff is broken down **by tier** rather than reported as one number, and the report states how many expectations changed since the last commit alongside how many passed. **The fixture's bars are `CAPTURED` from real vendor responses**, which the backfill at 1.6 makes possible, and the synthetic split case from 1.5 is kept and marked `AUTHORED`. **The report breaks inputs down by tier beside expectations**, and a path with no captured input reads as unexamined however many authored cases pass (see: Fixture inputs record where they came from, and a path a live run exercises needs a captured one). **1.7 back-fills expectations for 1.1 to 1.6 with their tiers**, discharging the obligation raised at 1.1, since those checkpoints predate the fixture and could not meet done condition seven when they landed. From here on every checkpoint adds its expectations to the fixture (see: Every phase ends in a generated phase report, not in a page somebody looks at) (see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything) |
| 1.8 | Web shell: routing, layout, the five-item nav, the status band, and one shared candlestick component | **Openable.** Every page is reachable and renders its empty state. Nothing is stubbed with fake data, because an empty page that says so is honest and a page of invented rows is not |
| 1.9 | IndexIngestor | `index_bar` populated for SPY, QQQ, IWM |
| 1.10 | Chart page: any ticker, candles, three averages, ADR readout | **Openable.** You compare it against a chart you already trust and agree |
| 1.11 | Migration rehearsal end to end | `RUNBOOK.md` "Moving the store to another machine" executed on a second machine, all ten steps, counts matched. Result recorded in PROGRESS |
| 1.12 | Phase sign-off | Fresh session reviews. Findings batched into one pass, and `tools/verify-phase` green with nothing unexamined |

**1.11 is not optional and not deferred.** It is cheap now and it is how you discover which paths are hardcoded and which secrets do not travel, at a point where losing the store costs nothing.

---

## Phase 2 — Detection

| # | Deliverable | Done when |
|---|---|---|
| 2.1 | `SIGNALS.md` section of SCHEMA: every signal, its formula, its source columns | Every signal traces to named stored columns. Any that does not is a finding, not an assumption |
| 2.2 | SignalVectorizer and the frozen signal row shape, `setup_signal` | Written once, never updated by the vectorizer. Asserted |
| 2.3 | ScanEngine, six scans, three per direction | Hit counts per scan per night recorded |
| 2.4 | TierClassifier | Ladder grade on every universe member per night |
| 2.5 | RegimeLabeler, two scores summed | Both raw scores stored alongside the label. Label filters nothing |
| 2.6 | LongSetupDetector, ten checks, all results recorded, with SectorResolver and ThemeClusterer behind the cluster check | `check-completeness` passes: every recorded setup has a result for every check |
| 2.7 | ShortSetupDetector, ten checks | Same. Plus: no setup row carries a direction its detector does not own |
| 2.8 | SetupCapper, 60 a night, 40 long 20 short, unused slots released | Truncation recorded with the pre-cap count |
| 2.9 | Setup inspector, the gallery page: prev/next, filter by failed check, agreement capture | **Openable.** You page through a night's setups by keyboard and record agree or disagree per setup |
| 2.10 | Point-in-time test | A deliberately future-dated column causes a loud failure. The test is permanent, not a manual break-and-revert |
| 2.11 | One-time calibration | Detector run over the full stored history **into `calibration_setup`, never into `setup`** (see: The evidence store holds only setups flagged forward, never setups reconstructed from history). Nightly count distribution inspected. If the median falls outside 5 to 60 per side, thresholds adjusted **once**, before phase 3. Recorded as a dated event with before and after counts. A test asserts `setup` is still empty when calibration finishes |
| 2.12 | Phase sign-off | Fresh session. The gallery review is part of it, and `tools/verify-phase` green with nothing unexamined |

**2.11 is calibration, not tuning.** At this point no forward return exists anywhere in the store, so there is nothing to fit toward. It is a row count and nothing else.

**And the rows it counts are not evidence.** A historical detector run has no record of who was listed on those dates, because the nightly universe snapshot only starts when the lab does. Delisted names are absent, so every reconstructed setup carries survivorship bias. Counting is unaffected; measurement would be destroyed. The rows go to `calibration_setup` and the evidence store stays empty until the first forward night. After phase 3 begins, these thresholds move only through the normal proposal route.

**2.9 is where the real time goes,** and it should. Paging through the gallery and disagreeing with the detector is the actual transfer of the strategy into code.

---

## Phase 3 — Measurement, and the decision point

| # | Deliverable | Done when |
|---|---|---|
| 3.1 | SetupJournal finalised | Setup rows immutable after write. Asserted |
| 3.2 | ForwardReturnFiller: 1, 3, 5, 10 sessions, signed by direction, plus MFE and MAE in ATR | Holiday handling correct: next trading day used, actual date stored beside the intended horizon |
| 3.3 | ControlSampler: loose and tight control sets per flagged setup | Controls drawn from names that cleared the liquidity floor and were not flagged. Match quality recorded |
| 3.4 | CeilingCalculator | Bound computed from the actual outcome distribution, recomputed weekly |
| 3.5 | ScoreboardBuilder and the Lab scoreboard page, bands 0, 1 and 2 | **Openable.** Flagged against both control sets with confidence intervals, rank-decile curve, ceiling gap |
| 3.6 | **The decision point** | Three months of accumulation, then read the scoreboard and decide whether to continue |
| 3.7 | Phase sign-off | Fresh session, and `tools/verify-phase` green with nothing unexamined |

**3.6 is the project's own question, answered.** If the tight-control comparison does not clear zero, the pattern has nothing in it beyond owning stocks in uptrends and everything built after this point would be optimising noise. If the ceiling gap is near zero, selection has no room and the loop should point at execution instead. Either answer is worth having before phase 4 starts, and both arrive before the trading and research layers exist.

---

## Phase 4 — Trading

| # | Deliverable | Done when |
|---|---|---|
| 4.1 | **Watchlist page** and WatchlistPublisher, long and short in divided panels, ranked, failed checks greyed with the failing check named, conflict banner | **Openable, and first in this phase on purpose.** Plans do not exist yet, so it renders the flagged setups with their computed trigger, stop and distance. Everything built after this checkpoint shows up on a page you already have rather than in a log |
| 4.2 | IntradayFetcher, every flagged setup | Minute bars stored for all 60, not only the planned ones |
| 4.3 | SpreadSnapshotter, twice per session | The only intraday job. 120 calls. Failure to capture is unrecoverable and is logged as such |
| 4.4 | VwapEngine, session and anchored | Values match an independent calculation on a fixture day |
| 4.5 | TriggerResolver over SessionReplayClock, point in time enforced within the day | A test proves the resolver cannot see a later minute than the one it is evaluating |
| 4.6 | RiskGate, all six caps, both directions, sizing off fixed notional equity | Blocked orders written with reason, including the contention case. A test asserts that when two plans trigger and only one slot remains, the earlier trigger fills and the later is blocked (see: Plans are resting orders and fills go in time order when the caps bind). `order-provenance` passes |
| 4.7 | PaperBroker and the fill model | Gap-through recorded as a loss greater than 1R and tagged. Never clamped |
| 4.8 | PositionManager, two rule sets | Long trail and short trim are separate code paths, not one routine with a sign flag |
| 4.9 | PlanAudit and TradeJournal | Planned stop against executed stop recorded on every trade |
| 4.10 | LossClassifier, four causes | Every closed loss classified. Unclassifiable is itself a recorded category, not a silent skip |
| 4.11 | Trade journal page, split long and short | **Openable.** Intraday chart per trade with trigger, fill, stop and exit drawn |
| 4.12 | Retire the mockups | `SCREENS.html` deleted (see: The corpus is eight documents plus one artefact, and a ninth requires retiring one). Any layout detail it carried that the built UI relies on has moved into the built UI, not into another document |
| 4.13 | Phase sign-off | Fresh session, and `tools/verify-phase` green with nothing unexamined |

---

## Phase 5 — Variants, without any AI

| # | Deliverable | Done when |
|---|---|---|
| 5.1 | `variant` store, VariantAdmitter registering it and VariantResolver deciding which are live, PlanBuilder produces one plan per live variant | Baseline V0 registered and frozen |
| 5.2 | VariantScorer, two families scored differently | Selection on forward return, execution on R. A variant spanning both is rejected at admission |
| 5.3 | ReplayHarness | Re-filtering the stored history with a new rule completes in seconds and reproduces the baseline's own selections exactly |
| 5.4 | HoldoutRegistry, eight quarterly windows | Spent windows recorded with date range, purpose and outcome. A spent window cannot be re-spent |
| 5.5 | Research ledger page | **Openable.** Variants, samples, targets, status |
| 5.6 | Two hand-written variants per family running | Paired differences accumulating and visible |
| 5.7 | Phase sign-off | Fresh session, and `tools/verify-phase` green with nothing unexamined |

**5.3's acceptance test is the important one:** running the baseline's own rule through the harness must reproduce the baseline's historical selections exactly. If it does not, the harness and the live detector disagree and every replay result is worthless.

---

## Phase 6 — The loop

| # | Deliverable | Done when |
|---|---|---|
| 6.1 | SignalBackfiller | A new signal computes across the entire stored setup history in one run |
| 6.2 | SignalAdmissionTest | A signal is admitted only if it tightens outcome-similar neighbourhoods, and rejected above 0.70 correlation unless stored as a residual |
| 6.3 | TwinPairFinder | Z-scored over the trailing 250 setups. Pairs under 0.5 distance with outcomes over 15 points apart |
| 6.4 | ContextPacker, versioned, with the planted null signal | Pack version recorded on every proposal. The null appears in the conditional tables |
| 6.5 | ResearcherSeat, built against the narrow interface, with both implementations present and selected by configuration | A test asserts the subscription implementation is constructed with an **empty tool set**. A test asserts `ANTHROPIC_API_KEY` is absent from the environment. A proposal row carries the **configured model**, the **transport** used and the **served model** string the response reported. Budget or plan-limit exhaustion queues the job and returns nothing, on both paths, and never a partial proposal (see: The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface) |
| 6.6 | ProposalRegistry, rule proposals and signal requests | Abstention is a valid recorded outcome, not a failure |
| 6.7 | AcceptanceGate | Reads only status and resolution date. Cannot touch a target |
| 6.8 | Scoreboard band 3 and the Pack comparison page | **Openable.** Proposal hit rate by pack version |
| 6.9 | Phase sign-off | Fresh session, and `tools/verify-phase` green with nothing unexamined |

**6.5 has a trap, and it survives the transport switch.** `ANTHROPIC_API_KEY` stays banned from the environment on both paths, for a reason that now applies to each of them: on the subscription path its presence silently defeats plan auth and bills API rates, and on the API path the key belongs in configuration like every other secret, so one in the environment means two places supply the same credential and nothing on the surface says which won. Assert its absence at startup and fail loudly.

**A proposal citing the planted null signal fails that pack version immediately.** No proposal from that version is admitted.

---

## Carried obligations

Findings that did not block a checkpoint. Each names the checkpoint at which it falls due.

| Raised | Obligation | Due at |
|---|---|---|
| 1.1 | Expectations for 1.1 to 1.6 owed to the golden fixture with their tier. Done condition seven cannot be met before the fixture exists | 1.7 |
| 1.6 | Both phase 1 defects that passed a unit test and failed live are re-read against the input tiers: each would show as unexamined rather than green, because the path had no captured input. If either would still read as green, the tier definition is wrong rather than the defect being unlucky | 1.7 |
| 1.6 | One `CONFIRMED` value per hand-picked ticker, checked against a charting platform's own readout. The derivation at 1.6 is a second implementation by the same author, which rules out a transcription error and not a shared misreading of a definition. The three values and the exact formula they were computed under are recorded in PROGRESS so the comparison is unambiguous | 1.7 |
| 1.6 | Indicator rows already written for past sessions are not recomputed when a rebuild lands, so a row computed on the old basis stands. `indicator_daily` is keyed on ticker and date with no observation, and SCHEMA declares TierClassifier as its only updater, so the fix is a decision about the store's shape rather than a code change | 1.7 |
| 1.9 | RUNBOOK's step 5, the split history for every survivor, is a second 2,070 calls and has not run. Nothing depends on it: splits arrive nightly from the bulk endpoint, so what is missing is only the history of splits from before the lab started | 1.7 |

---

## Elapsed time

Roughly 35 to 45 coding sessions. The calendar is set by accumulation, not by code: phase 3 needs about three months of running before phase 5 has anything to score, and phase 5 needs a few months more before proposals settle. Expect the loop to close once around month seven.
