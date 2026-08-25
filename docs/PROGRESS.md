# PROGRESS.md

The log. Append only. Nothing here is struck through: a correction is a new dated entry that names what it corrects and states the right value.

One entry per checkpoint, plus an entry for anything measured, found or decided along the way.

## Entry format

```
## <checkpoint> — <date> — <commit>
Built:      what exists now that did not before
Measured:   numbers, with how they were obtained
Verified:   which command, test count, coverage
Findings:   observation and reading kept separate
Carried:    obligations, each naming the checkpoint it falls due at
```

**Observation and reading stay separate.** "The nightly count is 14" is an observation. "The daily-range floor is too strict" is a reading. Recording them as one sentence is how a reading survives into later sessions as though it were a measurement.

**A measurement records how it was obtained.** A figure that came from a conversation rather than from a run is labelled as such, or it will be cited later as though it had been measured.

**Green means nothing I ran failed.** A checkpoint entry states its coverage, not only its result.

---

(the corpus was authored and reviewed before `git init`, and the same reasoning applies here as in `CHANGELOG.md`: drafting history of text no session ever read is noise on day one. The log starts at checkpoint 1.1.)

## 1.1 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `PullbackStrategyLab.sln` and the six projects under `/src`, all namespaced in full.
            `Directory.Build.props` pins net10.0, `InvariantGlobalization=false`, nullable and warnings-as-errors in one place.
            Core: `IClock` with `UtcNow`, zone conversion and session-boundary resolution by IANA identifier, rejecting Windows identifiers rather than translating them; the configuration record; `PullbackStrategyLabPaths` composing every path from one data root.
            Data: `StoreConnectionFactory` setting the four pragmas at open, a hand-written SQL migration runner versioned on SQLite's own `user_version`, `StoreText` as the one named crossing between decimal-in-code and TEXT-in-storage, and `RunLogger` as sole writer of `run_log` on both operations.
            Worker: one CLI entrypoint per job, with `migrate` and `snapshot-db`. Api: read-only store access and `/health`. Web: a Razor Pages host that reads through the Api and has no reference to Data.
            `tools/ci.ps1`, `tools/ci.sh`, `tools/migrate`, `tools/snapshot-db`. The GitHub Actions matrix on `windows-latest` and `macos-latest`, from this commit.
            Eight checks as named CI steps: decision-resolves, no-superseded-citation, stated-counts, pinned-constants, path-casing, writer-ownership, api-isolation, ci-parity.

Measured:   `tools/ci.ps1` green on Windows, 13 steps, 22 tests. Obtained by running it; the macOS half of the matrix has not run yet, so the two-platform check is unconfirmed rather than passed.
            Check coverage, read from `artifacts/checks/*.json` after that run:
            decision-resolves examined 152 (55 decision names, 45 citations, 32 files), unexamined 0.
            no-superseded-citation examined 49, unexamined 0, and vacuous: nothing has been superseded yet.
            stated-counts examined 17 claims, unexamined 0 within the registry.
            pinned-constants examined 16, unexamined 29 authored parameters that have no code constant yet.
            path-casing examined 522 (408 string literals, 8 naming a repository path, 8 compared byte for byte), unexamined 0.
            writer-ownership examined 57 (34 stores declared, 1 created by a migration, 18 source files, 2 writes found, 2 declared writers of a live store), unexamined 46.
            api-isolation examined 12 libraries in the compiled dependency file, unexamined 0.
            ci-parity examined 26, being 13 steps in each script.

Verified:   `dotnet build PullbackStrategyLab.sln`, then `tools/ci.ps1` against a dropped store. Both config properties pinned: an environment variable overrides a value present in `appsettings.Secrets.json`, and a host starts cleanly with no secrets file on disk. A third test asserts the provider list is exactly the three sources in the stated order, because a host builder installs several of its own and one left in place would change which value wins.

