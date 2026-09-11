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

**A row here says the instrument existed, never that it trades.** UniverseBuilder writes one for every survivor of the nightly screen, and one for every name the exchange has delisted, which is the second mode of the same stage and the same insert. Membership is `universe_member` and is written from the screen alone, so a delisted name has a row here and none there, for ever. That gap is exactly what the delisted backfill selects on, and `daily_bar`'s foreign key to this table is why the two verbs are ordered rather than independent (see: Delisted daily history is bought so a reconstructed walk is not confined to survivors).

**`first_seen` is weaker for a delisted name than for a listed one**, and the column's own note is the honest reading: the date it was first observed in a symbol list. For a survivor that is the night the lab first saw it trading. For a delisted name the vendor publishes no delisting date, so it is the night the delisted list was first read, which is a fact about the lab rather than about the instrument.

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

| Column | Type | Note |
|---|---|---|
| `as_of` | TEXT | |
| `ticker` | TEXT | |
| `screened_over_sessions` | INTEGER NULL | how many sessions the screen could see. The liquidity floor is a median over twenty |
| `screen_carried` | INTEGER NULL | 1 where the membership was carried from the last complete screen rather than screened fresh |

Insert UniverseBuilder · PK (`as_of`, `ticker`)

*A night that can see fewer than five sessions cannot screen, and it carries the membership that stands rather than writing nothing: membership drifts by a handful a month while a skipped night removes a whole session from the series, and phase 3 is where a missing night starts costing a sample. **What it may not do is look like a screened night.** `screen_carried` is what stops a later count reading a carried membership as fresh, which would be a survivorship claim the store cannot support. Both columns are null on every snapshot written before 3.0, because inventing a value for a night that did not record one is worse than admitting it is absent, and a null is a different fact from a night that screened over nought sessions.*

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

**And it is what the delisted purchase resumes from.** A name is finished when it has a row here, including a name whose history came back empty, so a run spread across nights asks for each name once and never again. It is deliberately not a second list of what is done: a copy can disagree with what it copies, and this is the record the fetch itself writes (see: Delisted daily history is bought so a reconstructed walk is not confined to survivors).

### `index_bar`
Grain: symbol + date + `observed_at`. SPY, QQQ, IWM.

Shape of `index_bar`: same as `daily_bar`, with `symbol` in place of `ticker`.

The same terms as `daily_bar` too: **append-only, never deleted, never updated**, a correction arriving as a new row with a later `observed_at`, and reads taking the latest observation at or before the as-of date.

Insert IndexIngestor · PK (`symbol`, `bar_date`, `observed_at`)

**No foreign key to `security`.** A tracker is not part of the tradable universe and never appears in a screen. It is read to say what the market did, which is a different question from what any one stock did.

### `intraday_bar`
Grain: ticker + minute. Phase 4. Fetched for every flagged setup, not only planned ones, because a variant selecting a name the baseline passed on must still be resolvable.

| Column | Type |
|---|---|
| `ticker`, `bar_ts`, `open`,`high`,`low`,`close`, `volume` | |
| `ticker`, `bar_ts` | TEXT. The stamp is the instant the bar opened, in UTC |
| `session_date` | TEXT. Which trading session the minute belongs to, stored rather than derived |
| `interval_code` | TEXT. `1m`, and the CHECK admits nothing else |
| `session_window` | TEXT. `regular` or `extended`, per bar |
| `price_basis` | TEXT. `raw`, because a minute bar is what a trade actually gets |
| `open`, `high`, `low`, `close` | TEXT holding a decimal, never REAL |
| `volume` | INTEGER |
| `vwap_session` | TEXT. **Written from 4.4 and not written from 4.7.** No reader for it was ever named and none exists |
| `observed_at` | TEXT. In the key, so a vendor correction is a new row |

Insert IntradayFetcher · PK (`ticker`, `bar_ts`, `observed_at`)

**`vwap_session` stopped being written at 4.7, and it is the reason there is no longer any declared
update against a bar table.** 4.4 wrote the running session average onto every stored minute and
raised the obligation in the same entry: either a reader is named or the column stops being written.
It fell due at 4.7 on the reasoning that the fill model was its most likely reader, and the fill
model does not read it. A fill is the resting price plus the captured spread; no rule in this lab
compares a price against a session average; and nothing else in the corpus reads it through phase 6.

**It stopped rather than being kept, because it is derivable and the anchored average is not.** A
running session average is a sum over the session's own stored minutes in order, so anything that
wants one computes it at the moment it is wanted, which is the ruling VwapEngine already took over
the day's high and low and WatchlistPublisher took over a watchlist table. `anchored_vwap` is
untouched: it needs a swing nothing else resolves and it is not recoverable from one session.

**The column itself is not dropped and the rows that carry a value keep it.** Dropping it would
delete what past nights wrote from the one kind of table this store never edits, to tidy a document.
What the stop buys is that `bar-append-only` no longer carries an exception by table, column and
component: nothing in the shipped source updates a bar table at all
(see: The session average is derived when it is wanted and is not stored on a bar).

**Extended-hours minutes are stored, not dropped.** A minute outside the regular session is exactly as unrecoverable as one inside it, so the fetch takes whatever the vendor holds for the day and every bar carries the window it fell in. A reader bounds on `session_window` and nothing has to be re-bought when a later question wants the other half.

### `intraday_fetch`
Grain: session + observation. Phase 4. What one night's fetch did, written whatever the outcome.

| Column | Type |
|---|---|
| `session_date` | TEXT. The session the bars are for, not the evening the fetch ran on |
| `setup_as_of` | TEXT. The session whose setups those bars resolve, always strictly earlier |
| `requested`, `fetched`, `empty`, `bars_written` | INTEGER |
| `stored` | INTEGER. What the night's asking left the store holding for the window it bought, being the bars written plus the bars already there unchanged |
| `window_sessions` | INTEGER. How many sessions the window covered, nought where the night asked for nothing |
| `outcome` | TEXT. `clean`, `partial` or `failed`. A `partial` carries a reason and the CHECK refuses one that does not |
| `stopped_because` | TEXT NULL. What shape the night was, where it was not an ordinary full buy |
| `observed_at` | TEXT |

Insert IntradayFetcher · PK (`session_date`, `observed_at`)

**A night with no row here is a night nobody ran**, which is a different fact from a night that ran and asked for nothing, and the two are only distinguishable because the stage writes a row either way. **The shortfall is recorded here rather than on the setup rows**: `setup.degraded_because` is written once by the detector that inserts the row, `setup` has one declared writer per operation, and an update from this stage would be a second writer on rows the corpus forbids rewriting. Which names went unfetched is `requested` against `fetched`, which is a join rather than an edit.

**`stored` exists because the outcome had nothing honest to turn on, and `bars_written` is not it.** The stage computed `clean` from the call ceiling alone, so the night of 2026-09-04 asked 92 names, was answered by all 92 with nothing, spent 460 calls, wrote 0 bars and recorded `clean` with no reason. `bars_written` was already nought on that row. It cannot be the quantity the outcome turns on either, because a rerun over minutes the store already holds writes nought bars and has lost nothing: `IntradayBarReader.IsStoredUnchanged` skips every bar already held. `stored` is the third of the three quantities a fetch has, being asked, returned and stored, and it is the one that separates a night that bought nothing from a night that needed to buy nothing (see: A gate handed an absent or degenerate quantity fails rather than passing).

**`window_sessions` is what says whether short's twenty-session count was running.** The count starts on the first night the fetch buys the full anchor window and not on the night the code landed, and those are different dates; without the column the only way to tell them apart is to date a run against a commit. It is the sessions the window actually covered rather than the width it asks for, because the window is read from the sessions `daily_bar` holds and the store held fewer than the full width for the whole of the lab's first year. A night at a narrower width is a permanent forfeit of that window's anchors rather than a delay (see: The intraday fetch buys the twenty-seven session anchor window, and the count starts on the first night it runs at that width).

**The bars a wide night stores are labelled with the session they traded in, not the session the fetch was for.** The two were one figure while the stage bought a single session a night, and a twenty-seven session window returns bars from twenty-seven sessions in one answer; stamping all of them with the night the fetch ran would put every anchor's minutes under the wrong day, and the reader bounds on `session_date`.

### `anchored_vwap`
Grain: ticker + anchor + through-session + observation. Phase 4. The declining average price the short side's `reached-ceiling` clause reads.

| Column | Type |
|---|---|
| `ticker` | TEXT |
| `anchor_session` | TEXT. The session the swing sits in |
| `anchor_ts` | TEXT NULL. The minute inside it the extreme traded in, UTC. Null where no level could be priced |
| `anchor_kind` | TEXT. `swing-high` or `swing-low`, and the CHECK admits nothing else |
| `through_session` | TEXT. The last session the average includes |
| `setup_as_of` | TEXT. The evening whose setup named the anchor |
| `value` | TEXT NULL holding a decimal, never REAL |
| `bars`, `volume` | INTEGER. What the figure was computed over |
| `absent_because` | TEXT NULL. Why a row carries no value |
| `observed_at` | TEXT. In the key, so a recomputation after a vendor correction is a new row |

Insert VwapEngine · PK (`ticker`, `anchor_session`, `through_session`, `observed_at`)

**The anchor is two columns and a kind, because until 4.4 it was a phrase.** ARCHITECTURE asks whether the bounce reached "the declining average price anchored to the last swing high", and nothing said which bar the swing high was, which minute inside it, or what the average was taken over. Three sessions computing that level would all have produced plausible prices and no two would have had to agree. `anchor_session` is the swing the thrust ran from, resolved from the same geometry the detector reads through `ShortSetupDetector.AnchorSessionOf`; `anchor_ts` is the minute inside it the high actually traded in, taken from the stored minutes and earliest on a tie; `anchor_kind` names what was measured rather than which detector asked, so a long-side anchor would read `swing-low` on the same terms (see: The anchored average price is anchored at the swing the thrust ran from).

**`through_session` is the half that is easy to lose.** An anchored average is a level as at a moment and the moment is the last session it includes. The engine runs at 21:00 on minutes the fetch landed at 20:30, and those minutes are for the session before that evening, so a level a detector reads on the evening of N is computed through N−1 by construction. That is point-in-time clean and it is not the same number as a level through N, so the column says which rather than leaving a reader to date it from `observed_at`.

**A row with no value is the ordinary case and is a row rather than a silence.** `IntradayFetcher` buys one session a night per flagged name and a swing sits three to twenty-seven sessions back, so most anchors are inside the vendor's reach and outside the store's. `absent_because` separates the reasons, and a night that anchored nothing stays distinguishable from a night nobody ran. `bars` and `volume` are on every row because a volume-weighted average over eleven minutes of a thin name carries the same name and none of the authority (see: A gate handed an absent or degenerate quantity fails rather than passing).

### `vwap_run`
Grain: session + observation. Phase 4. What one night's engine did, written whatever the outcome, on the same terms `intraday_fetch` records the fetch.

| Column | Type |
|---|---|
| `session_date` | TEXT. The session the minutes are for |
| `setup_as_of` | TEXT. The evening whose setups named the anchors, always strictly earlier |
| `names` | INTEGER |
| `anchors_asked`, `anchors_priced` | INTEGER. Two figures, because the gap between them is the state of the third clause |
| `outcome` | TEXT. `clean`, `partial` or `failed` |
| `stopped_because` | TEXT NULL |
| `observed_at` | TEXT |

Insert VwapEngine · PK (`session_date`, `observed_at`)

**An anchor out of the store's reach does not make the run partial.** Nothing was asked of the vendor and nothing failed; the rows say what could not be reached and `anchors_asked` against `anchors_priced` is the figure. A run that called that partial would report every night as partial until the store had accumulated years of minutes, which is a signal that means nothing.

### `spread_snapshot`
Grain: ticker + session + pass + observation. Phase 4. **Unrecoverable if missed.** The only intraday job.

**Read by entry slippage at 4.7**, through `SpreadSnapshotReader`. Named here because until 4.3 this was the one store in this document whose reader was neither named nor recorded absent, and a capture spending 120 unrecoverable calls a session on an input nothing consumes is one nobody can justify. What fraction of the spread a fill is charged, and whether it is symmetric between the two directions, are 4.7's and nothing in the capture computes them.

| Column | Type | Note |
|---|---|---|
| `ticker` | TEXT | |
| `session_date` | TEXT | the session the sample is inside |
| `setup_as_of` | TEXT | the evening whose capped names these are, always strictly earlier |
| `pass` | TEXT | `after_open` or `before_close`. A name rather than an index, so a row says which sample it is without a lookup |
| `snapshot_ts` | TEXT | when the lab asked |
| `bid`, `ask` | TEXT NULL | prices. Null where the vendor carried no such side |
| `bid_size`, `ask_size` | INTEGER NULL | |
| `bid_ts`, `ask_ts` | TEXT NULL | what the vendor stamped **each side** at. They differ: on the capture of 2026-09-01 the two sides of AAPL's book were 32 seconds apart, so a spread is a figure across two instants and the store keeps both |
| `last_trade`, `last_trade_ts` | TEXT NULL | what actually traded, which is what makes a quoted spread interpretable against a price somebody paid |
| `spread_bps` | REAL NULL | basis points of the mid. A statistic and not money, which is the one column here the prices rule points the other way on |
| `quote_lag_seconds` | INTEGER NULL | how stale the quote was when it was taken, measured from the **older** of the two sides |
| `absent_because` | TEXT NULL | why a row carries no spread. Null on a usable two-sided book |
| `observed_at` | TEXT | |

Insert SpreadSnapshotter · PK (`ticker`, `session_date`, `pass`, `observed_at`)

**Every price column is nullable, and that is the shape of the table rather than a concession.** A quote the vendor had no bid for, a name it answered with one side, and a name it never mentioned are three different facts, and a column forced to hold a number flattens all three into whatever the writer chose. A spread of nought is not a missing spread: it is a free entry, and it clears every threshold written as a maximum, which is the same argument the geometry columns make three tables down (see: A gate handed an absent or degenerate quantity fails rather than passing). A crossed or locked book, where the bid is at or above the ask, is recorded the same way and for the same reason.

**The lag is recorded rather than corrected for.** The feed is delayed by design, so a sample asked for at 10:15 describes the book at about 10:00, and the delay is the vendor's to change. Subtracting a constant would make the design's assumption invisible and would leave a later reader unable to tell a normal sample from a stale one; the stamps make it a fact per row, and 4.7 can bound on it or exclude a row instead (see: A delayed quote records its own lag rather than being corrected for it).

**Append-only, with the observation in the key**, on the same terms as the bar tables. A pass rerun for a session it already has takes a genuinely different quote, because the market moved between them, so it is a second observation and not a correction of the first.

### `spread_pass`
Grain: session + pass + observation. Phase 4. What one pass did, written whatever the outcome.

| Column | Type |
|---|---|
| `session_date`, `setup_as_of`, `pass` | TEXT |
| `requested`, `answered`, `quoted`, `unquoted`, `rows_written` | INTEGER |
| `outcome` | TEXT. `clean`, `partial` or `failed` |
| `stopped_because` | TEXT NULL |
| `observed_at` | TEXT |

Insert SpreadSnapshotter · PK (`session_date`, `pass`, `observed_at`)

**This row is the whole of how a missed snapshot is detectable.** A stage that never ran cannot record that it never ran, so absence is the only signal available, and absence is only readable because a pass that does run always writes. One row for a session is a session sampled once; two is the design; none is a hole no later call can fill. The three cases and what each does are in ARCHITECTURE's failure behaviour under "A spread snapshot is missed".

**It is stamped and bounded, unlike `intraday_fetch`, which it otherwise resembles.** The difference is that something reads it to decide an answer: whether a session was sampled at all is what the spread reader refuses on, so "sampled, as far as the lab could know by this date" is a point-in-time question and a replay seeing a pass recorded after the instant it is answering would refuse differently from the night itself.

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
| `observed_at` | TEXT | when the lab observed the hit. Null on rows written before the column existed and never backfilled |

Insert ScanEngine · Update ThemeClusterer (`cluster_count` only) · PK (`ticker`, `as_of`, `scan`)

