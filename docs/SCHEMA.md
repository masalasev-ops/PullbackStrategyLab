# SCHEMA.md

Every store, its grain, and its declared writer per operation. The conformance test asserts this file against the code in both directions: every declared writer exists, and every writer in code is declared.

Complete for phases 1 to 3. Phases 4 to 6 are declared at store level with columns owed at their checkpoint, because writing exact columns for machinery that does not exist yet produces a document that is wrong in ways nobody notices.

**Conventions.** Prices and money are `TEXT` holding a decimal, never `REAL`. Timestamps are `TEXT` in ISO-8601 UTC. Dates are `TEXT` as `YYYY-MM-DD`. Ratios are stored as fractions, not percentages, with one exception noted at `adr_20`. Booleans are `INTEGER` 0 or 1.

---

## Reference

### `security`
One row per listed instrument, ever. Grain: ticker.

| Column | Type | Note |
|---|---|---|
| `ticker` | TEXT PK | vendor dash form |
| `name` | TEXT | |
| `exchange` | TEXT | |
| `type` | TEXT | common stock only survives the universe filter |
| `first_seen` | TEXT | date first observed in the symbol list |
| `sector` | TEXT NULL | resolved lazily on first flagging, SectorResolver |
| `industry` | TEXT NULL | |
| `market_cap` | TEXT NULL | refreshed quarterly. Short side only |
| `sector_resolved_at` | TEXT NULL | |

Insert UniverseBuilder · Update SectorResolver (`sector`, `industry`, `market_cap`, `sector_resolved_at` only)

### `universe_member`
Current tradable set. Grain: ticker.

| Column | Type | Note |
|---|---|---|
| `ticker` | TEXT PK | |
| `added_on` | TEXT | |
| `removed_on` | TEXT NULL | membership is state, not a filter |

Insert UniverseBuilder · Update UniverseBuilder

### `universe_snapshot`
Who was listed on a given night. Grain: date + ticker. Append-only. This is what makes replay free of survivorship bias, so it is written every night without exception.

| Column | Type |
|---|---|
| `as_of` | TEXT |
| `ticker` | TEXT |

Insert UniverseBuilder · PK (`as_of`, `ticker`)

---

## Market data

### `daily_bar`
Grain: ticker + date. **Append-only. Never deleted, never updated.** A vendor correction arrives as a new row with a later `observed_at`; reads take the latest `observed_at` at or before the as-of date.

| Column | Type | Note |
|---|---|---|
| `ticker` | TEXT | |
| `bar_date` | TEXT | |
| `open`,`high`,`low`,`close` | TEXT | raw, as traded |
| `adj_close` | TEXT | split and dividend adjusted |
| `volume` | INTEGER | raw |
| `observed_at` | TEXT | |

Insert DailyBarIngestor · PK (`ticker`, `bar_date`, `observed_at`)

**The trap this exists to prevent.** Averages and ranges are computed on adjusted prices, so a split five years ago does not poison them. Trigger and stop prices in a plan are raw prices, because that is what trades tomorrow. Mixing them produces a plan that says buy at 37.67 when the real price is 150.68, and it is silent because both numbers look reasonable.

### `corporate_action`
Grain: ticker + effective date + type.

| Column | Type | Note |
|---|---|---|
| `ticker` | TEXT | |
| `effective_date` | TEXT | |
| `type` | TEXT | `split` or `dividend` |
| `ratio` | TEXT | on a split, new shares over old as a factor, so 4 for a four-for-one. On a dividend, cash per share |
| `observed_at` | TEXT | |

Insert ActionIngestor · PK (`ticker`, `effective_date`, `type`, `observed_at`)

*Note on `ratio`.* It carries a factor for one type and a money amount for the other, which is the one column in this schema whose name does not describe half its contents. It stays one column because the grain already separates the two by `type` and a second nullable column would put the same fact in two places, but the note stays here so nobody averages the column.

**Append-only. Never deleted, never updated,** on the same terms as `daily_bar` and for the same reason. Vendors restate corporate actions. A restatement arrives as a new row with a later `observed_at`, and reads take the latest `observed_at` at or before the as-of date, so a ratio revised on Thursday does not change what Monday's replay sees.

**A restatement raises a rebuild demand of its own.** Whatever was computed against the old ratio was computed against a number the vendor no longer publishes, and the demand is keyed on the observation rather than on the action, so the new one stands beside the old rather than trying to reopen it.

