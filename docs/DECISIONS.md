# DECISIONS.md

Decisions a later session could reasonably make differently, where the wrong choice would be invisible. Mechanisms with one obvious implementation, and anything a test already enforces, are not here.

**Each decision is identified by its name, in bold, and cited by that exact name.** In code, `// see: Long and short are never pooled into one figure`. In a document, `(see: Long and short are never pooled into one figure)`. Headings carry no terminal punctuation, because a name is not a sentence and a name ending in a period is awkward to cite. `decision-resolves` fails on a near-miss rather than ignoring it, since a paraphrased citation silently stops resolving.

**Grouped by topic, not by date.** Related decisions sit together, which is what keeps this readable at forty entries rather than at ten.

A decision is changed only by another decision. Work that changes one writes a new entry, names what it supersedes, and moves the old entry to "Previously decided" in the same commit with its reasoning intact. Nothing here is struck through.

---

## What is being measured

**The subject is the flagged setup population, not the trade log**
At a 25% win rate with expectancy carried by the right tail, roughly 420 trades are needed to establish that expectancy is above zero and over 1,100 to detect a small improvement. The setup population gives dozens of observations a day at far lower dispersion.

**Forward returns are recorded for every flagged setup, traded or not**
Separates "was the pattern worth spotting" from "was the execution any good". Recording only trades makes those two indistinguishable, and they have different fixes.

**The evidence store holds only setups flagged forward, never setups reconstructed from history**
The detector is a pure function of stored daily bars, so it can be run over years of backfilled history in seconds. That run is useful for one thing, counting how many setups a night the thresholds produce, and useless for measurement, because the record of who was actually listed on those dates does not exist. Delisted names are simply absent from a reconstructed universe, so every historical setup would carry survivorship bias.

Historical runs therefore write to a separate calibration table that nothing downstream reads. The evidence store begins empty on the first forward night and fills one session at a time. This is what the nightly universe snapshot exists to protect, and it is the difference between the replay this project relies on and the backtest it deliberately is not.

**A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store**
The decision above says a historical run is useful for one thing, counting setups a night. That is narrower than the run can answer, and the narrowness is what had the project waiting eighteen trading nights for the number that decides whether to continue. A reconstructed read is permitted: the same detectors over history, with matched controls and forward outcomes, producing a paired comparison per direction. It writes to calibration tables that nothing downstream reads, it never writes a row to the evidence store, and no figure it produces reaches the scoreboard, the panel, or 3.6's trigger. **3.6 still fires on forward evidence and on nothing else** (see: The evidence store holds only setups flagged forward, never setups reconstructed from history).

Nothing above is changed by this. The evidence store still begins empty on the first forward night and fills one session at a time, the nightly universe snapshot still exists to protect exactly that, and the reason a reconstructed row is not evidence is unchanged. What is added is that a row which may not be evidence may still be informative, provided what it carries is stated where it is read.

**Three reconstructions ride on every row, and `SCHEMA.md` already names all three.** Membership is today's, because a night the lab was not running has no snapshot. The market-capitalisation clause of `tradable-shortable` is exempt, because a reconstructed session has no capitalisation at all. And the bar series is read as the store knows it now, corrections included, because a backfill takes a name's whole history in one evening and a read bounded on the session's own instant returns nothing.

**The survivorship direction is stated per side, because the two sides are biased opposite ways and one caveat hides that.** A reconstructed universe holds the names that survived to today. On the **long** side those are disproportionately the winners, so a long figure taken over them is a **ceiling** and the honest number is lower. On the **short** side the names missing are the ones that fell furthest, which are exactly the ones a short profits most from, so a short figure is a **floor** and the honest number is higher. Reporting a single "carries survivorship bias" beside both would let a reader take the long number as conservative and the short number as generous, and both readings are backwards.

**The paired difference reduces this and does not remove it**, and the gap between those two claims is why this is written rather than assumed. Controls are drawn from the same survivor pool as the subjects, so the bias common to both cancels in the subtraction. What does not cancel is any difference in survival between flagged names and their matched controls, which is unmeasured and is not assumed to be nought.

**The range a read covers is the population, stated beside every figure rather than once.** A reconstructed read runs over a stated number of recent sessions and every figure it prints carries that range and its start date, so the survivorship exposure is visible as a date rather than as a caveat. The range is not a lookback constant and no bound is added to `ControlSampler`, whose own source says the fix for its cost is a decision about how far back a control may be drawn from rather than a constant added quietly. That decision is not taken here.

**Delisted daily history is bought so a reconstructed walk is not confined to survivors**
Approved by the operator on 2026-08-30 and built on 2026-08-31. The daily history of every name the exchange has delisted is bought, one call each, spread across the nights the ceiling leaves room on.

**The reason on the record is not 3.6.** 3.6 fires on forward evidence and this changes nothing about that. It is that phases 5 and 6 are built and tested against stored history: ReplayHarness re-filters it, SignalBackfiller computes across it, and TwinPairFinder z-scores over the trailing 250 setups. A surface of a hundred-odd rows over two nights is not something 5.3's acceptance test can be checked against, and the alternative to buying the history is waiting for it at one night a night.

**What it fixes is one of the three reconstructions, named as such.** `SCHEMA.md` says three ride on every reconstructed row: membership is today's, the market-capitalisation clause of `tradable-shortable` is exempt, and the bar series is read as the store knows it now. This closes the first and only the first. A name that traded in 2024 and was delisted in 2025 now has bars for the dates it traded, so a walk finds it on those dates instead of not at all, and the liquidity floor can be applied to it from the same bars every survivor is screened on.

**What it does not fix, priced, because a partial repair read as a whole one is worse than none.** The market-capitalisation clause stays exempt: closing it needs a shares-outstanding history per name at one fundamentals call each, which is worth doing and is scoped at the checkpoint that can narrow it. Restated bars stay restated, because the vendor publishes one adjusted series and not the series as it stood on a past date. And no minute bars exist for any night before capture begins, at any price, which is why 5.3 screens selection variants and never execution ones.
(see: Replay screens proposals and the forward paired test admits them)

**It changes no rule about evidence.** The bought rows are daily bars, and a bar is not a setup. The evidence store still begins empty on the first forward night and fills one session at a time, a reconstructed run still writes to the calibration tables that nothing downstream reads, and the survivorship direction is still stated per side, because the correction is partial and the residual is not assumed to be nought.
(see: The evidence store holds only setups flagged forward, never setups reconstructed from history)
(see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store)

**It is two verbs because the store's own constraint says so.** `delisted-list` records the names as securities and as no membership at all; `backfill --delisted` buys their history. `daily_bar` has a foreign key to `security`, `security` is written by UniverseBuilder and `daily_bar` by DailyBarIngestor, so one stage doing both would be a second writer of a table it does not own. The fetch takes its list from the store rather than from the endpoint, which makes the set it can buy the same set the store can hold: a night the lister did not run buys nothing, instead of spending its calls on bars that fail the constraint one at a time.

**It is bounded by security type and by venue, and the venue bound is the one that decides the size.** The list returned 59,920 rows, 32,851 of them common stock and about 16,000 of those on NASDAQ or NYSE, which is about 16,000 calls against about 33,000 for every venue. What the extra four nights buy is the delisted history of venues the current universe holds 30 names on out of 2,005. Both bounds are configuration rather than code, because a name on a thin venue could in principle have cleared the price and liquidity floors while it traded, so this is a bound on the purchase and not a claim that nothing was missed.

**It is charged against the daily ceiling although it is one-time work**, and that is what spreads it over nights when the allowance is what bounds it. The whole-universe backfill is charged outside the ceiling because it runs in one sitting. This one cannot: it takes what the evening's stages left, stops on the budget rather than overrunning it, and the next night resumes from `history_refetch`, which already carries a row per ticker per refetch and is the record the fetch itself writes. Nothing keeps a second list of what is done, because a copy can disagree with what it copies.

**A late answer is attributed to the session it was fetched for, up to a recorded lateness bound**
An answer the lab asks for on behalf of one session and receives after it may be attributed to that session, provided the delay is inside a stated bound and is recorded on the row. Beyond the bound it is refused, exactly as everything late was refused before.

**What the superseded form got right and what it cost.** It was written to stop a plan being improved once its outcome is visible: a trigger nudged, a stop widened, a check re-run until it passes, each of which turns the record into a description of what would have worked. That reasoning is unchanged and most of the rule still stands on it. What it got wrong was equating "arrived after the session" with "could not have been known during it", which are different facts for an answer the lab was already asking for.

**The cost is stated so a later session does not re-broaden this by feel.** Under the superseded form, fifteen of the forty-four setups of 2026-08-27 keep a null cluster verdict for ever, because a stage crashed at 18:12 and the sectors it never fetched were fetched at `2026-08-28T04:19:33.201Z`, which is 00:19 Eastern: about six hours after the crash and 20 minutes after that session's own end of day. Against a minimum sample of 262 effective observations over at least twenty sessions, fifteen setups is about **5.7%** of the target and more than one session's worth of evidence, lost on the first night to a stage falling over rather than to anything the night could not know. A rule that discards that is not being careful; it is paying for its carefulness with the evidence the project exists to gather.

**Three conditions, all asserted.**

**The input is one the session itself asked for.** A sector the sector walk was already resolving for that night's scan is late; a sector nobody asked for until a month afterwards is new information. The distinction is the request, not the value.

**The lateness is inside the bound and is recorded on the row, in a countable column.** It is measured from the last instant of the session's own day in the session zone, which is the bound every reader in the lab already applies, and never from the stage that failed. Naming the origin is not pedantry, and this corpus has paid for it twice in one pass: the same arrival is six hours after a 18:12 stage and 20 minutes after that session's end of day, and it read as 260 minutes for as long as the end of day was being computed in UTC. A record carrying a lateness without an origin, or with an origin in the wrong zone, is a figure over an unstated basis. Not a sentence, because a sentence cannot be summed, filtered or excluded, and the first thing anybody will want to know is how much of a figure rests on late answers. `setup.correction_lateness_minutes` carries it. The bound itself is an authored value in ARCHITECTURE's parameters table, read by the recomputer rather than written into it, so moving it is one edit in the place every other authored value lives.

**Every other input is bounded to the session's own date, unchanged.** The exception is one column wide. A repair that admitted a second late input would be reconstructing the night rather than completing it, and the difference between those two is the whole rule.

**What stays forbidden, which is most of it.** A trigger, a stop, a size, or any gating check verdict computed from prices is never rewritten. Those are the plan. What may be completed is a recorded-not-required verdict whose input a failed stage never delivered, which today is `cluster` and nothing else. A row already corrected is not corrected again.

**And the mark has a reader, which the superseded form promised and did not have.** It said a correction is recorded "so a later reader can exclude corrected rows" while the guard it shipped with made corrected rows impossible, so the mark had neither producer nor consumer. That is CLAUDE.md's sixth failure shape written into a decision: correct upstream, discarded downstream. The scoreboard now reports how many rows in the population it measures were corrected and by how much lateness, so the exclusion is available to the reader the sentence describes (see: Every phase ends in a generated phase report, not in a page somebody looks at).

**Failed checks are recorded rather than discarded**
The research loop exists to find which checks carry the strategy, which is unanswerable if the store only remembers the setups that passed.

**A gate handed an absent or degenerate quantity fails rather than passing**
Every gate on both check lists compares a computed quantity against a threshold, and every one of those quantities can genuinely be missing or degenerate: no averages before the warm-up, no thrust without a scan hit, no entry level and no give-up point on a thrust that has not pulled back yet. The gate fails and records what was absent.

The alternative is not a crash, which is what makes this worth deciding rather than assuming. A thrust whose extreme is the current session puts the entry and the give-up point at the same price, so the give-up distance is zero, and zero clears every threshold expressed as a maximum. `exit-tight` passed on that in the first fixture run of the long detector: the tightest possible stop, on a trade that does not exist. A vacuous pass is worse than a fail here, because the loop reads these verdicts to find which checks carry the strategy and a check that passes on nothing is indistinguishable from one that is easy to clear.

Asserted over the gate list rather than per gate, so a check admitted in phase 6 inherits it without anyone remembering to.

**Matched control populations are drawn nightly, loose and tight**
Flagged setups returning 2% is not a result if everything returned 2%. The loose set matches on liquidity and daily range and measures the whole funnel. The tight set also matches on the trend ladder and market mood, isolating the pullback checks from simply owning stocks in uptrends. The tight comparison is the one that can embarrass the project, which is why it is on the scoreboard.

**The tight control set draws within the night, because a within-night draw controls the market mood exactly**
Ruled by the operator on 2026-08-31, superseding **The tight control set draws from any session sharing the market mood, and the loose set stays within the night**. Both sets now draw from the setup's own session. The tight set matches on the trend ladder, which varies across a night's pool and is what makes it tighter than the loose one; the market mood is not matched by exclusion because it does not need to be.

**Within one session every name carries the same market move.** That sentence is already in this register, where it derives the dispersion the minimum sample is built on, and it is the whole of this decision read from the other end. The mood is a property of the session, so over a single night's pool it is a constant, and a constant is the strongest control there is: a within-night tight set holds the mood fixed at the subject's own value on every row, exactly, with no residual to carry.
(see: The minimum sample is 262 effective observations, ratified at two points and 90% power)

**The superseded ruling read that invariance as an absence of control, and it is the presence of a perfect one.** Its reasoning was that a dimension which always matches reads in the record as a dimension that was checked, so the choice was to make the mood an active filter or to drop it. That framing has the sign wrong. A dimension that always matches within the population being compared is not an unperformed comparison; it is a comparison whose answer is guaranteed, which is what matching is for. Reaching into other sessions to make the mood vary so that excluding on it would do work is making a dimension active by first making it uncontrolled.

**What the reach cost is the cancellation the pairing exists to produce**, and it is measured rather than argued. The paired difference removes the market factor common to a night by construction, and that is the only reason a night is worth more than one observation. A tight control drawn from another session does not share the subject's night, so its side of the difference carries a different market move and the factor stops cancelling. Every pair on a night then carries the same uncancelled term, which inflates the within-night design effect, and the term persists across overlapping ten-day windows, which cuts the across-night factor. Measured over reconstructed history on 2026-08-31, over identical rows and nights: at 60 sessions the loose panel ran at an across-night factor of 0.3718 and a design effect of 3.40 for 428 effective observations, and the tight panel at 0.1108 and 6.71 for 65. At 120 sessions the tight design effect was 10.31 against the loose set's 3.75. The tight comparison was worth about a seventh of the loose one over the same 4,824 rows.

**That is not a cost worth a dimension that was already controlled.** The minimum sample is stated in effective observations precisely so that rows carrying less information cannot be spent as though they carried more, and at the measured rate the tight panels needed 202 to 1,030 forward nights to reach 262 against a band 1 condition of twenty sessions. A ruling that puts the project's central question out of reach in exchange for excluding rows that a constant had already excluded is the wrong side of the trade in both terms.
(see: The minimum sample is 262 effective observations, ratified at two points and 90% power)

**The tight set is still tighter than the loose one, on the dimension that varies.** The trend ladder is a property of the name rather than of the session, so a night's pool holds all three grades and matching on it excludes candidates. That is the question the tight set was built to ask, which is whether the pattern is worth anything beyond owning stocks in uptrends, and it is unaffected by this decision. What is dropped is only the reach.

**An unlabelled night now draws tight controls, where under the superseded ruling it drew none.** No session could be said to share a mood that was never recorded, so a missing label emptied the tight pool. Within the night the label is not what does the controlling: every candidate sat through the same session whether or not the label was written, so the mood is held fixed either way. A night whose regime stage failed keeps its tight comparison rather than losing it to a bookkeeping absence.

**`control_as_of` and migration 035 stay, and the reversal is why they earn their place.** The column records the session a control's outcome is measured from. That is now the subject's own session on every row, and stating it beats inferring it: the invariant is asserted rather than assumed, and a tight draw whose `control_as_of` differs from its subject's session fails a test. It is also the column the reach would need if it ever returns, and a reach that was tried, measured and reversed is a better argument for keeping the instrument than for removing it.

**Ruled before the evidence accumulates, which is again the only time it could be.** The superseded ruling stood for one day and no forward night ran under it. The live store holds two scoreboard dates, both drawn before it, no setup has closed its ten-session horizon, and no interval has ever been taken over either definition. Nothing accumulated is discarded and nothing is spent twice.

**3.6 gates what may be admitted, not what may be built**
Ruled by the operator on 2026-08-31. `BUILD_PLAN.md` said that phase 4 should not start without 3.6's answer. That is corrected: 3.6 gates admitting a variant, changing a rule and spending a holdout window. It does not gate constructing the apparatus that would do any of those things.