**`observed_at` exists because a hit inserted for a past session was otherwise invisible to every bound rather than merely unbounded by one.** A rerun of `scans` for an old date wrote rows no point-in-time read could tell from the originals, and a cluster count derived afterwards would have counted them silently. Every read now bounds it, and **a null is refused by a read of any session other than the row's own** rather than treated as always-visible: a row with no provenance is honestly unavailable to history and honestly available to the session it is dated for. Migration 029 backfilled the 300 rows that predate the column from the `scans` run that wrote them, matched on stage, clean outcome, session date in the session zone, and a `rows_written` equal to the hit count for that date.

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
| `indexes_above` | INTEGER | how many of the three trackers closed above their own 50-day average, which is what `breadth_score` is derived from |

Insert RegimeLabeler

---

## Setups

### `setup`
Grain: date + ticker + direction. **Rows are immutable after write, except by a correction the lateness bound admits.** The spine of the whole system (see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound).

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
| `trigger_price`, `stop_price` | TEXT NULL | raw prices, null where the geometry is absent |
| `stop_distance_ranges` | TEXT NULL | the number check nine turns on, null where the geometry is absent |
| `agreement` | TEXT NULL | `agree`, `disagree`, null. What a person thought, recorded from the gallery. Null is "not looked at" and is a different fact from disagreeing |
| `agreement_note` | TEXT NULL | |
| `thrust_scan` | TEXT NULL | which of the six scans produced the thrust this setup was measured against |
| `thrust_session` | TEXT NULL | the session that scan flagged |
| `degraded_because` | TEXT NULL | which stages had already ended other than cleanly when the row was written, comma separated, **bounded on the session's own calendar day in the session zone rather than on the session**, so a stage that ran in that day's early hours may have been repairing the session before. The value says which day it bounded and says that outright, because telling the two apart from the log alone would need a session on `run_log`, which it does not carry. Null on an ordinary night. The third clause of the vendor-ceiling rule, which had no column until migration 032 |
| `corrected_at` | TEXT NULL | when a check verdict on this row was recomputed. Null on a row nothing has corrected |
| `corrected_because` | TEXT NULL | why, naming the check and the stage that failed on the night |
| `correction_lateness_minutes` | INTEGER NULL | how far past the session's own end of day the latest input the correction used arrived. Zero where every input was inside the session's own day |
| `corrected_from` | TEXT NULL | the check results as they stood before the correction, verbatim, so a repaired row is reversible and a reader can see the verdict was absent rather than only that the row was touched |
| `corrected_check` | TEXT NULL | which check the correction recomputed, as a value rather than as a phrase inside `corrected_because`. It is what a restore scoped to one check selects on, and the one thing a caller must be able to select on was the one thing that was prose |

**The three geometry columns are nullable as of migration 031, and nought is not the same answer as none.** They were `NOT NULL`, so a setup whose geometry the detector could not compute had nowhere to record that: the detector wrote nought, `SignalVectorizer` froze the nought into `setup_signal`, which is written once and never updated, and the gallery rendered a trade whose give-up was nothing. A give-up distance of 0 is not a tight stop; it clears every threshold written as a maximum. The golden fixture's `2026-08-24-INTC-short` is the case: `exit-tight` is recorded on that row as failed with value null, and the frozen signal for the same setup on the same night said `0.0000`. Rows written before 031 keep the flattened nought, because reconstructing a detector's decision from a sentinel is a rewrite of a stop and the rule against that has no exception (see: A gate handed an absent or degenerate quantity fails rather than passing).

Insert LongSetupDetector / ShortSetupDetector, **disjoint by `direction`** · Update SetupCapper (`capped_out`, `rank`) · Update LabSetups (`agreement`, `agreement_note`, the two columns the Worker cannot own because the Worker has no judgement to record) · Update CheckRecomputer (`check_results`, `corrected_at`, `corrected_because`, `correction_lateness_minutes`, `corrected_from`, `corrected_check`, and only for a check the baseline records without requiring)

*Two detectors write this table on disjoint rows rather than disjoint columns. A test asserts neither ever writes a row of the other's direction.*

*`rank` and `capped_out` are the night's, not a version's, and there is deliberately no column that could make them a version's. The cap is applied to the shared candidate list before any version selects, and a cap applied per version would leave their disagreements unscoreable. A test asserts the absence rather than the intent, because the intent is unassertable once versions exist and the record it would have destroyed cannot be reconstructed.*

*Both are null on a setup that failed a gating check. Such a row is evidence and was never a candidate, so a rank among names it was not ranked against would be a number with no meaning.*

*`corrected_at` and `corrected_because` are how a corrected row is told from one that was right the first time. The two are not the same evidence and a later reader has to be able to exclude the corrected ones without knowing the correction happened, which is why the mark is a condition of the permission rather than a note beside it. Null on every row until something corrects one, and the pair is written together: a correction with no reason recorded is the shape the rule exists to refuse.*

*`thrust_scan` and `thrust_session` record what the detector already resolved and used to throw away. Four gates read a quantity computed from the thrust's location, and `gainer` and `gapper` flag a move over one session where `leader` and `laggard` flag one over twenty, so a row that does not say which scan flagged it cannot be told from a row measured over a different span. The same fact is also the `thrust_scan` signal, and that is not enough: `setup_signal` has a foreign key to `setup`, calibration writes to `calibration_setup`, so the population a threshold is counted over is exactly the population the signal cannot reach.*

### `calibration_setup`
Grain: date + ticker + direction. Output of a historical detector run, used to count setups per night while thresholds are being calibrated.

Shape of `calibration_setup`: same as `setup`, less `corrected_at`, `corrected_because`, `correction_lateness_minutes`, `corrected_from`, `degraded_because` and `corrected_check`.

**The six are the correction and degradation columns, and the divergence is deliberate.** A calibration row is not evidence, nothing corrects one and no night is degraded by one, so three migrations decline to add them and say so in their own comments. It read "same shape as `setup`" until 4.6, which was six columns wrong and was the sentence a reconciliation would have read as licence to skip the table.

It is a separate table that no downstream component reads. Rows here are reconstructed against today's universe rather than against a recorded snapshot, so they carry survivorship bias and are not evidence. (see: The evidence store holds only setups flagged forward, never setups reconstructed from history)

*Three reconstructions ride on every row, and each is recorded rather than assumed. **Membership** is today's, because a night the lab was not running has no snapshot. **The market-cap clause of `tradable-shortable` is exempt**, because the lookup is bounded on when it was made and a 2024 session has no capitalisation at all; every short verdict here says which clauses ran. And **the bar series is read as the store knows it now**, corrections included, rather than as it stood on the night: a backfill takes a name's whole history in one evening, so every historical bar was observed later than its own session and a read bounded on the session's own instant returns nothing. That third one is not a choice between two readings; it is the only reading that returns anything, which is why it is written down here rather than left as a property of a query.*

Insert LongSetupDetector / ShortSetupDetector in calibration mode, **disjoint by `direction`** · Read by nobody

### `calibration_control_setup`
Grain: control draw. The matched controls of a reconstructed setup, drawn by the same sampler.

Shape of `calibration_control_setup`: same as `control_setup`.

Pointed at `calibration_setup` by its foreign key, so a reconstructed control cannot be hung off an evidence setup and an evidence control cannot be hung off a reconstructed one. Nothing downstream reads it (see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store).

*Two tables rather than a `source` column on `control_setup`, and that is the safety property rather than a preference. A column would put reconstructed rows one predicate away from every read the scoreboard makes, and the rows are real draws over real names: nothing about their shape says which population they came from. Separate tables make the mixing impossible to write rather than merely wrong, on the same grounds `calibration_setup` is a table rather than a flag.*

Insert ControlSampler · UNIQUE (`setup_id`, `control_set`, `control_ticker`)

### `calibration_forward_return`
Grain: subject + kind + horizon. What a reconstructed setup and its controls did next.

| Column | Type | Note |
|---|---|---|
| `subject_id`, `subject_kind`, `horizon_days` | TEXT / TEXT / INTEGER | PK |
| `intended_date`, `actual_date` | TEXT | |
| `return_signed` | TEXT | signed by the setup's direction, as on the evidence side |
| `mfe_atr`, `mae_atr` | TEXT | **nullable here, as on the evidence side from 050**; until then the evidence side was NOT NULL and coalesced an undefined pair to nought |
| `excursions_absent_because` | TEXT | why the two above are null, on every row that has none |
| `filled_at` | TEXT | |

*The excursions are the one shape difference and it is deliberate. They are expressed in the subject's own ATR, `indicator_daily` holds no row for a session the lab was not running, and the calibration walk computes its averages in memory and discards them, so there is no ATR to express them in. Approximating one from daily bars is the stand-in the anchored clause of `reached-ceiling` already refuses by name. Null with the reason on the row rather than nought, because coalescing an undefined excursion to nought is a defect the evidence side already carries as an obligation raised at 3.5 and is not worth shipping twice.*

Insert ForwardReturnFiller · PK (`subject_id`, `subject_kind`, `horizon_days`) · Read by nobody

### `detector_error`
Grain: date + ticker + direction. What a detector could not decide, rather than what it skipped.

| Column | Type | Note |
|---|---|---|
| `as_of` | TEXT | the night |
| `ticker` | TEXT | |
| `direction` | TEXT | `long` or `short` |
| `message` | TEXT | what went wrong, so the same failure is recognisable across nights |
| `observed_at` | TEXT | |

Insert LongSetupDetector / ShortSetupDetector, **disjoint by `direction`** · Read by nobody

*Each detector issues its own insert rather than calling a shared helper, which is what lets `writer-ownership` attribute the write to the component that made it. The same price the `setup` insert pays, and for the same reason.*

### `below_floor`
Grain: date + ticker + direction + generation. The names a forward night's detector examined and did not record, each with every verdict it was given.

| Column | Type | Note |
|---|---|---|
| `as_of` | TEXT | the night |
| `ticker` | TEXT | |
| `direction` | TEXT | `long` or `short` |
| `generation` | INTEGER | whose gate set scored the vector, 0 until the switch night. Defaults to 0 |
| `check_results` | TEXT | JSON, every check with pass or fail, in the shape `setup.check_results` has |
| `failed_floor` | TEXT | which of the side's recording-floor clauses the name failed, comma separated |
| `observed_at` | TEXT | |

Insert LongSetupDetector / ShortSetupDetector, **disjoint by `direction`** · Read by nobody

**The recording floor, which had no statement in this document until 7.3.** A detector evaluates every check on every name it examines and writes a `setup` row only for a name that passes four of them: `tradable`, `moves-enough`, `uptrend` and `thrust` on the long side, and `tradable-shortable`, `moves-enough`, `downtrend` and `thrust` on the short. They are the cheap filters deciding whether the pattern test has anything to say about a stock at all, and two thousand rows a night of names that barely move would bury the record the research loop reads. The lists are `LongSetupDetector.RecordingFloor` and `ShortSetupDetector.RecordingFloor`. **Lowering the floor is a threshold change** and is not what this table is for: it widens the record and changes no rule.

**What the table buys is a replayable floor.** Until 7.3 the vector a name below the floor was scored with was computed in full and discarded, 7,201 names a side on the golden fixture's night, so a change to the floor could be measured only forward, and ARCHITECTURE ruled it so. **A table and not a flag on `setup`**, because membership in `setup` is itself the fact the minute fetch, the control pool, band 1, the plan and the vendor budget read, and a below-floor name was not flagged; one predicate away from all of those is where it must never be, on the grounds `calibration_setup` is a table rather than a flag. Forward nights only, because a calibration row is not evidence and nothing replays over one.

*A silent skip shrinks the recorded universe without anyone noticing. Every count downstream is over the setups that were recorded, so a name the detector could not read is simply absent: the night looks lighter, the counts stay plausible, and nothing says a name was lost. The run that lost one is recorded `partial` rather than `clean`.*

### `setup_signal`
Grain: setup + signal. The frozen point-in-time evidence.

| Column | Type |
|---|---|
| `setup_id`, `signal_name`, `value` | TEXT |
| `computed_at` | TEXT |

Insert SignalVectorizer · Insert SignalBackfiller, **disjoint by date and signal**: the vectorizer writes new rows nightly and the backfiller adds signals to old setups, and may never touch a signal the vectorizer owns for that date

### `signal_definition`
Grain: signal name. The library as data, seeded from the Signals section below and never authored beside it.

| Column | Type | Note |
|---|---|---|
| `signal_name` | TEXT PK | |
| `formula` | TEXT | traces to named stored columns, as the Signals section writes it with the markup normalised away |
| `source_columns` | TEXT | for the point-in-time test |
| `status` | TEXT | `active`, `candidate`, `rejected_correlation`. The section may state the first two and never the third, because a rejection is measured rather than declared |
| `is_null_control` | INTEGER | the planted tripwire |
| `decided_at` | TEXT NULL | when the last verdict was taken, whatever the verdict was. Null on a signal nothing has ruled on |
| `long_outcome` | TEXT NULL | `admitted`, `rejected_correlation`, `not_tightened` or `undecided`, over the long side's population |
| `long_because` | TEXT NULL | why, on every outcome that is not a plain admission |
| `long_tightness_before`, `long_tightness_after` | TEXT NULL | the outcome-similar ratio without the candidate and with it. **Null together**, because one of them alone is a number with no comparison in it |
| `long_correlation` | TEXT NULL | the highest absolute correlation measured against an admitted signal |
| `long_correlated_with` | TEXT NULL | which one |
| `short_outcome`, `short_because`, `short_tightness_before`, `short_tightness_after`, `short_correlation`, `short_correlated_with` | as above | the short side's, on exactly the same terms and **never added to the long side's** (see: Long and short are never pooled into one figure) |
| `observed_at` | TEXT NOT NULL | when the row last changed, which is what a read of the library bounds on |

Insert SignalAdmissionTest · Update SignalAdmissionTest · **Read by nobody yet**, ContextPacker reading it at 6.4 for the signals a pack screens and the scoreboard's signal-library panel at 6.8. One writer per operation and the same one: the seed and the verdict are one question asked twice, and splitting them would put the library's only writer in one component and the verdicts that change it in another

***`decided_at` replaced `admitted_on`, which was declared at 6.0(b) and is the one column the built table renames.*** A date only an admission can carry cannot date a rejection, and the failure table requires a rejection to be recorded with the correlation it was measured at and the signal it was measured against. The two could have sat side by side, and then every admitted row would carry the same instant twice: two columns that must agree are two columns that will not. So one column holds the date of the last verdict and `status` says which verdict it dates.

*Every figure is per side, and the alternative was direction in the grain.* `ceiling_bound` keys on direction because every column of that row is a per-direction figure. Here half the row is the specification, being the formula, the source columns and the null-control flag, and those are facts about the signal rather than about a side; a grain of signal plus direction would write each of them twice per signal and leave two copies that have to agree. The tightness ratio itself has to be per side whatever the grain: it is a mean over pairs of setups whose outcomes sit near each other, and a long outcome and a short outcome are signed in opposite senses, so a pooled population would measure the gap between the two books and report it as discrimination.

*Four outcomes reach three statuses, and the missing one is deliberate.* A candidate measured and found not to tighten stays a candidate, because nothing about it was refused and it can be asked again over a wider population; a fourth status would say the question was closed. What separates it from a candidate nobody has measured is `decided_at`, null on the second and set on the first. **The status is one value over two sides**: a signal earns its place if it tightens on either, because the library is one library and a signal admitted for shorts is computed on every setup, and it is refused only where both sides refuse it.

### `check_definition`
Grain: check name + direction + the session it was introduced on. The two gate lists as data, with the sessions each check was in force for, written from `SetupChecks` by the detectors on the night they run and never authored beside it (see: The signal library stays a spec section and gains a runtime table, reconciled in both directions).

| Column | Type | Note |
|---|---|---|
| `check_name` | TEXT | as `SetupChecks` names it |
| `direction` | TEXT | `long` or `short`. The two gate lists are two lists and a name can be on both (see: Long and short are never pooled into one figure) |
| `introduced_on` | TEXT | the first session the check was in force for |
| `retired_on` | TEXT NULL | the first session it was not. Null while it runs |
| `observed_at` | TEXT | when the row was written. Never touched after the insert |
| `retired_observed_at` | TEXT NULL | when the retirement was written, **null together with `retired_on`** |

Insert CheckRegister · Update CheckRegister · Read by `check-completeness` and SetupJournal, each holding a setup row to the list in force on its own night

**What it is for.** `check-completeness` claimed from 2026-08-26 that every setup row records a result for every check defined at its date, and compared every row against the list the build carries today, so a gate added to a detector would have read every historical row as missing it. From 7.2 a row is held to the checks this table says were in force on its `as_of`, read as of the instant the reading is for: a check registered afterwards is not yet in the list and a retirement written afterwards has not happened yet.