### `history_refetch`
Grain: ticker + the instant its whole series was re-observed. Append-only, one row per refetch.

| Column | Type | Note |
|---|---|---|
| `ticker`, `refetched_at` | TEXT | PK |
| `from_date`, `to_date` | TEXT | the window asked for |
| `bars_written` | INTEGER | how many bars actually changed, which is often zero and is not what the row is for |

Insert DailyBarIngestor

**The row is written even when nothing changed.** The fact anybody downstream needs is that the series was looked at, not that it moved, and those are different facts (see: A rebuild is satisfied by a recorded refetch, not by inferring one from what changed).

**This is what satisfies a rebuild demand.** IndicatorEngine reads the latest refetch of a ticker at or before the as-of date and treats every demand observed at or before it as accounted for.

### `index_bar`
Grain: symbol + date + `observed_at`. SPY, QQQ, IWM. Same shape and the same terms as `daily_bar`: **append-only, never deleted, never updated**, a correction arriving as a new row with a later `observed_at`, and reads taking the latest observation at or before the as-of date.

Insert IndexIngestor · PK (`symbol`, `bar_date`, `observed_at`)

**No foreign key to `security`.** A tracker is not part of the tradable universe and never appears in a screen. It is read to say what the market did, which is a different question from what any one stock did.

### `intraday_bar`
Grain: ticker + minute. Phase 4. Fetched for every flagged setup, not only planned ones, because a variant selecting a name the baseline passed on must still be resolvable.

| Column | Type |
|---|---|
| `ticker`, `bar_ts`, `open`,`high`,`low`,`close`, `volume` | |
| `vwap_session` | TEXT, written by VwapEngine |

Insert IntradayFetcher · Update VwapEngine (`vwap_session` only)

### `spread_snapshot`
Grain: ticker + snapshot time. Phase 4. **Unrecoverable if missed.** The only intraday job.

| Column | Type |
|---|---|
| `ticker`, `snapshot_ts`, `bid`, `ask`, `spread_bps` | |

Insert SpreadSnapshotter

---

## Computed

### `indicator_daily`
Grain: ticker + date. Computed locally from `daily_bar`, never requested from the vendor.

| Column | Type | Note |
|---|---|---|
| `ticker`, `as_of`, `computed_at` | TEXT | PK |
| `ema_9`, `ema_21`, `ema_50` | TEXT | on adjusted close |
| `atr_14` | TEXT | |
| `adr_20` | TEXT | **fraction**, so 0.068 not 6.8. Named against the convention; see note |
| `dollar_volume_median_20` | TEXT | |
| `range_avg_20` | TEXT | for the contraction test |
| `ladder_grade` | TEXT | `rising`, `mixed`, `falling`. Null until TierClassifier writes an observation carrying it |

Insert IndicatorEngine · Insert TierClassifier, **disjoint by computation**: each writes its own `computed_at` and neither ever writes the other's

**Append-only, on the same terms as `daily_bar`.** A computation is an observation, and a read takes the latest `computed_at` at or before its as-of date. This is what lets a rebuild reach the rows it invalidates: a ticker recomputed after a corporate action is honoured gains a second row for every affected session, and a replay of a night before the rebuild still returns the figures the lab acted on, wrong ones included. It was keyed on ticker and date alone until 2026-08-25, which meant a row computed on a basis the vendor had since restated stood for ever.

**A rerun that produces the same figures writes nothing.** Append-only is not the same as writing a row every time.

**Why two inserters rather than an inserter and an updater.** With the table append-only there is nothing to update, so TierClassifier writes a later observation of the session carrying the grade. It copies the seven computed figures forward, which is duplication and is the price of the row being a complete observation rather than a fragment: a reader takes the latest row and gets an answer, rather than assembling one from two.

*Note on `adr_20`.* Every ratio in this schema is a fraction. `adr_20` reads as though it were a percentage because of its name, so it is the one column whose name argues against the rule. It stays a fraction and this note stays here rather than the column being renamed, because `adr` appears in the signal library and in the screens.

### `indicator_rebuild`
Grain: the corporate action that raised the demand, as that action was observed. One row per observation, and the row stays after it is satisfied.

| Column | Type | Note |
|---|---|---|
| `ticker`, `effective_date`, `type`, `observed_at` | TEXT | PK. The key of the `corporate_action` row that raised it |
| `rebuilt_at` | TEXT | NULL until the ticker has been recomputed against a history observed after the action |