**The apparatus is rule-agnostic, and that is the whole reason.** VariantAdmitter, VariantScorer, ReplayHarness, HoldoutRegistry and the phase 6 loop are machinery for testing **any** rule against evidence. If the baseline turns out to have no edge, they are precisely the instrument you would use to find one that does, and holding them back until the baseline is vindicated makes the project's recovery from a negative answer depend on work nobody had started. RiskGate, PaperBroker and PositionManager are needed whichever rule wins. **Optimising noise is tuning the baseline; it is not building the apparatus**, and the two were read as one sentence.

**What still waits, named rather than implied.** Until band 1 reports both conditions for the direction concerned: no variant is admitted, no rule is changed, and no holdout window is spent. Band 1 reports per direction and per control set and is never pooled, so a long-side answer licenses nothing on the short side.
(see: Long and short are never pooled into one figure)

**It is enforced by gates the checkpoints already carry, not by anyone remembering.** VariantAdmitter writes a version's target and minimum sample at creation and never again, AcceptanceGate writes only status and resolution date and cannot touch a target, and a spent holdout window is recorded with its date range and cannot be re-spent. A rule that depended on a session recalling this decision would be the shape this corpus refuses; these three already exist as done conditions and each one refuses on its own.
(see: Targets and minimum samples are written at creation and are immutable)
(see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight)

**The baseline is still frozen at 5.1 and this does not touch that.** Registering V0 freezes it, and editing it afterwards closes every open version as unresolved and starts a new generation. That is why the measurement defects phase 3 carries fall due before 5.1 rather than whenever somebody gets to them: a figure repaired after the freeze costs the accumulated variant record.
(see: An approved proposal creates a new version from zero, and a running version is never edited)

**What is given up by this, said outright.** If band 1 eventually reports that the pattern has nothing in it, the phase 4, 5 and 6 code will have been written against a baseline that failed. That work is not wasted, because none of it encodes the baseline's thresholds: the caps, the fill model, the variant machinery and the research loop are all indifferent to which rule they carry. What would be wasted is any tuning of the baseline done before the answer, which is what this decision continues to forbid.

**The win-rate ceiling is computed from the outcome distribution, never assumed**
A give-up point at half a daily range sits 0.8 standard deviations away, so a coin flip with that stop wins about 20% of the time and the observed 25% is mostly geometry. What matters is the gap between achieved and the computed bound: a wide gap means selection has room, a narrow one means the stop is the binding constraint and the loop should point at execution instead.

**Controls are drawn by nearest neighbour on the matched dimensions, five per set, with no randomness**
The sets and what they match on are settled above. What was not settled is how many, how they are chosen, and whether the same night draws the same names twice.

Five per set per flagged setup. One control inherits that single name's idiosyncratic move; fifty reaches so far down the distance ordering that the match stops meaning anything. Five keeps a thin night visibly thin.

**Deterministic nearest neighbour rather than a random draw**, ordered by distance on the matched dimensions with ticker as the tiebreak, exactly as the scans and the cap already break ties. A seeded draw would be a second thing to keep point in time, a value the phase report cannot diff, and a number nobody could reproduce from the store alone. Nearest neighbour also makes the match quality the ranking rather than an afterthought: the fifth control is by construction the worst of the five, and `match_quality` records the distance on each dimension separately so nobody averages them.

Drawn from the same session's universe members that cleared the liquidity floor and were not flagged, at 18:26, **before the cap at 18:28**. Controls answer for the flagged population and not for the sixty that survived truncation, and drawing after the cap would compare the kept setups against controls for a different question.

**What weakening this looks like, written down because it is easy and silent.** A widened decile band, a dropped dimension when a night is thin, a fallback that reaches outside the session, or a draw that quietly takes fewer than five and reports the same figure. Each makes the tight comparison flattering, and the tight comparison is the one that can embarrass the project, which is the only reason it is worth having.

**The ceiling is computed from the path, not from the terminal return**
The question is what win rate perfect foresight achieves *given that it still has to survive the path*. So a setup counts toward the bound when its ten-session return is positive **and** its worst excursion never reached the give-up point. A setup that ends ahead having first been stopped out is not available to any selection rule, and counting it would produce a bound no system could reach.

That reads three stored figures together, `return_signed`, `mae_atr` and `stop_distance_ranges`, which is why the excursions are columns on `forward_return` rather than derived on read, and why 3.2 lands before 3.4.

**The trap, and it is the reason this is a decision rather than a formula.** The excursion is recorded in ATR and the give-up distance in daily ranges. Those are two different units on two different bases, and comparing them without a stated conversion is exactly the silent plausible number `PullbackGeometry` carries a warning about: both are small, both look like volatility, and a wrong one produces a bound that reads as reasonable. **The excursion is converted into daily ranges before the comparison, through the same `adr_20` the give-up distance is expressed in, and the conversion is named at the point of use.** Storing it twice was the alternative and it is worse: two columns that must agree are two columns that will not.

Per direction, never pooled. Recomputed weekly over the population that has closed its tenth session, and a later week's bound is a new dated row rather than a revision of the old one.

**The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured**
Supersedes **The interval is a block bootstrap over paired differences, and the effective sample is measured**, which named the right method and specified percentile bounds. The shipped code implemented neither, and correcting only the resampling would not have been enough.

This is not the textbook case, and the textbook interval is wrong here in the direction that matters.

**Ten-day labels overlap.** A ten-session horizon means adjacent nights share most of their window, so consecutive observations are serially correlated by construction. **Same-night setups share a market factor.** Forty names flagged on one night rise and fall together with the market over that fortnight.

Either alone makes an interval assuming independent observations too narrow. Together, band 1 clears zero before it should, and band 1 is the project's central question. A too-narrow interval does not produce a wrong number, it produces a confident one, which is the failure this whole system exists to avoid.

So: the statistic is the **paired difference**, a setup's return minus the mean of its own matched controls. That removes the shared market factor inside a night by construction rather than by adjustment, which is why the control draw is a prerequisite of this decision rather than a neighbour of it. The remaining serial overlap is carried by a **moving-block bootstrap over the session axis with a block length of ten sessions**, being the scoring horizon, at ten thousand draws.

**Each draw takes its block starts independently.** Stated because the decision it supersedes did not, and the code read as though it had: block starts were mixed by two coprime strides, which reads as spreading the draws and is not. Every start in draw `d` was the corresponding start in draw 0 shifted by the same `d * 7919`, so every draw was one fixed lattice rotated, at most one distinct resample mean existed per night however many draws were asked for, and ten thousand draws was bit-identical to N draws. On the five committed scenarios long enough to produce an interval, of six in `fixtures/interval-cases.json`, the intervals came back two to three point seven times narrower than a real moving-block bootstrap, worst on the AR(1) series written to exercise exactly the overlap it got most wrong.

**Deterministic from a fixed published seed, rather than from having no seed at all.** The superseded decision asked for deterministic ordering and the code met it by making each draw a function of its own index, which is what collapsed the resample space. Reproducibility is the property that was wanted, a published constant buys it, and `PairedInterval.Seed` is that constant.

**Studentised rather than percentile bounds, and this is the substantive change.** Independent block starts alone do not reach the confidence the panel prints. Measured over three hundred authored null series per row, all three schemes seeing the same series, against a nominal 5%: the superseded scheme clears zero 48.3% of the time at twenty independent nights and 46.0% at forty; a percentile interval over correctly drawn blocks clears it 20.3% and 12.3%; scoring each resampled mean against its own block-to-block standard error clears it 4.7% and 5.0%. With an AR(1) of 0.7 the three read 78.7%, 37.3% and 6.0% at twenty nights. Studentising holds 3.7% to 7.7% over independent nights and an AR(1) up to 0.7, from twenty to a hundred nights, and neither of the others does.

**Where it stops holding is stated rather than left to be discovered.** Against the process a ten-session overlapping label actually creates, a moving average of order nine whose correlation cuts off inside the block length, it clears zero 3.0% to 11.7% of the time from twenty to two hundred and forty nights. Against an AR(1) of 0.9 it reads 7.0% to 24.0%. That is a limit of the **block length** rather than of the method: correlation at 0.9 runs well past ten sessions and no block of ten absorbs it. The block length is set to the scoring horizon because the overlap the label creates cuts off there; if the realised series turns out to carry dependence past it, the block length is the thing that moves, and that is a decision rather than a tuning. Band 1 turns on whether a bound clears zero, so an interval that clears it four times too often is not a narrower version of the right answer.

**A series that cannot disperse is withheld rather than given an interval of no width.** An interval of no width clears zero always. That is the first route to this failure and it shipped for one run at 3.5; it is now a state the method returns nothing from.

A Newey-West style adjustment with the lag set from the horizon was the alternative and would have cost less to compute. It was not taken because it corrects the variance of a mean and this scoreboard also shows decile curves and win rates, and one resampling scheme that serves every panel is worth more than a closed form that serves one.

**The effective sample is measured from the realised series, never assumed.** The number of rows and the number of independent observations are different quantities here, and the ratio is a property of the realised autocorrelation rather than of the design. It is computed from the series and reported beside every interval. This half is unchanged and its arithmetic did not move.

**Any minimum-sample figure written against this is in effective observations, not rows.** Stated because it is the half that gets dropped: a pre-registered target reading "160 observations" is satisfiable by 160 rows carrying far less than 160 observations' worth of information, and nothing on the surface says so. The figure itself is settled by the decision below.

**What a later session should take from this.** Two independent implementations agreeing proves transcription and nothing else. `tools/derive-indicators.py` restated this interval and hard-coded the same two strides, so the DERIVED tier reported agreement about the wrong algorithm for the whole of phase 3. Where a method is named rather than tabulated, assert the property the name implies: more draws must buy more resamples, and an interval must not clear zero far more often than its own confidence claims.

**The minimum sample is 262 effective observations, ratified at two points and 90% power**
A sample size has three inputs: the difference worth detecting, the confidence demanded, and the dispersion of the statistic. Two of those are judgements and belong to a person. The third is a fact about the market, and until the decision this supersedes nothing in the corpus had measured it.

**So the figure that stood was an estimate wearing a derivation's clothes.** ARCHITECTURE stated 160 paired setup observations "detecting about a two-point difference in ten-day forward return", and every reading of it treated the 160 as falling out of the two points. It did not. Nothing had taken the dispersion over anything, nothing said what power the sample was sized for, and nothing said whether the observations were rows or independent ones.

**The dispersion is measured, over a named population.** Within one session every name carries the same market move, so the cross-sectional sample variance of that session's forward returns estimates the idiosyncratic variance directly: the common term cancels and the `n-1` denominator makes the estimate unbiased. That is the same cancellation the paired difference buys on the scoreboard, which is why it measures the right quantity rather than a near neighbour of it. Pooled by degrees of freedom across sessions, over the captured fixture's thirty names and 241 sessions, the single-name figure is **0.091115**. A setup's difference against the mean of five controls disperses by `sqrt(1 + 1/5)` times that, because the control mean carries noise of its own, giving **0.099811**.

**And the population is stated because it is a floor rather than an estimate.** Thirty names, hand-picked for liquidity, still listed at the end of the year. A universe with delistings in it disperses further, so the real figure is larger and the minimum it produces is larger. Measured again over the calibration store's 1,671 names clearing the liquidity floor across 742 sessions, the single-name figure is 0.088371, below the fixture's; that store carries survivorship bias by construction, so the two are recorded as agreeing rather than as one confirming the other, and the larger of the two is the one used.

**The arithmetic, with every input named.** `n = ((z_alpha + z_beta) * sigma_d / delta)^2`, the one-sample form, because pairing has already turned two populations into one series tested against zero and the two-sample factor of two would double the answer for nothing.

| Input | Value | Kind |
|---|---|---|
| `delta`, the difference worth detecting | two points of ten-day forward return | judgement, ratified |
| `z_alpha`, two-sided 95% | 1.959964 | fixed by the interval, which reads green on a 2.5th percentile bound |
| `z_beta`, 90% power | 1.281552 | judgement, ratified |
| `sigma_d`, the paired dispersion | 0.099811 | measured |

Which gives **262 effective observations**, rounded up because a fractional observation cannot be had and up is the direction that asks for more evidence. Not rounded to a round number: 250 or 300 would be an authored step in a figure whose whole point is that no step in it is authored.

**Two points, because it is the size of the effect being hunted rather than a target chosen for roundness.** The strategy's claimed expectancy is about 0.55R on a 3% stop, which is about 1.7 points of forward return. Detecting less than two points would be detecting something too small to trade after costs, so the threshold is set at what is worth having rather than at what is claimed.

**One consequence of that, recorded because it is the half a later reader would otherwise have to derive.** 262 detects two points at 90% power. Against the 1.65 points the strategy actually claims, the same sample carries about **76% power**, and 90% power at 1.65 points would need 385. That is not an objection to the ratification, which deliberately sizes on what is worth trading rather than on what is claimed. It is stated so nobody reads "90% power" as 90% of finding the strategy's own claimed edge.

**90% rather than the conventional 80%, because the costs here are asymmetric and in an unusual direction.** A false positive is caught downstream: the forward paired test and the variant machinery both sit after band 1, and a spurious reading does not survive them. **A false negative is caught by nothing**, because band 1 reading flat means the pattern has nothing in it and the project stops. There is no downstream from that.

At about eleven effective observations a night, 90% costs roughly six sessions more than 80%. Six days against a one-in-ten chance of abandoning a working strategy is the cheapest power anyone will buy in this project. **The convention was rejected rather than not considered**, which is why the reasoning is here: 80% is what a later session will otherwise assume was meant.

**The sensitivity, stated so the choice stays visible.** The sample goes as the inverse square of the difference and rises with the power demanded.

| At two points, power | Effective observations |
|---|---|
| 70% | 154 |
| 80% | 196 |
| **90%, ratified** | **262** |
| 95% | 324 |

At 90% power, detecting three points needs 117 and detecting one and a half needs 466. Moving either input is a superseding decision, not an adjustment.

Supersedes **The minimum sample is derived from a measured dispersion and counted in effective observations**, which left both judgements open and stood at 196 on an unratified 80%. The measurement, the population statements and the arithmetic are unchanged and are carried here in full; what changed is that the two judgements are now ratified and recorded with their reasoning, so a later session reads a choice rather than a convention.

**Long and short are never pooled into one figure**
In code, in a report, or on a screen. Short results carry a borrow assumption that long results do not, so a pooled number silently inherits it.

**Every fixture expectation records how it was produced, and only the independently derived ones verify anything**
An expectation generated by running the engine proves that the code agrees with itself. That is worth having, and it is regression detection rather than verification. Calling it verification is how a suite goes green over nothing, so the tier is recorded per expectation rather than inferred later.

| Tier | Meaning | Strength |
|---|---|---|
| `DERIVED` | Computed by a second method sharing no line of code with the engine: a spreadsheet, a different formula path, a published figure | Strongest, expensive, so there are few |
| `CONFIRMED` | A human checked the value against a source they already trust | Strong for a handful. What the chart page and the setup gallery produce |
| `FROZEN` | Taken from engine output once a `DERIVED` or `CONFIRMED` expectation at the same checkpoint passed | Catches change, never error |

The phase report breaks fixture coverage down by tier rather than as one number. "52,500 values diffed, 21 derived, 12 confirmed, 52,467 frozen" is the honest form, and it makes an all-frozen phase visible at a glance.

Settled before the fixture existed, because retrofitting provenance onto expectations already generated is guesswork.

**Fixture inputs record where they came from, and a path a live run exercises needs a captured one**
Two tiers. CAPTURED, stored verbatim from a real vendor response with the date and endpoint recorded. AUTHORED, hand-built to hold a case a live run cannot produce.

Authored inputs are necessary and they encode their author's beliefs about the vendor, which is precisely what a fixture cannot check, because the person writing the assumption and the person writing the test are the same. Two defects in phase 1 passed their unit tests and failed on live data for that single reason: an ingestor that compared against observations made by the bar date, and a rebuild rule that assumed a refetch restates every bar in a series.

So a path a live run exercises has at least one CAPTURED input. An authored fixture may hold the edge case; it may not be the only evidence for the ordinary case. The phase report breaks inputs down by tier alongside expectations, and a path with no captured input is reported as unexamined however many authored cases pass.

**Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it**
Two instruments answering two questions. The captured fixture holds a real market day and catches change on real data. An authored case sits either side of one gate's threshold and answers whether that gate's two branches both work. Neither substitutes for the other, and asking the captured fixture for branch coverage is what makes a fixture purchase look like the remedy for a coverage problem.

Measured at 2.6, which is what forced the choice: eight of the ten long checks were one-sided over the fixture, meaning one branch of each had never returned an answer. The cause is arithmetic. Thirty names on one session record two setups, and a gate with two results is one-sided unless those two happen to disagree. The priced remedy was a second as-of date at 33 per-ticker calls; it would have left those eight gates with three results each instead of two, which is a bigger instance of the wrong instrument. What actually closes it is two authored cases per gate, one just inside the threshold and one just outside, at no vendor call and no committed megabyte.