**The detectors register their list before they write a row under it.** A check the night's list holds and the register does not is introduced on that session, and one the register holds and the list does not is retired on it, so the register moves exactly when a detector's list does. **The first registration of a side reaches back** to the earliest session `setup` already holds a row of that side for, because those rows were written by a detector with no register to write to, and dating its list on the first registered night would read every one of them as recording checks not yet defined. A check retired and brought back is a second row, so the sessions a check was in force for are never overwritten.

### `control_setup`
Grain: setup + control ticker + set. Matched controls, drawn nightly, no API cost. Still one row per ticker per set per setup after the tight set was allowed to reach across sessions: where a name qualifies on several sessions the nearest is drawn and the others are not, so a set is five distinct names rather than one name seen five times.

| Column | Type | Note |
|---|---|---|
| `control_id` | TEXT PK | `{setup_id}-{control_set}-{control_ticker}`, so `forward_return` has one column to point at |
| `setup_id`, `control_ticker` | TEXT | |
| `control_set` | TEXT | `loose` or `tight` |
| `control_as_of` | TEXT NULL | the control's **own** session, which is the setup's on every row of both sets. It could be an earlier one for a tight draw for one day, from 2026-08-30 to 2026-08-31 |
| `match_quality` | TEXT | the distance on each matched dimension, separately, never as one number |
| `rank` | INTEGER | 1 to 5 by distance, ticker as the tiebreak. The fifth is by construction the worst of the five |
| `drawn_at` | TEXT | when the draw was made, which is what a point-in-time read of a night's controls bounds on |

Insert ControlSampler · UNIQUE (`setup_id`, `control_set`, `control_ticker`)

*Five per set per setup, drawn by deterministic nearest neighbour with no randomness, before the cap rather than after it (see: Controls are drawn by nearest neighbour on the matched dimensions, five per set, with no randomness). `match_quality` is per dimension because a single blended distance cannot say which dimension the match was bad on, and that is the thing a later reader needs.*

*`control_as_of` is the session a control's outcome is measured from. **The reach it was added for was tried, measured and reversed, and the column stays.** The tight set is declared to match on the trend ladder **and** the market mood, and within one night the mood excludes nobody, so on 2026-08-30 the operator ruled that the tight set draws from any session sharing the mood. `ForwardReturnFiller` had read a control's session off the setup it was drawn against, and its own comment said why: a control's session is the session it was drawn for. The ruling made that false for half the rows, and left alone it would have measured a tight control's ten-day return from the setup's night rather than the control's: a real return of a real stock over a real window, and the wrong one, which nothing downstream could have seen. Migration 035 added the column and backfilled every existing row from its setup. **On 2026-08-31 the reach was reversed**, because what it cost was the cancellation the paired difference exists to produce, measured at about six sevenths of the tight comparison's effective sample (see: The tight control set draws within the night, because a within-night draw controls the market mood exactly). Every row's `control_as_of` is its subject's session again. The column and the migration are kept for two reasons: the equality is a fact worth stating rather than inferring from a join, and it is asserted rather than assumed by `ControlSamplerTests.A_tight_control_is_drawn_from_the_subjects_own_session`, which fails if a tight draw ever leaves the night; and a reach that was tried and reversed is a better argument for keeping the instrument that measures it than for removing it.*

*`control_id` is a surrogate and it is here for one reason: `forward_return` records outcomes for setups and controls in one table, and a control had no single column to be named by. The alternative was a composite subject key on `forward_return`, which puts three columns in the subject of every outcome row and makes the point-in-time read wider than it needs to be.*

### `forward_return`
Grain: (setup or control) + horizon. Signed by direction, so a short that fell is positive.

| Column | Type | Note |
|---|---|---|
| `subject_id` | TEXT | a `setup.setup_id` or a `control_setup.control_id` |
| `subject_kind` | TEXT | `setup` or `control` |
| `horizon_days` | INTEGER | 1, 3, 5, 10 |
| `intended_date`, `actual_date` | TEXT | the calendar step from the subject's own session, and the session the horizon landed on. They differ wherever the step is not a session, which is every weekend as well as a holiday, and both are stored so the difference is visible; the run counts the slips per subject kind (see: An intended date is a calendar step from the subject's session, stated as such, and the slip past it is counted per subject kind) |
| `return_signed` | TEXT | signed by direction, so a short that fell is positive |
| `mfe_atr`, `mae_atr` | TEXT NULL | best and worst reached along the way, in ATR. **Null together where the subject has no range to state them in**, from 050, and never nought for that: a nought here read as a path that never went against its entry, which is a different fact from one that could not be measured (see: A gate handed an absent or degenerate quantity fails rather than passing) |
| `excursions_absent_because` | TEXT NULL | why the two above are null, on exactly the rows that have none, which the table asserts in both directions. The evidence side took this shape at 050; the calibration side had it from 036 |
| `filled_at` | TEXT | when the lab could first have known this, which is what bounds the read |

Insert ForwardReturnFiller · PK (`subject_id`, `subject_kind`, `horizon_days`)

*`subject_kind` is part of the key rather than a description, because a setup and a control are two different subjects and a surrogate that happened to collide would silently overwrite one with the other.*

*`mfe_atr` and `mae_atr` are in ATR and the give-up distance the ceiling compares them against is in daily ranges. **The conversion happens at the point of use and is named there**, rather than either figure being stored twice: two columns that must agree are two columns that will not (see: The ceiling is computed from the path, not from the terminal return).*

***This is the one stage that reads bars dated after its subject's own date, and it does so by design.** Point in time is not weakened for it: the fill's as-of is `filled_at`, the date the lab is filling on, and the read is bounded by that rather than by the setup date. A setup flagged on Monday has no ten-session outcome until the following Monday fortnight, and the row appears when it exists rather than being backdated to the night that flagged it.*

### `ceiling_bound`
Grain: date + direction. The win-rate bound perfect foresight could have reached, recomputed weekly.

| Column | Type | Note |
|---|---|---|
| `as_of` | TEXT | the week the bound was computed on |
| `direction` | TEXT | `long` or `short`, never pooled |
| `horizon_days` | INTEGER | 10, the scoring horizon the bound is defined at |
| `subjects` | INTEGER | how many closed setups the bound was computed over, which is the population |
| `bound` | TEXT | the fraction a system with perfect foresight could have won |
| `achieved` | TEXT | the fraction actually won over the same rows |
| `computed_at` | TEXT | when the bound was computed, which is what a read of it bounds on |

Insert CeilingCalculator · PK (`as_of`, `direction`)

*Direction is in the grain rather than in a note, because a pooled bound would inherit the short side's borrow assumption and the whole point of the figure is the gap between it and what was achieved (see: Long and short are never pooled into one figure).*

*A later week's bound over a larger population is a new dated row and the old one stays. Recomputed means recomputed, not revised: the gap narrowing over time is itself the thing a reader wants to see.*

### `scoreboard`
Grain: date + panel + generation. What each band showed on a given day, so a panel can be read back as it stood; a rebuild is a new generation beside the old, keyed on its own `computed_at`, and a reader takes the latest at or before its bound (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood).

| Column | Type | Note |
|---|---|---|
| `as_of` | TEXT | |
| `panel` | TEXT | which band and which figure |
| `direction` | TEXT NULL | `long`, `short`, or null where the panel is not per direction, as band 0 is |
| `figure` | TEXT | the number shown |
| `low`, `high` | TEXT NULL | the interval bounds, null on a panel that carries no interval |
| `n_rows` | INTEGER | the rows the figure was computed over |
| `n_effective` | INTEGER NULL | the effective observations, which is not the same number and is what a minimum sample is counted in |
| `population` | TEXT NULL | which rows the figure was computed over, said on the panel |
| `n_minimum` | INTEGER NULL | what `n_effective` must reach before the panel may be read. Band 1 only |
| `n_sessions` | INTEGER NULL | how many sessions carry a pair, which is the other half of what the panel is read against. Band 1 only |
| `n_minimum_sessions` | INTEGER NULL | what `n_sessions` must reach. Band 1 only |
| `reads_badly`, `reads_badly_because` | INTEGER NULL / TEXT NULL | whether the panel's own condition is met on this date, and what that condition is. **Present together and the store says so**: a panel reading badly with no reason is a mark nobody can act on, and a reason with no state is the caption this replaced. **Null on every panel that states no threshold**, which is most of them: a condition written in prose beside a figure is a caption and belongs on the page, and only a threshold a number can be held against belongs here. Built at 6.8, and until then ARCHITECTURE said band 0 reads red above 5% of the record while the caption was a static string, nothing formed the ratio and nothing rendered anything red. **Computed by the builder rather than by the page**, on the same terms every other figure here is (see: The averages are one implementation, computed nightly and drawn on demand) |
| `computed_at` | TEXT | when the panel was built, which is what a read of a night's scoreboard bounds on, and the generation it belongs to: in the key from 049, so a rebuild of a date is a second row per panel and the first stays as it stood |
| `withheld_because` | TEXT NULL | why a panel carries no figure, on every panel that carries none. A withheld panel is a row rather than an absence |

Insert ScoreboardBuilder · PK (`as_of`, `panel`, `direction`, `computed_at`) · Unique (`as_of`, `panel`, `computed_at`) where `direction IS NULL`

**The second index is what the primary key was believed to be.** SQLite treats nulls as distinct in a unique index, and `direction` is null on every band 0 panel, so the primary key never constrained an account-wide row: a rebuild of a date inserted a second copy of every band 0 panel rather than skipping it, and a third build a third copy. The panels carrying a direction were skipped correctly the whole time, which is why the defect read as the no-op it was half of. Migration 030 deduplicates and adds the partial index, and the insert drops its conflict target so any uniqueness violation is skipped rather than only the primary key's.

*`n_rows` and `n_effective` are both stored because they are different quantities: ten-day labels overlap, so the information in 3,180 rows is worth fewer than 3,180 independent observations and the ratio is a property of the realised series rather than of the design (see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured).*

*`n_effective` starts from rows rather than from nights, and that is what the control draw bought. Same-night setups share a market factor, which is why an unpaired figure over forty names is worth about one observation; the paired difference removes it by construction, so what is left inside a night is each name's own move against its own controls. Two discounts are then measured from the series: the label overlap across nights, and whatever common movement the matching failed to remove. A night that cannot say how its own pairs dispersed counts as one, which makes the pessimistic reading the limiting case rather than the assumption.*

*`n_sessions` and `n_minimum_sessions` are the second condition, stored beside the first rather than derived on the page. Checkpoint 3.6 fires on at least twenty sessions **and** at least 1802 effective observations, and the two are needed because they are settled by different things: sessions are what the block bootstrap needs before an interval exists at all, observations are what the decision needs before the interval means anything, and neither substitutes for the other. `PairedInterval.Estimate` has carried the session count since the interval was written and nothing read it: the builder took five of its six fields, this table had no column, and the panel rendered the row count and the effective count. The session count reached a reader only inside `withheld_because`, in prose, and that column is null the moment an interval exists, so it disappeared at exactly the point it starts deciding how much the interval is worth (see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured).*

*`n_minimum` is stored rather than looked up on the page, on the same grounds the interval is: the panel is read back as it stood, and a minimum that moved after a night was recorded would silently restate what that night's reading meant. It is set on band 1 alone, because band 1 is the panel checkpoint 3.6 fires on and a minimum on every panel would read as a threshold each of them is held to (see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion).*

*Every panel stores its own count, because a number without one is not shown at all.*

*And its population, because two panels on one page do not share one. Band 1 is over every flagged setup, which is what ARCHITECTURE means by the word: its worked night is twenty-two flagged of which fourteen pass every check, and all twenty-two are followed up. Band 2's rank-decile curve is over the capped candidates, because a decile is a position in an ordering and only a candidate carries a rank. At the calibrated thresholds the two differ by three orders of magnitude, so a stored panel that could not say which rows it used would be a figure a later reader compares against the wrong one (see: The subject is the flagged setup population, not the trade log).*

---

## Signals

The library. Every quantity the frozen row can carry, its formula, and the stored columns it reads.
**This section is the specification and `signal_definition` is the runtime form of it**, seeded from
here by SignalAdmissionTest since 6.2 and reconciled against it in both directions by
`signal-library`, so a signal in one and not the other fails rather than reading as new. It stays a
section rather than becoming a document of its own (see: The corpus is nine documents, and the ninth records where the strategy came from),
and it is not retired into the store, because a formula and its source columns are what the
point-in-time test is asserted against and what makes a signal proposable at all, and a
specification nobody can diff is not one (see: The signal library stays a spec section and gains a runtime table, reconciled in both directions).

**Every signal traces to named stored columns, and one does not.** That is the point of writing the
library down: the source columns are what the point-in-time test is asserted against, and a signal
whose formula reads something nothing stores cannot be computed, cannot be replayed, and cannot be
proposed against. The one that does not trace is named at the bottom, as a finding rather than as an
assumption.

**Status here is `active` or `candidate`, and never the third value the table can hold.** A
rejection at the correlation limit is something SignalAdmissionTest measured rather than something
this section declares, which is why the reconciliation admits exactly one difference between the two
sides: a stored row may read `rejected_correlation` where this section reads `candidate`, and no
other disagreement passes. Active means SignalVectorizer freezes it on every setup, and
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
| `ema_gap_21_50_over_avg` | \|`ema_gap_21_50`\| over the mean of \|`ema_gap_21_50`\| across the last 20 sessions. Below one is a squeeze | `indicator_daily.ema_21`, `indicator_daily.ema_50` | active |
| `ceiling_distance_ranges` | the nearest of the 21-day average, the 50-day average and the anchored average price, over `adr_20` × close. The anchored level is included only where one was computed | `daily_bar.adj_close`, `daily_bar.close`, `indicator_daily.ema_21`, `indicator_daily.ema_50`, `indicator_daily.adr_20`, `anchored_vwap.value` | active |
| `ladder_grade` | the grade TierClassifier wrote for that session | `indicator_daily.ladder_grade` | active |

*`ema_50_distance` is the extension-from-the-long-average measurement the architecture lists as
missing. It costs nothing beyond a subtraction over two columns already stored, so it is active
rather than a candidate.*

***The two added at 6.1 are quantities two gates already compared, not new evidence about a name.***
`averages-squeezing` compares the ratio and `reached-ceiling` compares the folded distance, and until
6.1 the row froze what each was computed from rather than what each was compared. The gap is signed
and the ratio is absolute, and the ceiling is a disjunction over three levels, so a replay rebuilding
either quantity from its inputs would have been a second implementation of a step the detector had
already taken; both gates were therefore unjudgeable and a version moving either threshold was
refused at admission (see: A version whose moved gate cannot be judged from the frozen signals is refused at admission).
**They widen the library and not its reach**, which is why they arrive here rather than through the
admission route a genuinely new axis takes, and why they cost nothing against the corrected threshold:
nothing new is screened (see: The correction threshold scales with signals screened, not signals shown).
SignalBackfiller put both onto every setup recorded before the change, which is what a signal added to
the library is supposed to cost.

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
| `closes_beyond_floor` | sessions in the pullback closing below the 21-day average **as at that session**, long; above the 50-day, short. The average is a series over the window, not the value at the as-of date | `daily_bar.adj_close` | active |

*`closes_beyond_floor` reads a different average per direction, because the checks do: `held-floor`
is the 21-day and `no-reclaim` is the 50-day. One signal rather than two, because the pair is one
question asked of whichever average that direction's floor is.*

### The trade geometry

| Signal | Formula | Source columns | Status |
|---|---|---|---|
| `trigger_price` | as written, a raw price. **Absent where the setup has none** | `setup.trigger_price` | active |
| `stop_price` | as written, a raw price. **Absent where the setup has none** | `setup.stop_price` | active |
| `stop_distance_ranges` | \|trigger − stop\| / (`adr_20` × close). **Absent where the setup has none** | `setup.stop_distance_ranges` | active |
| `trigger_distance_ranges` | \|trigger − close\| / (`adr_20` × close). **Absent where the trigger is** | `daily_bar.close`, `setup.trigger_price`, `indicator_daily.adr_20` | active |

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
| `intraday_pullback_shape` | the fraction of each pullback session's range travelled after midday | `intraday_bar` | candidate |
| `day_of_month` | the calendar day of `as_of`, meaning nothing | `setup.as_of` | candidate |