Insert ActionIngestor · Update IndicatorEngine (`rebuilt_at` only)

**A row with a NULL `rebuilt_at` is a stock whose calculations must refuse to run.** That is where the architecture's unprocessed-action behaviour is read from. Any corporate action moves every adjusted close before it, so an average taken across the boundary is arithmetic on two different units and its answer is wrong while looking entirely reasonable. Magnitude does not enter it (see: An unprocessed corporate action of any kind blocks calculation, not only a split).

**The key is the action as observed, not the ticker and the date.** A vendor restating a ratio writes a second `corporate_action` observation, which raises a second demand rather than failing to reopen a demand already satisfied. Without that, a ticker rebuilt against a factor that later changed would stay rebuilt, permanently, with the record showing a satisfied demand and the wrong number computed from it (see: A rebuild demand is keyed on the action as observed, and a restated action raises a new one).

**The demand is recorded, not queued.** The row is never deleted and never cleared; it gains a date. A queue that empties answers "is anything outstanding" and destroys the history on its way to the answer.

**Two components, on purpose.** ActionIngestor raises the demand and IndicatorEngine satisfies it. A component that can both raise and close its own condition raises nothing.

**No foreign key to `corporate_action`, though the key is its key.** SQLite rewrites a child's foreign key clause when the parent is renamed, and a hand-written table rebuild renames, so declaring one would make each table's rebuild depend on the order of the other's. A test asserts every demand joins to an action instead, which is the property the constraint would have bought.

### `scan_hit`
Grain: ticker + date + scan.

| Column | Type | Note |
|---|---|---|
| `ticker`, `as_of`, `scan` | TEXT | `gainer`, `gapper`, `leader`, `decliner`, `gapdown`, `laggard` |
| `rank` | INTEGER | 1 to 50, by that scan's own magnitude (see: The scans select a fixed count by rank, not a threshold on the move) |
| `magnitude` | TEXT | the ratio the rank was taken on, a fraction, on the adjusted basis |
| `cluster_count` | INTEGER | same-industry hits that night. Written by ThemeClusterer |

Insert ScanEngine · Update ThemeClusterer (`cluster_count` only) · PK (`ticker`, `as_of`, `scan`)

**`magnitude` is stored rather than recomputed.** It is what the thrust signals freeze, and deriving it later from bars would put the same arithmetic in two places in the one situation where a disagreement is invisible: a wrong magnitude still produces a plausible ranked list. Storing it also makes the rank auditable, since the ordering can be checked against the number it was taken on.

*Note on `cluster_count`.* Null until ThemeClusterer runs, and same-**industry** rather than same-sector: sector and industry are different columns giving different answers on the same night, and both cluster checks read industry (see: The cluster grouping key is industry, not sector).

### `regime_daily`
Grain: date.

| Column | Type | Note |
|---|---|---|
| `as_of` | TEXT PK | |
| `index_score` | INTEGER | −1, 0, +1 |
| `breadth_score` | INTEGER | −1, 0, +1 |
| `label` | TEXT | `risk_on`, `mixed`, `risk_off` |
| `long_ladder_count`, `short_ladder_count` | INTEGER | the raw breadth inputs |

Insert RegimeLabeler

---

## Setups

### `setup`
Grain: date + ticker + direction. **Immutable after write.** The spine of the whole system.

| Column | Type | Note |
|---|---|---|
| `setup_id` | TEXT PK | |
| `as_of` | TEXT | the night it was flagged |
| `ticker` | TEXT | |
| `direction` | TEXT | `long` or `short` |
| `check_results` | TEXT | JSON, every check with pass or fail (see: Failed checks are recorded rather than discarded) |
| `passed_all` | INTEGER | |
| `rank` | INTEGER | give-up distance in range units, ascending |
| `capped_out` | INTEGER | truncated by SetupCapper |
| `trigger_price`, `stop_price` | TEXT | raw prices |
| `stop_distance_ranges` | TEXT | the number check nine turns on |
| `agreement` | TEXT NULL | `agree`, `disagree`, null. Written by Setup inspector from the gallery |
| `agreement_note` | TEXT NULL | |

Insert LongSetupDetector / ShortSetupDetector, **disjoint by `direction`** · Update SetupCapper (`capped_out`, `rank`) · Update Setup inspector (`agreement`, `agreement_note`)

