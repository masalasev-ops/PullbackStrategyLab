# CLAUDE.md

Rules for any session working in this repository. Read this file first, every session, before touching anything.

---

## What this repo is

A paper-trading laboratory that tests two mirror-image price patterns on US equities every day, records every setup it flags whether or not it trades it, and runs a research loop that improves the selection rules from that record. No real money, ever.

**PullbackStrategyLab.** Solution, projects, namespaces and the root config section all use that name in full, with no abbreviation anywhere in code. A shortened form in one place and the full form in another is the kind of inconsistency that survives for years and then bites during a rename.

**.NET with C#, SQLite for the store.** One solution, one store file under the configured data root, no server to install on either machine.

The design source of truth is `docs/ARCHITECTURE.html`. It is the only place the system is described as a whole. If code and architecture disagree, that is a finding, not a licence to change either one silently.

## Where the build is right now

The repository exists and the document corpus is committed. There is no `src/`, no solution file, and no code of any kind.

The next thing to happen is checkpoint 1.1. Everything in "Conventions", "Definition of done for a checkpoint" and "Merge" is written against commits, and from the first commit onward those rules are live: a clean edit to a spec records its prior text in `CHANGELOG.md` with the decision authorising it, and a checkpoint ends in a `PROGRESS.md` entry.

Keep this section current. A session that reads "Repository layout" and goes looking for `src/` has been misled by the document that was supposed to orient it.

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
/tools            ci.ps1  ci.sh  verify-phase  snapshot-db  migrate
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
| **Verify a phase** | `tools/verify-phase` | same |
| Apply migrations | `tools/migrate` | same |
| Snapshot the store | `tools/snapshot-db` | same |

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

| Check | Asserts |
|---|---|
| `writer-ownership` | Every store has exactly one declared writer per operation, verified in both directions against SCHEMA |
| `point-in-time` | No signal definition reads a column whose observed date can exceed the setup date |
| `decision-resolves` | Every decision name cited in code or docs matches a bold decision name in DECISIONS.md exactly, and no two decisions share a name |
| `no-superseded-citation` | No cited name resolves to a decision under "Previously decided" |
| `pinned-constants` | Numeric constants stated in docs match the code constant they describe |
| `coverage-reported` | Every check reports how much it examined, not only whether it passed |
| `path-casing` | Every file path appearing as a string literal in source matches the on-disk path exactly, byte for byte |
| `two-platform` | The suite passes on both windows and macos runners |
| `order-provenance` | No order row exists whose writer was not RiskGate |
| `check-completeness` | Every setup row has a result recorded for every check defined at its date |
| `stated-counts` | Every count a spec states about itself matches the derived count. Record entries are dated measurements and are exempt |

**`coverage-reported` is the one that matters most and is easiest to lose.** Under-reporting is survivorship: a check that errors loudly gets fixed because it blocks, while a check that silently narrows its own scope keeps passing. So the only broken checks that survive in verification code are the ones that under-report. Green means "nothing I ran failed", never "nothing is wrong".

**`decision-resolves` fails on a near-miss rather than ignoring it.** Names invite paraphrase and a paraphrased citation silently stops resolving. Exact match is the rule, which is why decision names carry no terminal punctuation: a name that ends in a period is awkward to cite and invites the paraphrase the check exists to reject.

**`stated-counts` exists because prose counts go stale silently.** A header stating a checkpoint count over a table with a different number of rows, or a total that does not add up, reads as authoritative and is wrong. Any number a spec states about its own contents is derived from the document it describes and checked, or it is not written. Records are exempt, because an entry in PROGRESS states what was measured on a date and is history rather than a claim about the corpus today.

**`path-casing` targets a bug neither of your machines can see.** Case sensitivity is a property of the filesystem, not the operating system. Windows and macOS are both insensitive by default and Linux is not, so a path written with the wrong case works on both development machines and fails on a runner. Note the check has no work to do if nothing in the project reads a file by a path built from a string; if that turns out to be true after 1.1, drop it rather than carrying it.

## Conventions

**Decisions are named, not numbered.** A decision belongs in `DECISIONS.md` when a later session could reasonably choose differently **and** the wrong choice would be invisible. Mechanisms with one obvious implementation, and anything a test already enforces, do not.