*`day_of_month` is the planted null control and carries `is_null_control` on its stored row. It
is declared here rather than at 6.4 so the column it reads is on the record; planting it is
ContextPacker's job (see: One meaningless signal is planted in the conditional tables).*

***The status cells carry a status and nothing else, from 6.2.*** Two of them read "candidate, owed
at 4.2" and "candidate, planted at 6.4", which is a status and a deferral in one cell. The first had
gone stale without anything being able to say so: 4.2 landed on 2026-08-24 and `intraday_bar` has
existed since, so what the cell deferred had arrived and the cell still read as pending. What each
of the two is actually waiting for is a producer rather than a checkpoint, which is the sentence
above about a candidate costing nothing until something computes it, and it is said there once
instead of in two cells that drift separately.

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
| `plan_run` | session + observation | Insert PlanBuilder. What one evening's plan stage did, with its refusals counted by reason |
| `trigger_resolution` | plan | Insert TriggerResolver. What the session did to each plan resting in it, one row per plan and no price copied from either the plan or the bar |
| `trigger_run` | session + observation | Insert TriggerResolver. What one replay walked beside what it decided |
| `trade_order` | order id | Insert RiskGate only (see: RiskGate is the sole writer of orders, for both directions and every version). Blocked orders written with a reason, never dropped |
| `order_run` | session + observation | Insert RiskGate. What one evening's gate decided, with its refusals and reductions counted by cap |
| `fill` | fill id | Insert PaperBroker · Insert PositionManager, **disjoint by leg**: the broker writes the `entry` and the manager writes the `exit` and the `trim`, and neither may write the other's. One row per fill, with what it cost and what the cost was computed from |
| `position` | position id | Insert PaperBroker · Update PositionManager. One writer per operation and not two stages that can both close a row: from 4.8 the broker opens a position and the manager is the only thing that trims, arms or closes one (see: Every exit is PositionManager's and every entry is PaperBroker's). Carries `risk_intended` and `risk_realised` so share rounding and entry slippage are visible rather than assumed away (see: Equity is a fixed $100,000 notional that never compounds) |
| `fill_run` | session + observation | Insert PaperBroker. What one evening's fill stage priced, and the book it was handed |
| `manage_run` | session + observation | Insert PositionManager. What one evening's two rule sets closed, trimmed and armed, with each exit counted under the rule that produced it |
| `trade` | trade id | Insert TradeJournal. Result in R after the borrow a short is charged, which is the whole reason it is a row rather than a view over `position` |
| `plan_audit` | trade | Insert PlanAudit. Three pairs and not one field: execution at both ends, the plan's stop against where the trade ended, and the size the plan carried against the size the gate placed (see: The audit holds three pairs and they answer three different questions) |
| `trade_run` | session + observation | Insert TradeJournal. What one evening's journal wrote, counted by side |
| `audit_run` | session + observation | Insert PlanAudit. What one evening's audit read and wrote, with the two figures worth reading off a total |
| `loss_class` | trade | Insert LossClassifier · Update LossClassifier. Two answers per loss, arriving at different times: the mechanism at the close and the aftermath when the horizon does (see: A loss awaiting its horizon carries no aftermath, and that is not the same as being unclassified) |
| `loss_run` | session + observation | Insert LossClassifier. What each of the two passes wrote, counted apart |

### The plan, and the size it carries

Columns of `trade_plan`. Built at 4.16, and the columns are the ones that checkpoint owes rather than the whole eventual
shape.

| Column | Form | Why |
|---|---|---|
| `plan_id` | TEXT, the key | One plan per capped candidate **per live version** from 5.1. A second write for one pair is refused by the key rather than by the stage remembering to check |
| `setup_id` | TEXT | The candidate the plan was written for, carried rather than keyed since the fan-out |
| `variant_id` | TEXT | The version that plan belongs to, carried so a row reads without a join (see: Targets and minimum samples are written at creation and are immutable) |
| `as_of` | TEXT | The evening the plan was written on |
| `live_session` | TEXT | The session it is live in, stored rather than derived |
| `trigger_price`, `give_up_price`, `give_up_distance` | TEXT | Prices, and the distance between them in money. **The final pullback session's regular-hours extremes with the give-up point 0.1 ADR beyond, from 4.18, and not `setup.trigger_price` and `setup.stop_price`**, which are the screening geometry a whole dip wide and were what this stage copied here from 4.16 until the 4.13 sign-off found it (see: The order prices are derived from the final pullback session's minutes, not from the screening geometry) |
| `shares` | INTEGER, `> 0` | The size, which is PlanBuilder's and not RiskGate's |
| `equity`, `risk_fraction`, `risk_budget` | TEXT | What the size was computed from, so a plan can be re-derived without knowing which constants were in force |
| `risk_at_stake` | TEXT | What the rounded share count actually risks, at or below `risk_budget` |
| `ticker`, `direction` | TEXT | The name and the side, carried so a plan reads without a join |
| `observed_at` | TEXT | When the plan was written, which is what a point-in-time read of an evening bounds on |

**There is no variant column and the grain above is not wrong.** Columns are owed at their
checkpoint, and there is one baseline and no versions, so the key at 4.16 is the setup alone. 5.1
adds the fan-out. A variant column now would hold one value and would give the key nothing to refuse.

**`live_session` is a column rather than an inference**, because deriving it means stepping a
calendar forward over weekends, which is the shape that made `intended_date` differ across every
weekend in the forward-return table. 4.5 asserts that a plan is never resolved against a session at
or before its own date, and it should be reading a stored fact.

**`risk_budget` sits beside `risk_at_stake` so the rounding is visible.** The share count rounds
down, so a plan risks at or below the budget it was sized from. One column would state either a
number no trade will ever lose or an outcome with no way to tell rounding from a different rule. It
is the shape `position` already declares with `risk_intended` beside `risk_realised`.

**A refused candidate is not a row here, and `plan_run` counts it instead.** A capped candidate gets
no plan because its geometry is absent, because its trigger and give-up point are the same price, or
because the risk budget cannot buy one share. The first two are already in `setup`, so a per-setup
refusal table would be a second statement that could disagree with the first, which is the ruling
WatchlistPublisher took over a watchlist table and VwapEngine took over the day's high and low. The
third is not derivable from `setup` alone, since it depends on the budget, and none of the three is
derivable as a *count for a night* without replaying the stage. So the counts are stored, one column
per reason: a single `refused` total would let the reason that is a defect hide inside the reason
that is ordinary arithmetic.

### What the session did to each plan

Columns of `trigger_resolution`. Built at 4.5, and the columns are the ones that checkpoint owes.

| Column | Form | Why |
|---|---|---|
| `setup_id` | TEXT, the key | One resolution per plan. A plan is live in exactly one session, so a resolution per plan is a resolution per plan per session with the second half derivable |
| `plan_id` | TEXT | The plan this row belongs to, and the key everything below the plan is joined on from 5.1. One per setup per live version |
| `variant_id` | TEXT | The version that plan belongs to, carried so a row reads without a join (see: Targets and minimum samples are written at creation and are immutable) |
| `live_session` | TEXT | The session that was walked, read from the plan rather than stepped to |
| `outcome` | TEXT, one of three | `touched`, `not_touched`, `unresolvable`. Three and not two, because no fill and cannot-resolve are different answers |
| `touched_at` | TEXT NULL | The minute the trigger was reached, and the only thing that carries a time. Constrained to be present exactly when the outcome is `touched` |
| `minutes_walked` | INTEGER | How many of the name's minutes the resolution was taken over, so a decision cannot be told apart from one taken over a session the store barely holds |
| `unresolved_because` | TEXT NULL | Which of the two blindnesses it was. Constrained to be present exactly when the outcome is `unresolvable` |
| `ticker`, `direction` | TEXT | The name and the side, carried so a resolution reads without a join |
| `observed_at` | TEXT | When the replay ran, which is what a point-in-time read of a session bounds on |

**Three outcomes, and the third is the one that would otherwise disappear.** A plan whose name traded
all day and never reached its trigger did not fire, which is the ordinary result. A plan whose session
holds no stored minute was never asked: the fetch did not run, the name was not in it, or the live
session was not a trading day at all. Folding the second into the first would record a strategy that
declines to trade on exactly the nights the lab was blind, and every rate computed from these rows
would be wrong in the flattering direction with nothing to show it
(see: A gate handed an absent or degenerate quantity fails rather than passing).

**No price is copied here.** The trigger price is `trade_plan`'s and the bar is `intraday_bar`'s, and
`touched_at` addresses that minute exactly. Restating either would be a second statement of a fact the
store already holds, which is the ruling WatchlistPublisher took over a watchlist table and VwapEngine
took over the day's high and low. PaperBroker at 4.7 prices a fill from the plan and the minute named
here, so a gap fill reads the same bar the touch was found in.

**A session with plans resting in it and no minutes is recorded partial rather than clean.** The row
carries `minutes_walked` and `names_walked` for that reason: a blind night reported as a night on
which nothing triggered is the shape that cost this lab a second evening of evidence, and the figure
that says so has to be on the morning's run row rather than in a rate three months later.

### The order, and the caps that shaped it

Columns of `trade_order`. Built at 4.6, and the columns are the ones that checkpoint owes.

| Column | Form | Why |
|---|---|---|
| `order_id` | TEXT, the key | One order per plan. A plan triggers at most once, because the resolver records the first minute that reached the trigger and no later one moves it |
| `plan_id` | TEXT | The plan this row belongs to, and the key everything below the plan is joined on from 5.1. One per setup per live version |
| `variant_id` | TEXT | The version that plan belongs to, carried so a row reads without a join (see: Targets and minimum samples are written at creation and are immutable) |
| `setup_id` | TEXT, unique | The plan this order came from, which is what `plan_audit` joins on at 4.9 |
| `triggered_at` | TEXT | The minute the trigger was reached, read from `trigger_resolution` |
| `status` | TEXT, one of two | `placed` or `blocked`. A blocked order is a row and never an absence |
| `planned_shares`, `shares` | INTEGER | What the plan carried and what the caps granted. Both, because a reduction that overwrote the first would leave the plan and the order agreeing about a number the caps had changed |
| `risk_at_stake` | TEXT | What the granted size actually risks, at the plan's give-up distance. Nought on a blocked row |
| `bound_by` | TEXT NULL | The cap that changed the answer, null where nothing bound. A placed row may carry one, which is the reduction case |
| `blocked_because` | TEXT NULL | What that cap saw, in figures. Present exactly when the status is `blocked` |
| `live_session` | TEXT | The session the order belongs to, read from the plan rather than derived |
| `ticker`, `direction` | TEXT | The name and the side, carried so an order reads without a join |
| `observed_at` | TEXT | When the gate ran, which is what a point-in-time read of a session bounds on |

**The table is `trade_order` and not `order`, which this document declared until 4.6.** `order` is a
reserved word in SQLite, so every statement touching it would carry quotes and one unquoted use would
be a syntax error found at runtime. The half that decided it is that every parser in the verification
harness reads an unquoted identifier after `CREATE TABLE` or `INSERT INTO`, so a quoted table is one
`writer-ownership`, `bar-append-only`, `price-storage-form` and `point-in-time` cannot see. A store
nothing scans is the shape this corpus keeps finding, and it is not worth buying with a name.

**No give-up price is copied here.** A reduction keeps the plan's give-up price, so there is one
give-up price for a trade and it lives in `trade_plan`. A column here would be a second statement of
it that a later reduction could move, and R would then depend on which row a reader opened
(see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it).

**Two of the six limits are not applied by RiskGate and both are named rather than absent.** Risk per
trade is what the plan was sized from, so it is asserted and a plan over its budget stops the stage
rather than being trimmed. The give-up distance cap is `exit-tight` at detection, so a plan that
reached a trigger cleared it hours before, and re-applying it here would be a second implementation of
a gate that could disagree with the first on a day the daily range was restated.

**The count caps see only the session being walked, until `position` exists.** A position is
PaperBroker's row and arrives at 4.7, so what RiskGate can count today is what it has placed inside
one session: a position held overnight occupies no slot the next morning. That makes the caps looser
than the design rather than tighter, which is the direction that flatters, so it is recorded here and
carried rather than left to be inferred from a cap that never binds.

### What a resting order got, and the position it opened

Columns of `position`. Built at 4.7, and the columns are the ones that checkpoint owes.

| Column | Form | Why |
|---|---|---|
| `position_id` | TEXT, the key | One position per plan today, so the key is the plan's own identifier. 5.1 fans plans out per variant and this follows `trade_plan`'s |
| `plan_id` | TEXT | The plan this row belongs to, and the key everything below the plan is joined on from 5.1. One per setup per live version |
| `variant_id` | TEXT | The version that plan belongs to, carried so a row reads without a join (see: Targets and minimum samples are written at creation and are immutable) |
| `setup_id` | TEXT, unique | A second position for one plan is unexpressible rather than merely unwritten |
| `order_id` | TEXT | The order this came from, so a position reads back to the cap that granted it |
| `ticker`, `direction` | TEXT | The name and the side, carried so a position reads without a join |
| `status` | TEXT, one of three | `unfilled`, `open`, `closed`. Three and not two, because a placed order the session quoted no usable book for is a row rather than an absence |
| `opened_session` | TEXT | The session the entry was priced in |
| `opened_at` | TEXT NULL | The minute the entry filled. Null exactly on an unfilled row |
| `shares` | INTEGER | What was actually bought or sold short, nought exactly on an unfilled row |
| `entry_fill_id`, `entry_price` | TEXT NULL | The fill and the price it got. Null exactly on an unfilled row |
| `value_at_entry`, `fraction_at_entry` | TEXT / REAL NULL | What the position was worth once filled, and that as a fraction of the account. Here because the position-size cap is applied by RiskGate at the plan's trigger price and the fill is a spread worse, so the overshoot is a figure on the row rather than an argument in a comment |
| `risk_intended`, `risk_realised` | TEXT NULL | The share count against the give-up distance the plan named, and the same count against the distance from the price the fill actually got. The two differ by the entry slippage, and R is taken over the second |
| `unfilled_because` | TEXT NULL | Which of the two blindnesses it was. Present exactly on an unfilled row |
| `borrow_rate_assumed`, `borrow_availability` | TEXT NULL | The two unmodelled short assumptions, present exactly on the shorts |
| `closed_session`, `closed_at`, `exit_fill_id`, `exit_price`, `exit_reason` | TEXT NULL | The exit. Present exactly on a closed row |
| `realised_pnl` | TEXT NULL | The money, at the two prices the fills actually got |
| `realised_r` | REAL NULL | The result in R, which is a ratio and not money. Below minus one wherever the exit cost more than the risk it was measured against, which a gap through the give-up point always does |
| `trim_fill_id`, `trimmed_at`, `trimmed_shares`, `trim_price`, `trim_realised_pnl` | TEXT / INTEGER NULL | The short trim at 3R, which reduces a position and leaves it open. Present exactly on a trimmed row, and asserted by a test rather than by a CHECK: `fill` holds a foreign key into this table, so rebuilding it to add one would rewrite that clause as a side effect of a tidiness. `realised_pnl` on the close is this figure plus the close's own, so a reader is never asked to derive the first half from a fill row nothing points at |
| `exit_armed_session`, `exit_armed_reason` | TEXT NULL | An exit decided in one session and filled at the open of the next, with the rule that decided it. The long trail needs this because it is evaluated on a daily close and the next price after that close is the next session's open; the short reclaim needs it in the one case where the store holds no later minute of the session |
| `observed_at` | TEXT | When the position was opened, which is what a point-in-time read bounds on |
| `closed_observed_at` | TEXT NULL | When the close was observed. The second half of the bound |
| `trim_observed_at` | TEXT NULL | When the trim was observed. The third |

**Three stamps, because this is the one updated table in the phase.** A row is inserted when it
fills, updated when a short is trimmed and updated again when it closes, so a single stamp would
answer a replay standing between any two of those with the state the row ended in. Every read
bounds `observed_at` for whether the row exists at all, `trim_observed_at` for whether the trim
had happened yet and `closed_observed_at` for whether the close had, and either event observed
after the as-of reads as not having happened, which is what it was.

**`exit_armed_session` carries no stamp of its own and does not need one**, because the column is
the session that armed the exit rather than the fact that something did. A session later than the
as-of is a reading the lab had not made yet and reads as unarmed, and the reason goes with it.