*Two detectors write this table on disjoint rows rather than disjoint columns. A test asserts neither ever writes a row of the other's direction.*

### `calibration_setup`
Grain: date + ticker + direction. Output of a historical detector run, used to count setups per night while thresholds are being calibrated.

Same shape as `setup`, in a separate table that no downstream component reads. Rows here are reconstructed against today's universe rather than against a recorded snapshot, so they carry survivorship bias and are not evidence. (see: The evidence store holds only setups flagged forward, never setups reconstructed from history)

Insert LongSetupDetector / ShortSetupDetector in calibration mode, **disjoint by `direction`** · Read by nobody

### `setup_signal`
Grain: setup + signal. The frozen point-in-time evidence.

| Column | Type |
|---|---|
| `setup_id`, `signal_name`, `value` | TEXT |
| `computed_at` | TEXT |

Insert SignalVectorizer · Insert SignalBackfiller, **disjoint by date and signal**: the vectorizer writes new rows nightly and the backfiller adds signals to old setups, and may never touch a signal the vectorizer owns for that date

### `signal_definition`
Grain: signal name. The library.

| Column | Type | Note |
|---|---|---|
| `signal_name` | TEXT PK | |
| `formula` | TEXT | traces to named stored columns |
| `source_columns` | TEXT | for the point-in-time test |
| `admitted_on` | TEXT NULL | |
| `status` | TEXT | `active`, `rejected_correlation`, `candidate` |
| `is_null_control` | INTEGER | the planted tripwire |

Insert SignalAdmissionTest · Read SignalVectorizer, ContextPacker, SignalBackfiller

### `control_setup`
Grain: setup + control ticker + set. Matched controls, drawn nightly, no API cost.

| Column | Type | Note |
|---|---|---|
| `setup_id`, `control_ticker` | TEXT | |
| `control_set` | TEXT | `loose` or `tight` |
| `match_quality` | TEXT | how close the match was on each matched dimension |

Insert ControlSampler

### `forward_return`
Grain: (setup or control) + horizon. Signed by direction, so a short that fell is positive.

| Column | Type | Note |
|---|---|---|
| `subject_id` | TEXT | a `setup_id` or a control row |
| `subject_kind` | TEXT | `setup` or `control` |
| `horizon_days` | INTEGER | 1, 3, 5, 10 |
| `intended_date`, `actual_date` | TEXT | differ across a holiday, and both are stored |
| `return_signed` | TEXT | |
| `mfe_atr`, `mae_atr` | TEXT | best and worst reached |

Insert ForwardReturnFiller

---

## Signals

The library. Every quantity the frozen row can carry, its formula, and the stored columns it reads.
`signal_definition` holds this as data from 6.2, when SignalAdmissionTest exists to write it; until
then this section is the library, and it is a section here rather than a document of its own (see: The corpus is eight documents plus one artefact, and a ninth requires retiring one).

**Every signal traces to named stored columns, and one does not.** That is the point of writing the
library down: the source columns are what the point-in-time test is asserted against, and a signal
whose formula reads something nothing stores cannot be computed, cannot be replayed, and cannot be
proposed against. The one that does not trace is named at the bottom, as a finding rather than as an
assumption.

**Status is `active` or `candidate`.** Active means SignalVectorizer freezes it on every setup, and
the set of active signals is what "copies every number the decision depended on" resolves to.
Candidate means the formula and the source columns are settled and nothing computes it yet: the raw
material is stored and append-only, so SignalBackfiller at 6.1 computes a specified formula across
the whole setup history rather than inventing one at the time. Declaring a candidate costs nothing
statistically, because the correction threshold scales with signals **screened** rather than signals
declared (see: The correction threshold scales with signals screened, not signals shown).

**Prices are read on the adjusted basis and ratios are fractions,** on the conventions above. Where a
formula needs an intraday price on the adjusted basis, it is put there through that bar's own factor
`adj_close / close`, which is what IndicatorEngine does for high and low. Raw prices appear only in
the trade geometry, because that is what trades tomorrow.