A decision is identified by its bold name in `DECISIONS.md`, not by a heading: the topic groupings are headings, the decisions under them are bold lines. Cite the exact name. In code: `// see: Long and short are never pooled into one figure`. In a document: `(see: Long and short are never pooled into one figure)`. Same string either way, so one checker covers both. A number tells a reader nothing and forces a lookup; a name tells them the thing directly. A misremembered name fails to resolve, where a misremembered number resolves to the wrong decision and nobody notices.

**A decision is changed only by another decision.** No finding, progress entry, checkpoint note or conversation supersedes one. Work that changes a decision writes a new one, names what it supersedes, and moves the old entry to "Previously decided" in the same commit, reasoning intact.

**Nothing in the corpus is struck through.** A spec is edited cleanly and its prior text goes to `CHANGELOG.md`. A record is corrected by a new dated entry naming what it corrects. Strikethrough leaves a document that is half history and half current state, and the reader has to work out which is which on every line.

**Components are named, not coded.** A component name says what the thing is. A code needs a lookup, and half the time the lookup does not happen. The catalogue lives in `ARCHITECTURE.html` under "Component catalogue", and a new component is added there in the same commit that introduces it.

**Headings carry no numbers, and cross-document references cite the heading text.** A misremembered number resolves to the wrong place and nothing notices, and every insertion renumbers everything after it. HTML anchors are slugs of the heading text, with any nested markup excluded, since an id like `s16` is as positional as the number was. `ARCHITECTURE.html` renders labels inside two of its headings, so `The long checks <span class="pill">buy</span>` is cited as "The long checks" and anchored as `the-long-checks`. Checkpoint identifiers are the exception and keep their numbers, because they name work in a sequence where the sequence is the point. (see: Headings carry no numbers, and anchors are slugs)

**Anything issued in conversation that will later be cited must land in the repo when it is issued,** not afterwards. A citation to something that lives only in a chat transcript is a hole in the record.

**Prose.** Standard keyboard punctuation, no em dashes. State the mechanism rather than asserting a virtue: write "the plan row is immutable after publication", not "we are honest about not changing plans".

## Verification

Three rules learned the hard way:

- Greps over markdown must be whitespace-tolerant.
- A sweep expecting a non-zero count states that count in advance. "Returns nothing" is self-validating; "returns 17" is not.
- A test proving a check works must be permanent, not a break-and-revert done by hand once.

One more, from this corpus's own history: **a mechanical sweep that satisfies a grep can destroy the meaning it was carrying.** Replacing a code with a generic phrase clears the pattern and leaves a citation pointing at nothing. If a sweep replaces identifiers, it maps each one to its replacement rather than to a placeholder, and the result is checked by resolving every citation afterwards.

## Definition of done for a checkpoint

All seven, or it is not done:

1. The checkpoint's stated deliverable exists and runs.
2. `tools/ci.*` is green, with the test count recorded in PROGRESS.
3. Every new store write is declared in SCHEMA and passes `writer-ownership`.
4. Any new numeric constant stated in a doc is pinned, and every decision name cited in new code or docs resolves.
5. The suite passes on both runners.
6. A PROGRESS entry naming what was built, what was measured, and any carried obligation.
7. The checkpoint's expectations are added to the golden fixture **with their tier**, so `tools/verify-phase` covers it from now on, and at least one of them is `DERIVED` or `CONFIRMED` rather than `FROZEN`. A checkpoint that adds behaviour and no expectation has widened the unexamined set; one that adds only frozen expectations has added regression detection and called it verification. Expectations are owed at the checkpoint that produces them, **or carried to the checkpoint that first can** where the fixture does not exist yet, and a carried obligation is recorded in `BUILD_PLAN.md` when it is created rather than remembered. (see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything)

Done conditions are written against **what the file will say after the edit**, not as statements of intent. A done condition narrower than its clause is the most common defect in this corpus.

## Stopping rules

**Interrupt a phase only if a finding blocks a checkpoint from being built, or would put a silently wrong result into shipped code.** Everything else becomes a carried obligation and waits for sign-off. A reply that holds this rule is short, carries no fenced block, and ends by saying go build.

**A finding reopens a phase only if it fails a done condition or breaks a check.**

**Three passes each finding less is the signal to stop,** not to run a fourth. A report that repeats a previous report's defect has nothing new in it.

**Fresh session rule, narrowed on purpose:** a session that has committed **code** to this repository must not sign that code off. A session whose only commits are documents may. The protection being bought is that a session does not review its own code, and the wider wording costs a session per phase for nothing.

## Merge

CI green before merge. That is the only condition. Sign-off is a separate activity with its own record and does not gate the merge.

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