**No give-up price is copied here.** It is `trade_plan`'s and a reduction never moves it, so a column
here would be a second statement of the one price R is measured against. That is the ruling
`trade_order` took at 4.6 and `trigger_resolution` at 4.5.

**A position can end three ways from 4.8, and one component decides which.** The give-up point is a
resting instruction the plan carried from 18:30; the long trail and the short reclaim are rules
PositionManager evaluates. The exit is whichever is reached first, which is a comparison across
rules, and a comparison cannot be made by two components each of which sees one side of it. So the
give-up exit moved out of PaperBroker with the rest of them, and `exit_reason` is `give-up`, `trail`
or `hourly-reclaim` (see: Every exit is PositionManager's and every entry is PaperBroker's).

### What one end of one trade cost

Columns of `fill`. Built at 4.7 and given a third leg at 4.8. Every leg is the same shape, priced by
the same rules.

| Column | Form | Why |
|---|---|---|
| `fill_id` | TEXT, the key | One per end of a trade |
| `plan_id` | TEXT | The plan this row belongs to, and the key everything below the plan is joined on from 5.1. One per setup per live version |
| `variant_id` | TEXT | The version that plan belongs to, carried so a row reads without a join (see: Targets and minimum samples are written at creation and are immutable) |
| `position_id`, `setup_id` | TEXT | What it belongs to |
| `session_date` | TEXT | The session the fill happened in, which for an exit is not the session the position opened in |
| `ticker`, `direction` | TEXT | The name and the side, carried so a fill reads without a join |
| `leg` | TEXT, one of three | `entry`, `exit` or `trim`. Named `leg` and not `end` because `END` is a SQLite keyword, on exactly the grounds `trade_order` is not called `order`. `trim` arrived at 4.8 by migration 045 rebuilding the table, because a trim is one end of nothing: the position it reduces stays open, and calling it an exit would make `exit_fill_id` ambiguous on every trimmed short |
| `filled_at` | TEXT | The minute |
| `basis` | TEXT, one of two | `slipped` or `gapped`. The countable half of how a fill was priced, so a night groups without parsing a price |
| `resting_price` | TEXT | The price the rule named, being the trigger on an entry, the give-up point or the minute's open on an exit, and the 3R level on a trim. Never the price the fill got, which is the next column |
| `price` | TEXT | What it actually got |
| `slippage` | TEXT | The money per share the crossing cost, nought on a gap because the gap is the adverse move |
| `shares` | INTEGER | |
| `spread_bps` | REAL NULL | The quote charged, in basis points of the mid. A statistic and not money. Present on every slipped fill; present on a gap fill where the session had a quote, so the charge that was not made is legible |
| `spread_pass` | TEXT NULL | `after_open` or `before_close`, so a fill says which of the session's two samples it was charged |
| `quote_lag_seconds` | INTEGER NULL | How stale the quote already was, from the older of its two sides |
| `straddle_seconds` | INTEGER NULL | How far apart the vendor stamped the two sides of that quote. Recorded and never acted on |
| `observed_at` | TEXT | |

**The straddle is recorded because a stored spread is a figure across two instants.** The vendor
stamps a quote's bid and its ask separately: on the capture of 2026-09-01 AAPL's two sides were 32
seconds apart, one name on one response. So `spread_bps` need not be a width that existed at either
stamp, and on a name whose book moved between them it can be wider or narrower than anything a trader
could have crossed. It is charged anyway and the gap is stored, on the same terms the capture already
takes for the vendor's delay: the corpus holds one measurement of a straddle, and a threshold that
widened or refused a quote would be a number authored from it
(see: A straddled quote is charged and the straddle is recorded, never widened or refused).

### The four run rows of the trading night

One row per run of a stage, on the pattern `vwap_run` and `intraday_fetch` set. All four carry
`session_date` and `observed_at` as their key, an `outcome` of `clean`, `partial` or `failed`, and a
`stopped_because` naming which shape of nothing a night was. What differs is the middle, and the middle
is where each stage declines to state one total: a single `refused` or `blocked` figure would let the
reason that is a defect hide inside the reason that is ordinary arithmetic.

#### `plan_run`
Grain: session + observation. What one evening's plan stage did, at 18:30.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `live_session` | TEXT | the session the evening's plans are live in, stored rather than stepped to |
| `candidates`, `planned` | INTEGER | capped candidates, and plans written for them |
| `candidates_planned` | INTEGER NULL | The candidates behind the plans, which is the same number as `planned` only while one version is live. `planned` counted candidates until the fan-out and counts plans now, so a night with two versions would read as twice the funnel from the one figure. Null on every row written before 5.1, because the two were one number then and inventing a value would state a figure nobody measured |
| `refused_absent_geometry`, `refused_equal_prices`, `refused_below_one_share` | INTEGER | the three reasons a candidate got no plan, counted apart |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | which of the three shapes of nothing the night was |

#### `trigger_run`
Grain: session + observation. What one replay walked and what it decided, at 21:05.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `setup_as_of` | TEXT NULL | the evening the session's plans were written on, null where none rested |
| `plans`, `touched`, `not_touched`, `unresolvable` | INTEGER | what the replay found, with the third outcome counted apart from the second |
| `names_walked`, `minutes_walked` | INTEGER | what the clock actually handed out, which is the figure that says a night was blind |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | partial where plans rested and no minute was walked |

#### `order_run`
Grain: session + observation. What one evening's gate decided, at 21:10.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `triggers`, `placed`, `reduced`, `blocked` | INTEGER | what the gate was given and what it did |
| `blocked_open_positions`, `blocked_open_shorts`, `blocked_below_one_share` | INTEGER | the three ways an order was refused |
| `reduced_position_size`, `reduced_total_risk` | INTEGER | the two proportional caps, counted apart, because a night of trims is a different night from a night of blocks |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | a night of blocked orders is clean: the caps binding is what they are for |

#### `fill_run`
Grain: session + observation. What one evening's fill stage priced, at 21:15.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `open_at_start` | INTEGER | the book RiskGate read at 21:10, reported rather than walked, because a night that opened four and closed none is a night the next morning's fifth trigger is refused on |
| `orders_placed`, `entries_filled`, `entries_unfilled` | INTEGER | what it was given and what it priced |
| `gapped`, `slipped` | INTEGER | how the fills were priced, counted apart |
| `names_walked`, `minutes_walked` | INTEGER | what the clock actually handed out, which is the figure that says a night was blind |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | partial where the session was never sampled or held no minute |

**`exits_filled` and `open_at_end` were dropped at 4.8 rather than kept reading nought.** Exits moved
to PositionManager, so both would report zero on every future night, and a stage's record that can
only report zero is one a later session reads as broken. That is the ruling migration 044 took over
the two `vwap_run` counters one checkpoint earlier. The night's book at its end is `manage_run`'s,
because the manager is the last stage that can change it.

#### `manage_run`
Grain: session + observation. What one evening's two rule sets did, at 21:20.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `open_at_start`, `open_at_end` | INTEGER | the positions handed to the manager, being everything open at any point in the session including the entries priced five minutes earlier, and what was still open when it finished |
| `longs_managed`, `shorts_managed` | INTEGER | the two rule sets are separate code paths, so the two populations are separate figures (see: Long and short are never pooled into one figure) |
| `closed_give_up`, `closed_trail`, `closed_reclaim` | INTEGER | each exit under the rule that produced it. A night of trail exits is a different night from a night of stop-outs, and a single total lets the one that is a finding hide inside the one that is ordinary |
| `trimmed` | INTEGER | shorts that reached 3R and were reduced, which is not an exit and is counted apart from them |
| `exits_armed` | INTEGER | exits decided on this session and filled at the next open. The rule that armed each one is on the position row |
| `gapped`, `slipped` | INTEGER | how the fills were priced, counted apart, on the same terms `fill_run` counts entries |
| `held_no_quote` | INTEGER | positions a rule reached and the session quoted no usable book for, held rather than closed at a price nobody measured. Counted once per position however many minutes the hold lasts |
| `closed_in_their_own_session` | INTEGER | the size of an approximation rather than a result. RiskGate runs at 21:10 and reads the book as it stood coming into the session, so a position opened at 09:31 and closed at 09:45 still occupied a slot the 10:00 trigger was refused on (see: RiskGate reads the book as it stood coming into the session, and what that costs is counted) |
| `names_walked`, `minutes_walked` | INTEGER | what the clock actually handed out, which is the figure that says a night was blind |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | partial where the session was never sampled or held no minute |

### What a closed position came to

Columns of `trade`. Built at 4.9. One row per closed position, written the evening the position closed.

| Column | Form | Why |
|---|---|---|
| `trade_id` | TEXT, the key | One trade per position, so the key is the position's own identifier, which is the plan's. 5.1 fans plans out per variant and this follows |
| `plan_id` | TEXT | The plan this row belongs to, and the key everything below the plan is joined on from 5.1. One per setup per live version |
| `variant_id` | TEXT | The version that plan belongs to, carried so a row reads without a join (see: Targets and minimum samples are written at creation and are immutable) |
| `position_id`, `setup_id` | TEXT, unique | A second trade for one position is unexpressible rather than merely unwritten |
| `ticker`, `direction` | TEXT | Carried so a trade reads without a join, which is the point of the table |
| `opened_session`, `closed_session` | TEXT | The two ends |
| `held_calendar_days` | INTEGER | What the borrow is charged on, because borrow accrues overnight rather than per session and a Friday-to-Monday hold costs three days |
| `held_sessions` | INTEGER | What a person reads, counted from the daily bars the store holds rather than from an authored calendar (see: A session is a date the store holds minutes for, and no calendar is authored here). The two differ over every weekend, and both are here rather than leaving a reader to guess which one they are looking at |
| `entry_price`, `exit_price`, `exit_reason` | TEXT | What the two fills got and which rule ended it |
| `shares`, `trimmed_shares` | INTEGER | The count the entry opened with, and what a short trim took out of it before the close. The close covered the difference |
| `value_at_entry`, `risk_realised` | TEXT | Carried from the position because the borrow is charged on the first and R is taken over the second |
| `gross_pnl` | TEXT | The money before borrow, which is the trim's plus the close's on a trimmed short |
| `borrow_rate_assumed`, `borrow_cost` | TEXT NULL | Present exactly on the shorts, in both directions. The rate is the one that position stamped on itself when it opened, so a trade closed today is charged what its own position assumed rather than what the constant says now |
| `net_pnl`, `result_r` | TEXT / REAL | After borrow. **`result_r` is after and `position.realised_r` is before**, so the two are equal on every long and differ by the borrow line on every short. Both names stay, because one name over two numbers is the fault this corpus keeps finding |
| `exit_armed_session`, `armed_sessions_waited` | TEXT / INTEGER NULL | Present exactly when a rule armed the exit on an earlier session. The trail fills at the next open the store holds minutes for, so a session the lab was blind on postpones the fill rather than reconsidering it, and this is the size of that on each trade rather than an argument about how often it happens |
| `observed_at` | TEXT | One stamp, because nothing updates this table |

**One stamp and not three.** A trade is written when a position closes and is never revisited: a
correction to a trade would be a second answer to what a night produced, which the append-only
records in this store refuse everywhere else. `position` needs three because it is inserted, trimmed
and closed; this is inserted and left alone.

### The plan against what happened

Columns of `plan_audit`. Built at 4.9. Three pairs answering three different questions
(see: The audit holds three pairs and they answer three different questions).

| Column | Form | Why |
|---|---|---|
| `trade_id` | TEXT, the key | The trade it audits, and a foreign key into it, which is what makes the ordering between the two stages expressible rather than remembered |
| `plan_id` | TEXT | The plan this row belongs to, and the key everything below the plan is joined on from 5.1. One per setup per live version |
| `variant_id` | TEXT | The version that plan belongs to, carried so a row reads without a join (see: Targets and minimum samples are written at creation and are immutable) |
| `setup_id`, `ticker`, `direction` | TEXT | Carried so an audit reads without a join |
| `planned_trigger`, `executed_entry`, `entry_difference`, `entry_difference_bps`, `entry_basis` | TEXT / REAL | **The first question, execution at the entry.** The price the instruction named against the price it got, positive where the trade was worse off. Basis points beside the money because six cents on a six-dollar stock and six cents on a four-hundred-dollar one are two different facts. `entry_basis` is `slipped` or `gapped`, so a gap is never read as slippage |
| `exit_resting_price`, `executed_exit`, `exit_difference`, `exit_difference_bps`, `exit_basis`, `exit_reason` | TEXT / REAL | The same question at the exit, against the price the exit rule named rather than against the plan's stop |
| `planned_give_up`, `give_up_difference`, `give_up_difference_bps` | TEXT / REAL | **The second question, and not the first restated.** The plan's stop against where the trade actually ended. Equal to the exit pair on a give-up exit and a different quantity on every other one: a trail exit ends nowhere near the give-up point by design, so reading the two as one would report every winner as an enormous execution failure |
| `planned_shares`, `executed_shares`, `shares_difference`, `reduced_because` | INTEGER / TEXT NULL | **The third question, the gate.** The size the plan carried against the size that was placed, with the cap that bound or null where none did. RiskGate may reduce a size and may never recompute one, so this is an intention against an outcome rather than two runs of one formula (see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it) |
| `risk_intended`, `risk_realised`, `risk_difference` | TEXT | The two above in the unit everything is scored in |
| `observed_at` | TEXT | |

**Every difference is derived from the two prices, never copied from `fill.slippage`.** An audit
reading the model's own charge would be comparing a number against itself, and the two legitimately
differ on a gap, where the model charges nothing and the price moved anyway.

#### `trade_run`
Grain: session + observation. What one evening's journal wrote, at 21:25.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `closed_in_session`, `journalled` | INTEGER | what it was handed and what it wrote, which differ only on a rerun |
| `longs`, `shorts` | INTEGER | counted apart (see: Long and short are never pooled into one figure) |
| `shorts_charged` | INTEGER | shorts that were held overnight and so paid borrow. A same-day short pays none, and the two are told apart here rather than by reading a cost of nought |
| `trimmed` | INTEGER | trades whose short was reduced at 3R before it closed |
| `armed_exits` | INTEGER | trades whose exit was decided on an earlier session and filled at an open |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | clean where nothing closed, which is a shape of nothing rather than a failure |

#### `audit_run`
Grain: session + observation. What one evening's audit read and wrote, at 21:26.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `trades_read`, `audited` | INTEGER | the two differ on a rerun and on a trade missing a fill at one end, which is refused rather than filled with noughts |
| `longs`, `shorts` | INTEGER | counted apart |
| `reduced_by_a_cap` | INTEGER | trades the gate sized down. The figure the third pair exists to make readable |
| `gapped_at_an_end` | INTEGER | trades where one end or the other filled at an open rather than at the price it named, so the night's difference figures are never read as though they were all slippage |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | |

### Why each loss happened

Columns of `loss_class`. Built at 4.10. One row per closed loss, carrying two answers that arrive at
different times.