The cases are AUTHORED and are never counted as evidence about the market. They are the same tier as the synthetic split at 1.5 and carry the same limitation: they encode this author's reading of the gate, which is why the expectation over them is produced by a second implementation rather than by the rules themselves.

**A frozen-only permit names an open obligation or the settled reason nothing could close it**
Done condition seven asks every checkpoint for one expectation that is `DERIVED` or `CONFIRMED`. A checkpoint that contributed none is permitted to be frozen-only by a named permit in `fixtures/expectations.json`, and until now a permit had exactly one shape: it rested on a carried obligation, and `fixture-replay` failed it once that obligation's due checkpoint landed.

That shape is right for a checkpoint nobody has yet examined, and it cannot express the answer. Some checkpoints could have contributed an expectation and did not, which is a debt. Others could not, and never will: a phase sign-off adds no stage to the replayed pipeline and no behaviour to freeze, a spec pass produces documents, and a repair whose defect only appears across a daylight-saving boundary cannot be distinguished by a fixture holding one market day in August. Resting those on an obligation gives them a due point that moves at every sign-off, which is permanent while reading as pending.

So a permit carries exactly one of two things. **An obligation**, meaning nobody has established whether this checkpoint could have contributed, the question is carried in `BUILD_PLAN.md`, and the permit expires when it falls due. **A settled reason**, meaning it has been established that no replayed market day could produce a figure for that checkpoint, recorded at the permit. A permit carrying both, or neither, fails.

This is the third shape `OutOfScopeReason.ByDesign` already needed at 2.2, arrived at independently and for the same reason: forcing a permanent exemption into a shape that names a checkpoint invents one, and recording it as prose loses the distinction the guard is for.

The risk is the one by-design already names. If everything drifts into settled, the rule is decoration. What holds it is that the two counts are reported separately in the phase report, so the settled set growing is visible rather than absorbed into a number that reads as temporary. Settling a permit is a judgement about one checkpoint, made by a session that reads what that checkpoint built, and it is recorded with its reasoning rather than as a flag.

**A fixture expectation changes only with a recorded reason, and the report counts what changed**
When a diff fails, regenerating the expectations is the cheapest way to make it pass and it destroys the fixture's only purpose. So a changed expectation carries a reason line, and the report states how many expectations changed since the last commit alongside how many passed.

A phase whose report shows more changed expectations than new ones has re-baselined rather than built, and that is visible without anyone remembering to look for it.

## What is traded

**Two directions are tested, with separate detectors, separate management and separate scoring**
The strategy has a short side. Testing only half of it answers a different question.

**Trades are resolved by replaying minute bars after the close, not by watching live**
The vendor publishes minute data two to three hours after the close, so live monitoring buys nothing and costs an entire category of infrastructure. The plan is locked before the open either way.

**The plan is written before the session and is immutable after publication**
A strategy test whose rules can move after the outcome is visible is not a test of anything. Most of the architecture exists to protect this one property.

**A session is a date the store holds minutes for, and no calendar is authored here**
A plan's `live_session` is the next weekday after the evening it was written on, and about nine weekdays a year the American exchanges are shut. Nothing in this lab knows which nine. The store holds bars for sessions that have happened, no holiday calendar exists anywhere in the corpus, and 18:30 on the evening before is too early for the next session's bars to answer it.

The alternative is to author one, and that is the reason this is a decision rather than a mechanism. A hand-written list of American market holidays is a plausible thirty lines that would be wrong in some future year, would be wrong silently, and would be a rule this lab invented sitting in the middle of a record of what a market did. It is the ruling 4.15 took over the support definition, in a place where the temptation is stronger because the list feels like a fact rather than a choice.

So the lab records what it can observe. A plan resting in a session the store holds no minute for is resolved as `unresolvable` with the reason on the row, and the run reports partial rather than clean. **That answer is the same for a holiday, for a fetch that did not run and for a name the fetch missed, and the corpus does not pretend to tell them apart.** What it refuses to do is call any of them a plan that did not trigger, because a strategy that declines to trade on exactly the nights the lab was blind is a different strategy with better numbers (see: A gate handed an absent or degenerate quantity fails rather than passing).

**The cost is bounded and is a plan that does not fire rather than one that fires on the wrong day.** Plans are immutable and single-session, so a plan carrying a holiday is not rolled forward: it finds no minutes and resolves against nothing. What that costs is the candidates of about nine evenings a year, which at a median of nought candidates a night is currently nothing and stops being nothing the moment the thresholds move.

**Two of the six limits are not applied at trigger, and which two is stated rather than left to the code**
RiskGate applies four: two count caps that can only block, being four positions open and two of those short, and two proportional caps that reduce to fit, being 35% of the account in one position and 3% of it at risk at once. The other two are limits of the strategy and not of the gate.

**Risk per trade is what the plan was sized from**, so RiskGate asserts it rather than enforcing it. A plan risking more than the budget it names is a defect at 18:30, and gating it would treat a broken plan as an ordinary large one and carry the defect into a position. The stage stops on one.

**The give-up distance cap is `exit-tight` at detection.** A setup whose give-up point is further than half a daily range is refused on the evening it is flagged, so a plan that reached a trigger cleared it hours before. Re-applying it at trigger would be a second implementation of a gate, and the two would disagree the day a daily range was restated between the evening and the session.

It is a decision because the alternative is coherent and its failure is quiet. A gate that re-applied all six would look more careful, would pass every test written against it, and would silently refuse trades on a recomputed range while the record said the setup had passed. **And 4.6's own row said three count caps where the tables hold two**, which is how a miscount in a done condition becomes a component with a cap nobody wrote: the reconciliation is here rather than in the row that got it wrong.

**A tie in trigger time is broken by ticker, and never by rank**
Two plans touched in the same minute with one slot left have to be ordered somehow, and the corpus says outright what may not do it: rank governs which setups are recorded under the nightly cap and how the screen sorts, and it governs no fill (see: Plans are resting orders and fills go in time order when the caps bind). So the tie falls to the ticker, alphabetically, which is the tiebreak the screen and the cap already use.

**It is deterministic rather than fair, and that is the property being bought.** A tie decided by whatever order a query happened to return is a fill nobody can reproduce, and a replay of the same session on a different day would place a different order. Alphabetical is arbitrary and admits it; the alternative is arbitrary and hides it.

**The cost is small and is stated so nobody re-derives it later.** A minute is the finest resolution the vendor sells, so two plans in one minute is the finest tie the data can express, and at a median of nought candidates a night it has never happened. It is decided now because the first time it happens is a night nobody is watching.

**One replay clock walks every name of a session at once**
The contention rule fills the earliest trigger and blocks the later ones, so which name fired first is a comparison across names rather than a property of any one of them. A clock per name would answer each name correctly and would produce no ordering at all, leaving it to be reconstructed afterwards by whoever needed it (see: Plans are resting orders and fills go in time order when the caps bind).

It is a decision rather than a mechanism because the per-name reading is the obvious one and its failure is invisible. Every stored reader in this lab before 4.5 takes one ticker and one session, so a resolver written on that pattern reads correctly, resolves correctly, and records the same trigger times. The reconstruction that follows is a second implementation of the one ordering 4.6 has to get right, and a second implementation that agrees with the first on ordinary days is exactly the kind that disagrees on a tie.

**The walk is forward-only because the type holds it, not because the caller is careful.** The clock hands out ascending minutes and each one carries that minute's bars and nothing else; there is no method that takes an instant, and the walk may be enumerated once, because a second enumeration from inside the first is precisely how a caller sees a minute later than the one it is standing on. A resolver that could look ahead would produce answers that look exactly like honest ones.

**RiskGate is the sole writer of orders, for both directions and every version**
Two writers puts the caps in two code paths, which voids every comparison between versions and does so silently.

**The short borrow problem is mitigated by a filter, not solved**
Market cap above $2 billion, dollar volume above $50 million, ninety sessions since listing. The borrow cost of 1.0% annualised is immaterial, about 0.4% of one R on a four-day hold, so availability rather than cost decides the short side and the filter stands in for information the feed does not carry. To be recorded as an unmodelled assumption on every short trade from 4.7, and shown in the trade journal from 4.11. No trade row exists yet, so this is what the assumption is owed rather than where it currently sits.

**Equity is a fixed $100,000 notional that never compounds**
Every cap in the design is a percentage of equity and nothing stated what equity was, so no share count could be computed.

The number is set by share rounding rather than by ambition. At 0.75% risk, a three percent stop on a $400 stock is $12 a share, so $100,000 buys 62 shares and the rounding error is under one percent of intended risk. At $10,000 the same trade is six shares and the error is four percent, which pollutes the R figures the whole project is measured in. Below roughly $50,000 the arithmetic stops being trustworthy.

It does not compound, and that matters more than the level. If equity tracked results, a version that ran well would size larger than one that ran badly, so the two would stop taking the same position on the same setup and the paired comparison would quietly stop being paired. The equity curve is reported as an output; it is never an input to sizing.

Share counts round down, and the realised risk is recorded beside the intended risk on every position so the gap is visible rather than assumed away. The position row arrives at 4.7 and the journal that shows the pair to a person at 4.11; until then this is what is owed rather than what is displayed.

**One account per version, holding both directions**
Not one account per direction. The caps are four concurrent positions, three percent total risk at stake, and at most two shorts, and those only mean anything if a short and a long compete for the same budget. That competition is real: in an account, a short ties up risk a long cannot then use.

The rule that long and short are never pooled governs reporting, not accounting. Shared account, separate books. Expectancy, win rate and every scoreboard panel are computed per direction; the caps and the equity are shared.

**The nightly cap is 60, split forty long and twenty short, unused slots released**
Applied to the shared candidate list before any version selects. The split is deliberately not proportional: short setups are rarest in a strong market, which is exactly when they are most interesting, and a proportional split would erase them from the record on those nights. Capping per version would leave the disagreements unscoreable, which is the wrong data to lose.

**A released cap slot goes to the side that still has candidates**
Each side takes the lesser of its candidate count and its allocation. Whatever either leaves unfilled is offered to the other, by rank within that other side.

No priority order is needed and that is worth stating rather than leaving as an omission, because the obvious reading of "unused slots released" is that two sides compete for a freed slot and something has to break the tie. They cannot. A slot is only released by a side that ran out of candidates, and a side that ran out is not also asking for more, so the two conditions are mutually exclusive and one pass is deterministic. A stated tiebreak would be a rule covering a case that cannot arise, which reads to the next session as though it can.

**The scans select a fixed count by rank, not a threshold on the move**
Each of the six scans takes the top fifty universe names by its own magnitude, ranked one to fifty.

The mechanism is calibration. Phase 2 sets these against nightly counts with no forward return anywhere in the store, and a rank cut is the only form that can be calibrated that way: moving fifty to forty changes the count by construction. A percentage floor cannot, because whether eight percent is too strict is a fact about market volatility over the sample rather than about the corpus, and the same floor produces nothing in a quiet quarter and hundreds in a violent one.

Rank is also what makes the six comparable to each other. A one-day move of eight percent and a twenty-session move of twenty-five percent are not the same strictness, and nothing in the design says what would make them so; rank fifty is the same strictness on all six by construction. It bounds the sector-lookup cost as a side effect, which is a convenience rather than the reason.

**Every scan magnitude is computed on the adjusted basis**
The one-day change, the gap from the previous close to the open, and the twenty-session change, all on adjusted prices, with the open put on that basis through its own bar's `adj_close / close` factor.

Read raw, a split turns a rise into a collapse. The failure produces a plausible ranked list rather than an error, which is the same shape as the basis trap the averages already closed and which nothing had closed for the scans. Stated as a decision rather than left to the implementation, because a later session could reasonably read "yesterday's biggest movers" as raw and nothing on the screen would look wrong.

*Corrected on 2026-08-26, after the scans were built and measured.* This entry first said a two-for-one split reads raw as a fifty percent decline that would top the **decliner** scan on the day it happens. That is wrong about which scan, and the correction is worth keeping because it changes where the guard has to sit. The vendor adjusts the history **behind** a split and leaves the sessions after it alone, so on the split date itself the one-day change is identical on both bases and the daily and gap scans cannot tell them apart. It is the twenty-session scans that span the adjustment. Measured on the fixture: IESC's month magnitude is **+0.0746 adjusted and −0.4627 raw**, so raw it would top the laggard scan ahead of NCLH at −0.1403 rather than sitting tenth among the leaders. A rise of seven percent and a fall of forty-six, from the same two rows.

**The cluster grouping key is industry, not sector**
Both cluster checks say "same industry" and the authored parameter says "same industry"; the component catalogue said "same-sector" and the two are different columns giving different answers over the same night. One key, read by ThemeClusterer and by both checks.

Industry rather than sector because the check exists to distinguish an industry shift from one company's news, and sector is too coarse for that: two names in the same sector routinely have nothing to do with each other, so a sector count would report grouped movement on almost every busy night and mean nothing.

**A calibration run reconstructs against current membership and computes its indicators in memory**
The nightly universe snapshot only starts when the lab does, so a historical detector run has no record of who was listed on those dates and reads membership from `universe_member` as it stands today. That is the survivorship bias the calibration table exists to quarantine, and naming it as the mechanism is what keeps it from being read as an oversight later.

It computes each session's averages through the shared arithmetic in Core and writes no `indicator_daily` row. Writing them would be the reconstruction the evidence rule forbids, arrived at from a different direction: the engine computes for the members of a night's snapshot, and there is no snapshot for a night the lab was not running (see: The evidence store holds only setups flagged forward, never setups reconstructed from history) (see: The averages are one implementation, computed nightly and drawn on demand).

It is the same detector, in a mode, rather than a second one. A separate implementation would make the count it produces a fact about the calibration code rather than about the thresholds, which is the one thing the run is for.

**The market-cap clause of `tradable-shortable` is exempted by name in calibration mode**, on the pattern `reached-ceiling`'s anchored clause already uses. `SecurityReader` bounds the lookup on `sector_resolved_at` like every other point-in-time read, so a reconstructed 2024 session sees no capitalisation at all: it was resolved in 2026 or it was never resolved. Left alone, every short candidate fails the first gate and the short half of the distribution is empty, and a threshold calibrated against an empty distribution is worse than no threshold. Dropping the gate was the other option and it is worse still, because it changes what the short side is without saying so. Exempting one clause by name changes one thing, says which, and leaves the other nine measuring. Every calibration verdict records that the short side was measured against a nine-clause detector, so a later session reading a count knows what produced it.

**Which sessions the run covers is a separate question from how the detector reads them, and the two were confused until 2.11.** The averages are computed in memory, as above. The detectors were not: they read `indicator_daily` and `scan_hit` from the store, and both hold one session. So a run over stored history was not a run of what exists, whatever range it covered.

Replaying the nightly pipeline session by session was considered and does not work, and the reason is worth keeping because it is the same reason this entry gives for the averages. `IndicatorEngine`, `ScanEngine` and `TierClassifier` all read `UniverseSnapshotReader.Members` for the night they are computing, and a night the lab was not running has no snapshot. Giving those three a mode that reads current membership instead would write reconstructed `indicator_daily` rows, which is exactly what the paragraph above forbids, arrived at from a third direction.

**So the detector carries the whole session in memory, and it is assembly rather than a second implementation.** `IndicatorEngine.Calculate` takes a window of bars and returns the figures; `TierClassifier.Grade` takes those figures and returns the ladder; `ScanMagnitudes` and `ScanEngine.Top` rank the six scans over a session's candidates. All four are public and shared, put that way at 2.6 so the nightly run, the calibration run and a test would have one implementation between them, and this is the run that spends it. What calibration mode adds is the assembling: one pass over the members per session, reading each name's window once, computing that name's figures from it, ranking the session's candidates from the same windows, and handing the detector a session it can read. Nothing is computed a second way, and a change to any of the four moves the nightly answer and the calibration count together.

**The run that sets a threshold has to be over the live universe, and that is a property of the scans rather than a preference.** A scan takes the top fifty names by its own magnitude. The golden fixture holds thirty names with a history, so every one of them is inside the top fifty of all six scans on every session: `thrust` passes on every row, the most recent hit is always the session itself, no pullback has any bars, and every geometry check fails. The count is nought by construction and no threshold could be read off it. It was narrowed to the fixture before 2.11 on the belief that a live run needed three stages rebuilt; the belief was wrong, the in-memory session is one file, and the fixture turned out to be the thing that could not answer.

So both run and each has its own job. Over the fixture the run is a diff, session by session, so a change to any gate or to the assembly of a reconstructed session shows up as a named difference rather than a count. Over the live universe it is the measurement, and its figures live in `PROGRESS.md` as a dated event rather than in the fixture, because they are a reading of one store on one date and not a property the pipeline reproduces.

