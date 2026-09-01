# CLAUDE.md

Rules for any session working in this repository. Read this file first, every session, before touching anything.

---

## What this repo is

A paper-trading laboratory that tests two mirror-image price patterns on US equities every day, records every setup it flags whether or not it trades it, and runs a research loop that improves the selection rules from that record. No real money, ever.

**PullbackStrategyLab.** Solution, projects, namespaces and the root config section all use that name in full, with no abbreviation anywhere in code. A shortened form in one place and the full form in another is the kind of inconsistency that survives for years and then bites during a rename.

**.NET with C#, SQLite for the store.** One solution, one store file under the configured data root, no server to install on either machine.

The design source of truth is `docs/ARCHITECTURE.html`. It is the only place the system is described as a whole. If code and architecture disagree, that is a finding, not a licence to change either one silently.

## Where the build is right now

The solution exists under `/src` with the six projects "Repository layout" describes, and the rules in "Conventions", "Definition of done for a checkpoint" and "Merge" are live: a clean edit to a spec records its prior text in `CHANGELOG.md` with the decision authorising it, and a checkpoint ends in a `PROGRESS.md` entry.

**Which checkpoint the build is on is the furthest checkpoint `docs/PROGRESS.md` records,** and the one to build next is the checkpoint after it in `docs/BUILD_PLAN.md`. It read "the last entry" until 3.14, and that cannot be read literally alongside the rule below that a record is corrected by a new dated entry: correcting an older checkpoint appends an entry naming it, and the pointer then names a checkpoint the build passed phases ago. A ruling recorded against 2.11 on 2026-08-29, with 3.14 landed, retitled the phase report "Phase 2 report". The proxy gives way rather than the correction rule, because "last" was only ever standing in for "furthest" while every entry happened to be a new checkpoint. That is stated as a pointer rather than as a number here on purpose: a number in this file is a second place the same fact lives, and it goes stale the moment a checkpoint lands without anyone noticing.

Anything a checkpoint has not built yet does not exist, however completely `docs/ARCHITECTURE.html` describes it. The phase report at 1.7 is what says which of the two you are looking at.

## Read order for a fresh session

1. This file.
2. `docs/BUILD_PLAN.md`, the checkpoint you are on and its done condition.
3. `docs/SCHEMA.md`, if you will touch a store.
4. `docs/ARCHITECTURE.html`, the sections covering the components in your checkpoint.
5. `docs/DECISIONS.md`, the entries cited by the above.

Do not read the whole corpus. It is small on purpose and it is still larger than any single checkpoint needs.

## Repository layout

```
/src
  PullbackStrategyLab.Core        domain, clock, config
  PullbackStrategyLab.Data        stores, migrations
  PullbackStrategyLab.Worker      every scheduled stage, sole writer
  PullbackStrategyLab.Api         read surface
  PullbackStrategyLab.Web         pages
  PullbackStrategyLab.Tests       the suite
/docs             ARCHITECTURE.html  SCHEMA.md  BUILD_PLAN.md
                  DECISIONS.md  PROGRESS.md  CHANGELOG.md  RUNBOOK.md
                  SCREENS.html
/tools            ci.ps1  ci.sh  verify-phase  verify-phase.ps1  snapshot-db  migrate
                  nightly.ps1  the slot dispatcher the scheduler calls, not run by CI
                  slot-log-verdict.ps1  what a slot log says about a stage, read by the
                  two runner jobs so one predicate serves both
                  derive-indicators.py  one-time verification aid, not run by CI
/fixtures         captured  the golden fixture's inputs, verbatim vendor responses
                  with a manifest naming the endpoint, query and instant of each
                  expectations.json  what the pipeline should produce over them,
                  each expectation carrying its tier, its checkpoint and its producer
/prompts          gitignored. spent build prompts, kept locally
/data             gitignored. the store lives here
.gitattributes    line endings, normalised to LF in the repository
```

`PullbackStrategyLab.Tests` sits alongside the projects it tests rather than in a sibling tree. One consequence worth stating, because a check depends on it: the isolation check asserts that `PullbackStrategyLab.Api` has no transitive reference to `PullbackStrategyLab.Worker`, read from the compiled dependency file rather than from the project file, and the test project is exempt because it references everything by design. That exemption is named here so a later session does not find it and assume the check is broken.

**`/prompts` is a local scratch archive.** Name files `YYYY-MM-DD-<checkpoint>-<short-description>.md` so the folder sorts chronologically and a checkpoint's prompt can be found without opening anything. It is gitignored because superseded prompt text is noise in a diff. That is safe only while prompts stay scratch copies: anything inside a prompt that the corpus will later refer to, a decision, a rule, a threshold, a done condition, is written into the proper document at the time it is issued.

## Commands

| Purpose | Windows | macOS |
|---|---|---|
| Build | `dotnet build PullbackStrategyLab.sln` | same |
| Run the suite | `dotnet test src/PullbackStrategyLab.Tests` | same |
| Run one test | `dotnet test --filter FullyQualifiedName~<name>` | same |
| **Verify a checkpoint** | `tools/ci.ps1` | `tools/ci.sh` |
| **Verify a phase** | `tools/verify-phase.ps1` | `tools/verify-phase` |
| Apply migrations | `tools/migrate` | same |
| Snapshot the store | `tools/snapshot-db` | same |