| Column | Form | Why |
|---|---|---|
| `trade_id` | TEXT, the key | The trade it explains, and a foreign key into it |
| `setup_id`, `ticker`, `direction`, `closed_session` | TEXT | Carried so a classification reads without a join |
| `net_pnl`, `result_r` | TEXT / REAL | What is being explained, after the borrow a short is charged |
| `mechanism` | TEXT, one of two | `gap` or `ordinary`. **How** the loss occurred, known the moment the trade closes |
| `exit_basis` | TEXT, one of two | What the mechanism was read from, carried so the reading is checkable on the row rather than only reproducible from a join to `fill` (see: A gap loss is detected from the exit fill's basis, not from the size of the loss) |
| `aftermath` | TEXT NULL, one of three | `noise`, `failed-setup` or `unclassified`. **What happened next**, and null while the ten-session horizon has not closed. Null and `unclassified` are different facts and only the second is a finding |
| `forward_return_signed`, `one_r_in_return` | TEXT NULL | The two figures the boundary was read from, present exactly on the two aftermaths that came from them, and both are fractions of the trigger price: the first is the direction-signed return from the trigger to the adjusted close of the tenth session after the one it was touched in, and the second is the give-up distance over the trigger. **Neither is `forward_return.return_signed`**, which is measured from the setup session's close over the ten sessions after the setup; the classifier read that column until 4.18 and compared it against one R over the trigger, which is a comparison across two populations, found at the 4.13 sign-off. `unclassified` means the horizon closed and the store held no close to read the figure from, so a row carrying both would be one nobody could tell from a placed one |
| `exit_return_signed` | TEXT NULL | **What the trade earned, beside what the day offered.** The direction-signed return from the trigger to the exit fill, as a fraction of the trigger and on the adjusted basis at both ends, where `forward_return_signed` is the same return taken to the close of the tenth session after the trigger's. Two figures and never one: the gap between them is what the trail rule is judged on, since with one figure a trail that captured a move and one that gave a move back are the same number. Written when the aftermath is and only beside the first figure, which the table asserts, because what the trade earned is half of a comparison; absent, with the sentence saying so, where the store holds no bar for the session the trade closed in to put the exit on the adjusted basis. Added at 5.0(c) by migration 048 (see: The aftermath is measured from the exit as well as from the close, as two figures and never one) |
| `aftermath_because` | TEXT NULL | The sentence a person reads, with both figures in it |
| `observed_at`, `aftermath_observed_at` | TEXT / TEXT NULL | Two stamps, on the terms `position` carries three: an update overwrites a state without moving the stamp that says when it was observed, so a replay standing between the close and the horizon has to see a mechanism and no aftermath |

**Both questions are asked of every loss.** A gap loss that later recovers satisfies both without
contradiction, and it can only do so if the second is put to it. Asking the aftermath only of the
losses that were not gaps is what a single ranked list would have done, and the ranked list is what
the decision refuses (see: A stop-out is noise when the ten-day return reached one R, and cause of
loss is two questions rather than one ordered list).

#### `loss_run`
Grain: session + observation. What each of the classifier's two passes wrote, at 21:35.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `losses_closed`, `mechanisms_written` | INTEGER | the first pass. They differ on a rerun and on a closed trade with no exit fill, which is refused rather than classified from the size of the loss |
| `gap`, `ordinary` | INTEGER | the mechanism, counted apart |
| `longs`, `shorts` | INTEGER | counted apart (see: Long and short are never pooled into one figure) |
| `awaiting_aftermath` | INTEGER | rows still waiting on a horizon at the end of the run, read back from the store rather than derived. **Not the same figure as `unclassified`**, which is two columns along |
| `aftermaths_written`, `noise`, `failed_setup`, `unclassified` | INTEGER | the second pass. A night that wrote three mechanisms and no aftermaths is an ordinary night early in a horizon; one that wrote three aftermaths and no mechanisms is an ordinary night ten sessions later, and one total would make both read the same |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | clean where nothing closed and nothing was waiting |

## Research — phases 5 and 6

| Store | Grain | Writer |
|---|---|---|
| `variant` | variant id | Insert VariantAdmitter (definition, target, min sample, **once**) · Update AcceptanceGate (status and resolution date **only**) (see: Targets and minimum samples are written at creation and are immutable) |
| `variant_score` | variant + date + direction | Insert VariantScorer |
| `score_run` | session + observation | Insert VariantScorer |
| `twin_pair` | pair id + date + observation | Insert TwinPairFinder. **A rerun of a date writes a new generation beside the old rather than rebuilding it**, so `observed_at` is in the key and a reader takes the latest at or before its bound. The pairs of a date are a derived reading rather than evidence, which is the difference between this and `setup_signal`; but the window grows as outcomes fill, so a second run of one date can honestly differ, and the stale generation is what a person saw (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood) |
| `twin_run` | date + direction + observation | Insert TwinPairFinder. What one run's window actually held against what the metric wants, and why it found nothing where it found nothing |
| `pack_version` | version | Insert ContextPacker. **No update path, and it is the one research store where a rerun does not write a generation**: the fingerprint is the identity, so a cut computing the same tuple finds the row it already has and a tuple differing in any component takes the next ordinal. A version is a definition rather than a reading, and the same definition twice is the same row (see: An approved proposal creates a new version from zero, and a running version is never edited) |
| `pack_run` | date + observation | Insert ContextPacker. What one cut of the pack held, section by section, and the populations it was built over. It exists because a version is deliberately the same across every night it is cut on, so with the version alone a pack whose sections were all empty would be indistinguishable from a pack that was never cut |
| `proposal` | proposal id | Insert ResearcherSeat (see: The AI writes only to the proposal store) · Update ProposalRegistry (status) |
| `replay_result` | proposal + window + observation | Insert ReplayHarness. **Store built at 6.6**, which is where a proposal first exists to key a result on. **The grain read `proposal + window` until 6.6 and it could not hold a re-screen**: a screen is a reading rather than a definition, the same proposal screened twice over the same window is two readings because the evidence underneath moved, and this writer has no update path at all, so the second reading would have been a primary-key collision and the first would have gone on reading as current (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood). ReplayHarness itself is built at 5.3 and writes nothing: a screen kills a proposal and never admits one, so until there is a proposal store there is nothing for a result row to belong to, and a screen run before then would be a third statement about a version with nothing reconciling the three (see: Replay screens proposals and the forward paired test admits them) |
| `acceptance_reading` | variant + observation | Insert AcceptanceGate. **What the gate read of one version, whether or not it settled it.** A settlement is two columns of `variant` and may be no more, the target and the minimum sample being written at creation and never again, so everything the gate weighed lives here (see: Targets and minimum samples are written at creation and are immutable). **Written on every run rather than on the run that settles**, because a version whose sample never accumulates is a state the failure table names and the sequence of readings is what says so. **No baseline has a row and that is a refusal rather than an omission**: it is the arm every other version is differenced against, so there is no series to take an interval over |
| `acceptance_run` | session + observation | Insert AcceptanceGate. What one night's gate read, with the baselines it passed over counted apart from the versions it read, so a run that settled nothing is told from a run that found nothing registered |
| `holdout_window` | window id | Insert HoldoutRegistry. **No update path at all**: a window is a fact about the calendar and does not become untrue |
| `holdout_spend` | window id | Insert HoldoutRegistry. **The key is the rule.** One row per window, so a second spend of the same window is refused by the store rather than by a stage remembering to look (see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight) |
| `holdout_run` | observation | Insert HoldoutRegistry. What one run of the register did, and why it held nothing where it held nothing |

### The twin pairs

Columns of `twin_pair`. Built at 6.3, and the columns are the ones that checkpoint owes rather than
the whole eventual shape.

| Column | Form | Why |
|---|---|---|
| `pair_id`, `as_of`, `observed_at` | TEXT, the key | The two members in settled order, and the date the reading was taken. A pair is a reading of a window, and the window grows, so the same two setups are a separate row on a later date |
| `direction` | TEXT | `long` or `short`. In the row rather than in a note, because the outcome is signed by direction and a pair drawn across the two sides could differ by twenty points with both names having done the same thing (see: Long and short are never pooled into one figure) |
| `left_setup_id`, `right_setup_id` | TEXT | The two members, lower id first. The order is the identity rather than a convention the stage remembers, and the store holds it: a row whose left is not below its right is refused |
| `distance` | TEXT | In standard deviations across `signals_compared` axes, standardised over the window this run held |
| `gap_points` | TEXT | In percentage points of the ten-day return. Points rather than a fraction, because that is how the authored-parameters row states it and how a person reads it |
| `left_outcome`, `right_outcome` | TEXT | Each member's own outcome, so the pair reads without a join and the gap can be checked against the two figures it came from |
| `signals_compared`, `window_setups` | INTEGER | **What the two figures above were computed over, on the pair's own row.** Neither is a fact about the pair alone: a pair found today at 0.42 over 38 setups is not the same measurement as the same two at 0.42 over 250, and a row carrying only the ids would invite the second reading of the first answer |
| `observed_at` | TEXT | In the key, so a rerun of a date writes a new generation beside the old and a reader takes the latest at or before its bound. The window grows as outcomes fill, so a second run of one date can honestly produce a different answer, and the stale generation is what a person saw (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood) |

### What one twin run looked at

Columns of `twin_run`. Built at 6.3 alongside the table above.

| Column | Form | Why |
|---|---|---|
| `as_of`, `direction`, `observed_at` | TEXT, the key | One row per side per run |
| `window_setups`, `window_wanted` | INTEGER | What the run could form against what the metric is defined over, side by side. **This is the figure 6.3 owes and it is a property of the run rather than of any pair**, so it survives a run that qualifies nothing, which is every run for months (see: The twin-pair threshold is reviewed at the first full window rather than at a phase) |
| `signals_compared` | INTEGER | The width of the space the distance was taken in, being the signals every setup in the window carried as a number. Nought where the window was too thin to form a space |
| `candidate_pairs`, `pairs_found` | INTEGER | What was looked at against what was found. Nought twins over four setups and nought over two hundred are different statements and only the second says anything about the thresholds |
| `empty_because` | TEXT NULL | Why a run held nothing, on exactly the runs that held nothing and never beside a run that found something, which the store asserts in both directions. A window under two forms no pair, a window with no numeric signal common to every row forms no space, and a full window whose pairs were all refused by the thresholds is a finding rather than a gap; the three are different and the column says which |
| `outcome` | TEXT | |

*`twin_run` exists because the figure this checkpoint owes could not otherwise be recorded.* The
row's done condition is that the run reports how many setups the window actually held rather than
treating a short window as a full one, and with pairs alone a run that qualified nothing would write
nothing at all. The window figure would then be missing on exactly the runs where it is the only
thing there is to say.

*The window is not stored as a session range and that is deliberate.* It is the trailing 250 setups
of one side, taken in session order with the setup id as the tiebreak, so it is a count of rows
rather than a span of dates and two sides of one date hold different windows. Storing a date range
beside it would be a second statement of the same population, and the two would disagree the first
time a session carried setups on one side only.

### What the model was shown and judged under

Columns of `pack_version`. Built at 6.4, and the columns are the ones that checkpoint owes rather
than the whole eventual shape.

| Column | Form | Why |
|---|---|---|
| `version` | INTEGER, the key | The ordinal a person says out loud, as in "proposals against version 4 beat those against version 2". Ascending in creation order and never reused |
| `fingerprint` | TEXT, unique | SHA-256 of the canonical rendering of the tuple. This is the identity the ordinal is a label for: two cuts computing the same tuple find this row rather than writing a second one |
| `sections` | TEXT | The sections the model was shown, in the order it was shown them. Order is part of the identity here, unlike the screened set below, because a reordered pack is a different pack |
| `signals_screened`, `signals_screened_count` | TEXT / INTEGER | The signals screened, sorted ordinal, and how many. Sorted rather than in library order so that reordering SCHEMA's Signals section without changing a signal does not fork the version. The count sits beside the list rather than being derived on read, because it is what both thresholds were computed over and a figure's population belongs on its own row |
| `correction_form`, `correction_level` | TEXT | What admission is decided under, and the level both corrections are taken at. One level rather than two, so the two thresholds cannot drift apart into answers to different questions |
| `family_wise_threshold` | TEXT NULL | The level over the count screened. Null only where nothing was screened, which is a library with no signals in it rather than a night with no evidence, and the store holds that as a biconditional so a null cannot arrive from a packer that simply failed to compute it |
| `model_identifier` | TEXT | The model the seat is pinned to. In the version because it is a confounder for the phase's own success criterion, so changing it forks the record rather than continuing it (see: The model is a frozen parameter of the pack version, and changing it forks the record) |
| `created_at` | TEXT | |

***The realised false-discovery bar is not a column here and that is the load-bearing absence.*** The
family-wise threshold is the level divided by the number of signals screened, so it is determined
entirely by the version's own inputs. The bar the Benjamini-Hochberg step-up yields is a function of
the p-values, which are a reading of the store on one night. Storing it here would fork the version
whenever the evidence moved, every version would hold one proposal, and the success criterion would
be a comparison of populations of one. It is a column on `pack_run` instead, which is the row that
is about one night (see: The realised false-discovery bar is a reading of a pack and the version carries the procedure).

*What is deliberately not in the tuple at all is the store's contents.* A pack cut on two different
nights is the same version over different evidence, which is the whole point of holding the version
fixed while the evidence accumulates.

### What one cut of the pack held

Columns of `pack_run`. Built at 6.4 alongside the table above.

| Column | Form | Why |
|---|---|---|
| `as_of`, `observed_at` | TEXT, the key | The date packed and the instant the cut was taken. A date packed twice writes two rows, because the evidence underneath moved and the stale reading is what a person saw (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood) |
| `version` | INTEGER NULL | Which version this cut was. The same across every night a version is in force, which is what makes proposal hit rate by pack version a comparison of anything at all. **Null on a refused run**, which is the whole of what "no pack is written" means |
| `body_digest`, `body_bytes` | TEXT NULL / INTEGER NULL | The pack's own digest and size. Byte-stability is a property of the packer, and these are what make it checkable from the store rather than only from a test holding two strings (see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable). Null on a refused run, which produced no body to measure |
| `sections_rendered`, `sections_empty` | INTEGER | How many sections were rendered and how many had nothing to say. Every section is rendered in every pack, so the second figure is what makes an almost-empty pack legible as almost-empty rather than as a pack. **Five of the ten rest on outcomes that have not closed and the figure is not therefore five**: Twin pairs is one of them and states its window whether or not it found a pair, so a pack cut before the twin finder has ever run reads five and one cut after it reads four |
| `refused_because` | TEXT NULL | Why no pack was written, on exactly the nights none was. **A pack missing a section is not a smaller pack**: the correction is computed over the signals screened, so a short pack would carry a threshold for a set it did not screen and every claim against it would be judged against the wrong number. So the night refuses, says which section it could not build, and writes no version at all |
| `signals_screened` | INTEGER | What the correction was paid on, on the night it was paid. Screened and never shown (see: The correction threshold scales with signals screened, not signals shown) |
| `false_discovery_bar` | TEXT NULL | What the step-up yielded against this night's p-values. Null where no claim was measured, which is a different statement from a bar of nought and is why the column is nullable rather than defaulted |
| `long_setups`, `short_setups` | INTEGER | The populations the sections were built over, counted apart and never added (see: Long and short are never pooled into one figure) |
| `null_control_planted` | INTEGER | Whether the null control reached the conditional tables. A pack that failed to plant it is a pack whose tripwire is not armed, and that has to be legible from the row rather than inferred from the body (see: One meaningless signal is planted in the conditional tables) |
| `outcome` | TEXT | |

*A refused run is held as a biconditional in both directions.* A run with outcome `failed` carries a reason, no version, no digest and no rendered section; a run with any other outcome carries none of those and does carry all of these. Stated in the store rather than left to the stage, so a half-written refusal is refused rather than recorded.

*The body is not stored and the digest is.* A pack is aggregates over a store the lab still holds,
so it can be rebuilt from the version and the as-of; what cannot be rebuilt is whether two runs at
one commit agreed, and that is exactly what the digest records. Storing the body would put a
copy of the evidence beside the evidence, and the two would disagree the first time a reader
corrected one.

### What the researcher returned

Columns of `proposal`. Built at 6.5.

| Column | Form | Why |
|---|---|---|
| `proposal_id` | TEXT, the key | The week, the transport and the instant. The instant is in it because a week can be asked twice, and the transport because a week asked on two transports is two answers from two readers rather than one answer twice |
| `as_of`, `pack_version`, `pack_digest` | TEXT / INTEGER / TEXT | Which week, what the model was judged under, and which cut it actually read. The version and the digest are different questions: a version is deliberately the same across every night it is cut on, and the digest is the one body this proposal was made against (see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable) |
| `transport`, `configured_model`, `served_model` | TEXT / TEXT / TEXT NULL | The three recordings every proposal carries. **The served string is the strongest of them**, because it says what actually ran rather than what was asked for, and it is nullable because a transport that stopped reporting it has stopped pinning anything and a copy of the configured value would hide that (see: The model is a frozen parameter of the pack version, and changing it forks the record) |
| `counts_toward_hit_rate` | INTEGER | Whether this answer counts toward proposal hit rate by pack version. Stored rather than derived on read, so the exclusion is a fact on the row and not a filter one query can forget (see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate) |
| `outcome` | TEXT | One of `proposed`, `requested`, `abstained`, `unavailable` and `unreadable`. **`requested` is the second kind the registry accepts and it arrived at 6.6**: the shape this table carried held a rule change and had nowhere for a signal request, so a seat that wanted a measurement had to write a rule change or nothing (see: Proposals come in two kinds, rule changes over existing signals and requests for a new signal). **And four of the five are not one, because a week with no proposal has three causes.** A seat that could not be asked, a seat whose answer is not the agreed document, and a seat that read the evidence and declined are different facts about the lab, and one column reading "no proposal" for all three would be the silence the loop exists to break (see: Abstention is a valid recorded proposal outcome) |
| `direction`, `gate`, `threshold_name`, `from_value`, `to_value` | TEXT | The one thing that changes, and they are the five columns `variant` already holds. That tie is the point rather than a coincidence: an accepted proposal becomes a version, and two shapes would put a translation step between them and a place for the meaning to move (see: A proposal is a document whose one change is the five fields the version register already stores). The two values are TEXT because a threshold is compared against prices and ratios, and never `REAL` |
| `family` | TEXT NULL | Selection or execution. One value and never both: the two are scored on different quantities and a version moving one of each could be attributed to neither |
| `mechanism` | TEXT NULL | Why it should work, in one sentence, committed before any result exists. A mechanism stated in advance is a prediction and the same sentence written afterwards is a story |
| `evidence_setup_ids`, `evidence_signals` | TEXT NULL | The evidence as ids already in the store, and the signals the reasoning rests on. **The second is the tripwire's subject and is a column of its own for that reason**: which signals a proposal rests on is a shorter and more answerable question than which words its prose used |
| `refutation`, `observations_to_settle` | TEXT NULL / INTEGER NULL | What would refute it, and how many observations settle the question |
| `requested_signal`, `requested_axis` | TEXT NULL | The signal a request wants computed and the axis of measurement it belongs to, present exactly on a request. **A name rather than a formula**: what to compute is decided by a person reading the request, which is what keeps the library a ceiling the model cannot lift for itself. The axis is stored because the axes are the thing worth enumerating rather than the signals, and a run of requests all naming price path is legible as the library's actual shape rather than as eight separate ideas |
| `abstained_because` | TEXT NULL | Why there was nothing to propose, on exactly the weeks the seat declined |
| `unavailable_because`, `answer_problems` | TEXT NULL | Why nothing was asked, and why what came back could not be read. Kept apart from the abstention reason because a seat that could not ask and a seat that considered the evidence and declined are opposite facts about the same empty week |
| `answer_text` | TEXT NULL | What the seat actually returned, stored whatever became of it. A week the model returned prose is only distinguishable from a week it was never asked if the prose is kept |
| `pins` | TEXT NULL | What pins this answer beyond the model string, as `name=value` lines. Empty on the two hosted transports and populated on the local one, where the weights digest, the quantisation, the runtime and the sampling together make the proposal reproducible by anyone holding the same file |
| `cites_null_control`, `fails_pack_version` | INTEGER | The tripwire, in two columns because the citation and the consequence are different facts (see: One meaningless signal is planted in the conditional tables) |
| `status` | TEXT | `filed` when written here, and thereafter the only column ProposalRegistry may move. **Seven values from 6.6**: `screened` a rule change replay let through, `discarded` one it killed or one the register would not take, `admitted` one a version was created from, `recorded` an abstention, `build-task` a signal request, and `unactionable` a week with no answer in it. Folding the last three into one would put a considered decline and an outage in the same bucket, and telling those apart is the whole reason the seat records five outcomes rather than two |
| `observed_at` | TEXT | |

*The change columns are conditional on the outcome, and that clause is what the first real call bought.* Until 6.5 the document's proposal shape required a change whatever the answer, so a model that abstained had to put something in the gate field. Asked for a proposal against a near-empty pack on 2026-09-07, a model abstained correctly and put the planted null there, which under the tripwire as written failed pack version 1. Here an abstention has no change columns to fill, so there is nowhere for it to happen.

*The tripwire's scope is a CHECK and not a convention.* A pack version is failed only where the row proposes a change, rests on the planted null, and came from a seat the record compares. An abstention that named the control cannot fail one, a week with no answer cannot, and a stopgap cannot whatever it cited: a competent reader citing a meaningless signal indicts the pack, a weaker reader citing it indicts itself, and the rule as written cannot tell the two apart.

*The exclusion is held the same way.* A local transport writes `counts_toward_hit_rate` nought and every other transport writes one, stated as a biconditional so a reading that filtered on the transport cannot be one query away from forgetting to.

### What a screen made of a proposal

Columns of `replay_result`. Built at 6.6, which is where a proposal first exists to key a result on.

Grain: proposal, window and observation. A re-screen is a new generation beside the old one.

| Column | Type | Note |
|---|---|---|
| `proposal_id`, `observed_at` | TEXT, the key | Which proposal and when it was screened. **The instant is in the key and the window is not**, because a screen is a reading rather than a definition and this writer has no update path: without it a re-screen would be a collision and the stale reading would go on reading as current |
| `window_id` | TEXT NULL | The holdout window the screen was run over, or null where it was run over the accumulated store. **Null today and for months**, because holdout windows are quarters of forward-collected evidence and the first session the store holds is 2026-08-27. There is no sentinel here, because a screen over a window and a screen over everything are different screens and a count of each is a different fact (see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight) |
| `as_of`, `direction` | TEXT | The date screened to and the side. One side, because a version is one side's: a threshold belongs to one side's gate list (see: Long and short are never pooled into one figure) |
| `sessions_read`, `rows_examined` | INTEGER | What the walk read. Both, because the cost of a screen is a function of how many nights the store holds rather than of how many rows they hold between them, and the two figures come apart as the store fills |
| `baseline_selected`, `candidate_selected`, `both_selected`, `candidate_only`, `baseline_only` | INTEGER | The two selections and their overlap, which is what a screen *is*. Bounded against each other by CHECK, so a row that could not be a set relation is refused rather than filed and read later as one |
| `unjudgeable`, `unmeasured_verdicts`, `disagreements` | INTEGER | What the walk could not stand behind, kept apart from what it judged. A screen over a population it could not read is a screen with a smaller subject, and a figure that hid it would be a figure over a population other than the one its name says |
| `verdict` | TEXT | `killed`, `survived`, `refused` or `inconclusive`. **There is no value here that means admitted**, which is the decision held by the store rather than by the stage: replay is free, free tests are how you overfit, and only the forward paired test says a proposal is worth keeping (see: Replay screens proposals and the forward paired test admits them) |
| `refused_because` | TEXT NULL | Why the screen would not run, on exactly the screens that did not. A candidate the register would not take as a version is not screenable: reporting on it would report on something that could never run |
| `elapsed_ms` | INTEGER | How long the walk took. "In seconds" is the claim the whole screening channel rests on, and this is the figure that says whether it still holds as the store grows |

*`inconclusive` is the commonest verdict this lab can produce today, and it is a state rather than an edge case.* The funnel passes a median of nought candidates a night, so over a population where the baseline selected nothing a candidate that selected nothing too has not changed nothing, it has said nothing. Recording that as `killed` would be a verdict reached over a population of none. A proposal whose screen was inconclusive stays `filed` and is read again as the store grows, and the row written each time is the record that the evidence still cannot separate anything.

*A refused screen reads nothing and says why, held as a biconditional in both directions.* So a refusal carrying a population, and a screen that ran carrying a reason, are both refused by the store rather than filed and reconciled later by whoever reads them.

### The register of rule versions

Columns of `variant`. Built at 5.1, and the columns are the ones that checkpoint owes rather than the
whole eventual shape.

| Column | Form | Why |
|---|---|---|
| `variant_id` | TEXT, the key | One row per version. The baseline is a version like any other, so a plan always belongs to something |
| `generation` | INTEGER | The generation the version belongs to. Editing the baseline closes every open version as unresolved and starts a new one, so a version is only ever comparable to versions of its own (see: An approved proposal creates a new version from zero, and a running version is never edited) |
| `family` | TEXT | `baseline`, `selection` or `execution`. A selection change is scored on forward return and an execution change on R per trade, and the two are not comparable (see: Two experiment families, selection and execution, scored differently and never mixed in one version) |
| `definition` | TEXT | What the version changes, in words. A version with no definition is a row nothing can settle |
| `target` | TEXT | What would settle it, written at creation and never again. A target that can move after the result is not a target |
| `minimum_sample` | INTEGER | The pre-registered minimum, written at creation and never again |
| `minimum_sample_unit` | TEXT | What that integer counts: `effective_paired_setup_observations` for the baseline and a selection version, `paired_trades` for an execution one. **Beside the figure rather than inferred**, because 1802 effective observations and 200 rows are not comparable and one integer column with no unit would make them look it (see: The execution minimum is 200 paired trades and its conversion waits on a trade existing) |
| `status` | TEXT | `open`, `accepted`, `rejected` or `unresolved`. The fourth is closed without an answer, which is what editing the baseline does, and it is not a rejection: a rejected version was measured and fell short, an unresolved one was never measured because what it was compared against stopped existing |
| `resolved_at` | TEXT NULL | When it was settled, present exactly when the status is not `open`. AcceptanceGate writes this and `status` and has no path to any column above |
| `created_at` | TEXT | When the version was registered, which is what a point-in-time read of a night bounds on. A replay of an evening sees the versions that evening had and no others |
| `direction` | TEXT NULL | Which side the moved threshold belongs to. A version is one side's, because a threshold belongs to one side's gate list, and a version spanning both would be two experiments in one row (see: Long and short are never pooled into one figure) |
| `gate` | TEXT NULL | The gate the moved threshold sits under, as ARCHITECTURE's gate lists name it |
| `threshold_name` | TEXT NULL | Which named threshold moved. One, and `RuleAdmission` is what asserts that rather than a convention (see: A version changes one threshold over the existing gate list, and structural change is out of scope for this generation) |
| `threshold_from`, `threshold_to` | TEXT NULL | The baseline's value and the version's. Read through `StoreText.StorageTextToThreshold` and not through the price or the ratio crossing: `liquidity-floor` is twenty million dollars and `maximum-retrace` is the fraction 0.40, both live in this column, and a crossing named for either would be wrong about the other |
| `proposal_id` | TEXT NULL | The proposal this version came from, or null where it came from nobody. **The join band 3 is defined by, and it did not exist until 6.8**: the project's stated success criterion is proposal hit rate by pack version, `proposal` carries the pack version it was cut against, and the settlement is a status on a version, so with nothing carrying the second back to the first the rate could not be computed even in principle (see: The evidence pack is versioned, and the success criterion is proposal hit rate by pack version). Written once at creation by VariantAdmitter, on exactly the terms the target and the minimum sample are (see: Targets and minimum samples are written at creation and are immutable). Nullable because the baseline came from no proposal and an operator may register a version of their own |


*The five columns above are present exactly on a selection version, and the store holds that as five CHECK clauses rather than the admitter holding it as care. The baseline moves nothing and no execution version is admitted in this generation, so on both they are null together. A row carrying three of the five reads as a version and cannot be scored as one, which is the state the clauses refuse.*

*`definition` is derived from these columns for a selection version and typed only for the baseline. A sentence somebody types can disagree with the columns beside it, and the day it does there is nothing to say which of the two the version is (see: A version changes one threshold over the existing gate list, and structural change is out of scope for this generation).*

### What a night's difference came to

Columns of `variant_score`. Built at 5.2, and the columns are the ones that checkpoint owes rather than the whole eventual shape.

Grain: variant + date + direction. One night of one version against the baseline, on one side.

| Column | Type | Note |
|---|---|---|
| `variant_id`, `session_date`, `direction` | TEXT | PK. Direction is in the key rather than in a note, because a pooled score would add a long side's forward return to a short side's and a version that helps one side while hurting the other would read as no difference at all (see: Long and short are never pooled into one figure) |
| `generation`, `family` | INTEGER / TEXT | Carried rather than joined, because a score is read back as it stood and a generation that turned over afterwards would restate what the row meant |
| `horizon_days` | INTEGER | 10, the scoring horizon the difference is taken at |
| `flagged` | INTEGER | every setup of that side flagged on that night, which is the shared candidate list both rules saw |
| `baseline_selected`, `variant_selected` | INTEGER | what each rule picked out of it. **Two populations, not one**, so the two means below are over different rows. **They are not the denominators the means were taken over and this row said they were until 6.7**: a selection still inside its horizon is counted here and is in neither mean, so on any night an outcome is open the stated denominator is the larger of the two. That is a figure named over one population and computed over another, and nothing could see it because nothing had ever read the two numbers together |
| `both_selected`, `variant_only`, `baseline_only` | INTEGER | how the two sets overlap. The last two are derived from the first three and the store checks the arithmetic, because a version that changed nothing and a version whose disagreements cancelled are different facts |
| `baseline_mean_return`, `variant_mean_return` | TEXT NULL | the mean scoring-horizon forward return over each side's own selections, as a fraction |
| `mean_difference` | TEXT NULL | the second less the first. **A difference of two means and not a mean of differences**: there is no per-name pairing to take, because the two sets are not the same set. What is paired is the night |
| `baseline_scored`, `variant_scored` | INTEGER NULL | the rows each mean was actually taken over, being each side's selections whose forward return has landed. **Recorded from 6.7 rather than the sentence above being reworded**, because a denominator a reader has to derive from two other columns is a denominator nobody derives |
| `baseline_wins`, `variant_wins` | INTEGER NULL | how many of those rows ended ahead, direction-signed. **The diagnostic and never the settlement**: win rate is trivially improvable in the wrong direction, and a version that raised it while lowering expectancy is rejected under that name (see: Acceptance measures expectancy, never win rate). Here rather than recomputed by the gate, because a second reading of what the version selected sitting beside the scorer's own is two implementations of one population |
| `baseline_outside_cap`, `variant_outside_cap` | INTEGER | how many of each side's selections the night's cap did not reach, and which are therefore refused a fill. **On both sides rather than on the version alone**, because the baseline's own selections past the sixtieth rank are refused on identical terms and a column existing only on the version would read as a penalty the version alone pays (see: The spread capture stays at the capped sixty, and a version selecting outside it is scored as refused) |
| `unscoreable` | INTEGER | setups of that night whose moved gate the frozen signals could not judge. Counted rather than dropped: a night scored over nine of eleven names is a different fact from a night scored over eleven |
| `withheld_because` | TEXT NULL | why the row carries no figure, on exactly the rows that carry none, which the table asserts in both directions. A scored night that could not produce a difference is a row rather than an absence |
| `computed_at` | TEXT | when the difference was taken |

*The writers are declared in the Research ownership table above and not repeated here, which is what every prose-headed section does: the store-level table is the one statement of ownership, and a second one under a heading is a second place the same fact lives.*

*A night is scored once, when its scoring horizon has closed, and never rewritten. A figure recomputed as returns arrive would be a figure over a population that changed after somebody read it, which is the defect the population rule exists to name (see: The subject is the flagged setup population, not the trade log).*

*Only a selection version has rows here, which the `family` check states rather than leaves to the writer. An execution version is scored on R and none is admitted in this generation, so the absence is a decision rather than an omission (see: No execution variant is admitted in this generation, and the condition that would reopen it is named).*

### What the gate read of a version

Columns of `acceptance_reading`. Built at 6.7, and the columns are the ones that checkpoint owes
rather than the whole eventual shape.

Grain: variant + observation. Where one version stood at one instant, on its own side.

| Column | Type | Note |
|---|---|---|
| `variant_id`, `observed_at` | TEXT, the key | The version and the instant the reading was taken. A reading is a reading: the series underneath grows every night, so the same version read twice is two rows and the older one stays readable as it stood (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood) |
| `session_date` | TEXT | the session the run was for, which is what a ledger opened on an old date is asking about |
| `direction` | TEXT | which side. On the row rather than in a note, because every figure below is one side's and a reading over the pair would be a pooled figure with the arithmetic left out (see: Long and short are never pooled into one figure) |
| `generation` | INTEGER | carried rather than joined, on the same terms `variant_score` carries it |
| `age_days` | INTEGER | how old the version was at this reading, in whole days from the session it was registered in. The failure table's own clause: a version whose sample never accumulates stays open and the ledger shows its age |
| `nights_scored`, `nights_with_a_figure`, `nights_identical`, `nights_in_series` | INTEGER | **Four counts and never one.** A night the two rules selected the same names on carries a difference of exactly nought by construction, and one total would let it hide inside the first figure: the version would then mature on nights it was never exercised on, with its mean pulled toward nought by them |
| `disagreements` | INTEGER | the setups the two rules disagreed about across the series, which is the population the difference rests on |
| `effective_observations` | INTEGER | what the series is worth, counted the way the interval counts it. **A night is one observation and the store cannot say otherwise**: the score row is a difference of two means over two overlapping selections, so there is no per-name pairing and no within-night dispersion to record, and a series that cannot say how its own pairs dispersed is read the way that cannot overstate |
| `minimum_sample`, `minimum_sample_unit` | INTEGER / TEXT | carried from the pre-registration onto the reading, so a figure and the bar it is read against are one row rather than a join a reader has to make |
| `matured` | INTEGER | whether the first has reached the second. Recorded rather than derived at the point of reading, because it is the condition the settlement was allowed under |
| `mean_difference`, `interval_low`, `interval_high` | TEXT NULL | the estimate and the studentised moving-block interval around it, present together or absent together. Text on the same terms every other ratio in this store is text |
| `baseline_win_rate`, `variant_win_rate` | TEXT NULL | the diagnostic, over the two denominators above. **Two rates rather than one**, because the two rules selected different names and one denominator would put one rule's wins over the other's population |
| `verdict` | TEXT | `open`, `accepted` or `rejected`. There is no fourth: `unresolved` is what editing the baseline does to a version and this gate never writes it |
| `settled_because`, `withheld_because` | TEXT NULL | exactly one of the two, held in both directions. A settlement with no reason and a wait with no shortfall are both rows nobody could act on |
| `population` | TEXT | the rows every figure above was computed over, in words, on the row that carries them. A population that lives anywhere but beside its figure is one a later reader will pair with the wrong figure |

*A settled reading is one that reached its minimum and produced an interval, and the store holds that
rather than the stage.* Nothing here settles a version on the calendar: a timeout would be a decision
made by the clock rather than by evidence, so a row saying `accepted` or `rejected` without a matured
flag and a mean is refused outright.

*The gate writes `status` and `resolved_at` on `variant` and can reach no other column of it.* That
is the row where a future change could quietly let a result rewrite the target it was measured
against, so the settlement is one UPDATE naming two columns and there is no second statement to
declare (see: Targets and minimum samples are written at creation and are immutable).

### What one run of the gate did

Columns of `acceptance_run`. Built at 6.7 alongside the table above.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `versions_live`, `versions_read`, `baselines_passed` | INTEGER | **Three numbers because they answer three questions.** A night with one live version reads none of them, the baseline being the arm everything else is differenced against, and a run reporting a single nought could not be told from a run that found nothing registered at all |
| `versions_matured` | INTEGER | how many had reached the sample written into them. Beside the three verdicts rather than derived from them, because a version can mature and still be left open where the series cannot produce an interval |
| `accepted`, `rejected`, `left_open` | INTEGER | the verdicts, counted apart and summing to `versions_read`, which the store checks |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | partial where the register holds no open selection version, which is the state after the freeze and before the first proposal is accepted, and is not an error |

### What one run of the scorer did

Columns of `score_run`. Built at 5.2.

Grain: session + observation. At 21:40, after the forward returns and before the scoreboard.

| Column | Type | Note |
|---|---|---|
| `session_date`, `observed_at` | TEXT | PK |
| `versions_live`, `versions_scored` | INTEGER | how many versions the register held for the night, and how many of them carry a difference. **Two numbers because they answer different questions**: a night holding only the baseline scores none, and a single nought could not be told from a night that found nothing live at all |
| `nights_scored`, `nights_waiting` | INTEGER | nights whose horizon had closed and were written, and nights a live version was running on whose horizon has not. The second is the ordinary state of every recent night and is not a fault |
| `longs`, `shorts` | INTEGER | counted apart (see: Long and short are never pooled into one figure) |
| `unscoreable` | INTEGER | setups across every night this run touched whose moved gate could not be judged from the frozen signals |
| `outcome`, `stopped_because` | TEXT / TEXT NULL | partial where the register holds no version to difference, or no baseline to difference against, with which of the two named |

*Declared in the Research ownership table above, on the same terms as the table before it.*

---

### The holdout register

Columns of `holdout_window`. Built at 5.4, and the columns are the ones that checkpoint owes rather
than the whole eventual shape.

Grain: window id. One calendar quarter of forward-collected evidence, written the day it matures.

| Column | Type | Note |
|---|---|---|
| `window_id` | TEXT, the key | The quarter, as `2026-Q4`. A window's identity is the quarter and nothing else, so a register rebuilt from the same first session produces the same eight names in the same order |
| `ordinal` | INTEGER | One to eight, oldest first, which is the order they are spent in. Bounded by a CHECK and unique by its own index, so a ninth window fails on the first and a second first fails on the second: the cap is a property of the store rather than a count somebody keeps |
| `quarter_start`, `quarter_end` | TEXT | The quarter's own boundaries |
| `matures_on` | TEXT | The first day after the quarter, which is the earliest date the window can be spent on. A window is not available on the last day of its own quarter, because that day's session has not closed |
| `recorded_at` | TEXT | When the register wrote it down, which is what a point-in-time read of an evening bounds on |

*Insert only. There is no path here that changes a row, because a quarter's dates do not change and the window's availability is the absence of a spend rather than a flag on this row.*

### What a window was spent on

Columns of `holdout_spend`. Built at 5.4.

Grain: window id. One spend per window, ever.

| Column | Type | Note |
|---|---|---|
| `window_id` | TEXT, the key | **The key is the rule.** A spent window is never reused for any purpose, and holding the spend as a row keyed on the window makes a second attempt a primary-key collision rather than an update nobody guarded. Held instead as a nullable column on the window row, the rule would live in whatever `UPDATE` statement happened to carry the right `WHERE` clause |
| `spent_on` | TEXT | The decision it was spent on. A budget recording only that something was spent cannot say whether spending it was worth it |
| `outcome` | TEXT | What came of that decision |
| `spent_at` | TEXT | When, which is what a point-in-time read of an evening bounds on |

### What one run of the register did

Columns of `holdout_run`. Built at 5.4.

Grain: observation.

| Column | Type | Note |
|---|---|---|
| `observed_at` | TEXT, the key | When the run happened |
| `as_of` | TEXT | The date the register was read as of |
| `first_session` | TEXT NULL | The earliest session the evidence store holds, which is what the whole schedule is computed from. Null where it holds none, which is a state of its own: no quarter has begun |
| `matured`, `recorded` | INTEGER | What the calendar says should exist against what the register holds. **Two numbers because a register holding nothing has two causes**: no quarter has completed yet, which is ordinary, and quarters have completed and nothing wrote them down, which is a defect. One count could not tell them apart and the two read identically for the first months |
| `written` | INTEGER | How many this run recorded. Nought on every run after the one that recorded a window, because the insert is keyed and a rerun writes nothing |
| `spent`, `available` | INTEGER | Of the recorded windows |
| `outcome` | TEXT | `partial` where a matured window is missing from the register |
| `empty_because` | TEXT NULL | Why no window is available, present exactly when none is, which the store holds as a CHECK. Three readings and they are different facts: no session recorded, no quarter matured yet, and every matured window spent, the last being the designed dead end |
| `stopped_because` | TEXT NULL | Which matured windows the register does not hold |


## Cross-cutting

### Components that own no table

**Not every component writes.** A component whose work is to establish something about rows another
component wrote would, by writing, become the second writer of the thing it is about, and
`writer-ownership` would be reconciling a table against two owners. Recorded here rather than left
as an absence, because a store missing from this document and a component that deliberately owns
none are indistinguishable from the outside, and the first is a defect.

| Component | Why it owns none |
|---|---|
| SetupJournal | It seals the night: every setup row complete, its evidence frozen, and no column written that belongs to a later stage or to a person. A component enforcing immutability by writing would be the second writer of the thing it protects |
| SessionReplayClock | It reads `intraday_bar` one minute at a time and writes nothing. It is the walk rather than a stage: one clock per session hands ascending minutes to whatever is resolving against them, and the component that decides something is the one that owns a table. Declared here because a component missing from this document and one that deliberately owns none are indistinguishable from the outside |
| NightReconciler | It establishes which slots of a night did not run and why, and the row saying so is a `run_log` row. `RunLogger` is that table's one writer for both operations and is attributed by the type issuing the statement, so the reconciliation decides and `RunLogger` writes, on the terms every stage records its own run |
| WatchlistPublisher | **Ruled at 4.1**, having been the one phase-4 component with no store anywhere in this document. The two answers were a `watchlist` table freezing what was shown, or none and a page that projects the setups. The second holds: `setup` already carries `rank` and `capped_out`, every read of it is bounded on when its rows were observed, and a replay of an evening therefore returns the list that evening showed, corrections and all. A stored copy would be a second statement of one night, and it could disagree with the rows it was copied from with nothing reading both to notice. The stage runs at 18:40 to report what would be on the page, which is the only moment a night that was never capped is noticed without somebody opening a browser |

### `run_log`
Grain: run id. Every stage writes a start and an end entry here, so it is delivered at checkpoint 1.1 rather than with the research machinery.

| Column | Type | Note |
|---|---|---|
| `run_id` | TEXT PK | |
| `stage` | TEXT | the calling stage |
| `started_at`, `ended_at` | TEXT | |
| `outcome` | TEXT | `clean`, `partial`, `failed`, or `did-not-run` on a row recording a slot that never ran, present exactly when `did_not_run_because` is |
| `rows_written` | INTEGER NULL | measured from the store, not self-reported by the stage. **Null on a stage whose declared tables it only updates**, where the delta reports 0 on a perfect run and 0 on a run that died on the first name; null says the measure does not apply and nought says the stage wrote nothing (see: A run whose writes are updates records no row count rather than a nought) |
| `calls_used` | INTEGER | counted as the stage runs |
| `counts_against_ceiling` | INTEGER | 0 or 1. Whether the daily total sees this run's calls |
| `skipped` | INTEGER NULL | names the run walked past after a failure it survived. Null where it walked no list |
| `slot` | TEXT NULL | the slot `tools/nightly.ps1` dispatches by, on a row recording that it did not run. Null on every run that happened, because a stage cannot say which slot ran it |
| `session_date` | TEXT NULL | the session a did-not-run row is about. `started_at` is when the row was written, which is the next night, so the session is stated rather than read off the instant |
| `did_not_run_because` | TEXT NULL | `refused-by-tree-guard`, `market-closed`, `paused-by-operator` or `fault`. One row per slot and session, held by a partial unique index |

Insert RunLogger · Update RunLogger

**One writer, not one per stage.** Stages do not write this table. They call `RunLogger`, which owns both operations. Declaring every stage as a writer would put the run-accounting logic in a dozen places and `writer-ownership` could never pass.

**`rows_written` is measured, not reported.** A stage counting its own output will report what it believes it wrote. The nightly halt keys on this number, so it is read back from the store.

**And it is null rather than nought where the delta measures nothing.** `sectors`, `clusters` and `checks` issue `UPDATE` and never `INSERT`, so the row-count delta over their declared tables is 0 whether they resolved every name or died on the first. Applicability is declared at the stage's `Begin` rather than decided at the end, so it is part of what a stage says it writes; what is self-reported is whether a measure applies and not the value of one, which is the footing `skipped` beside it already stands on. The column stated a type it no longer holds from 4.8 until 4.9, which is a spec that went stale in the commit that made it stale (see: A run whose writes are updates records no row count rather than a nought).

**And it distinguishes nothing on a stage that only updates, which is why `skipped` exists.** The measurement is a row-count delta over the tables the stage declares, so `sectors`, which issues `UPDATE` against `security` and never `INSERT`, reports 0 rows whether it resolved every name or died on the first. On 2026-08-27 it recorded outcome `failed`, 149 calls and 0 rows, and 0 rows is exactly what a perfect run would have recorded. `skipped` is reported by the stage rather than measured, which is the opposite of the rule above and is deliberate: a skip is a decision the stage made and nothing in the store records it, so there is no belief here for a measurement to guard against. Null rather than 0 on a run that skipped nothing, so a stage that walks no list is distinguishable from one that walked its list cleanly, and so that the sixty-one runs recorded before the column existed are not asserted to have skipped none.

**A slot that did not run is a row from 7.1, written the next night.** The tree guard exits before the worker starts, so a refused slot never reaches this table's writer, and on 2026-09-07 thirty-one refused slots left the table holding nothing for the date. The guard writes a line to the night's log instead, and `reconcile-night`, the second verb of the next night's `index` slot, reads the schedule against this table and that log and writes the rows through `RunLogger`. Market closed is read from `index_bar`, which the slot's first verb refetches whole every night, and never from `daily_bar`, whose ingest asks for its own date alone; a slot whose reason nothing establishes gets no row. **The readers bounded on `started_at` leave these rows out**, because each is written on a later night than the one it describes, and read by `session_date` instead (see: A slot that did not run is recorded the next night from its log, and a holiday is read from the index history).

**`counts_against_ceiling` is how a one-time operation stays out of the nightly budget.** The ceiling guards the evening's job; the history backfill is not the evening's job, and charging the two against each other is what once made the backfill look like a two-day procedure. Its calls are still recorded, because what a run cost is worth knowing about every run. The run says so in the store rather than being recognised by its stage name, which would put the exception in the query rather than in the record.

**Fixture capture is one-time on the same grounds, and this is the scope statement rather than a second exemption.** The two operations are the same shape: each runs when a person decides to run it, each stores what it fetched for good, and neither recurs. A capture that added an endpoint cost 30 calls on 2026-08-26 because responses already in the manifest are reused verbatim rather than refetched; charged against the evening's allowance those 30 would compete with the night's work for no reason. What decides the flag is whether the run is the evening's job, not which stage issued it, and a third one-time operation inherits the answer without a third entry here.

## Store configuration

SQLite, one file under the configured data root. These are set at open, in one place, by the same shared extension that wires config.

| Pragma | Value | Why |
|---|---|---|
| `journal_mode` | `WAL` | The nightly job writes for hours while a page may be reading. Without it a reader blocks a writer and evening runs throw spurious lock errors |
| `synchronous` | `NORMAL` | Safe under WAL against a process crash. `FULL` costs write throughput for protection against power loss, which a nightly snapshot already covers |
| `busy_timeout` | 5000 ms | Brief contention retries rather than throwing. Zero is the default and it is the wrong default here |
| `foreign_keys` | `ON` | Off by default in SQLite, per connection, and silently so |

**One writer, one connection.** The Worker is the sole writer of everything the nightly job produces, and SQLite makes that a practical requirement rather than a stylistic one. A second writing connection working alongside it produces intermittent lock failures that look like load problems and are not.

**The one exception is the agreement a person records, and the reason is the boundary.** A person's judgement is captured on the page that asks for it, and the Worker never writes `setup.agreement` or `setup.agreement_note` because the Worker has no judgement to record. There is no run in which the nightly job could produce a value for either, which is what makes these two columns the only ones in the store it cannot own, and what stops the exception being read as a general licence for the read surface to write where writing is convenient. It is not the same kind of write in any case: a person saying what they thought of one row, at a keyboard, on two columns no computation reads, where every other write in the lab is the evening's job producing evidence on a schedule. It cannot contend for a row that job is writing, and under WAL a single short update is what the busy timeout exists for.

Nothing rests on that paragraph holding. The writer is declared above by the type that issues the statement rather than by the screen that asks for it, so `writer-ownership` reads every write in the shipped source and fails by name on a second one appearing in the read surface (see: The agreement a person records is written through the read surface, and it is the only write it makes).

### Expected size

About 0.8 GB a year including indexes, roughly 4 GB after five years. Minute bars are 69% of it: sixty flagged setups a night across 390 minutes and 252 sessions is close to six million rows a year. Everything else together is under 250 MB.

Nothing at this scale argues for a different database. The lever, if it ever matters, is storing 5-minute bars instead of 1-minute, which would cut the largest table by 80% and make the fill model coarser. The fill model is where the trade record's honesty lives, so that trade is not worth making at this size.

**Index `intraday_bar` deliberately.** A composite on ticker and timestamp is what the replay walks, and on six million rows a year the index is a material fraction of the store. Measure it rather than adding indexes by reflex.

### Backups

`VACUUM INTO` writes a full copy, so thirty nightly snapshots in year three is around 70 GB. Keep a short rolling window of nightlies plus one monthly. The backup total grows faster than the store and is the number more likely to bite.

## Migrations

Snapshot before every migration. `tools/migrate` calls `tools/snapshot-db` internally first and refuses to run without a successful snapshot.

Migrations are hand-written SQL. Generated table rebuilds have twice re-added constraints that a convention here strips, so a table rebuild carries a row-survival test asserting the count before and after.
