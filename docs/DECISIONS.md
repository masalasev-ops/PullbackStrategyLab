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

**It is bounded by security type and by venue, and the venue bound is the one that decides the size.** The list returned 59,826 rows, 32,851 of them common stock and 15,983 of those on NASDAQ or NYSE, which is about 3.8 nights of the ceiling against about 7.8 for every venue. What the extra four nights buy is the delisted history of venues the current universe holds 30 names on out of 2,005. Both bounds are configuration rather than code, because a name on a thin venue could in principle have cleared the price and liquidity floors while it traded, so this is a bound on the purchase and not a claim that nothing was missed.

**It is charged against the daily ceiling although it is one-time work**, and that is what spreads it. The whole-universe backfill is charged outside the ceiling because it runs in one sitting. This one cannot: it takes what the evening's stages left, stops on the budget rather than overrunning it, and the next night resumes from `history_refetch`, which already carries a row per ticker per refetch and is the record the fetch itself writes. Nothing keeps a second list of what is done, because a copy can disagree with what it copies.

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

## How changes are judged

**Versions select from one shared nightly candidate list rather than each re-scanning**
Makes the comparison paired. Unpaired, a small improvement needs thousands of observations; paired it needs hundreds. That is the difference between a verdict in months and a verdict in years.

**Two experiment families, selection and execution, scored differently and never mixed in one version**
An execution change alters the size of the R unit rather than the choice of stock, so its results cannot be differenced the same way. A version changing both teaches you nothing about which change caused the result.

**Acceptance measures expectancy, never win rate**
Win rate is reported alongside as a diagnostic. Any version raising win rate while lowering expectancy is rejected automatically. Widening the stop does exactly that, and this rule is written before any results exist on purpose.

**Targets and minimum samples are written at creation and are immutable**
Twenty worthless candidates give a 64% chance that at least one looks impressive by luck. Pre-registration is the only defence, and a target that can move after the result is not a target.

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

**The vendor screener endpoint is not used**
It cannot express three averages in a specific order, cannot express the pullback shape at all, and using it would leave no stored history to compute forward returns from. The local store is the measurement system, so it is not optional.

**Spread is captured intraday from day one**
It determines whether a tight stop is meaningful, and it is the one input that cannot be recovered later. Everything else can be re-queried.

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

**The corpus is eight documents plus one artefact, and a ninth requires retiring one**
Five specs, three records, one artefact. A corpus of the same shape grew past twenty on a previous project and the documentation tax stopped scaling with the size of the work.

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
