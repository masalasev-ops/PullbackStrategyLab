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

### `index_bar`
Grain: symbol + date. SPY, QQQ, IWM. Same shape as `daily_bar`.

Insert IndexIngestor

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
| `ticker`, `as_of` | TEXT | PK |
| `ema_9`, `ema_21`, `ema_50` | TEXT | on adjusted close |
| `atr_14` | TEXT | |
| `adr_20` | TEXT | **fraction**, so 0.068 not 6.8. Named against the convention; see note |
| `dollar_volume_median_20` | TEXT | |
| `range_avg_20` | TEXT | for the contraction test |
| `ladder_grade` | TEXT | `rising`, `mixed`, `falling`. Written by TierClassifier |

Insert IndicatorEngine · Update TierClassifier (`ladder_grade` only)

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
| `rank` | INTEGER | |
| `cluster_count` | INTEGER | same-sector hits that night. Written by ThemeClusterer |

Insert ScanEngine · Update ThemeClusterer (`cluster_count` only)

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

Insert LongSetupDetector / ShortSetupDetector, disjoint by `direction` · Update SetupCapper (`capped_out`, `rank`) · Update Setup inspector (`agreement`, `agreement_note`)

*Two detectors write this table on disjoint rows rather than disjoint columns. A test asserts neither ever writes a row of the other's direction.*

### `calibration_setup`
Grain: date + ticker + direction. Output of a historical detector run, used to count setups per night while thresholds are being calibrated.

Same shape as `setup`, in a separate table that no downstream component reads. Rows here are reconstructed against today's universe rather than against a recorded snapshot, so they carry survivorship bias and are not evidence. (see: The evidence store holds only setups flagged forward, never setups reconstructed from history)

Insert SetupDetector, calibration mode only · Read by nobody

### `setup_signal`
Grain: setup + signal. The frozen point-in-time evidence.

| Column | Type |
|---|---|
| `setup_id`, `signal_name`, `value` | TEXT |
| `computed_at` | TEXT |

Insert SignalVectorizer (nightly, new rows) · Insert SignalBackfiller (backfill, adds signals to old setups; may never touch a signal SignalVectorizer owns for that date)

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
| `calls_used` | INTEGER | counted as the stage runs, against the daily ceiling |

Insert RunLogger · Update RunLogger

**One writer, not one per stage.** Stages do not write this table. They call `RunLogger`, which owns both operations. Declaring every stage as a writer would put the run-accounting logic in a dozen places and `writer-ownership` could never pass.

**`rows_written` is measured, not reported.** A stage counting its own output will report what it believes it wrote. The nightly halt keys on this number, so it is read back from the store.

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