**Plans are resting orders and fills go in time order when the caps bind**
Every plan that passes its checks is placed, and each is a complete instruction before the open: this price, this stop, this size. Nothing is decided during the session. When more plans trigger than the caps allow, the earliest trigger fills and later ones are blocked with a reason, which is what resting orders actually do.

The alternative, reserving capacity for the best-ranked plans and placing only those, was rejected. It requires trusting the ranking rule, which is an authored guess with a scoreboard panel devoted to finding out whether it works, and it ends some days with slots unused because the reserved names never triggered. The cost of time order is real and worth naming: a mediocre setup triggering at 9:31 consumes capacity a better one triggering at 10:15 cannot then use. That is a first-class execution-family experiment once there is enough record to run it.

**The screen and the cap both rank on give-up distance in daily-range units, ascending**
R is the move divided by the stop, and range units normalise the stop against that stock's own noise, so a 0.30 setup earns more R per unit of noise risk than a 0.48 one for the same move. The only ranking available with a stated mechanism rather than a preference, and an obvious early proposal target.

**The market-mood label is recorded on every setup and filters nothing in the baseline**
Baking it in now would be an untested assumption. Adding it later as a version is a measurement. Both raw scores are stored beside the label so a later proposal can use the continuous form rather than the three buckets.

**The hourly grid anchors to the session open, and the closing stub is not an hourly bar**
The short exit triggers on an hourly bar closing back above the 50-day average. The store holds minute bars and no hourly bar, so the bars have to be aggregated, and where the boundaries fall changes which closes exist. The regular session is `SessionBoundaries.RegularSessionMinutes` long, which does not divide by an hour, so every grid over it leaves a remainder and the only question was where the remainder sits.

Anchored to the open, the session is six complete hourly bars and a closing remainder of half an hour. **That remainder is not an hourly bar and cannot trigger the rule.** The rule turns on a close, and a close is only the close of the thing it closes: a level held for the last thirty minutes of a session has not been held for an hour, so reading the remainder as an hourly close would fire the exit on a bar the rule never described.

Nothing is lost by excluding it. The session close is already its own signal and is handled by the exit rules that read a session rather than an hour, and this rule exists to catch the thesis breaking **during** the day rather than at the bell. What would be lost by including it is the meaning of the word: a rule whose bars are sometimes sixty minutes and sometimes thirty compares two different quantities under one name, and the shorter one is systematically noisier.

**The alternative was anchoring to the clock**, on hour boundaries at 10:00, 11:00 and so on. It is rejected because it puts the remainder at the front. The stub would then be 09:30 to 10:00, the least representative half hour of the session, carrying the opening auction and the widest quotes of the day, and it is precisely where a short's bounce most often looks like it has broken back above a level and has not. A grid that makes its shortest and noisiest bar the first one has put the stub where it does the most damage, and a rule reading it would exit good positions on the open.

The two boundaries are `SessionBoundaries.RegularSessionOpen` and `RegularSessionClose`, shipped at 4.2 with the minute bars. `HourlyGrid` derives the grid from them and restates neither, so the session's definition lives in one file.

**The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it**
PlanBuilder computes the share count at 18:30 from the risk budget and the plan's own give-up distance, and writes it on the plan. RiskGate at trigger may reduce that count to fit a cap, or block the order outright, and it never computes a size of its own.

Three places in this corpus answered it differently, which is what made it a decision rather than a mechanism. The vocabulary calls a plan a committed instruction naming this many shares. The catalogue gives sizing to the component that runs on trigger in the following session. The watchlist built at 4.1 renders no share count and says it is waiting for that component.

**The plan is locked before the open and published at 18:40, so a size has to exist by then.** A plan whose size arrives ten hours later is not a committed instruction, and the page that publishes it would have a column it could not fill.

**Recomputing at trigger would make `plan_audit` compare two of this lab's own numbers.** That component exists to hold a planned stop against an executed one, which is an intention against an outcome. If the size were derived twice from the same inputs, the comparison would be between two runs of one formula, and the only difference it could ever show is a bug in the second run.

**A reduction keeps the plan's give-up price.** Recomputing the distance from a reduced size would change R for the same trade, and R is the unit every downstream figure is denominated in: expectancy, the ceiling gap, the variant comparison and the loss classification all divide by it. A trade that risked less than planned is a trade that risked less than planned, and that is what `risk_intended` beside `risk_realised` is for.

**Every reduction records the cap that caused it.** A size that arrives smaller than the plan's with no reason is indistinguishable from a sizing bug, and the caps are the one thing a comparison between versions must be able to attribute.

**What 4.16 does not settle**, so it does not read as settled: RiskGate does not exist until 4.6, so the half of this that says nothing recomputes a size is held by a source scan naming its one caller rather than by a behavioural test. The scan cannot see a component that reimplements the division instead of calling the function. The behavioural half arrives with the component that could break it.

**The anchored average price is anchored at the swing the thrust ran from**
The short side's `reached-ceiling` asks whether price came back within half a daily range of "the declining average price anchored to the last swing high". Until 4.4 that anchor was a phrase: nothing said which bar the swing high was, which minute inside it, or what the average was taken over, so three sessions computing the level would all have produced plausible prices and no two would have had to agree. The clause decides the verdict of the gate 423 of the 432 short calibration rows that reach it are refused by, so the level is not a detail of it.

**The anchor is the extreme of the swing the flagged move ran from**, being the highest high between the start of the thrust span and the thrust's own extreme, earliest on a tie. `PullbackGeometry.SwingIndexOf` computes it from the same bars and the same span the rest of the geometry reads, so the anchor cannot drift from the move it belongs to. The span matters: `gainer` and `gapper` flag one session where `leader` and `laggard` flag twenty, so a swing searched without the scan is a swing searched over the wrong window. The mirror is a parameter, so a long-side anchor is the swing low over the same span rather than a second rule.

**The moment is a minute, not a session.** The level is a volume-weighted average from the anchor forward, and starting it at the session open rather than at the minute the high traded in would average in the part of the day before the event the level is named for. `anchored_vwap.anchor_ts` holds it, so the level can be reconstructed from the stored minutes by somebody who does not know which component wrote it.

**And the average is over typical price, being high, low and close over three, weighted by shares.** That is what a volume-weighted average price means everywhere it is drawn; weighting the close alone gives a different number under the same name, and a gate comparing today's close against the level would move with the convention while nothing said so.

*Corrected on 2026-09-01, after the clause was built and its ceiling measured.* This entry first said the clause decides "the gate that takes the short funnel from 432 rows to 9", which reads it as what empties that funnel, and 4.4 measured that it is not. Given the clause its maximum, admitting every one of the 432 short rows that reach `reached-ceiling`, the funnel still ends at **4 survivors over the 602 calibration sessions**, median nought a night, on 4 nights of 602. The gate that binds is `exit-tight`, at **0.93% over the 431 short rows** that then reach it against **1.51% over 1,981 long rows** on the same sessions: a comparable per-row rate, so what the short side is short of is rows reaching the gate rather than a gate set too strict. **The decision itself is unchanged** and so is the reason for it. What the correction removes is a motivation that was wrong about the consequence, and the motivation that survives is the smaller one already in the line above: the level decides a verdict, and three sessions computing it differently would disagree about that verdict whatever binds downstream.

**Where the store cannot reach the anchor the clause does not run, and the verdict says so.** The level is never approximated from daily bars, which is the refusal this check has carried since 2.7: a daily-bar stand-in produces a number that looks like the real thing inside the check deciding whether a bounce reached its ceiling. `reached-ceiling` records three clause sets rather than two, so a row that could not be anchored stays distinguishable from one written before anchoring existed and from one that ran the full disjunction (see: A gate handed an absent or degenerate quantity fails rather than passing).

**Entry slippage is the whole captured spread, symmetric between the directions**
The form was settled at 4.3 as the spread. The fraction is the whole of it, not half, on both sides.

The trigger is a traded price and a resting order entering on it crosses the book, so half a spread would price a fill at the midpoint of a book the order did not get. The fill model's stated stance is pessimism on purpose, and being too pessimistic understates edge, which is the safe direction for a lab whose question is whether edge exists at all.

**Short borrow cost is not modelled and the short side is understated by an amount nothing measures.** The assumed borrow rate is charged per calendar day held and is a separate line; what is absent is the cost of locating and holding the borrow itself, which varies by name and by day and which this lab buys no data for. It is recorded here rather than left out, because a symmetric slippage rule reads as though the two sides were treated alike and on this one term they are not.

**The spread applied is a proxy, and the approximation is stated rather than unnoticed.** `spread_snapshot` is captured twice a session, not at the trigger, so the figure charged is not the spread that existed at the fill. Worse, the vendor stamps a quote's bid and its ask separately: on the capture of 2026-09-01 AAPL's two sides carried stamps 32 seconds apart, so a stored `spread_bps` need not be a spread that existed at any instant. Charging a fraction of a figure taken across a gap as though it existed at a moment is an approximation this decision takes deliberately. What 4.7 owes is not this choice but what happens to a row whose two sides are far enough apart to describe different markets.

**Exit slippage is charged on the same terms as entry slippage**
The whole spread, both directions, and the same figure for a trail exit as for a give-up exit.

Pricing one end of the trade and not the other flatters every R figure by half the round trip, systematically and in the direction that manufactures edge. The lab exists to measure whether edge exceeds cost and the round trip is the cost, so an exit priced at nothing is not a conservative simplification but a thumb on the scale.

**The size of what is being left out is measured rather than assumed small.** The 4.3 capture found spreads from 0.9 basis points to 327 on one afternoon. At the wide end the round trip is 6.5%, against which a stop a third of a percent out is not a tight stop at all, so the term is capable of deciding whether a trade was ever viable rather than trimming a result.

A trail exit and a give-up exit are given the same treatment rather than two, because nothing in the corpus distinguishes the book one crosses on the way out by which rule sent the order.

**A minute that opens through a resting price fills at that open, whatever time of day it is**
The open of the minute bar in which the order would otherwise have been filled, read from `intraday_bar` rather than from any session-open field, and not slipped on top. Supersedes **A gap through a price fills at the session's first regular minute open, and is not slipped again**, which said the same thing about the session's first regular minute alone.

The loss is explicitly never clamped, so the gap is taken as it happened. Taking the worse of the open and the resting price would price an adverse move that did not occur, which is pessimism past the point where it is still measuring something. Tying the price to a bar the store holds keeps it derivable from what the lab has, on the same footing as every other price in the system, rather than resting on a field the vendor computes.

**A gap fill does not additionally slip**, because the gap is the adverse move. Charging a spread on top would be charging twice for one crossing.

This applies to an exit as much as to an entry, and it is the one exception to the slippage rule above.

**What changed is the clock, and the clock was never the reason.** The superseded wording named the session's first regular minute because that was the case 4.7 could see: an overnight gap. A minute in the middle of the day can open past a resting price too, on a halt or a thin book, and until 4.8 such a minute filled at the price the order named, which may be a price that did not trade in that minute at all. The argument for the open was always that a resting order cannot be hit at a price that did not exist, and that argument says nothing about the time of day.

**The size is small and the sign is not.** The capped sixty are large and liquid, so a minute-to-minute jump through a price is usually a fraction of a spread. Every instance of it flattered, which is the only direction of error this lab cannot afford, and 4.8 added two more resting levels a minute can open through, so the same question was about to be asked three times.

**The favourable side is refused rather than taken.** An open past a resting price in the position's favour is not a gap and is not filled there: the short trim is the case, and taking a better open would price a fill better than a resting instruction could have got.

**Every exit is PositionManager's and every entry is PaperBroker's**
From 4.8 the broker opens a position and the manager is the only thing that trims, arms or closes one. The give-up exit moved out of the broker with the two rule sets rather than staying beside them.

Until 4.8 a position ended one way, on the give-up point, which is a resting instruction the plan carried from 18:30 rather than a rule anybody evaluates, so the broker could run it without evaluating anything. From 4.8 it can end three ways and the rule is that the exit is whichever is reached first. **That is a comparison across rules, and a comparison cannot be made by two components each of which sees one side of it.** A broker that closed on the give-up point at 14:00 could not know the manager's hourly reclaim had fired at 10:31, and nothing downstream could have seen the difference: both are real exits at real prices and only one of them happened.

**It also gives `position` one writer per operation.** Two stages that can both close a row would put the exit rules in two code paths and void every comparison between versions, which is the argument RiskGate holds over orders one level up (see: RiskGate is the sole writer of orders, for both directions and every version). `fill` keeps two writers and they are disjoint by leg, on the shape `setup_signal` already carries.

**The 21:20-after-21:15 ordering is unchanged and is now load-bearing.** A position has to exist before it can be managed, and a position opened at 09:31 and stopped out at 09:45 is one the manager walks in the same session the broker opened it in.

**Neither exit rule takes over from the other, and a tie inside one minute resolves as a give-up**
The fixed give-up point and the direction's rule set are both live from the entry fill to the close. Neither replaces the other at any point, so there is no handover; the exit is whichever is reached first, and where two name the same minute the order is stated rather than left to the sequence a walk evaluates its rules in.

**A handover rule would need a moment to happen at, and every available moment is authored.** A number of R, a number of sessions, a distance: nothing in the strategy names one, so a threshold would be a fourth arbitrary-within-a-range value beside the trim fraction, the ADR offset and the noise boundary. Running both to the end needs no parameter at all, and the fixed give-up point already governs the early part of the trade, which is the same reasoning that gave the trail no arming threshold (see: The long trail is evaluated on the daily close and fills at the next open).

**What running both does need is a total order, because a minute bar carries no order inside it.** Two ranks and not four. An exit at a minute's open resolves before one reached inside that minute, which is a fact about the bar rather than a choice. Within the open, giving up comes first, on the pessimism the fill model takes everywhere else and for one further reason: **a gap through the stop names how the loss occurred**, and recording such a minute as a trail exit would hide a gap loss inside a rule exit where LossClassifier could not tell the two apart (see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list).

**The two rule-set exits share a rank because they can never contest each other**, one being the long side's and one the short side's. That is asserted rather than assumed, so a later session adding a third rule finds a rank missing rather than a silent tie (see: Long and short are never pooled into one figure).

**A trim is not an exit and is ordered under both of them.** A bar holding both the 3R level and an exit trigger takes the exit and no trim, on the same pessimism: a trim locks in a gain, and taking it first would credit the position with a price the bar cannot say traded before the other one.

**RiskGate reads the book as it stood coming into the session, and what that costs is counted**
The gate stays at 21:10 with the previous session's book, and `manage_run.closed_in_their_own_session` records how many positions closed inside the session it was gating.

The gate decides before either the entries or the exits of that session exist, so a position opened at 09:31 and closed at 09:45 still occupies a slot the 10:00 trigger is refused on. **The caps are therefore tighter than the design rather than looser**, which is the opposite direction of error from the one 4.6 carried and the safer of the two: it under-trades rather than over-trades.

**Merging the gate into the walk would fix it and would give orders a second writer**, which costs more than the approximation does. Reading the previous minute's fills instead would make the gate's answer depend on a stage that has not run, and a rerun in a different order would quietly change it, which is the fault `OpenComingInto` is bounded on a session rather than a stamp to avoid.

**So the cost is counted rather than argued.** A figure on the night is what makes the choice reviewable: if it stays at nought the approximation never bound, and if it grows the merge has a size attached to it rather than a plausible story.

**A fill is charged the widest usable quote of its session, not the nearest one**
Of the two passes a session gets, the fill model charges the one with the wider spread, whatever
minute the fill happened in. A session with one usable pass is charged that one.

**Three reasons, and the first is the fill model's own stance.** Pessimism on purpose is what every
other rule in the model is chosen for, and being too pessimistic understates edge, which is the safe
direction for a lab asking whether edge exists at all.

**The second is that it removes the within-day question entirely.** The passes are at 10:15 and 15:45
and a fill can happen at any minute, so a rule preferring the nearer sample would charge a fill at
09:31 from a quote taken three quarters of an hour later, which is a book that morning had not
reached. Choosing by width does not depend on when the fill was, so there is no reading of it under
which a fill is priced from the future.

**The third is that a nearest-in-time rule would claim a precision the data does not have.** The feed
is delayed by about fifteen minutes by design, and the two sides of one quote are stamped seconds
apart. A sample that is nearer in clock time is not thereby a better estimate of the book at the
fill, and treating it as one would put a false precision on top of two stated approximations.

A tie is broken by pass name, on the same grounds a tie in trigger time is broken by ticker: an
answer that depends on the order a query returned is one nobody can reproduce
(see: A tie in trigger time is broken by ticker, and never by rank).

**A straddled quote is charged and the straddle is recorded, never widened or refused**
The vendor stamps a quote's bid and its ask separately. A fill is charged the stored spread whatever
the gap between those two stamps, and the gap in seconds is written onto the fill beside the figure
it was charged.