### Trend and position

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `close_adjusted` | the setup session's adjusted close | `daily_bar.adj_close` | active |
| `ema_9_distance` | (adjusted close − `ema_9`) / `ema_9` | `daily_bar.adj_close`, `indicator_daily.ema_9` | active |
| `ema_21_distance` | (adjusted close − `ema_21`) / `ema_21` | `daily_bar.adj_close`, `indicator_daily.ema_21` | active |
| `ema_50_distance` | (adjusted close − `ema_50`) / `ema_50` | `daily_bar.adj_close`, `indicator_daily.ema_50` | active |
| `ema_gap_21_50` | (`ema_21` − `ema_50`) / `ema_50` | `indicator_daily.ema_21`, `indicator_daily.ema_50` | active |
| `ema_gap_21_50_avg_20` | mean of `ema_gap_21_50` over the last 20 sessions | `indicator_daily.ema_21`, `indicator_daily.ema_50` | active |
| `ladder_grade` | the grade TierClassifier wrote for that session | `indicator_daily.ladder_grade` | active |

*`ema_50_distance` is the extension-from-the-long-average measurement the architecture lists as
missing. It costs nothing beyond a subtraction over two columns already stored, so it is active
rather than a candidate.*

### Volatility

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `adr_20` | as stored, a fraction | `indicator_daily.adr_20` | active |
| `atr_14` | as stored | `indicator_daily.atr_14` | active |
| `range_avg_20` | as stored | `indicator_daily.range_avg_20` | active |
| `range_today_over_avg` | (high − low) on the adjusted basis, over `range_avg_20` | `daily_bar.high`, `daily_bar.low`, `daily_bar.close`, `daily_bar.adj_close`, `indicator_daily.range_avg_20` | active |

*`range_today_over_avg` is the number the contraction check turns on, stored as a value rather than
as a verdict. A check result says whether it was under one; the signal says how far under.*

### The thrust

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `thrust_scan` | which scan the most recent qualifying hit came from | `scan_hit.scan` | active |
| `thrust_rank` | its rank on that scan | `scan_hit.rank` | active |
| `thrust_session` | the session of that hit | `scan_hit.as_of` | active |
| `days_since_thrust` | trading sessions from `thrust_session` to the setup date | `scan_hit.as_of`, `daily_bar.bar_date` | active |
| `thrust_magnitude` | the scan magnitude that put it on the list, on the adjusted basis | `daily_bar.adj_close`, `daily_bar.open`, `daily_bar.close` | active |
| `thrust_size_in_ranges` | `thrust_magnitude` / `adr_20` | `daily_bar.adj_close`, `daily_bar.open`, `daily_bar.close`, `indicator_daily.adr_20` | active |

*`days_since_thrust` was bounded by a check and never stored, which the architecture lists as a gap.
`thrust_size_in_ranges` is the other one it lists, and it is the lever the computed ceiling moves on:
a 19% jump means something different for a 7% range stock than for a 3% one.*

### The pullback

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `pullback_bars` | sessions from the thrust extreme to the setup date | `daily_bar.bar_date` | active |
| `pullback_extreme` | lowest adjusted low since the thrust extreme, long; highest adjusted high, short | `daily_bar.low`, `daily_bar.high`, `daily_bar.close`, `daily_bar.adj_close` | active |
| `retrace_depth` | (thrust extreme − `pullback_extreme`) / (thrust extreme − thrust origin), signed so both directions read the same way | `daily_bar.high`, `daily_bar.low`, `daily_bar.close`, `daily_bar.adj_close` | active |
| `closes_beyond_floor` | sessions in the pullback closing below `ema_21`, long; above `ema_50`, short | `daily_bar.adj_close`, `indicator_daily.ema_21`, `indicator_daily.ema_50` | active |

*`closes_beyond_floor` reads a different average per direction, because the checks do: `held-floor`
is the 21-day and `no-reclaim` is the 50-day. One signal rather than two, because the pair is one
question asked of whichever average that direction's floor is.*

### The trade geometry

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `trigger_price` | as written, a raw price | `setup.trigger_price` | active |
| `stop_price` | as written, a raw price | `setup.stop_price` | active |
| `stop_distance_ranges` | \|trigger − stop\| / (`adr_20` × close) | `setup.stop_distance_ranges` | active |
| `trigger_distance_ranges` | \|trigger − close\| / (`adr_20` × close) | `daily_bar.close`, `setup.trigger_price`, `indicator_daily.adr_20` | active |