Findings:   Observation. `docs/ARCHITECTURE.html` read "The that check filter excludes the names most likely to be hard to borrow". Reading: a casualty of the sweep that replaced check codes with names, which left the sentence pointing at nothing. Corrected to name the `tradable-shortable` check, prior text in `CHANGELOG.md`.
            Observation. `Microsoft.Data.Sqlite` 10.0.0 resolves `SQLitePCLRaw` 2.1.11 transitively, which carries a published advisory against the native library, and warnings are errors. Pinned to 2.1.13 explicitly in `PullbackStrategyLab.Data.csproj`.
            Observation. `dotnet new sln` produces `.slnx` by default on the .NET 10 SDK. The solution was created with `-f sln`, because the commands table and the build plan are written against `PullbackStrategyLab.sln`.
            Observation. `writer-ownership` can examine 2 of 48 declared writers today. Reading: that is the expected shape at 1.1 and the coverage line is what keeps it visible; the number rises as each checkpoint creates its stores.

Carried:    Expectations for 1.1 to 1.6 owed to the golden fixture with their tier, due at 1.7. Recorded in `BUILD_PLAN.md`'s carried obligations table when it was created.
            The two-platform check is unconfirmed until the macOS runner has run. Due at 1.2, which is the checkpoint whose deliverable is the clock proven on both platforms.
            The daily call budget counts against the UTC date. Whether the vendor's own quota resets on that boundary is assumed rather than confirmed, and is confirmed at 1.3 when the first real call is made.

## 1.2 — 2026-08-25 — phase-1-ingest-and-charts

Built:      `ClockTests`, proving the clock on whichever platform the suite runs on. `America/New_York` resolves and sits behind UTC by more than nothing and at most a day, with both bounds read from the clock: reading one from the clock and the other from `DateTime` would prove the two agree rather than that the zone resolved, and it is the resolution that fails when IANA lookup is unavailable.
            Session boundaries asserted on both sides of the daylight transition, plus the hour the zone skips in March and the hour it repeats in November, since those are the two cases where a framework default would differ from what a reader assumed.
            The ban, as a named CI step: nothing outside `SystemClock.cs` reads the machine clock. `DateTime.Today` and `DateTimeOffset.Now` are banned alongside the three CLAUDE.md names, being the same mistake spelled differently.
            `CheckProofTests`, which feeds each scanner source it wrote itself. A test proving a check works has to be permanent rather than a break-and-revert done by hand once, so the violations live in the test and the repository is never broken to produce them.
            `CSharpSource.WithoutComments`, so a comment naming a banned construct in order to explain the ban is not read as the code doing it. Applied to the clock scanner, the store-write scanner and the path scanner.

Measured:   `tools/ci.ps1` green on Windows, 14 steps, 48 tests. Obtained by running it.
            clock-usage examined 19: 18 shipped source files scanned, 1 direct clock read inside the clock implementation. Unexamined 0.

Verified:   `tools/ci.ps1` against a dropped store. The macOS half of the matrix has still not run, so the two-platform check remains unconfirmed rather than passed.

Findings:   Observation. `TimeZoneInfo.TryConvertWindowsIdToIanaId("UTC")` succeeds, so `UTC` is a Windows timezone identifier as well as an everyday word and the guard rejects it. Reading: the code uses `Etc/UTC`, and the rejection is pinned by a test rather than left as a surprise. Accepting `UTC` would be the one exception that makes the rule unenforceable.
            Observation. `api-isolation`, `ci-parity` and `clock-usage` run as named CI steps but are not rows in CLAUDE.md's Checks table. Each is mandated by name elsewhere in CLAUDE.md: the isolation check under "Repository layout", the two-way script check under "Commands", the clock ban under "Hard rules". Reading: whether the table should list every executable check is a question the phase report answers better than a guess does, so it is carried rather than settled here.

Carried:    Whether CLAUDE.md's Checks table should list every check that runs as a CI step, or only the properties it currently names. Due at 1.7, where the phase report enumerates checks and their coverage and makes the answer obvious.