**The alternatives both require authoring a number this corpus does not have.** Refusing a quote
whose sides are far enough apart to describe different markets needs a threshold, and widening one
needs a factor. The corpus holds exactly one measurement of a straddle, being 32 seconds on AAPL on
the capture of 2026-09-01, one name on one response. A threshold set from that is a number invented
at the consumer and then relied on as though it had been measured.

**Recording it is what the store already does with the vendor's delay**, which is the same shape of
fact: the feed is delayed, the lag is stored per row, and nothing subtracts a constant, because
subtracting one would make the assumption invisible and leave a later reader unable to tell a normal
sample from a stale one (see: A delayed quote records its own lag rather than being corrected for it).
The straddle is stored per fill for the same reason, and a later session that accumulates enough of
them can set the threshold from data rather than from a guess.

**What this costs is stated rather than left implied.** A stored `spread_bps` need not be a width that
existed at either stamp, and on a name whose book moved between them it can be wider or narrower than
anything a trader could have crossed. Charging a fraction of it as though it existed at an instant is
an approximation taken deliberately, and it is the one 4.3 handed forward to be answered here.

**A fill with no usable quote for its name is refused and recorded, never charged nought**
Where a session ran its passes and quoted a name no two-sided book, the placed order is not filled.
It is written as a position row with the status `unfilled` and the reason, and no position is opened.

**Charging nought is the failure this refusal exists to prevent.** A spread of nought is not a missing
spread: it is a free entry, and it clears every threshold written as a maximum. A lab that filled such
an order at the trigger exactly would produce an encouraging figure computed over the names its own
capture could not price (see: A gate handed an absent or degenerate quantity fails rather than
passing).

**Charging a figure taken from other names is worse, because it looks like an answer.** A median or a
sector average would be a spread nobody measured wearing the authority of one that was, and nothing
downstream could tell the two apart from the row.

**The refusal is a row rather than an absence**, on the terms a blocked order already sits on. A
morning on which two orders could not be priced is evidence about the capture, and it is
indistinguishable from a quiet morning unless the refusals are stored.

**What it costs today is small and it grows at 5.1.** Plans are built for capped candidates only, and
the spread capture takes the capped sixty, so the only names this can refuse today are ones the vendor
quoted with one side. From 5.1 a version can select outside the cap, and a name outside it has minutes
bought and no spread at all, so every such order would be refused. That is the choice this decision
puts in front of 5.1: widen the capture to the flagged population, or accept that a version selecting
outside the cap trades nothing. It is stated here rather than discovered there.

**The session average is derived when it is wanted and is not stored on a bar**
`intraday_bar.vwap_session` was written onto every stored minute from 4.4 and stopped being written at
4.7. Anything wanting a running session average computes it from the session's own stored minutes at
the moment it wants one. The anchored average is unaffected and stays a stored table.

**No reader was ever named and none exists.** 4.4 raised the obligation in the same entry that built
the column: either a reader is named or the column stops being written. It fell due at 4.7 on the
reasoning that the fill model was its most likely reader, and the fill model does not read it. A fill
is the resting price plus the captured spread, no rule in this lab compares a price against a session
average, and nothing through phase 6 consumes it.

**It is derivable, which is what separates it from the anchored average.** A running session average
is a volume-weighted sum over one session's minutes in order, so it needs nothing the store does not
already hold. That is the ruling VwapEngine already took over the day's high and low and
WatchlistPublisher took over a watchlist table: a stored figure derivable from rows the store holds is
a second statement of those rows that can disagree with them. The anchored average needs a swing
nothing else resolves and is not recoverable from one session, so it stays.

**What stopping it buys is the last exception to a hard rule.** This was the one declared update
against a bar table anywhere in the store, and `bar-append-only` carried it by table, by column and by
component. With the write gone, nothing in the shipped source updates a bar table at all, and the rule
reads as it is written with nothing after the comma.

**The column is not dropped and the values already written stay.** Dropping it would delete what past
nights wrote from the one kind of table this store never edits, in order to tidy a document. It is
recorded in SCHEMA as written from 4.4 and not written from 4.7. The two `vwap_run` columns that
counted the annotation are dropped by migration 044, because a stage's record reading nought on every
future night is a stage a later session reads as broken.

**The trigger is touched, not closed through**
For a long, a minute bar whose high reaches the trigger price. For a short, a minute bar whose low reaches it. No margin.

The contention rule is stated as what resting orders do in a real account, and a resting order fills on a touch. A close-based reading would delay every fill to a minute boundary and would reorder which name wins contention, since the rule compares trigger times across names; the three readings of "the trigger traded" order the same session differently, which is why this is not a detail of the resolver (see: Plans are resting orders and fills go in time order when the caps bind).

**Touch is the optimistic reading of whether a fill happened, and the slippage decision is what prices it.** The two are kept as separate questions on purpose: conflating them would hide a fill assumption inside a cost assumption, where neither could be varied without moving the other.

**The long trail is evaluated on the daily close and fills at the next open**
A daily close below the 9-day average ends the position, and the fill is at the next session's open. Active from entry, with no arming threshold.

The 9-day average is a daily series, so an intraday touch of it exits on noise rather than on the rule the strategy states. Arming from entry needs no second parameter, and the fixed give-up point already governs the early part of the trade, so an arming threshold would be a rule nobody has described. Filling at the next open is free of lookahead and is the same mechanic as the gap answer above, so the trail and the give-up point behave alike on a gap rather than differently for no stated reason.

**The exit is whichever of the fixed give-up point and the trail is reached first**, which is the long side's answer to the question the short side already had. The short side has a stated exit trigger, an hourly close back above the 50-day average, and the long side's "let it run" stated nothing; this is the same asymmetry the pooling rule addresses one level up (see: Long and short are never pooled into one figure).

**The short trim is 15% of the planned position, once, at 3R**
Fifteen per cent of the share count the plan was sized at, not of what remains, fired once when 3R is reached and not repeated at further levels.

A fraction of the remainder is a decaying ladder that never fully exits, and it makes R accounting depend on how many times the rule has already fired. A fraction of the original is a fixed share count computable at plan time, which keeps it immutable with the rest of the plan (see: The plan is written before the session and is immutable after publication). Repeating levels would be a second rule set, and the document describes one.

**The 15 is arbitrary and is recorded as such.** It is inherited from the strategy's own "about 15%" and nothing derives it. A later session reading this row should see a choice made inside a defensible range rather than a value with a basis.

**Trimming into support is dropped from the baseline rather than defined here**
The clause is removed. The short side keeps its trim at 3R and its exit on an hourly close back above the 50-day average.

Support is defined nowhere in this corpus: not in the vocabulary, not in the signal library, not in SCHEMA. Any definition written now would be authored rather than recovered, so the choice was between inventing a level and dropping the clause.

**It is dropped rather than defined because phase 5 is where a rule variant is tested against evidence.** A support definition belongs there as a named variant carrying its own stated level, where it can be screened and paired; baked into the baseline it cannot be told apart from the rest of the rule set, and every figure the baseline produces would carry an invented number nobody could isolate.

Recorded as dropped, with the reason, so a later session reads a decision rather than an omission. Without this entry the clause's absence is indistinguishable from an oversight, and the next reader restores it.

**The audit holds three pairs and they answer three different questions**
`plan_audit` carries execution at both ends, the plan's stop against where the trade ended, and the size the plan carried against the size the gate placed. Three pairs and not one field.

**Each of the three sources named a different thing and none of them was wrong.** SCHEMA's ownership line said planned stop beside executed stop. The mockup's plan-against-actual column shows an entry difference in basis points, with "stop jumped" where a gap made the number meaningless. ARCHITECTURE's catalogue said "planned against executed" and named no field at all. Choosing one would have made the other two false, and each of the three is a thing somebody wanted to be able to read.

**The first pair is execution and it is what a defect surfaces in.** The price an instruction named against the price it got, in money and in basis points, at the entry and at the exit. Basis points rather than money alone, because six cents on a six-dollar stock and six cents on a four-hundred-dollar one are two different execution facts and the column is read across names.

**The second is the plan's stop against where the trade ended, and it is not the first one restated.** They are the same number on a give-up exit and a different quantity on every other: a trail exit ends nowhere near the give-up point by design, so reading the two as one would report every winner as an enormous execution failure. Keeping them apart is the same distinction the exit rules already draw between a resting instruction and a rule.

**The third is the gate, and it is what `plan_audit` was designed around.** RiskGate may reduce a size and may never recompute one, so what is compared here is an intention against an outcome rather than two runs of one formula (see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it). The cap that bound is on the row, so a reduction reads as a decision rather than as an unexplained difference.

**Every difference is derived from the two prices rather than copied from `fill.slippage`.** An audit reading the model's own charge would be comparing a number against itself, and a model that stopped charging what it says it charges would agree with the audit all the way down. The two also legitimately differ on a gap, where the model charges nothing and the price moved anyway, so each pair carries the fill's basis and a gap is never read as slippage.

**TradeJournal runs first and PlanAudit second, and the audit never changes a result**
Both run on exit. The trade is written at 21:25 and the audit at 21:26, and `plan_audit.trade_id` is a foreign key into `trade`.

**The ordering is expressible rather than remembered**, which is the same thing the PaperBroker-then-PositionManager pair already gets from a position having to exist before it can be managed. A note in a runbook is a convention, and a convention that exists only in what previous runs happened to do is one the next run will break.

**What it also buys is that the audit cannot correct anything.** The result was written before the audit ran, so nothing the audit computes can move it. A component that could both produce a result and adjust it would be auditing itself, which is the argument ActionIngestor and IndicatorEngine are two components for one demand: a component that can raise and close its own condition raises nothing.

**The reverse order would have been defensible and is worse for one reason.** Auditing first and journalling second would let the journal record a result already reconciled against the plan, which sounds like an improvement and is the thing being refused: the trade's result is what the fills say it was, and a figure adjusted by a later reading is no longer a measurement of the night.

**A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list**
A closed loss is noise when the direction-signed ten-day forward return from the trigger reaches +1R or better. Below that, the setup failed.

The boundary is in R rather than in per cent, because R is the unit every other figure in the lab is denominated in and a percentage is not comparable across names of different volatility. 1R is the point at which the trade would have paid for the risk it took, which is the narrowest defensible reading of "the move happened anyway".

**The 1 is arbitrary within a defensible range and is recorded as such**, on the same terms as the trim fraction.

**Precedence is not a single ordered list, and stating it as one is what made it look like a conflict.** A gap loss is classified as a gap loss first, because that names *how* the loss occurred. Noise against failed setup names *what happened afterwards*, and it classifies the losses that were not gaps. The two answer different questions, so a gap loss that later recovers satisfies both without contradiction, and reporting them as one ranked list would hide that a loss has a mechanism and an aftermath.

**A gap loss is detected from the exit fill's basis, not from the size of the loss**
`loss_class.mechanism` is `gap` where the exit filled at an open already past the price it named, and `ordinary` otherwise. Supersedes nothing, because nothing had been decided: ARCHITECTURE's failure table carried a detection line and no decision stood behind it.

**The stated detector fires on every ordinary stop-out.** The failure table has said since it was written that a gap loss is a "loss larger than one unit of risk". A round trip costs two crossings, so an ordinary stop loses slightly more than one unit of risk by construction: 4.7 measured exactly that and asserted it as an inequality rather than as a number. Implementing the line as written would put every stop-out in the gap bucket, and the noise and failed-setup buckets would be empty on every night the lab ever ran.

**A taxonomy whose largest bucket is guaranteed to contain another is one whose shares mean nothing**, which is the same argument `unclassified` exists for one level up: a cause that is always assigned can never be shown to be missing one.

**The basis says what happened rather than what it looked like.** `gapped` on a fill is an exit that could not be hit at the price it named because the market opened past it, which is the mechanism the bucket is about; the size of the resulting loss is a consequence of it and of the plan's stop distance together. The document is corrected at 4.10 rather than the code being written to it, because the code is right and the line was a symptom mistaken for a cause.

**A loss awaiting its horizon carries no aftermath, and that is not the same as being unclassified**
`loss_class.aftermath` is null while the ten-session horizon has not closed, and `unclassified` where it has closed and no forward return was filled.

**They are two different facts and only one of them is a finding.** Null is a question the lab cannot answer yet, which is the ordinary state of every loss for its first ten sessions. `unclassified` is a question the lab could answer and could not place, and a share that grows in it is a finding about the classifier rather than about the trades. Collapsing them would make the second unreadable, since the first is far more common and would swamp it.

**It is also why the classifier writes twice rather than waiting.** The mechanism is known the moment the trade closes and the aftermath is not, so holding the first back until the second exists would discard an answer the lab already has, which is what the recording floor refuses everywhere else. The row is inserted with a mechanism and updated with an aftermath, and carries a stamp for each so a replay standing between the two sees what stood then.

**The horizon is counted from the store's own bars rather than from a calendar** (see: A session is a date the store holds minutes for, and no calendar is authored here). The setup's own session is in the count, so eleven sessions is ten having passed, and a name whose series the fetch has not reached stays waiting rather than becoming unclassified for a reason that is about the fetch.

**The aftermath is measured from the exit as well as from the close, as two figures and never one**
Answered by the operator on 2026-09-02, at the phase 5 sitting, closing the row raised at 4.10. The aftermath keeps the window it has, measured over the ten sessions from the trigger, **and gains a second figure measured from the close of the trade**. Neither replaces the other and no figure adds them together.

**They answer two different questions and the gap between them is the thing being judged.** The first asks whether the move the pattern predicted happened, which is a question about the setup and is what the scoreboard's flagged-setup population is scored on. The second asks what the day offered against what the trade took, which is a question about the exit rule. **The exit price is what the trade earned and the close is what the day offered.**

**It is the only way the trail can be judged at all.** The trail is the one rule that lets a position run past its own session, so with a single figure a trail that captured a move and a trail that gave one back are the same number. The second figure is what separates them, and it is authored now rather than when the first long hold arrives, because a rule that has never fired is exactly when its measurement can be designed without a result in view.

**Nothing is wrong today and the cost is nought until it is.** No position has ever been held past its own session, so the two figures agree on every row the store holds and the second is a column nobody has to backfill.
(see: Long and short are never pooled into one figure)

**The order prices are derived from the final pullback session's minutes, not from the screening geometry**
`PullbackGeometry.Of` computes an entry level and a give-up point from daily bars and they are screening quantities feeding `trigger-near` and `exit-tight`. They are not the prices an order is placed at, and reading one as the other is how two different numbers silently become one.

**The intraday floor is the lowest low of the final pullback session's regular-hours minute bars**, and the ceiling is that session's highest high on the short side. The document already rejects the low of the whole dip as an order reference under "Why the exit-tight check is the interesting one", where it is 5.6% against a cap of 3.4% and the trade fails the strategy's own risk rule; the final session's extreme is tighter than the dip's. It must be computable on the evening before, because the plan is locked before the open, which rules out anything reading the entry session's own minutes.

**The give-up point sits one tenth of an ADR beyond that extreme, on both sides.** Above the bounce high for a short, below the pullback floor for a long. Expressed as a fraction of ADR so it is scale-free across names, and symmetric so neither side carries an offset the other does not. This closes the gap where the short check list says the give-up point is *above* the bounce high and the code puts it *at* the high, so the clause had no code and the offset had no value. **The 0.1 is arbitrary and is recorded as such**; the ADR form and the symmetry are not.

**Where several scan hits fall inside the window, the thrust is the one with the extreme**: highest high for a long, lowest low for a short, ties broken by recency. The pullback is a retrace from an extreme, and both the give-up point and the anchored average price are measured from it, so a most-recent rule can anchor to a smaller hit and place both prices against the wrong level. The rule this replaces is most-recent-then-rank and is written nowhere, so what changes is an undocumented rule rather than an authored one.

**A setup with no pullback gets no plan.** PlanBuilder writes nothing and records why, rather than sizing on a nought (see: A gate handed an absent or degenerate quantity fails rather than passing). This is the defect already carried against 4.16, where a setup whose thrust has not pulled back yet is stored with a trigger and a give-up point at the same price, and the answer is that it is not a value.