**The two cells differ for `verify-phase` alone, and that is the point.** `tools/verify-phase` is a bash script with no extension, so PowerShell will not execute it: called by name from a PowerShell session it returns 0 having done nothing, and the previous run's `artifacts/phase-report.*` stay on disk reading as current. The script clears those files at its own top, which is the right guard in the wrong place, because it is inside the thing that did not run. `tools/verify-phase.ps1` finds a bash and hands the work to the one script rather than reimplementing it, and exits 3 with a named message when the machine has none. **It took whatever `Get-Command bash` returned until 3.14, and on a stock Windows 11 that is the Windows Subsystem for Linux launcher in System32, ahead of Git for Windows on the path.** With no distribution installed the operator's documented command printed a WSL message and exited 1, which is the code a red report exits with, so the gate read as failing and nothing ran; with one installed it would have run the gate inside Linux against a different filesystem, which is a different answer rather than an error. It now rejects the launcher by name, asks the bash it chose to prove it can read the script before handing the gate over, and says which one it used. The other half is that the report now carries the commit that produced it and refuses to be written without one, so a stale artifact is identifiable as well as harder to produce. Found at the 3.12 sign-off, by a session that did exactly this and quoted an earlier run's figures.

**`tools/verify-phase` is what a phase signs off against.** It runs the pipeline over the committed golden fixture, diffs every stage's output against frozen expectations, parses the architecture document's tables and asserts each claim against the code, and writes `artifacts/phase-report.html` for you and `artifacts/phase-report.json` for a build session. A phase is not done until that report is green, and "green" includes that nothing is listed as unexamined. (see: Every phase ends in a generated phase report, not in a page somebody looks at)

**`tools/ci.*` is not a wrapper around `dotnet test`.** It runs every step of the CI workflow in order against a dropped database, exiting non-zero on the first failure. A green `dotnet test` does not satisfy the second done condition.

The PowerShell and shell versions are not translations of each other. `&&` is a parse error in Windows PowerShell, so the two files differ in syntax by necessity. A two-way check asserts they run the same steps in the same order, not that they contain the same text.

Checkpoint 1.1 is what makes this table true. Until it lands, these are the contract rather than a description.

**Secrets.** `appsettings.Secrets.json` sits beside `appsettings.json` in each project that needs one. Gitignored, plaintext, never committed, and registered before environment variables so an environment variable still wins. (see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables)

## Hard rules

Named, and cited by name. A violation is a defect regardless of what else is true.

**The plan is immutable after publication.** A plan row is written the night before its session and never updated afterwards. Any code path that can modify one after its session date is a defect, not a feature.

**Point in time.** No signal may read data whose observed date is later than the setup date. The single most important property in the system, because breaking it produces an encouraging result that means nothing. It gets an explicit test that fails loudly, not a convention that everyone remembers.

**One writer per table per operation.** Declared in `SCHEMA.md`, asserted by a conformance test in both directions: every declared writer exists in code, and every writer in code is declared.

**RiskGate is the sole writer of orders,** for both directions and every version. Two writers puts the caps in two code paths and voids every comparison between versions.

**Prices are decimal in code and TEXT in storage. Statistics are double.** Never `REAL` for a price or a money value, and no implicit conversion between the two worlds. A helper that crosses the boundary does so explicitly and is named for it. This rule can be satisfied in code while still writing a `REAL` column, which is why the storage form is stated here rather than only in SCHEMA.

**Time is UTC in storage.** Session boundaries resolve through the clock abstraction using IANA identifiers only. Direct `DateTime.Now`, `DateTime.UtcNow` and `DateTimeOffset.UtcNow` outside the clock are banned and grepped.

**The code runs unmodified on Windows and macOS.** No drive letters, no backslash separators, no registry, no Windows timezone identifiers, no shelling out to a platform-specific binary. Paths are composed through the platform API from one configured data root. Scheduling lives outside the application. `InvariantGlobalization` stays false, because it is the setting that silently breaks IANA timezone lookup. (see: Every line of code runs unmodified on Windows and on Apple Silicon macOS)

