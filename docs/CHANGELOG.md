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