*Corrected on 2026-09-02, at 4.18, after the 4.13 sign-off found that nothing had built it.* Two things, and neither changes what the operator answered. **The source is the final pullback session's daily bar and not its minute bars.** The answer named minutes and required the plan to be computable the evening before, and those two could not both hold at 18:30: the evening's minute fetch buys session N's bars for the names flagged on the evening of N−1, so on the evening a setup is flagged the store holds no minute of its final session. What settles it is a fact about the vendor rather than an assumption in this entry: the daily bar's high and low are the regular-hours extremes, established on 2026-09-02 by one `eod/AAPL.US` call for 2026-08-25 read against the 959 captured minutes of that session, 313.59 and 308.21 from both, where the extended-hours low was 290.4636. So the floor and the ceiling this decision names are in the store when the plan is written, as the same two numbers, and the schedule does not move and no call is added. The finding is recorded in ARCHITECTURE under "What each vendor endpoint carries", beside the endpoint note 4.3 left, because the next checkpoint reaching for a daily extreme will ask the same question. The trigger, which this entry names only as one of "the order prices", is the same session's extreme on the entry side, the high for a long and the low for a short; that is a reading taken at 4.18 and stated as one. **The thrust-selection clause is answered and not built.** The detectors still take the most recent hit and then rank, and the clause moves the thrust of every setup with two hits in its window, with the screening geometry, the gate verdicts, the anchored average and every calibration figure since 2.11 behind it. It is measured over the calibration sessions before it ships, on the terms 3.0(c) set for the last geometry change, and is carried as an obligation due at 5.1. **What was built until 4.18 was the reading this entry's first paragraph refuses**: PlanBuilder copied `PullbackGeometry.Of`'s pair into the plan from 4.16, and the 4.16 record did not say so.

## How changes are judged

**The once-only threshold adjustment is recorded unspent, and the baseline is frozen without it**
Answered by the operator on 2026-09-02, at the phase 5 sitting, closing the row raised at 2.11 and the sequencing question raised beside it. The adjustment 2.11's count distribution called for is not spent, and it is not held open against the freeze either.

**The middle reading is the one refused, and refusing it is most of the decision.** Three readings were on the table: spend the once before the freeze, freeze knowing the once costs a generation whenever it is spent, or record it unspent and freeze. The second converts a decision that can be taken now into one that closes every open version as unresolved the moment it is taken, and nothing about waiting improves the answer (see: An approved proposal creates a new version from zero, and a running version is never edited).

**What decided between the other two is the measurement of 4.4 rather than a preference.** Given the third clause its maximum, so that every one of the 432 short rows reaching `reached-ceiling` is admitted, `exit-tight` passes 0.93% of the 431 rows that reach it against the long side's 1.51% over 1,981, both over the same 602 calibration sessions. The per-row rates are comparable between the two sides, so what the short funnel is short of is rows reaching the gate rather than a gate set too strict. **A threshold adjustment moves a rate that is not the fault**, and spending a once-only adjustment on it spends it for nothing.

**Unspent is a decision and not an omission, which is the whole reason it is recorded here.** A later session reading 2.11's band of 5 to 60 against a median of nought a night would otherwise find a condition that fired and an adjustment nobody made. What this says is that the adjustment was located, priced against the gate it would move, and deliberately not made. **The once survives**, for a fault a measurement actually locates rather than for the first fault anybody points at, and V0 freezes with nothing outstanding against it.

**Versions select from one shared nightly candidate list rather than each re-scanning**
Makes the comparison paired. Unpaired, a small improvement needs thousands of observations; paired it needs hundreds. That is the difference between a verdict in months and a verdict in years.

**Two experiment families, selection and execution, scored differently and never mixed in one version**
An execution change alters the size of the R unit rather than the choice of stock, so its results cannot be differenced the same way. A version changing both teaches you nothing about which change caused the result.

**No execution variant is admitted in this generation, and the condition that would reopen it is named**
Answered by the operator on 2026-09-02, at the phase 5 sitting. The execution family is defined, scored nowhere and admitted nowhere for this generation, because both of the routes by which a version earns its place are closed and neither closes for a reason that expires on its own.

**It cannot be screened.** An execution rule is replayed against minute bars, minute bars exist only from the night capture begins at 4.2, and the vendor sells no minute history, so the stored record has nothing for a stop or a trail to be resolved against. **And it cannot accumulate.** It is scored on R, R needs fills, and the funnel passes a median of nought candidates a night on both sides, so no trade has ever fired and none is expected under the thresholds as they stand.

**Admitting one anyway would put a version in the register that cannot be screened, cannot be scored and cannot be resolved**, which is a row the corpus carries for ever: a version stays open with its age on the ledger and there is no timeout that quietly closes it, by design (see: Targets and minimum samples are written at creation and are immutable).

**What it settles for the three rows that turn on it.** VariantScorer scores one family and names the execution family's scoring as unreachable with the reason. ReplayHarness's acceptance test is the only screen this lab has, and its row says that rather than describing a half it does not build. And the hand-written versions are one family.

**What would reopen it, named rather than left to be rediscovered.** Minute bars accumulate forward from 4.2, so the screenable population grows at one night a night and an execution screen eventually has one; and a funnel that produces trades makes the forward route live. This is a decision for this generation rather than for ever, and the row it closes says which condition changes it (see: Replay screens proposals and the forward paired test admits them).

**A version changes one threshold over the existing gate list, and structural change is out of scope for this generation**
Answered by the operator on 2026-09-02, at the phase 5 sitting. A version differs from the baseline by **one gate's threshold**, with every other gate identical. It does not add a clause, remove one, or change the shape of one.

**What decides it is the acceptance test at 5.3 rather than a preference for simplicity.** That test is that the baseline's own rule through the harness reproduces the baseline's historical selections exactly, and it is meaningful over a threshold change and vacuous over a structural one: a version carrying a different clause set is not a rule the harness can reproduce the baseline with, so the one check standing between a proposal and the record says nothing about it.

**It is also what makes "differs by exactly one clause" assertable.** Over the gate list as it stands the claim is mechanical: one named gate's threshold moves and the rest compare equal. Over structural difference it is not, without a rule algebra nobody has specified, and an unassertable admission rule is one that holds only while everyone remembers it (see: Two experiment families, selection and execution, scored differently and never mixed in one version).

**This bounds phase 6 and it says so in terms.** The researcher proposes threshold moves over the named gates, and a structural proposal is out of scope for this generation rather than a proposal that gets rejected on its merits. A signal request is unaffected, because it widens the library rather than the rule shape, and it is the channel the loop's own design says makes the system better over time.

**Acceptance measures expectancy, never win rate**
Win rate is reported alongside as a diagnostic. Any version raising win rate while lowering expectancy is rejected automatically. Widening the stop does exactly that, and this rule is written before any results exist on purpose.

**Targets and minimum samples are written at creation and are immutable**
Twenty worthless candidates give a 64% chance that at least one looks impressive by luck. Pre-registration is the only defence, and a target that can move after the result is not a target.

**A minimum sample is derived from the store rather than authored, and the derivation is written before the freeze**
Answered by the operator on 2026-09-02, at the phase 5 sitting, over the two rows raised at 3.5 and 3.11 read as one question. Both said the 262 is too small and neither could say by how much, and the answer is that no one can: **the figure is a derivation, not a value, and what was owed was never a number**.

**Both corrections widen it and neither factor is stateable from the corpus.** The bootstrap correction depends on the tail behaviour of the statistic actually run, being a studentised moving-block bootstrap over `nights / 10` blocks, and no normal-theory sample size describes it. The dispersion correction depends on the ratio of flagged-name dispersion to universe dispersion, which is measurable from the 602 calibration sessions and has never been measured. So a number written today would be the same error the 160 was: an estimate wearing a derivation's clothes.

**The two are answered together because they compose.** Each alone gives a different figure and the pair gives a third, and answering them separately would leave nobody able to say which of the three was being asked for. One derivation applies both, one dated correction records it, and one pin holds the result.

**It runs before the freeze, and that is the operative half.** 262 stands until the derivation replaces it. After V0 is registered a re-derivation is a change to a figure the pre-registration made immutable, so it closes every open version and starts a generation, where before the freeze it costs an afternoon. **If the derivation cannot be specified without first running it, that is recorded as the result** and the rows stay with the operator saying so, which is an answer rather than a deferral.
(see: The minimum sample is 262 effective observations, ratified at two points and 90% power)

**The execution minimum is 200 paired trades and its conversion waits on a trade existing**
Answered by the operator on 2026-09-02, at the phase 5 sitting, over the row raised at 3.0(f). The figure is **200 paired trades converted at the measured trade-level design effect**, which is unmeasurable until trades exist, so it is stated as a derivation rather than as a number and the row count stands in the meantime.

**It is the same shape as the selection figure and it is not a stall.** The corpus's unit is effective observations because overlapping labels and a shared market factor make a row worth less than its own number, and a row count is what 200 is. The setup-level discount was measured at 3.40 rather than assumed at 1, and the honest form here is to name the same conversion and say what it waits on. **No trade has ever fired**, so nothing about a design effect over trades can be measured today, and stating one would be inventing the very quantity the setup-level measurement refused to assume.

**What is written into a version's pre-registration until then is the row count**, with the record saying it is a row count. That keeps the figure assertable, keeps the pin honest about what it holds, and leaves the conversion owed to the first checkpoint that has trades to measure it over.

**An approved proposal creates a new version from zero, and a running version is never edited**
Editing contaminates a record retroactively and there is no way to detect it afterwards.

**Replay screens proposals and the forward paired test admits them**
Replay is free, and free tests are how you overfit. It kills bad proposals cheaply and never accepts one.

**Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight**
One spent per evidence-pack decision, never reused. Makes the cost of a free test explicit and finite, and exhaustion is a designed dead end rather than a bug.

The windows cannot all exist when the register is created, and an earlier version of this decision wrongly implied they could. Evidence accumulates forward only, so the first quarter closes three months after go-live and the eighth two years after. The register is created empty and a window becomes available the day its quarter completes. In practice the research loop reaches its first pack-version decision with two or three windows in hand, which is enough, because a decision spends one.

## The research loop

**The AI writes only to the proposal store**
Nothing it produces feeds any component that scores AI output: no signal value, no forward return, no threshold, no ranking. An AI that can both propose and deploy will eventually find a rule that fits data it has already seen.

**Proposals come in two kinds, rule changes over existing signals and requests for a new signal**
The signal library is a hard ceiling on the proposal space and the model cannot lift it. Without the second kind the loop plateaus within months, because there are only so many ways to rearrange ten checks.

**The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface**
One method: a pack in, proposal text out. Two implementations behind it, chosen by configuration. The API implementation calls the Messages API with the key read from configuration. The subscription implementation uses the Agent SDK, which is the only path that can draw on a Claude plan, configured with an empty tool set.

The interface is the narrow path rather than the union of the two. An agent framework exists to let a model act, and this component's defining property is that it cannot: it writes a proposal and nothing else. On the API path that property is structural, because the Messages API with no tools has no mechanism to act through. On the subscription path it depends on configuration instead, so a test asserts the tool set is empty and the implementation never gains a file, shell or network tool. A capability that is absent by configuration is one edit away from being present, which is why it is asserted rather than assumed.

`ANTHROPIC_API_KEY` is never read from the environment on either path. On the subscription path its presence silently defeats plan auth and bills API rates; on the API path the key comes from configuration like every other secret. One assertion covers both cases: absent from the environment, always.

**Every proposal records three things: the model identifier configured, the transport used, and the model string the response reports as having served it.** The third is the strongest of the three, because it says what actually ran rather than what was asked for.

A transport switch part way through a record is neutral only if the served model string is unchanged. If it differs, the switch forks the record exactly as a model change does, and for the same reason: the success criterion compares proposals made against one pack version with proposals made against another, so anything that changes between them is a confounder. The subscription path carries the weaker pinning guarantee, because a plan-served model can move without anyone asking it to, which is what makes recording the served string rather than the configured one load-bearing.

**The model is a frozen parameter of the pack version, and changing it forks the record**
The success criterion is that proposals against a later pack beat proposals against an earlier one. If the model changes between them, that comparison measures the model and the pack together and can separate neither. The model is therefore pinned by exact identifier alongside the pack version, and every proposal records which model produced it.

A model change is not an upgrade, it is a new generation. The prior record keeps its meaning as history and the hit-rate comparison restarts.

Two consequences worth stating. A vendor retiring a pinned model is a **forced** fork, so the research record is bounded by model lifetime and that is a property of the design rather than an accident. And cost cannot be the reason to switch: the entire spread between the cheapest and dearest option is a few dollars a year, so a switch has to be justified by proposal quality measured against a spent holdout window, like any other change.

**The evidence pack is versioned, and the success criterion is proposal hit rate by pack version**
The model is a fixed function and does not improve between January and December. The evidence, the library and the arithmetic do. Attributing improvement to the pack is the honest form of the claim and the only form that is measurable.

**One meaningless signal is planted in the conditional tables**
Day of month, or something equally inert. It will show a spurious pattern in some deciles because everything does. A proposal citing it fails that pack version immediately. A tripwire that fires before damage, at the cost of one column.

**Abstention is a valid recorded proposal outcome**
A weekly job that must produce a suggestion will produce one in weeks when there is nothing there. A pack version that never abstains has learned to always find something, which is a warning rather than a triumph.

**Signals are admitted on whether they tighten outcome-similar neighbourhoods, independently of any rule using them**
A signal can be judged on rows already stored, with no version and no forward period. Rejected above 0.70 correlation with something already present, unless stored as a residual.

**The correction threshold scales with signals screened, not signals shown**
Adding a signal makes every other signal marginally harder to claim. Statistically correct, and it creates natural resistance to adding things casually.

## Data and platform

**Every line of code runs unmodified on Windows and on Apple Silicon macOS**
Development happens on Windows today and moves to macOS. Both are case-insensitive filesystems and both name timezones differently from Linux, so the two machines agree with each other and are capable of being wrong in the same way at the same time. The CI matrix catches it only after the code is written, so the constraint is stated in the hard rules where a session reads before writing. Anything platform-specific lives behind an abstraction or outside the application entirely: scheduling is the operating system's job, not the application's.

**Averages are computed locally, never through the vendor's technical endpoint**
About 45,000 calls a day for arithmetic that is one recursive loop over data already stored.

**The averages are one implementation, computed nightly and drawn on demand**

The arithmetic lives in Core and is called by two components in three shapes. IndicatorEngine computes the value at the as-of date and is the sole writer of `indicator_daily`, and it computes the same average at every session in a window for the checks that read a span rather than a point, which `held-floor` and `no-reclaim` do. The read surface computes that same series for the line a chart draws, and writes nothing.

**The third shape was added at 3.11 and the reason is the one this decision already gives.** `held-floor` compared every session of a dip against the average as at the setup date, which is one point of a series, so the gate and the drawn line disagreed about a stock while both were called the 21-day average. That is this decision's own failure mode reached from inside the lab rather than from the read surface.

The alternative is a second implementation in the read surface, and it is a bad one for a reason that is specific to averages: two exponential averages that disagree only in their seed converge to the same place and differ for a long time on the way, and both of them look like a moving average. A chart drawn from the second one would be a picture of numbers the lab never acted on, and there is no way to see that by looking at it. The one line the two share is asserted rather than assumed: the last value of the drawn series equals the value the engine stores for the same window.

Drawing a past session's average is not the reconstruction the evidence rule forbids. Nothing is stored, and a chart is a picture of one stock rather than a claim about what the lab would have flagged. Writing those rows into `indicator_daily` would be that reconstruction, and it is refused for the same reason 2.11 sends a historical detector run to `calibration_setup`: the lab has no record of who was listed on a night it was not running. (see: The evidence store holds only setups flagged forward, never setups reconstructed from history)

**An unprocessed corporate action of any kind blocks calculation, not only a split**
The rule was written naming splits because a split is the loud case. The reason it gives, that an unprocessed action corrupts every moving average a stock has at once, covers any action that moves the adjusted close, and a dividend does. A rule narrower than its own stated reason is a rule that will be applied inconsistently by whoever reads it next.

Magnitude does not enter it. A dividend distorts an average less than a split does, and "less wrong" is not a category this design has.

Supersedes **A split records a rebuild demand that is stamped rather than cleared**, together with the decision below.

**A rebuild is satisfied by a recorded refetch, not by inferring one from what changed**
The obvious implementation is to infer it. The refetch writes bars, bars carry an `observed_at`, so a window observed after the action must be a window that has been rebuilt. It does not work, and it fails quietly in both directions.

Taking the earliest observation in the window is too strict. A refetch rewrites only the bars whose figures moved, and the recent ones did not move: they were already ingested on the post-action basis. So the window keeps an old earliest observation and the ticker stays blocked for ever, which is what the live store did on the first real split.

Taking the latest observation is too lenient. The nightly ingest writes one new bar for every name every night, so every demand would satisfy itself by the following evening with nothing having been refetched at all. That failure is worse, because it produces numbers.

What the engine needs to know is not what changed. It is whether anybody looked, which is an event with a time, so it is stored as one. `history_refetch` carries a row per ticker per refetch, written even when the series came back identical.

**A rebuild demand is keyed on the action as observed, and a restated action raises a new one**
Vendors restate corporate actions. Keyed on ticker and effective date, a restated ratio has nowhere to go: the action cannot be stored twice, and the demand it should raise collides with one that may already have been satisfied. The stock stays rebuilt, against a factor the vendor no longer publishes, with the record showing a demand met and the wrong number computed from it. Nothing downstream can see any of that.