### Liquidity and the name

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `dollar_volume_median_20` | as stored | `indicator_daily.dollar_volume_median_20` | active |
| `market_cap` | as stored, short side only | `security.market_cap` | active |
| `listing_age_sessions` | trading sessions since `first_seen` | `security.first_seen`, `daily_bar.bar_date` | active |
| `industry` | as stored | `security.industry` | active |
| `cluster_count` | same-industry scan hits that night | `scan_hit.cluster_count` | active |

### The market

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `regime_index_score` | as stored | `regime_daily.index_score` | active |
| `regime_breadth_score` | as stored | `regime_daily.breadth_score` | active |
| `regime_label` | as stored | `regime_daily.label` | active |

*Frozen on the setup and filtering nothing, which is what keeps the label available as a clean
experiment (see: The market-mood label is recorded on every setup and filters nothing in the baseline). Both raw scores sit beside it so a proposal can use the continuous form.*

### Volume, the axis the library does not have

The architecture's own verdict on the library is that it "is almost entirely price path, and volume
appears nowhere except buried inside a scan definition". That stays true of the active set. The three
below are declared with their formulas and their source columns and are not frozen, because no phase
2 decision depends on them and adding them to the frozen row would be widening the library on a
guess rather than through the admission route.

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `volume_thrust` | raw volume on `thrust_session` | `daily_bar.volume` | candidate |
| `volume_pullback_mean` | mean raw volume over the pullback bars | `daily_bar.volume` | candidate |
| `volume_dryup` | `volume_pullback_mean` / `volume_thrust` | `daily_bar.volume` | candidate |

*Raw volume, not adjusted, on the same reasoning `dollar_volume_median_20` uses it: it is what
changed hands. `daily_bar` is append-only and holds volume from the first ingest, so 6.1 computes
these across the whole stored history whenever they are admitted.*

### The remaining candidates

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `prior_thrust_outcome` | this security's adjusted move over the ten sessions after each earlier scan hit, averaged | `daily_bar.adj_close`, `scan_hit.as_of` | candidate |
| `intraday_pullback_shape` | the fraction of each pullback session's range travelled after midday | `intraday_bar` | candidate, owed at 4.2 |
| `day_of_month` | the calendar day of `as_of`, meaning nothing | `setup.as_of` | candidate, planted at 6.4 |

*`day_of_month` is the planted null control and carries `is_null_control` when the table exists. It
is declared here rather than at 6.4 so the column it reads is on the record; planting it is
ContextPacker's job (see: One meaningless signal is planted in the conditional tables).*

### The one that does not trace, recorded as a finding

**`earnings_in_window` has no source column and no budgeted call, so it is not in this library at
all.** The architecture lists it among the missing measurements, with the reason: "A trade decided by
an earnings gap is not a test of the pattern, and today the system cannot tell those apart." Nothing
stored carries an earnings date. `corporate_action` holds splits and dividends and neither implies
one, and the vendor's calendar endpoint is not among the endpoints the call budget is built on.

Recorded here rather than written as a candidate with an empty source, because a candidate whose
source columns are blank reads as work scheduled and is actually a purchase nobody has priced. What
it would cost, so a later session decides rather than rediscovers: one calendar endpoint added to the
vendor client, and a per-symbol or per-day call whose price against the 5,000 ceiling has not been
measured. Until that is taken, no rule can refer to whether a setup straddled earnings, and the
effect sits inside the outcome distribution unlabelled.

---

## Trading — phase 4

Declared at store level. Columns owed at their checkpoint.

| Store | Grain | Writer |
|---|---|---|
| `trade_plan` | setup + variant | Insert PlanBuilder. **Never updated after its session date** (see: The plan is written before the session and is immutable after publication) |
| `order` | order id | Insert RiskGate only (see: RiskGate is the sole writer of orders, for both directions and every version). Blocked orders written with a reason, never dropped |
| `fill` | fill id | Insert PaperBroker |
| `position` | position id | Insert PaperBroker · Update PaperBroker. Carries `risk_intended` and `risk_realised` so share rounding is visible rather than assumed away (see: Equity is a fixed $100,000 notional that never compounds) |
| `plan_audit` | trade | Insert PlanAudit. Planned stop beside executed stop |
| `trade` | trade id | Insert TradeJournal. Result in R, borrow cost on shorts |
| `loss_class` | trade | Insert LossClassifier. Four causes plus `unclassified` as a real category |

## Research — phases 5 and 6