**The daily vendor call ceiling is 5,000 and the job counts as it goes.** A stage stops rather than overrunning, writes a partial run entry, and marks the affected setups degraded. Any component that adds a vendor request is constrained by this, so it is a rule rather than a configuration detail. (see: Averages are computed locally, never through the vendor's technical endpoint)

**Bars are append-only.** Never delete or update a stored bar. A vendor correction arrives as a new row with a later observed timestamp. CI greps for delete and update statements against bar tables.

**No absolute path is written into a database row.** The store must remain a directory that can be copied to another machine.

**Long and short are never pooled** into one figure, in code, in a report, or on a screen.

**The AI writes only to the proposal store.** Nothing it produces feeds any component that scores AI output.

**A version is never edited after creation.** A change creates a new version starting from zero observations.

**Pre-registration is immutable.** A version's target and minimum sample are written at creation by VariantAdmitter and never again. AcceptanceGate writes only status and resolution date.

**Failed checks are recorded, not discarded.** Any setup clearing the recording floor stores its result on every check, pass and fail.

**The baseline is frozen.** Editing it closes every open version as unresolved and starts a new generation.

## Checks

Executable, named, run by `tools/ci.*`. Each is a property that should hold at every moment, not a guideline anyone remembers.

| Check | Runs | Asserts |
|---|---|---|
| `writer-ownership` | every CI run | Every store has exactly one declared writer per operation, verified in both directions against SCHEMA |
| `point-in-time` | every CI run | No read answers with an observation the lab could not have had by its own date. Three halves: every public read on a store reader takes a date, every hand-written statement selecting from a stamped table bounds that stamp, and a row observed after the as-of is invisible until the as-of moves past it |
| `decision-resolves` | every CI run | Every decision name cited in code or docs matches a bold decision name in DECISIONS.md exactly, and no two decisions share a name |
| `no-superseded-citation` | every CI run | No cited name resolves to a decision under "Previously decided" |
| `pinned-constants` | every CI run | Numeric constants stated in docs match the code constant they describe |
| `coverage-reported` | every CI run | Every check the roster says runs is implemented, is invoked by `tools/ci.*`, states its own scope in numbers, and left a coverage record in the run the phase report reads. Every file in the suite that reads the shipped source belongs to a check, or is listed by name as a scan whose backing nothing records |
| `path-casing` | every CI run | Every file path appearing as a string literal in source matches the on-disk path exactly, byte for byte |
| `two-platform` | the matrix | The suite passes on both windows and macos runners |
| `order-provenance` | 4.6 | No order row exists whose writer was not RiskGate |
| `check-completeness` | every CI run | Every setup row has a result recorded for every check defined at its date, with the check names read from ARCHITECTURE's own gate lists and reconciled in both directions |
| `surface-claims` | every CI run | Every corpus sentence claiming something is stated, recorded on every row, or shown is asserted against the rendered page that carries it. The only check here that reads a surface rather than source, the store or a document |
| `carried-obligations` | every CI run | Every due point a PROGRESS `Carried` block names is one BUILD_PLAN's obligations table also has. The set of due points rather than the sentences, because prose against prose false-alarms and a suppressed guard is a dead one |
| `checkpoint-test-count` | every CI run | Every checkpoint PROGRESS records as built states a test count in one of its own entries, which is the half of done condition 2 that lives in prose and had never been asserted |
| `stated-counts` | every CI run | Every count a spec states about itself matches the derived count. Record entries are dated measurements and are exempt |
| `fixture-inputs` | every CI run | Every vendor endpoint a live run exercises has at least one `CAPTURED` input, and every captured response carries its endpoint, query and instant and no credential |
| `fixture-replay` | every CI run | The pipeline over the golden fixture matches every committed expectation, broken down by tier, with every figure it produces named by one, and every checkpoint in the fixture carries an independently produced expectation or names an open obligation |
| `architecture-conformance` | every CI run | Every claim a table in ARCHITECTURE.html makes has a verdict: pass, fail, out of scope for this phase, or unexamined, and every table in the document is placed so none can go unread |
| `store-portability` | every CI run | No row in a populated store carries an absolute path, so the store stays a directory that can be copied |
| `price-storage-form` | every CI run | No migration declares a price or money column `REAL`. The storage form is the half of the decimal rule that code review cannot see |
| `api-isolation` | every CI run | `PullbackStrategyLab.Api` has no transitive reference to `PullbackStrategyLab.Worker`, read from the compiled dependency file |
| `bar-append-only` | every CI run | Nothing in the shipped source deletes or updates a bar table, and no migration deletes, updates or drops one |
| `ci-parity` | every CI run | `tools/ci.ps1` and `tools/ci.sh` run the same steps in the same order, and a step that fails fails the script it runs in, proved by running each shipped step function against a command that fails |
| `slot-diagnostics` | every CI run | Every stage the slot script runs is invoked through the one function that keeps a native command's stderr, so a failing stage's message and the line saying the slot stopped both reach the night's log |
| `clock-usage` | every CI run | Nothing outside the clock reads the machine clock |
| `shell-executable` | every CI run | Every shell entry point is recorded executable in the index, which is the bit Windows does not have |

**The table lists every check that runs, not only the properties this file argues for.** A check that runs as a CI step and is not declared here is a property nobody wrote down, and the phase report enumerates checks by name, so the two would disagree with nothing to reconcile them.

**The Runs column is what makes the table a roster rather than a wish.** Every check declared here either runs on every CI run, runs as the matrix, or names the checkpoint that starts it, and `coverage-reported` asserts each of those three against the corpus: an "every CI run" row has to be implemented and invoked by `tools/ci.*`, and a checkpoint row has to name one `BUILD_PLAN.md` has and `PROGRESS.md` does not yet record. Without that column the table reads as the live set while quietly holding rows nothing runs, which is the same defect as a check that under-reports, one level up.

**`coverage-reported` is the one that matters most and is easiest to lose.** Under-reporting is survivorship: a check that errors loudly gets fixed because it blocks, while a check that silently narrows its own scope keeps passing. So the only broken checks that survive in verification code are the ones that under-report. Green means "nothing I ran failed", never "nothing is wrong".

**A check that stops running is the sharpest form of that,** because it narrows its scope to nothing and still says nothing. `dotnet test --filter` exits zero when the filter matches no test, so a renamed check leaves a CI step that passes by running nothing at all. Two things close it: `coverage-reported` reconciles the roster against the implemented checks and the CI steps, and the phase report requires a coverage record from every check the roster says runs, so a check that vanishes between the roster and the run turns the report red instead of shrinking it.

**And a check that keeps running while looking at less is the quiet form of it.** Stating a scope in numbers is not the same as holding one. Until the third sign-off pass of phase 1 nothing compared a check's examined count against anything, so cutting `bar-append-only` from three bar tables to one left the suite green, the phase report green, and one summary number nobody reads a few lower. `fixtures/checks-baseline.json` records a floor per check. It is committed, and it sits beside the golden fixture for the fixture's own reason: it is a reference the run is measured against, never a result the run produces, which is why `artifacts/` stays gitignored in full. At or above the floor passes; below it fails, names the check and prints both figures. That closes the narrowing a check can do on its own and it does not close the one the corpus pays for, which is the paragraph below.

**The floor is a floor and not an equality, and that is the part worth defending.** Counts differ between platforms and grow with the corpus, so a baseline demanding equality goes red on a file being added. False alarms get suppressed, and a suppressed guard is a dead one arrived at slowly. Raising a floor is a one-line commit whose message says what the check examines. Lowering one carries what changing a fixture expectation carries: the new figure, how it was produced, and why the old one no longer holds.

**A floor holds a scope, not a total, and on the day it is recorded the two are indistinguishable.** A check names several scopes and the baseline compares their sum, so a scope that grows with the corpus pays for a scope that shrank, and the arithmetic is not close. `bar-append-only` names three bar tables, two of them created, the writes it found, and the source files it read; the source-file count is most of its total. `path-casing` reads two thousand four hundred string literals to compare twenty-seven paths, so the property is one percent of the number the floor is set on. Both were run at the phase 1 sign-off rather than argued: five ordinary new files let `bar-append-only` fall to one bar table of three and stay green through `tools/ci.*` and a GREEN phase report, and forty ordinary new literals let `path-casing` compare no paths at all and do the same. The sum is wrong in the other direction too, and that half fires first: deleting two string literals from one test file dropped `path-casing` below its floor and turned it red for a reason that has nothing to do with path casing, which is the false alarm the floor was written to avoid.

**So a check states a floor under each scope it names, and a run is measured scope by scope.** A scope whose size is a fact about the corpus rather than about the property, files read, literals scanned, values in a store, is either left without a floor and marked as context, or given one far enough below its value that ordinary growth never moves it; it is never summed with the scope that carries the property. Write the check so the scope carrying the property is the one with a floor on it, and say which that is. The rule is here rather than in the check because this is the third time the same defect has shipped and each time it was in the thing built to catch the last one, so what fails is the writing rather than the code: a verification number that is easy to compute gets floored, and the number that matters is the one nobody thought to separate out. (see: Every phase ends in a generated phase report, not in a page somebody looks at)

**That rule is implemented as of 2.1, and the shape it took is worth stating.** `fixtures/checks-baseline.json` holds one entry per check: a floor under each scope carrying the property, and the names of the scopes that are context. A scope recorded through `CheckCoverage.Context` carries no floor at all rather than a low one, because a floor far below its value is a number nobody can read as either held or broken. The comparison runs in both directions: a scope below its floor fails, a scope with no floor fails, a scope the check reclassified from property to context fails unless the baseline says so too, and a floor naming a scope the run did not produce fails, because a scope that stops being reported has narrowed to nothing and a single total could never have seen it. `examined` in the phase report is the property scopes only; the corpus scopes are reported beside it and never added to it, which makes the total smaller and makes it mean something.

**Unexamined and out of scope are counted separately, and only one of them is a defect.** Unexamined means a claim this phase should have been able to assert and could not. Out of scope means the corpus places it at a checkpoint that has not landed, or exempts it by name and says why. Reporting them as one number would let sixty later rows hide the one row nobody can check, which is the same failure arrived at from the other direction. `tools/verify-phase` is green only with zero unexamined; out of scope is shown beside it and never added to it.

**Unexamined counts admissions, not the things they cover.** A check recording that it examined none of something admits it with a count of zero, and zero added to a sum is silence: the record carried the line and the report said "unexamined 0" on the same page. The number is how many admissions were made, so it is non-zero whenever anything was admitted, and the sizes stay in the detail where they can be read.

**An out-of-scope claim names the checkpoint that ends it.** Otherwise it rests there forever and is indistinguishable from one nobody got to, and the count reads as a permanent number rather than as one that falls as checkpoints land. The checkpoint has to exist in `BUILD_PLAN.md` and has to be one `PROGRESS.md` does not yet record: a claim still deferred to a checkpoint that has landed is a claim that checkpoint shipped without coming back to, and nothing said so at the time. The report groups out-of-scope claims by the checkpoint that closes them.

**An out-of-scope coverage item names one of three things, and as of 2.2 it is structured rather than prose.** A checkpoint, asserted exactly as a claim's is. A price, where it rests on a decision nobody has scheduled: two `fixture-replay` exemptions read as equivalent in prose while one costs 1,900 vendor calls and about 130 MB committed for ever and the other costs a single per-ticker call at the next capture, and the price is exactly what prose loses. Or by design, where nothing could close it: a citation inside a dated record, a runner set asserted against the workflow file, a column exempted by name in a migration. The third shape is how this rule would be lost if everything drifted into it, so the report counts the three separately and a permanent exemption growing is visible rather than absorbed into one number.

**`decision-resolves` fails on a near-miss rather than ignoring it.** Names invite paraphrase and a paraphrased citation silently stops resolving. Exact match is the rule, which is why decision names carry no terminal punctuation: a name that ends in a period is awkward to cite and invites the paraphrase the check exists to reject.

**`stated-counts` exists because prose counts go stale silently.** A header stating a checkpoint count over a table with a different number of rows, or a total that does not add up, reads as authoritative and is wrong. Any number a spec states about its own contents is derived from the document it describes and checked, or it is not written. Records are exempt, because an entry in PROGRESS states what was measured on a date and is history rather than a claim about the corpus today.

**`path-casing` targets a bug neither of your machines can see.** Case sensitivity is a property of the filesystem, not the operating system. Windows and macOS are both insensitive by default and Linux is not, so a path written with the wrong case works on both development machines and fails on a runner. Note the check has no work to do if nothing in the project reads a file by a path built from a string; if that turns out to be true after 1.1, drop it rather than carrying it.

**And a runner now exists for it to fail on.** The workflow carries a `rehearsal` job on `ubuntu-latest` that runs `tools/ci.sh` and then the store copy, so the whole pipeline opens its files on a case-sensitive filesystem on every push. It is a separate job rather than a third row in the matrix because Linux is not a platform this lab supports; it is an instrument for one class of fault, and `two-platform` still claims exactly what it says. It came out of the 1.11 obligation, which wanted a second machine and could not have one: a container reaches every step of the move procedure except copying the secrets file, which is a human act and stays open against the real move.

## Conventions

**Decisions are named, not numbered.** A decision belongs in `DECISIONS.md` when a later session could reasonably choose differently **and** the wrong choice would be invisible. Mechanisms with one obvious implementation, and anything a test already enforces, do not.

A decision is identified by its bold name in `DECISIONS.md`, not by a heading: the topic groupings are headings, the decisions under them are bold lines. Cite the exact name. In code: `// see: Long and short are never pooled into one figure`. In a document: `(see: Long and short are never pooled into one figure)`. Same string either way, so one checker covers both. A number tells a reader nothing and forces a lookup; a name tells them the thing directly. A misremembered name fails to resolve, where a misremembered number resolves to the wrong decision and nobody notices.

**A decision is changed only by another decision.** No finding, progress entry, checkpoint note or conversation supersedes one. Work that changes a decision writes a new one, names what it supersedes, and moves the old entry to "Previously decided" in the same commit, reasoning intact.

**Nothing in the corpus is struck through.** A spec is edited cleanly and its prior text goes to `CHANGELOG.md`. A record is corrected by a new dated entry naming what it corrects. Strikethrough leaves a document that is half history and half current state, and the reader has to work out which is which on every line.

**Components are named, not coded.** A component name says what the thing is. A code needs a lookup, and half the time the lookup does not happen. The catalogue lives in `ARCHITECTURE.html` under "Component catalogue", and a new component is added there in the same commit that introduces it.

**Headings carry no numbers, and cross-document references cite the heading text.** A misremembered number resolves to the wrong place and nothing notices, and every insertion renumbers everything after it. HTML anchors are slugs of the heading text, with any nested markup excluded, since an id like `s16` is as positional as the number was. `ARCHITECTURE.html` renders labels inside two of its headings, so `The long checks <span class="pill">buy</span>` is cited as "The long checks" and anchored as `the-long-checks`. Checkpoint identifiers are the exception and keep their numbers, because they name work in a sequence where the sequence is the point. (see: Headings carry no numbers, and anchors are slugs)

**A commit subject is `Phase {phase} / {checkpoint} — {what changed}`.** `Phase 3 / 3.2 — the forward fill, and a future bar two implementations disagreed about`. The checkpoint is never omitted, including on a commit that builds nothing: a ruling, a document pass, a correction and a sign-off addendum all belong to a checkpoint, and `Phase 2 / 2.12 — the ruling the sign-off owed` is the pattern for all four. A part of a lettered checkpoint carries its letter, as `3.0(c)` does. Where work is done ahead of the checkpoint that owes it, the subject names that checkpoint rather than the one being worked on now, because what a reader wants from a log is which checkpoint a change belongs to.

This was undocumented until 3.7 and lived only in sixty commits of history, which is exactly how it got broken: a session inferred it from the log, decided a ruling was not a checkpoint's work, and dropped the second field. **A convention that exists only in what previous sessions happened to do is a convention the next session will break.** It is written here rather than made a check because the failure is loud, sits in every `git log`, and costs one amend; the shapes this corpus builds checks for are the silent ones. If it is broken twice more, that reasoning is wrong and the check is owed.

**Anything issued in conversation that will later be cited must land in the repo when it is issued,** not afterwards. A citation to something that lives only in a chat transcript is a hole in the record.

**Prose.** Standard keyboard punctuation, no em dashes. State the mechanism rather than asserting a virtue: write "the plan row is immutable after publication", not "we are honest about not changing plans".

## Verification

Three rules learned the hard way:

- Greps over markdown must be whitespace-tolerant, **and markup-tolerant over the span they match**. A phrase in this corpus is written with emphasis wherever the writer wanted emphasis, so a pattern reading `due at` finds nothing in `due **at 3.6**` or `Due **4.1**`, and one built on a literal space cannot cross the line break a long table cell puts in the middle of it. Both cost `carried-obligations` six of the seventy-one due points the record names, and one of the six was a real obligation in no table row. The failure is silent in the way this file's whole Checks section is about: a match that never happens is not a match that broke, so the count it feeds was never higher and no floor under that count can see it.
- A sweep expecting a non-zero count states that count in advance. "Returns nothing" is self-validating; "returns 17" is not.
- A test proving a check works must be permanent, not a break-and-revert done by hand once.

One more, from this corpus's own history: **a mechanical sweep that satisfies a grep can destroy the meaning it was carrying.** Replacing a code with a generic phrase clears the pattern and leaves a citation pointing at nothing. If a sweep replaces identifiers, it maps each one to its replacement rather than to a placeholder, and the result is checked by resolving every citation afterwards.

**An assertion must fail when the thing it guards is removed, and the proof of that is permanent.** A source scan that finds a pattern is not evidence the behaviour exists; a behavioural test that exercises the path is. Where both are cheap, write both and let the scan report coverage while the test carries the claim.

**This is the fourth instance, which is why it is a rule and not another pass.** The failure table's "Detector errors on one stock" claim was asserted by looking for the insert statement and the partial outcome in each detector, and it passed with the catch clause deleted: the private method issuing the insert was still in the file with nothing calling it. Before it, `path-casing` compared no paths and passed, `bar-append-only` held one bar table of three and passed, and `MarkdownTable` dropped a malformed obligation row and reported nothing missing. Each time the subject was gone and the assertion said what it always said.

**So each check names, per source-scan assertion, whether a behavioural test backs it.** `Backing.Test` names a test method, and the name has to resolve to one that exists, on the same grounds `decision-resolves` demands an exact name: a backing that has gone stale is worse than none, because it reads as covered. `Backing.Runner` names a job in the workflow, for the properties a runner exercises and no test can. `Backing.None` says nothing exercises it, and the three are counted separately so the third growing is visible rather than absorbed. A check that declares neither a scan nor `NoSourceScan` fails, so the declaration cannot be forgotten by a check that is added later. **An unbacked scan is reported and does not fail the run**, because the fix is a behavioural test per scan and that is scheduled work rather than a condition on the next commit; `coverage-reported` also lists any file in the suite that reads the shipped source and left no such record, which is how a scan written outside a check gets seen.

**A fifth shape, and it is not an absent subject.** The four instances above are one shape wearing four coats: the thing an assertion guarded went away and the assertion kept saying what it always said. The phase 2 sign-off found a different one, three times in one pass, and every count in every instance of it was correct. Tiers guard which rows may be believed and scope floors guard how many rows were looked at; nothing guards **which rows a stated figure was computed over**. The fixture's one-sidedness figures count an `AUTHORED` setup row into the captured population, so two long gates read as exercised on both sides by a real market day when one side of each came from a row inserted to give the vectorizer a subject. The 2.11 record's long retrace median is taken over dips of two bars or more while the phrase beside it says moves of the right length, which is the gate's own two to seven and a different population. And its "about 6 a night" is the long side's pass rates multiplied out, stated once for a reading that is asserted of both sides, where the short side recounted is nought.

**So a figure states the population it was computed over, in the same breath, and a figure over a mixed population is not stated at all.** Population is the rows, the filter and the tier together. "The median retrace over dips of 2 to 7 bars, long side, over the calibration rows" is a figure; "the median give-back among moves of the right length" is a phrase that fits four populations and picks whichever one the writer had open. Where a figure covers one side only it says which side, because the pooling rule is about never adding the two together and this is its other half: a one-side figure offered as covering both is a pooled figure with the arithmetic left out. Where a measurement over captured rows admits an authored one it reports the two separately or not at all, on exactly the grounds that keep the gate cases out of the market counts. The shape is worth its own rule because it survives every guard the corpus has: the numbers are right, the check is live, the subject is present, and the sentence is still false. (see: Long and short are never pooled into one figure) (see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it)

**A sixth, and it is the first one that is not about a check at all.** The four early shapes were a check asserting less than its label. The fifth was a figure computed over a population other than the one named beside it. This one is neither: the instrument is correct, it asserts exactly what it says, and **its answer is discarded downstream**. `reached-ceiling` records that it ran two of its three clauses, `check-completeness` confirms the result is there, ARCHITECTURE says the narrowing is stated outright rather than left to be inferred from a passing verdict, and every one of those is true of the store. The gallery then dropped the note whenever a value sat beside it, so the claim was false of the screen, which is the only place the sentence was ever about. Nothing was wrong upstream, so nothing upstream could have caught it. **A claim that something is visible is a claim about a surface, and this corpus verifies those claims against the store.** Where a property is asserted to be stated, recorded on every row, or shown, the assertion names the surface a person reads it on and checks that surface. (see: Every phase ends in a generated phase report, not in a page somebody looks at)

**A seventh, and it is the first that is not about an assertion at all.** The six above are all faults in something the corpus wrote: a check that says less than its label, a figure over the wrong population, a surface that drops a correct answer. This one is a fault in what the corpus points at. On the night of 2026-08-28 migrations 031 and 032 had landed and the live store was never migrated, so four stages died on a missing column, the lab flagged nothing, and its second night of evidence does not exist. `tools/ci.*` was green at 27 steps and 516 tests and the phase report was GREEN with 125 claims and zero unexamined, on that tree, that night. Neither was wrong. **Every check in this corpus takes its subject from the source, the documents, the golden fixture, or a store the check itself builds, and the running lab is in none of those.** The one place the fault was written down was the night's own log, which nothing reads, and the checkpoint's record had even read `data/live` that evening and copied its `user_version` down as provenance without reading it as a defect.

**So a green report is a statement about the build and never about the lab.** The two are different subjects and the corpus had one word for both. Where a property is about the running system, being what the store holds, what the night produced, or what the schedule did, it is asserted against the running system or it is not asserted: a guard the code carries, so the fault refuses instead of passing, and a figure a person reads on the morning it happens. Nothing in the verification harness is asked to reach the live store, because a check that opened it would be a check whose result depends on last night, and that is a different instrument from the one this corpus builds. (see: Every phase ends in a generated phase report, not in a page somebody looks at)

**An eighth, and it is the first where the assertion exists, is correct, and is not applied to the thing it is about.** The four early shapes are an assertion whose subject went away. The fifth is a figure over the wrong population, the sixth a correct answer dropped by a surface, the seventh a subject the corpus never points at. This one is none of those: the clause is written, it is right, a proof test exercises it and passes, and **the rows it governs go down a different branch**. `fixture-replay` asked done condition seven in two loops, one over the checkpoints the fixture holds expectations for and one over the landed checkpoints that contributed none. The clause failing a permit whose obligation has already fallen due was written in the first and never copied to the second. Every permit in the file names a checkpoint with no expectations at all, so every one of them took the second loop, and the guard BUILD_PLAN calls the one thing that collects itself at 4.1 would have stayed green through 4.1 and every checkpoint after it.

**The proof tests could not have seen it, and that is the half worth keeping.** All seven of them called the four-argument overload, which supplies an empty landed set, so the loop carrying every live permit never ran in any of them. The suite exercised one branch and the data took the other, and neither side could see the gap because a scope floor counts what a check looked at rather than which population it looked at.

**So a guard over a population states which population, and where a check has two, they are one loop or the split is the thing asserted.** A clause that governs a set is applied to that set, and a proof written about a method names the population it passes in rather than accepting the default. Where two paths through one check are genuinely different, the test that says so is a test that runs both. The rule is here rather than in the check because the defect is in the shape of the code and not in any line of it: every line of both loops was correct.

## Definition of done for a checkpoint

All seven, or it is not done:

1. The checkpoint's stated deliverable exists and runs.
2. `tools/ci.*` is green, with the test count recorded in PROGRESS.
3. Every new store write is declared in SCHEMA and passes `writer-ownership`.
4. Any new numeric constant stated in a doc is pinned, and every decision name cited in new code or docs resolves.
5. The suite passes on both runners.
6. A PROGRESS entry naming what was built, what was measured, and any carried obligation.
7. The checkpoint's expectations are added to the golden fixture **with their tier**, so `tools/verify-phase` covers it from now on, and at least one of them is `DERIVED` or `CONFIRMED` rather than `FROZEN`. A checkpoint that adds behaviour and no expectation has widened the unexamined set; one that adds only frozen expectations has added regression detection and called it verification. Expectations are owed at the checkpoint that produces them, **or carried to the checkpoint that first can** where the fixture does not exist yet, and a carried obligation is recorded in `BUILD_PLAN.md` when it is created rather than remembered. **The carrying is asserted per checkpoint, not remembered either.** `fixtures/expectations.json` names each frozen-only checkpoint under `frozenOnly` with the obligation it rests on, and `fixture-replay` fails a checkpoint that is frozen-only and names nothing, names an obligation BUILD_PLAN does not carry, or names one whose due checkpoint `PROGRESS.md` already records. Asserting it over the fixture as a whole would let one checkpoint's derived expectation discharge the condition for every other checkpoint in it, which is what happened between 1.7 and the third sign-off pass. (see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything)

Done conditions are written against **what the file will say after the edit**, not as statements of intent. A done condition narrower than its clause is the most common defect in this corpus.

**And a checkpoint that amends its own done condition says so in its PROGRESS entry, in those words.** Amending one is legitimate and sometimes right: 2.11 added the clause that let it decline the once-only threshold adjustment, the reasoning is on the record, and the sign-off ruled the clause stands. What is not legitimate is that the amendment and the escape it authorises land in the same commit from the same session with nothing outside that session's own prose marking it, because the next reader sees a condition and a run that met it. Naming it costs a line and gives the sign-off something to rule on without diffing `BUILD_PLAN.md` against itself.

## Stopping rules

**Interrupt a phase only if a finding blocks a checkpoint from being built, or would put a silently wrong result into shipped code.** Everything else becomes a carried obligation and waits for sign-off. A reply that holds this rule is short, carries no fenced block, and ends by saying go build.

**A finding reopens a phase only if it fails a done condition or breaks a check.**

**Three passes each finding less is the signal to stop,** not to run a fourth. A report that repeats a previous report's defect has nothing new in it.

**Fresh session rule, narrowed on purpose:** a session that has committed **code** to this repository must not sign that code off. A session whose only commits are documents may. The protection being bought is that a session does not review its own code, and the wider wording costs a session per phase for nothing.

## Merge

**CI green before merge. That is the only condition** (see: A phase branch merges on CI green, and the sign-off reviews what is already on the default branch). Sign-off is a separate activity with its own record, owed on the phase as a whole before the next phase's plan, and it does not gate the merge.

**This rule has now moved twice, and the decision says so rather than reading as though it had always been this way.** It was CI green alone, then CI green plus a phase sign-off, and is CI green alone again. The trade is written out in the decision so a third change argues against both halves rather than against whichever it finds.

**What decided it is the cost of holding a correct pass back.** A phase waiting on something that is not code keeps a branch open for as long as it waits, and the nightly job runs from that checkout for the whole of it. Phase 3 waits three months for accumulation. Production running from a branch is the more immediate defect, because it is a live system rather than a property of a history.

**A checkpoint still lands as its own commit** and still satisfies all seven done conditions on its own, and a session that has committed code still may not sign it off.

**Every change reaches `main` through a branch and a pull request, and none is committed to `main` directly.** That includes a document pass, a correction, a ruling and a sign-off, on the same grounds the commit subject includes them: the exception that feels too small to branch for is the one that gets taken, and a record of what reached the default branch and how is worth more than the minute it costs. The branch is deleted after the merge and the working tree is returned to `main`, because the tree the nightly runs from is this repository's production checkout and a branch left checked out is the hazard the row at 3.12 carries.

**This was undocumented until the 3.12 sign-off and lived only in two records naming a PR number.** `PR #4` appears in the 3.7 sign-off and `PR #5` in an answered question, and neither is a rule; the Merge section above said only that CI green is the condition, which is a statement about when a merge may happen and never about how a change arrives. So the convention was inferred from history, and history is where it broke: `ecf5a3b`, `3e88a35` and `2b5316c` were committed straight to `main`, and the sign-off session that found them was one command away from doing it a fourth time. This is the second convention in this corpus to be lost that way, after the commit subject at 3.7, which is the paragraph in "Conventions" that says a convention existing only in what previous sessions happened to do is one the next session will break. Two instances of the same failure in the same corpus is the point at which the reasoning there stops being an argument for writing it down and starts being an argument for a check.

## Document lifecycle

Five specs and three records, plus one artefact. A ninth document requires retiring one or writing down why not.

| Document | Kind | Rule |
|---|---|---|
| `CLAUDE.md` | spec | clean edits, prior text to CHANGELOG |
| `ARCHITECTURE.html` | spec | clean edits, citation at the point of change |
| `SCHEMA.md` | spec | clean edits. **The only place data ownership is declared** |
| `BUILD_PLAN.md` | spec | checkpoints and their done conditions |
| `RUNBOOK.md` | spec | clean edits |
| `DECISIONS.md` | record | grouped by topic, superseded entries move to "Previously decided" keeping their reasoning |
| `PROGRESS.md` | record | append only, corrections are new dated entries |
| `CHANGELOG.md` | record | prior text of every clean spec edit |
| `SCREENS.html` | artefact | mockups, retired when the real UI ships |

A corpus of the same shape grew past twenty documents on a previous project and the documentation tax stopped scaling with the size of the work. (see: The corpus is eight documents plus one artefact, and a ninth requires retiring one)