Bars already solve this and the discipline is copied without variation: append-only, keyed with `observed_at`, reads take the latest observation at or before the as-of date. The demand is keyed on the action as it was observed, so a restatement raises a new demand rather than failing to reopen an old one. Nothing is mutated and nothing is cleared.

The demand's key is the action's key rather than a copy of the action's value. The two say the same thing about which observation is owed a rebuild, and one of them cannot drift from the row it describes.

Supersedes **A split records a rebuild demand that is stamped rather than cleared**, together with the decision above.

**Minute bars are fetched for every flagged setup, not only the planned ones**
Otherwise a version selecting a name the baseline passed on cannot be resolved, and the missing cases are exactly the disagreements.

**Minute bars are fetched for the session a plan was live in, never the session it was written on**
The stage runs at 20:30 because minute bars publish two to three hours after the close, while detection runs at 18:20 and the plan is written at 18:30, both for the *next* session. So on the evening of session N the bars stored are session N's, and the setups they resolve are the ones flagged on the evening of N-1. Pairing a session with the setups flagged on it would resolve a plan against the prices it was computed from, which is the point-in-time rule inside a single day rather than across days.

It is a decision rather than a mechanism because both readings are coherent and the wrong one is invisible. A fetch aimed at the flagging session returns real bars, of a real day, for a real name; it stores cleanly, costs the same, and produces a resolver that answers every plan. Nothing downstream could tell, and the resulting fill would be an entry taken at a price the entry level was derived from.

Asserted fail-closed rather than by convention. The pairing is a type that refuses to be constructed from a session at or before its own, on the grounds that no fill and cannot-pair are different answers and only the second should stop a night. The first night has no prior evening, so it fetches nothing and records that it fetched nothing, which is a third answer again and is why the fetch writes a row whatever happens.

**The vendor screener endpoint is not used**
It cannot express three averages in a specific order, cannot express the pullback shape at all, and using it would leave no stored history to compute forward returns from. The local store is the measurement system, so it is not optional.

**Spread is captured intraday from day one**
It determines whether a tight stop is meaningful, and it is the one input that cannot be recovered later. Everything else can be re-queried.

**A delayed quote records its own lag rather than being corrected for it**
The vendor's quote feed is delayed, so a sample asked for at 10:15 describes the book at about 10:00, and both sides of it carry the vendor's own stamp. The stage stores those stamps and the difference between the older of them and the instant it asked, rather than shifting the sample time to compensate.

It is a decision rather than a mechanism because the other reading is coherent and its failure is invisible. Subtracting a constant produces rows that look exactly like live ones, so a reader has no way to tell a normal sample from one taken while the feed was running late, and the design's assumption about the delay lives in a stage rather than in the data. Recording it makes the lag a fact per row that 4.7 can bound on or exclude, and it means a change to the vendor's delay shows up as a column moving rather than as every stored spread quietly describing a different minute.

The lag is measured from the older of the two sides, because a spread is only as fresh as its stalest half: an ask stamped a second ago against a bid stamped four minutes ago is a four-minute-old spread whatever the ask says.

**Two spread samples a session, not one and not three**
One quote cannot be checked. A stale quote, a locked or crossed book and a one-off blowout all look like an ordinary row, and nothing on that row says which it was; two independent observations of one name on one day give the fill model something to disagree with, and the disagreement is itself the finding, because a name whose spread doubles across a session is a name no single figure describes. A third would cost sixty more unrecoverable calls every session for the life of the lab and would buy the shape of the intraday curve rather than a check on its level, which is not what a fill model charges. If the two turn out to disagree often enough that no level is usable, that is an argument for a third made from the record rather than ahead of it.

**The spread capture stays at the capped sixty, and a version selecting outside it is scored as refused**
Answered by the operator on 2026-09-02, at the phase 5 sitting, closing the row raised at 4.7. The two spread passes go on sampling the capped sixty, and the capture is not widened to the flagged population, which is 88 to 166 calls a pass against the 120 it spends today.

**Affordability is not what decides it.** The calls exist inside a nightly total of 2,723 to 4,118 against a ceiling of 5,000. What decides it is that the widening buys spreads for names no version can trade today, on the chance that a version admitted later selects them, which is a purchase against a hypothetical rather than against a shortfall anybody has measured.

**The alternative is already built and is the more honest of the two.** A version selecting outside the cap is refused a fill and trades nothing, which is a recorded fact about that version rather than a silent absence (see: RiskGate is the sole writer of orders, for both directions and every version).

**What the ruling does owe costs no vendor call.** The refusal is legible where the scores are read, so a version scoring poorly because it selected outside the capped sixty is distinguishable from one scoring poorly on its merits. That distinction is what the widening was really for, and it is a column rather than a call. Owed at 5.2, where a version's score is computed, and rendered on the ledger at 5.5.

**The runtime is .NET with C#, one codebase for both halves**
The components most likely to produce a silently wrong answer are the trading and journal ones, and those benefit most from a compiler. Analysis can be pointed at the same store later without changing the application.

**Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables**
A plaintext gitignored file rather than user-secrets, because user-secrets are encrypted per user per machine and would have to be re-entered on every move, and because one visible file is easier to reason about than a store the tooling hides.

Two properties have to hold or the choice quietly breaks things. The file is registered **before** environment variables in every project, so an environment variable still wins, which is what CI and any future container depend on. And it is optional, so a machine without one falls back to environment variables rather than failing to start. Adding a JSON source after the host builder is constructed puts it last and inverts both properties, and doing that in one project but not another makes two projects resolve the same key differently with nothing on the surface to show it.

**The lab keeps one store per purpose under one data root, and CI never opens the operator's**
Two stores exist and only one of them holds evidence. `data/live` is the lab: the nightly job writes it, it is the thing a move procedure copies, and it is the only store any figure in the record comes from. `data/ci` is a scratch store `tools/ci.*` creates, drops and recreates on every run.

**The split is not tidiness, it is the drop.** The first step of both CI scripts is `drop-store`, deliberately, so that a migration which only works against an already-populated file fails on a runner rather than on the second machine. Pointed at the operator's root, that step deletes the evidence store, and it would do it quietly and on every run. Nothing in the code can tell the two apart, because a store is a file path, so the property is held by the entry points setting the root and by this entry saying why.

**A third store is the failure this exists to prevent, and it has already happened once.** The configured default is a data root that is neither of them, so a stage run by hand without the environment variable set does not open the live store and does not fail either: it creates a new one, migrates it, and reports success against an empty file. `data/pullbackstrategylab.db` sat in the repository from 2026-08-27 at `user_version` 15 with sixteen tables and no rows, produced during the session that was diagnosing why the live store was eight migrations behind. The phantom is the reason that diagnosis took a session: two stores answered the question differently and the operator was reading the wrong one. It was deleted at 3.11 and the default is what stops the next one.

**What may create a store, stated so a fourth does not arrive by accident.** `tools/nightly.ps1` against `data/live`, `tools/ci.*` against `data/ci`, and a hand-run of `migrate` against whichever root is configured. Nothing else. `tools/verify-phase` names a `data/verify` root it has never created, which is dead configuration rather than a third store, and it is named here so that a step which later opens one is a change somebody made rather than one nobody noticed.

**The store contains no absolute paths**
What keeps it a directory that can be copied to another machine. Easy to preserve from the start, tedious to retrofit once chart exports or log paths have been persisted.

**A reader's signature does not establish point-in-time; the query does**
Every public read on every reader in `PullbackStrategyLab.Data` takes an as-of date and there is no overload that omits it. That has been true since 1.4 and the corpus treated it as the property. It is necessary and it is not sufficient: a hand-written statement beside a reader is not bound by the reader's shape, and when `point-in-time` was first run at 2.10 four of them were in the shipped source. Two joined `security` for `industry` and `market_cap` with no bound on `sector_resolved_at`; two enumerated calibration sessions from `daily_bar` with no bound on `observed_at`.

The worst of the four is worth keeping in the entry rather than in a progress note, because it says why the distinction matters rather than that it exists. `SignalVectorizer` freezes what was knowable on the night into `setup_signal`, and that is the one row in the lab nothing ever recomputes. Everything else can be rebuilt from bars; a frozen signal is the record. It was freezing two attributes resolved afterwards, which is a permanent wrong value that no later run corrects and no later read can distinguish from a right one.

So the property is asserted over queries and not over signatures. `point-in-time` reads every statement in the shipped source, matches it against the tables that carry an observation stamp, and requires the stamp to be bounded or the file to be exempt by name with its reason. The signature half stays, because it is what stops the next reader being written without a date; it is the first of three halves rather than the property itself.

Note what a fixture would not have caught. The four figures did not move when the reads were bounded, because in the replay the sector lookup runs before the vectorizer on the same session, so every expectation held either way. A test over the golden fixture would have passed on all four.

**The vendor is EODHD, and the endpoint mix is what the call budget is built on**
Three decisions already say "the vendor" without naming it, and the backfill order in `RUNBOOK.md` depends on this vendor's specific split between two differently priced endpoints. A later session choosing endpoints freely would blow the 5,000 call ceiling invisibly, so the vendor and the endpoints the budget assumes are named here rather than left to be inferred.

The mix the budget is built on: the exchange **symbol list** for the universe, **bulk end-of-day** priced per market day for the nightly pull and for the twenty-session screen, **per-ticker end-of-day** priced per ticker regardless of depth for backfilling survivors, **bulk splits and dividends** for corporate actions, **per-ticker intraday** for minute bars, and **fundamentals** for the lazy sector lookup. Going deep is free on the per-ticker endpoint and ruinous on the bulk one, which is the whole reason the backfill screens before it fetches.

The technical endpoint and the screener endpoint are not in the mix, and both have their own entries. The key is not here: it lives only in `appsettings.Secrets.json`.

**Pages are server-rendered with no build step, and any script is local rather than fetched**
Razor Pages, charts as server-rendered SVG. The property worth protecting is that the lab opens and works with no network, no build step, and nothing fetched at page load. A CDN `<script>` tag breaks it: it works on the machine that added it and fails silently on the other one, or offline, or once the CDN moves.

Written as a property rather than as a ban on JavaScript, because a ban would be wrong within one phase. Checkpoint 2.9 requires paging through a night's setups by keyboard with agreement captured per setup, across hundreds of charts. A blanket ban makes each setup a form post and a full page reload, which is exactly the friction that makes a reviewer stop at thirty and the checkpoint fail at what it exists to do. Script that is local, small, unbundled and confined to keyboard navigation and form submission is permitted; script fetched from anywhere is not.

**The agreement a person records is written through the read surface, and it is the only write it makes**
Two columns of one table, `setup.agreement` and `setup.agreement_note`, written by the read surface on the gallery's behalf. Everything else it does is a read, and nothing else it ever does may be a write.

The three rules around it left nowhere else to put it, and that is worth spelling out because the obvious readings all fail. The Web project reads through the Api and never opens the store, so the page cannot write. The Worker is the sole writer of everything the nightly job produces, and it has no channel a browser can reach. And one writer per table per operation means the write cannot be split between them.

What makes this the right exception rather than the first crack in the rule is that it is not the same kind of write. Every other write in the lab is the evening's job producing evidence on a schedule; this is a person saying what they thought of one row, at a keyboard, on their own time. It touches two columns that no computation reads, it can never conflict with the nightly job over a row that job is writing, and under WAL a single short update from a second connection is what the busy timeout exists for.

**Stated as the property rather than as a permission, because the permission is the part that would be cited for something else.** "The read surface writes these two columns and nothing else, ever" is right about the columns and reads as a general licence for the Api to write where writing is convenient. The property is narrower and it is what a later session needs: a person's judgement is captured on the page that asks for it, and the Worker never writes these two columns because the Worker has no judgement to record. That is not a division of labour that could have gone the other way. There is no run in which the nightly job could produce an `agreement` value at all, which is why these two columns are the only ones in the store the Worker cannot own.

Nothing rests on the sentence holding. The Api writes no other column because `writer-ownership` reads every write in the shipped source and attributes it to a declared writer, and SCHEMA declares this one by the type that issues the statement rather than by the screen that asks for it. A second write appearing in the read surface fails the check by name, which is what makes this an exception with a boundary rather than the first crack in the rule.

**The Web project reads through the Api and never opens the store**
One read path, so a page cannot acquire a second connection to a file the Worker is writing. The store's own rule is one writer and one connection, and a page opening the file directly is the easiest way to lose that quietly. It also keeps the isolation check meaningful: Web talks to Api over HTTP with the base address in configuration, and no page holds a store connection to inspect.

**A run whose writes are updates records no row count rather than a nought**
`run_log.rows_written` is null on a stage whose declared tables it only updates, and the measured delta is written on every other stage exactly as it always was.

The figure is a row-count delta over the tables a stage declares, taken at the start of the run and again at the end, and it is measured rather than self-reported because a stage counting its own output reports what it believes it wrote. **That reasoning is unchanged and it is why the fix is not a reported count.** What the delta cannot do is see a write that changes a row rather than adding one: `sectors` and `clusters` issue `UPDATE` and never `INSERT`, so a perfect run and a run that died on the first name both report 0, and on 2026-08-27 `sectors` recorded 149 calls against 0 rows, which is what a clean run would also have recorded.

**Null and nought are different statements, which is the whole of it.** Nought says the stage wrote nothing and is a figure the nightly halt keys on. Null says the delta does not apply to this stage, so a person reading the row is told the measure is absent rather than shown a measurement that happens to be wrong. The alternative was a stage reporting rows affected, which breaks the rule the column exists for.

**Applicability is declared at `Begin` and not decided at the end**, so it is part of what a stage says it writes rather than something a stage could forget to mention. It is self-reported, on the terms `run_log.skipped` already is: what is being reported is whether a measure applies, not the value of one, and there is no belief about its own output to guard against.

**A stage that both inserts and updates keeps the measured delta.** PositionManager inserts a fill for every exit and every trim, so its delta is a real count of the rows it added, and the updates it also makes are not something the column ever claimed to cover.

## Corpus and process

**Decisions are named, not numbered**
A comment citing a number tells you nothing and forces a lookup. A comment citing a name tells you the thing directly. A misremembered name fails to resolve; a misremembered number resolves to the wrong decision and nobody notices.

**Components are named, not coded**
Same reason. The codes were only ever there because a table wanted a key column.

**Every phase ends in a generated phase report, not in a page somebody looks at**
"Openable" asks a human to squint at a screen and form an opinion, which does not scale, cannot be automated, and gives a build session nothing to check its own work against. One command produces one report covering three questions, and a phase does not sign off until that report is green.

**Does the code match the architecture.** The component catalogue, the limits table, the check lists and the call budget are all structured tables. The report parses them and asserts each claim against the code: every component named exists and is registered, every limit matches its config value, every named check exists in the detector, every declared writer is the only writer. A claim the report cannot examine is listed as unexamined rather than passed.

**Does it produce the right numbers.** A committed golden fixture holds real bars for a small set of tickers over a fixed window, with expected outputs frozen beside them. The pipeline runs against it and the report diffs actual against expected. Live data changes nightly and cannot be diffed against anything; a frozen fixture gives the same answer today and in six months, so a difference means the code changed.

**How much was actually checked.** Every check reports its coverage, so the report shows what was examined rather than only what passed.

The report is written as HTML for a person and as JSON beside it for a machine, from the same run. The build session reads the JSON and can tell whether its own checkpoint landed without asking anyone.

**Headings carry no numbers, and anchors are slugs**
Section numbers have the property that got decisions renamed: a misremembered number resolves to the wrong place silently, and every insertion renumbers everything after it. Sections in this corpus were renumbered four times before the first commit, each time leaving stale cross-references behind.

It matters more than style because the phase report parses this document's tables. A parser anchored on numbers breaks on every insertion; anchored on heading text it does not. HTML anchors are slugs of the heading for the same reason, since an id of `s16` is as positional as the number was.

Checkpoint identifiers keep their numbers. `1.7` and `4.5` name work in a sequence where the sequence is the point, and nothing parses the build plan.

**Nothing in the corpus is struck through**
A spec is edited cleanly and its prior text goes to the changelog. A record is corrected by a new dated entry naming what it corrects. Strikethrough leaves a document that is half history and half current state, and the reader has to decide which on every line.

**Data ownership is declared once, in SCHEMA.md**
Restating writers in the architecture document would be the same fact in two places, which is how a corpus starts to drift and how sync passes start eating whole sessions.


**The corpus is eight documents and a ninth requires retiring one**
Five specs and three records. The artefact is gone: `SCREENS.html` was deleted at 4.12, which is the checkpoint it was scheduled to be retired at from the day the plan was written. Supersedes **The corpus is eight documents plus one artefact, and a ninth requires retiring one**, and the ceiling it sets is unchanged.

A corpus of the same shape grew past twenty on a previous project and the documentation tax stopped scaling with the size of the work.