| Store | Grain | Writer |
|---|---|---|
| `variant` | variant id | Insert VariantAdmitter (definition, target, min sample, **once**) · Update AcceptanceGate (status and resolution date **only**) (see: Targets and minimum samples are written at creation and are immutable) |
| `variant_score` | variant + date | Insert VariantScorer |
| `ceiling_bound` | date | Insert CeilingCalculator |
| `twin_pair` | pair id | Insert TwinPairFinder |
| `pack_version` | version | Insert ContextPacker |
| `proposal` | proposal id | Insert ResearcherSeat (see: The AI writes only to the proposal store) · Update ProposalRegistry (status) |
| `replay_result` | proposal + window | Insert ReplayHarness |
| `holdout_window` | window id | Insert HoldoutRegistry · Update HoldoutRegistry (spend, once) |
| `scoreboard` | date + panel | Insert ScoreboardBuilder |


---

## Cross-cutting

### `run_log`
Grain: run id. Every stage writes a start and an end entry here, so it is delivered at checkpoint 1.1 rather than with the research machinery.

| Column | Type | Note |
|---|---|---|
| `run_id` | TEXT PK | |
| `stage` | TEXT | the calling stage |
| `started_at`, `ended_at` | TEXT | |
| `outcome` | TEXT | `clean`, `partial`, `failed` |
| `rows_written` | INTEGER | measured from the store, not self-reported by the stage |
| `calls_used` | INTEGER | counted as the stage runs |
| `counts_against_ceiling` | INTEGER | 0 or 1. Whether the daily total sees this run's calls |

Insert RunLogger · Update RunLogger

**One writer, not one per stage.** Stages do not write this table. They call `RunLogger`, which owns both operations. Declaring every stage as a writer would put the run-accounting logic in a dozen places and `writer-ownership` could never pass.

**`rows_written` is measured, not reported.** A stage counting its own output will report what it believes it wrote. The nightly halt keys on this number, so it is read back from the store.

**`counts_against_ceiling` is how a one-time operation stays out of the nightly budget.** The ceiling guards the evening's job; the history backfill is not the evening's job, and charging the two against each other is what once made the backfill look like a two-day procedure. Its calls are still recorded, because what a run cost is worth knowing about every run. The run says so in the store rather than being recognised by its stage name, which would put the exception in the query rather than in the record.

## Store configuration

SQLite, one file under the configured data root. These are set at open, in one place, by the same shared extension that wires config.

| Pragma | Value | Why |
|---|---|---|
| `journal_mode` | `WAL` | The nightly job writes for hours while a page may be reading. Without it a reader blocks a writer and evening runs throw spurious lock errors |
| `synchronous` | `NORMAL` | Safe under WAL against a process crash. `FULL` costs write throughput for protection against power loss, which a nightly snapshot already covers |
| `busy_timeout` | 5000 ms | Brief contention retries rather than throwing. Zero is the default and it is the wrong default here |
| `foreign_keys` | `ON` | Off by default in SQLite, per connection, and silently so |

**One writer, one connection.** The Worker is the sole writer by design, and SQLite makes that a practical requirement rather than a stylistic one. The Api opens the file read-only. A second writing connection produces intermittent lock failures that look like load problems and are not.

### Expected size

About 0.8 GB a year including indexes, roughly 4 GB after five years. Minute bars are 69% of it: sixty flagged setups a night across 390 minutes and 252 sessions is close to six million rows a year. Everything else together is under 250 MB.

Nothing at this scale argues for a different database. The lever, if it ever matters, is storing 5-minute bars instead of 1-minute, which would cut the largest table by 80% and make the fill model coarser. The fill model is where the trade record's honesty lives, so that trade is not worth making at this size.

**Index `intraday_bar` deliberately.** A composite on ticker and timestamp is what the replay walks, and on six million rows a year the index is a material fraction of the store. Measure it rather than adding indexes by reflex.

### Backups

`VACUUM INTO` writes a full copy, so thirty nightly snapshots in year three is around 70 GB. Keep a short rolling window of nightlies plus one monthly. The backup total grows faster than the store and is the number more likely to bite.

## Migrations

Snapshot before every migration. `tools/migrate` calls `tools/snapshot-db` internally first and refuses to run without a successful snapshot.

Migrations are hand-written SQL. Generated table rebuilds have twice re-added constraints that a convention here strips, so a table rebuild carries a row-survival test asserting the count before and after.