**What retiring it cost is stated rather than assumed to be nothing.** The mockup was the only place four of the five screens were drawn, and the built pages have absorbed the layout of every one that exists. The fifth is the research ledger, which is 5.5's and is not built, so its drawing is gone and 5.5 designs from ARCHITECTURE's description instead. That is a real loss and it is carried as an obligation rather than left to be discovered at 5.5.

**What it does not cost is any statement about the method.** The one sentence in the mockup that was about how the lab works rather than about how a page looks, being that a twin pair is put to the model as "these look identical in everything I record and did opposite things, what would you want to see", is in ARCHITECTURE under "The question" and was before this. Checked rather than assumed, because a mockup deleted with a claim in it is the same defect as a decision deleted with reasoning in it.

**The rule that made it deletable is that a mockup and a built page are two answers to one question.** SCREENS drew a journal with a plan-against-actual column and the built one has that column too; the day the two disagreed, nothing would have said which was the specification. An artefact is retired when the thing it stands in for exists, and the alternative is a document that is either duplicated or wrong.

**Phase 2 thresholds are calibrated once against nightly counts, before phase 3**
At that moment no forward return exists anywhere in the store, so there is nothing to fit toward. It is a row count and nothing else. Recorded as a dated event with before and after counts. After phase 3 begins those thresholds move only through the normal proposal route.

The once is the part worth defending, and it is why the population matters. The band is stated per side per night for the live universe, so the run it is read from is the one over the live universe; the record names which distribution the adjustment was made against and states the rate per name per session beside the raw count, so a later session reading the figure knows what it was a count of. What the once is not is a second calibration held in reserve: a threshold adjusted twice against two populations is a threshold fitted to whichever gave the answer somebody preferred.

**The fresh-session rule applies to sessions that have committed code, not documents**
The protection being bought is that a session does not review its own code. The wider wording costs a session per phase and buys nothing.

**A phase branch merges on CI green, and the sign-off reviews what is already on the default branch**
Merge is gated on CI green and on nothing else. Sign-off is a separate activity with its own record, owed on the phase as a whole before the next phase's plan rather than before any merge.

**This is the second time this rule has moved, and saying so is the point of writing it down.** It began as CI green alone, was changed to add a sign-off gate, and is changed back here. A rule that oscillates without a record reads each time as though it had always been that way, so the trade is stated once and a third change has to argue against both halves rather than against whichever half it happens to find.

**What the sign-off gate bought.** A default branch on which every phase is complete and reviewed, and a sign-off that can decline something at the cost of a conversation rather than a revert.

**What it cost, which is the half that decided it.** A phase waiting on something that is not code keeps a branch open for as long as it waits, and the nightly job runs from that checkout for the whole of it. Phase 3 waits three months for accumulation. Production running from a branch is not a lesser defect than an unreviewed default branch; it is the more immediate one, because it is a live system rather than a property of a history, and it degrades the longer the wait it is caused by.

**A correct pass held back is not held safely.** The work still exists, it still runs the nightly job, and the only thing the delay changes is which ref it runs from and how large the eventual merge is. A review of a hundred commits made three months after the fact is worse than a review of the same commits on the default branch a week after they landed.

**What does not move.** A checkpoint still lands as its own commit and still satisfies all seven done conditions on its own, CI is still green before any merge, and a session that has committed code still may not sign it off.

---

## Previously decided

**The corpus is eight documents plus one artefact, and a ninth requires retiring one**
Five specs, three records, one artefact. A corpus of the same shape grew past twenty on a previous project and the documentation tax stopped scaling with the size of the work.

Superseded on 2026-09-02 by **The corpus is eight documents and a ninth requires retiring one**. The ceiling is unchanged and the artefact is gone: `SCREENS.html` was deleted at 4.12, the checkpoint it was scheduled to be retired at from the day the plan was written.


**A gap through a price fills at the session's first regular minute open, and is not slipped again**
The open of the first regular-hours minute bar of the session, read from `intraday_bar` rather than from any session-open field.

The loss is explicitly never clamped, so the gap is taken as it happened. Taking the worse of the open and the stop would price an adverse move that did not occur, which is pessimism past the point where it is still measuring something. Tying the price to the store's own first regular minute keeps it derivable from what the lab holds, on the same footing as every other price in the system, rather than resting on a field the vendor computes.

**A gap fill does not additionally slip**, because the gap is the adverse move. Charging a spread on top would be charging twice for one crossing.

This applies to an exit as much as to an entry, and it is the one exception to the rule above.

Superseded on 2026-09-02 by **A minute that opens through a resting price fills at that open, whatever time of day it is**. Every word of the reasoning survives; what does not is the clause naming the session's first regular minute, which was the only case 4.7 could see and was never what the argument rested on.


**The minimum sample is derived from a measured dispersion and counted in effective observations**
A sample size has three inputs: the difference worth detecting, the confidence demanded, and the dispersion of the statistic. The first two are judgements and belong to a person. The third is a fact about the market, and until this decision nothing in the corpus had measured it.

**So the figure that stood was an estimate wearing a derivation's clothes.** ARCHITECTURE stated 160 paired setup observations "detecting about a two-point difference in ten-day forward return", and every reading of it since has treated the 160 as falling out of the two points. It did not. Nothing had taken the dispersion over anything, nothing said what power the sample was sized for, and nothing said whether the observations were rows or independent ones.

**The dispersion is measured, over a named population.** Within one session every name carries the same market move, so the cross-sectional sample variance of that session's forward returns estimates the idiosyncratic variance directly: the common term cancels and the `n-1` denominator makes the estimate unbiased. That is the same cancellation the paired difference buys on the scoreboard, which is why it measures the right quantity rather than a near neighbour of it. Pooled by degrees of freedom across sessions, over the captured fixture's thirty names and 241 sessions, the single-name figure is **0.091115**. A setup's difference against the mean of five controls disperses by `sqrt(1 + 1/5)` times that, because the control mean carries noise of its own, giving **0.099811**.

**And the population is stated because it is a floor rather than an estimate.** Thirty names, hand-picked for liquidity, still listed at the end of the year. A universe with delistings in it disperses further, so the real figure is larger and the minimum it produces is larger. Measured again over the calibration store's 1,671 names clearing the liquidity floor across 742 sessions, the single-name figure is 0.088371, below the fixture's; that store carries survivorship bias by construction, so the two are recorded as agreeing rather than as one confirming the other, and the larger of the two is the one used.

**The arithmetic, with every input named.** `n = ((z_alpha + z_beta) * sigma_d / delta)^2`, the one-sample form, because pairing has already turned two populations into one series tested against zero and the two-sample factor of two would double the answer for nothing.

| Input | Value | Kind |
|---|---|---|
| `delta`, the difference worth detecting | two points of ten-day forward return | judgement, and ARCHITECTURE's own |
| `z_alpha`, two-sided 95% | 1.959964 | fixed by the interval, which reads green on a 2.5th percentile bound |
| `z_beta`, 80% power | 0.841621 | judgement, and the input nothing ever stated |
| `sigma_d`, the paired dispersion | 0.099811 | measured |

Which gives **196 effective observations**, rounded up because a fractional observation cannot be had and up is the direction that asks for more evidence. Not rounded to a round number: 200 would be an authored step in a figure whose whole point is that no step in it is authored.

**What the old figure turns out to have been.** 160 is this same arithmetic at about 72% power. It was not a different calculation, it was this one with a power nobody chose, and the gap between 160 and 196 is entirely that choice.

**The two judgements are the operator's and the sensitivity is stated so the choice is a real one.** At the measured dispersion, detecting three points needs 87 and detecting one and a half needs 348, because the sample goes as the inverse square of the difference. At two points, 70% power needs 154 and 90% needs 262. Moving either is a superseding decision, not an adjustment.

Superseded on 2026-08-27 by **The minimum sample is 262 effective observations, ratified at two points and 90% power**. Everything it measured survives unchanged: the dispersion, the population it was taken over, and the arithmetic. What it could not do was settle its own two judgements, which it said outright and left to a ratification. 196 was that arithmetic at an unratified 80% power, and it was never a figure anybody had chosen.

**A setup row is corrected only where the correction uses no information the night did not have**
Superseded on 2026-08-28 by **A late answer is attributed to the session it was fetched for, up to a recorded lateness bound**, which keeps every condition below and adds one: an answer the session itself asked for may arrive inside a stated bound and be recorded as late, rather than being refused for having arrived at all. The reasoning is kept in full because it is still the reasoning, and only the treatment of lateness moved.

Setup rows are immutable after write, and that rule was too broad. It exists to stop a plan being improved once its outcome is visible: a trigger nudged, a stop widened, a check re-run until it passes, each of which turns the record into a description of what would have worked. A value missing because an input stage failed is none of those. Nothing about the outcome is known, nothing about the plan changes, and the repair uses only what existed on the night.

Left as written, the rule forbids that repair, and it did: on 2026-08-27 the sector walk died on its 149th name, `clusters` ran three minutes later over a store it had half filled, and fifteen of that night's forty-four setups recorded a cluster verdict of failed with no value. Under immutability as stated those fifteen carry a wrong verdict for ever, and the reason is not that the night was uncertain but that a stage fell over.

**Two conditions, and both are asserted rather than intended.**

**Inputs are bounded to the setup's own date.** A sector resolved today may not be the sector that name carried then, and reading it back into that night is using information the night did not have, however slowly the fact moves. This is the same bound every reader in the lab already applies, and it is the condition that makes the correction a repair rather than a rewrite. A recompute whose inputs are stamped after the setup's date fails and says which value was too late, rather than quietly producing a better-looking number (see: A reader's signature does not establish point-in-time; the query does).

**The row records that it was corrected, with the date and the reason.** A corrected row is not the same evidence as one that was right the first time, and a later reader has to be able to exclude them without knowing this happened. An unmarked correction is indistinguishable from the plan-improvement the rule was written against, which is why the mark is a condition of the permission rather than a courtesy.

**What stays forbidden is unchanged, and it is most of the rule.** A trigger, a stop, a size, or any gating check verdict computed from prices is never rewritten. Those are the plan. What may be corrected is a recorded-not-required verdict whose input a failed stage never delivered, which today is `cluster` and nothing else.

**The narrowness is deliberate and it is what makes the permission safe.** A rule reading "a wrong value may be fixed" would be cited for exactly the thing immutability protects against, because every improvement looks like a correction from the inside. The two conditions are what a later session has to satisfy, and the second one leaves a trail even when the first is satisfied wrongly.

**And it does not reach backwards to the fifteen it was written for.** Their sectors were resolved on 2026-08-28, after the night they are wanted for, so the date bound refuses them and the fifteen keep their null verdict permanently. That was tested rather than assumed and it is the right outcome: a decision whose first act is to exempt its own motivating case is a decision with no conditions on it (see: The evidence store holds only setups flagged forward, never setups reconstructed from history).


A superseded decision moves here under its original name, gains one line naming what replaced it, and keeps its reasoning. A superseded decision that loses its reasoning is worse than one never written down, because the next session will re-derive the same wrong answer.

**The interval is a block bootstrap over paired differences, and the effective sample is measured**
Superseded by **The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured**, which keeps the block length, the draw count and the effective-sample arithmetic, and changes two things: each draw now takes its block starts independently, and the bounds are studentised rather than percentile. The method named here was the right one; what shipped under it was one fixed lattice rotated, and a percentile bound over correctly drawn blocks still under-covers at the sample sizes band 1 will have.
This is not the textbook case, and the textbook interval is wrong here in the direction that matters.

**Ten-day labels overlap.** A ten-session horizon means adjacent nights share most of their window, so consecutive observations are serially correlated by construction. **Same-night setups share a market factor.** Forty names flagged on one night rise and fall together with the market over that fortnight.

Either alone makes an interval assuming independent observations too narrow. Together, band 1 clears zero before it should, and band 1 is the project's central question. A too-narrow interval does not produce a wrong number, it produces a confident one, which is the failure this whole system exists to avoid.

So: the statistic is the **paired difference**, a setup's return minus the mean of its own matched controls. That removes the shared market factor inside a night by construction rather than by adjustment, which is why the control draw is a prerequisite of this decision rather than a neighbour of it. The remaining serial overlap is carried by a **moving-block bootstrap over the session axis with a block length of ten sessions**, being the scoring horizon, at ten thousand draws, percentile bounds, deterministic ordering so the figure is reproducible and diffable.

A Newey-West style adjustment with the lag set from the horizon was the alternative and would have cost less to compute. It was not taken because it corrects the variance of a mean and this scoreboard also shows decile curves and win rates, and one resampling scheme that serves every panel is worth more than a closed form that serves one.

**The effective sample is measured from the realised series, never assumed.** The number of rows and the number of independent observations are different quantities here, and the ratio is a property of the realised autocorrelation rather than of the design. It is computed from the series and reported beside every interval.

**Any minimum-sample figure written against this is in effective observations, not rows.** Stated because it is the half that gets dropped: a pre-registered target reading "160 observations" is satisfiable by 160 rows carrying far less than 160 observations' worth of information, and nothing on the surface says so. The figure itself is settled by the decision below.

**A split records a rebuild demand that is stamped rather than cleared**
A split rescales every adjusted close before it. The stored ones were adjusted as of the night each was observed, so the evening after a four-for-one everything already in the store is on the old scale and everything arriving is on the new one, and an average taken across that boundary is arithmetic on two different units. It is wrong by a factor and it looks entirely reasonable, which is why the architecture's answer is that calculations refuse to run for that stock rather than that they carry on.

Refusing requires somewhere to read the refusal from, and the alternative to a stored demand is deriving it: comparing the split's observation time against the time the indicators were last computed. That was rejected because it makes the store answer only the present tense. The question worth answering months later is which splits this store has honoured and when, and a derivation cannot answer it at all.

So the demand is a row, and the row is never deleted and never cleared. It gains a `rebuilt_at`. A queue that empties answers "is anything outstanding" and destroys the history on its way to the answer.

**ActionIngestor raises the demand and IndicatorEngine closes it**, which is one writer per operation rather than an arrangement of convenience. A component that can both raise and satisfy its own condition raises nothing, and the failure mode is silent: the demand is created and closed in the same pass, every check still passes, and no calculation is ever blocked.

Superseded on 2026-08-25 by **An unprocessed corporate action of any kind blocks calculation, not only a split** and **A rebuild demand is keyed on the action as observed, and a restated action raises a new one**. What it got right survives in both: the demand is a stored row rather than a derivation, it is stamped rather than cleared, and the component that raises it is not the one that closes it. What it got wrong was the trigger, which named splits alone, and the key, which named the ticker and the date rather than the action as observed.

**The tight control set draws from any session sharing the market mood, and the loose set stays within the night**
Ruled by the operator on 2026-08-30. The decision above declares that the tight set matches on the trend ladder **and the market mood**, and nothing had ever implemented the second half, because within one night it cannot be implemented: the mood is a property of the session, so every candidate on a given night carries the same one and matching on it excludes nothing. The draw left it out rather than performing a comparison that is true by construction, on the grounds that a dimension which always matches reads in the record as a dimension that was checked.

**So the choice was to make the dimension real or to drop it**, and the dimension is kept. The tight set may reach into other sessions carrying the same mood label, which is what makes the mood a filter rather than a formality.

**What it costs is stated rather than left to be discovered.** The tight comparison stops being a within-night comparison. A setup and its tight controls may now come from different sessions, so the market factor common to one night no longer cancels between them, and the difference series carries whatever moved between those sessions on top of the idiosyncratic term the comparison is for. That is the trade: a matched dimension bought with a comparison across time. It is taken because the alternative is a tight set that differs from the loose set by the trend ladder alone, which is a weaker question than the one the scoreboard says it asks.

**The loose set is unchanged and stays within the night.** It matches on liquidity and daily range, both properties of the name rather than of the session, so it has nothing to gain from reaching across sessions and would pay the same cost for it. Keeping one set within the night also keeps a within-night comparison on the scoreboard beside the across-session one, which is what makes the cost above readable rather than assumed.

**Ruled before the evidence accumulates, which is the only time it could be.** A tight set whose definition changes after a series has been accumulated spends that accumulation twice. At the ruling the live store held two scoreboard dates and no setup had closed its ten-session horizon, so no interval had ever been taken over the old definition and none is discarded.

Superseded on 2026-08-31 by **The tight control set draws within the night, because a within-night draw controls the market mood exactly**. Everything it observed survives unchanged: the mood is a property of the session, matching on it inside one night excludes nothing, and the tight set had only ever matched on the ladder. What it got wrong is what that means. It read a dimension that always matches as a dimension that was never checked, and drew the conclusion that making it real required making it vary; the replacement reads the same invariance as a perfect control and keeps it. Its own third paragraph states the price and is the reason the replacement exists: the market factor common to one night no longer cancels. That was written as a trade accepted rather than a quantity measured, and when it was measured on 2026-08-31 it cost the tight comparison about six sevenths of its effective sample and put 3.6's second condition out of reach.
