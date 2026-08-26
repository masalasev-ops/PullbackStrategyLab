using System.Globalization;
using System.Text;
using System.Text.Json;
using PullbackStrategyLab.Tests.Checks;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// What a check examined, not only whether it passed.
///
/// This is the property most easily lost. Under-reporting is survivorship: a check that
/// errors loudly gets fixed because it blocks, while a check that silently narrows its own
/// scope keeps passing forever. So every check states its scope in numbers, the numbers are
/// written where the phase report can read them at 1.7, and green means "nothing I ran
/// failed" rather than "nothing is wrong".
///
/// <b>And the number is compared against a committed floor, because stating a scope is not the
/// same as holding one.</b> Until the 1.12 review this class accepted any count and compared it
/// against nothing, which left the mechanism built to catch silent narrowing silently narrowable:
/// cutting <c>BarAppendOnlyCheck.BarTables</c> from three tables to one left the suite passing,
/// the phase report GREEN, and one summary number nobody compares eight lower. The floor lives in
/// <c>fixtures/checks-baseline.json</c>, committed beside the golden fixture and for the same
/// reason: it is a reference the run is measured against, never a result the run produces, which
/// is why it is a fixture and why <c>artifacts/</c> stays gitignored in full.
///
/// <b>A floor rather than an equality.</b> Counts legitimately differ between platforms and grow
/// with the corpus, and a baseline demanding equality would go red on a file being added. False
/// alarms get suppressed, and a suppressed guard is a dead one. At or above the floor passes;
/// below it fails, names the check, and prints both figures.
///
/// <b>And a floor under each scope, not under their sum.</b> Until the phase 1 sign-off this
/// compared one number per check, and that number was every scope added together. In five of the
/// seventeen checks the sum is dominated by a size-of-corpus figure rather than by the property:
/// <c>bar-append-only</c> read 47 source files to hold 3 bar tables, <c>path-casing</c> read 2,412
/// string literals to compare 27 paths. So ordinary growth paid for a narrowing. Run rather than
/// argued: <c>BarAppendOnlyCheck.BarTables</c> cut to one table with five new files added passed at
/// 55 against a floor of 54, with the phase report GREEN and the coverage total <i>higher</i> than
/// the committed run. The same sum misfires from the other side and that half fires first, because
/// deleting two literals from one test file turned <c>path-casing</c> red for a reason that has
/// nothing to do with path casing.
///
/// A scope whose size is a fact about the corpus rather than about the property is recorded through
/// <see cref="Context"/> instead of <see cref="Examined"/>. It is still reported, because it is what
/// makes the property's number readable, and it carries no floor and is never summed with the scope
/// that does. Write the check so the scope carrying the property is the one with a floor on it.
/// </summary>
public sealed class CheckCoverage
{
    private readonly List<Scope> _examined = [];
    private readonly List<Unexamined> _unexamined = [];
    private readonly List<Unexamined> _outOfScope = [];
    private readonly List<Deferred> _reasons = [];
    private readonly List<ScanAssertion> _scans = [];
    private readonly ITestOutputHelper _output;
    private string? _noSourceScan;

    public CheckCoverage(string checkName, ITestOutputHelper output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkName);
        CheckName = checkName;
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string CheckName { get; }

    /// <summary>
    /// Records something the check actually looked at, and how many of them there were. A scope
    /// recorded this way carries a floor and is what the check narrowing would show up in.
    /// </summary>
    public CheckCoverage Examined(string what, int count)
    {
        _examined.Add(new Scope(what, count, IsContext: false));
        return this;
    }

    /// <summary>
    /// Records a scope whose size is a fact about the corpus rather than about the property: files
    /// read, literals scanned, values in a store. Reported like any other scope and given no floor,
    /// because it grows with the repository and a floor on it would be paid by ordinary growth
    /// rather than held by the check.
    ///
    /// The separation from <see cref="Examined"/> is the whole repair. Summed together, a scope that
    /// grows covers for a scope that collapsed, and the arithmetic is not close: `path-casing` reads
    /// two thousand literals to compare twenty-seven paths, so the property is one percent of the
    /// number the floor was set on.
    /// </summary>
    public CheckCoverage Context(string what, int count)
    {
        _examined.Add(new Scope(what, count, IsContext: true));
        return this;
    }

    /// <summary>
    /// Records one assertion this check makes by reading the shipped source, and what exercises
    /// the behaviour it concludes.
    ///
    /// A source scan that finds a pattern is not evidence the behaviour exists. The failure table's
    /// "Detector errors on one stock" claim was asserted by looking for the insert statement and the
    /// partial outcome in each detector, and it passed with the catch clause deleted: the private
    /// method issuing the insert was still in the file with nothing calling it. That is the fourth
    /// instance of an assertion surviving the removal of its own subject, which is why the backing is
    /// declared here rather than left to be remembered.
    ///
    /// <b>An unbacked scan does not fail the run.</b> The fix is a behavioural test per scan, which
    /// is scheduled work rather than a condition on the next commit, and a rule that blocks on it
    /// would be answered by writing <see cref="Backing.None"/> reasons that say nothing. What fails
    /// is a backing that has gone stale: a test name that resolves to no test, or a job name the
    /// workflow does not have. A stale backing is worse than none, because it reads as covered.
    /// </summary>
    public CheckCoverage Scan(string what, Backing backing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(backing);
        _scans.Add(new ScanAssertion(what, backing));
        return this;
    }

    /// <summary>
    /// Declares that this check concludes nothing about the shipped system's behaviour by reading
    /// its source, with the reason.
    ///
    /// Required rather than optional, which is the half that makes the sweep complete. A check
    /// added later that declares neither a scan nor this fails in <see cref="Report"/>, so the
    /// declaration cannot be forgotten by the one check nobody thought to revisit.
    ///
    /// The usual reason is that the text the check reads is its own subject. A document's own
    /// consistency, the two CI scripts, a migration's SQL and the compiled dependency file are all
    /// the thing itself rather than a description of something else, so removing what they assert
    /// removes the subject and the check with it.
    /// </summary>
    public CheckCoverage NoSourceScan(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        _noSourceScan = why;
        return this;
    }

    /// <summary>
    /// Records something the check could not look at, with the reason. Unexamined is not a
    /// pass, and a check that quietly omits this is the failure mode the whole idea exists
    /// to catch.
    /// </summary>
    public CheckCoverage NotExamined(string what, int count, string why)
    {
        _unexamined.Add(new Unexamined(what, count, why));
        return this;
    }

    /// <summary>
    /// Records something this check was not asked to look at, and what would end it.
    ///
    /// Separate from <see cref="NotExamined"/> on purpose, and the separation is the discipline.
    /// Unexamined means a claim this phase should have been able to assert and could not, and it
    /// is not a pass. Out of scope means nobody owed it yet. Collapsing them would let forty
    /// later-phase rows hide the one row nobody can check, which is the failure this whole idea
    /// exists to catch, arrived at from the other direction.
    ///
    /// <b>The reason is structured rather than prose, as of 2.2.</b> An out-of-scope architecture
    /// claim has always had to name the checkpoint that ends it, so the count falls as checkpoints
    /// land rather than resting as a permanent number; a coverage item carried free text and
    /// nothing read it, and seven checks recorded 149 of them. The rule does not transfer
    /// unmodified, which is why <see cref="OutOfScopeReason"/> has two shapes: some of these close
    /// on a checkpoint and some close on a decision nobody has scheduled. Read as prose those two
    /// are indistinguishable, and two of `fixture-replay`'s exemptions differ by three orders of
    /// magnitude in what they would cost.
    /// </summary>
    public CheckCoverage OutOfScope(string what, int count, OutOfScopeReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        _outOfScope.Add(new Unexamined(what, count, reason.ToString()));
        _reasons.Add(new Deferred(what, count, reason));
        return this;
    }

    /// <summary>
    /// What the check examined of the property it guards, with the size-of-corpus scopes left out.
    ///
    /// It summed those in too until the 2.1 pass, and the total that produced was not an aggregate:
    /// it was <c>store-portability</c>'s 189,726 stored values plus noise, so it could not have
    /// shown any other check's scope collapsing and twice did not. Excluding context makes the
    /// number smaller and makes it mean something, which is the trade the whole repair is.
    /// </summary>
    public int TotalExamined => _examined.Where(s => !s.IsContext).Sum(s => s.Count);

    /// <summary>The size-of-corpus scopes, reported beside the property rather than added to it.</summary>
    public int TotalContext => _examined.Where(s => s.IsContext).Sum(s => s.Count);

    public int TotalOutOfScope => _outOfScope.Sum(u => u.Count);

    /// <summary>
    /// How many admissions the check made, not how many things they covered.
    ///
    /// It summed the counts until the 2.1 pass, and <c>PathCasingCheck</c> records its no-work
    /// branch as <c>NotExamined(..., 0, ...)</c>. Zero adds nothing to a sum, so the record carried
    /// an unexamined line and the phase report said "unexamined 0" on the same page: an admission
    /// that counts as silence, which is the exact failure the separation between unexamined and out
    /// of scope exists to prevent. Counting admissions makes the number non-zero whenever anything
    /// was admitted, whatever its size, and the sizes stay in the detail where they are readable.
    /// </summary>
    public int TotalUnexamined => _unexamined.Count;

    /// <summary>
    /// Writes the coverage to the test output and to artifacts/checks, which is where the phase
    /// report harness reads it from at 1.7, then fails the check if what it examined fell below
    /// the committed floor.
    ///
    /// The record is written before the comparison on purpose. A check that narrowed should still
    /// leave the number it narrowed to where the report and the next session can read it, rather
    /// than leaving a hole that reads the same as a check which never ran at all.
    /// </summary>
    public void Report()
    {
        var summary = new StringBuilder();
        summary.Append(CheckName).Append(": examined ").Append(TotalExamined);
        if (TotalContext > 0)
        {
            summary.Append(", over a corpus of ").Append(TotalContext);
        }

        if (TotalOutOfScope > 0)
        {
            summary.Append(", out of scope ").Append(TotalOutOfScope);
        }

        if (TotalUnexamined > 0)
        {
            summary.Append(", unexamined ").Append(TotalUnexamined).Append(" admission(s)");
        }

        if (_scans.Count > 0)
        {
            summary.Append(", ").Append(_scans.Count).Append(" source scan(s) of which ")
                .Append(_scans.Count(s => s.Backing.IsUnbacked)).Append(" unbacked");
        }

        _output.WriteLine(summary.ToString());

        foreach (ScanAssertion scan in _scans)
        {
            _output.WriteLine($"  scan               {scan.What} — {scan.Backing}");
        }

        if (_noSourceScan is not null)
        {
            _output.WriteLine($"  no source scan     {_noSourceScan}");
        }

        foreach (Scope scope in _examined)
        {
            _output.WriteLine($"  {(scope.IsContext ? "context " : "examined")}   {scope.Count,6}  {scope.What}");
        }

        foreach (Unexamined outOfScope in _outOfScope)
        {
            _output.WriteLine($"  not owed  {outOfScope.Count,7}  {outOfScope.What} — {outOfScope.Why}");
        }

        foreach (Unexamined unexamined in _unexamined)
        {
            _output.WriteLine($"  unexamined {unexamined.Count,6}  {unexamined.What} — {unexamined.Why}");
        }

        string directory = Path.Combine(RepositoryLayout.Artifacts, "checks");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, $"{CheckName}.json"),
            JsonSerializer.Serialize(
                new Record(
                    CheckName, TotalExamined, TotalContext, TotalUnexamined, TotalOutOfScope,
                    _examined, _unexamined, _outOfScope,
                    [.. _scans.Select(s => new ScanRecord(s.What, s.Backing.TestName, s.Backing.JobName, s.Backing.Why))],
                    _noSourceScan),
                new JsonSerializerOptions { WriteIndented = true }));

        ArchitectureConformanceCheck.Schedule schedule = ArchitectureConformanceCheck.Schedule.Read();
        IReadOnlyList<string> deferrals =
            DeferralProblems(CheckName, _reasons, schedule.Exists, schedule.HasLanded);

        Assert.True(deferrals.Count == 0,
            $"{deferrals.Count} deferral(s) in {CheckName} name a checkpoint that cannot end them:\n  "
            + string.Join("\n  ", deferrals));

        IReadOnlyList<string> scans = ScanProblems(
            CheckName, _scans, _noSourceScan, TestNames.Contains, WorkflowJobs.Contains);

        Assert.True(scans.Count == 0,
            $"{scans.Count} problem(s) with {CheckName}'s source-scan declarations:\n  " + string.Join("\n  ", scans));

        IReadOnlyList<string> shortfalls = Shortfalls(CheckName, _examined, Baseline);
        Assert.True(shortfalls.Count == 0,
            $"{shortfalls.Count} coverage shortfall(s) in {CheckName}:\n  " + string.Join("\n  ", shortfalls));
    }

    /// <summary>
    /// What is wrong with a check's examined count against the committed floor, or null if
    /// nothing is.
    ///
    /// Pure, and separated from <see cref="Report"/> so the guard can be proved against a
    /// baseline written by hand rather than against whatever the repository happens to hold
    /// today. A guard nobody can break on purpose is a guard nobody knows the state of, and this
    /// one exists because the fault it catches is silent by construction.
    /// </summary>
    public static IReadOnlyList<string> Shortfalls(
        string check,
        IReadOnlyList<Scope> examined,
        IReadOnlyDictionary<string, CheckFloors> baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);
        ArgumentNullException.ThrowIfNull(examined);
        ArgumentNullException.ThrowIfNull(baseline);

        if (!baseline.TryGetValue(check, out CheckFloors? floors))
        {
            // A check with no floors is a check whose narrowing nothing would catch, which is the
            // hole this closes rather than an inconvenience it can wave through. The message
            // carries the lines to add, so recording them is copying rather than deriving numbers
            // by hand and mistyping one.
            string suggested = string.Join(
                ", ",
                examined.Where(s => !s.IsContext).Select(s => $"\"{s.What}\": {s.Count}"));

            return
            [
                $"{check} has no floors in fixtures/checks-baseline.json, so nothing would notice it narrowing. "
                + $"Add it to the checks object as {{ \"scopes\": {{ {suggested} }} }}, with any size-of-corpus scope "
                + "listed under \"context\" instead, in a commit whose message says what the check examines.",
            ];
        }

        var problems = new List<string>();
        var context = new HashSet<string>(floors.Context ?? [], StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> scopes = floors.Scopes ?? new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Scope scope in examined)
        {
            if (scope.IsContext)
            {
                // Recorded as context by the check. The baseline is asked to agree, because a scope
                // silently reclassified from property to context is a floor removed without a diff
                // anyone would read as one.
                if (!context.Contains(scope.What))
                {
                    problems.Add(
                        $"\"{scope.What}\" is recorded as context by the check and is not listed under \"context\" for "
                        + $"{check} in fixtures/checks-baseline.json. A scope moving from a floor to context is a guard "
                        + "being removed, so it is a deliberate line in that file rather than a property of the code.");
                }

                continue;
            }

            if (!scopes.TryGetValue(scope.What, out int floor))
            {
                problems.Add(
                    $"\"{scope.What}\" examined {scope.Count.ToString(CultureInfo.InvariantCulture)} and has no floor "
                    + $"under {check} in fixtures/checks-baseline.json. Add it as \"{scope.What}\": {scope.Count}, or "
                    + "record it through Context if its size is a fact about the corpus rather than about the property.");
                continue;
            }

            if (scope.Count >= floor)
            {
                continue;
            }

            problems.Add(
                $"\"{scope.What}\" examined {scope.Count.ToString(CultureInfo.InvariantCulture)} against a floor of "
                + $"{floor.ToString(CultureInfo.InvariantCulture)}, so it is looking at "
                + $"{(floor - scope.Count).ToString(CultureInfo.InvariantCulture)} fewer things than when that floor was "
                + "recorded. Either the narrowing is a defect and the check is wrong, or the scope genuinely shrank, and "
                + "lowering a floor carries what changing a fixture expectation carries: the new figure, how it was "
                + "produced, and why the old one no longer holds.");
        }

        // The other direction, and the one the sum could never see. A scope that is renamed or
        // dropped altogether takes its property with it, and under a single total the check would
        // keep passing on whatever else it still counts.
        var produced = new HashSet<string>(examined.Select(s => s.What), StringComparer.Ordinal);

        foreach (string named in scopes.Keys.Where(k => !produced.Contains(k)).Order(StringComparer.Ordinal))
        {
            problems.Add(
                $"\"{named}\" has a floor under {check} in fixtures/checks-baseline.json and the run produced no scope "
                + "by that name. A scope that stops being reported has narrowed to nothing, which is the failure this "
                + "guard exists for; if it was renamed, rename it in the baseline in the same commit.");
        }

        return problems;
    }

    /// <summary>
    /// The committed floors per check. Read once: every check in a run reads the same file and it
    /// does not change underneath them.
    /// </summary>
    public static IReadOnlyDictionary<string, CheckFloors> Baseline { get; } = ReadBaseline();

    /// <summary>Where the floor lives: a fixture, committed, never an artefact of a run.</summary>
    public static string BaselineFile => Path.Combine(RepositoryLayout.Root, "fixtures", "checks-baseline.json");

    private static IReadOnlyDictionary<string, CheckFloors> ReadBaseline()
    {
        if (!File.Exists(BaselineFile))
        {
            // Empty rather than thrown, so a missing file fails every check by name with the lines
            // each one needs, rather than failing whichever test happened to touch this class
            // first with a message about a file.
            return new Dictionary<string, CheckFloors>(StringComparer.Ordinal);
        }

        BaselineFileShape? file = JsonSerializer.Deserialize<BaselineFileShape>(
            File.ReadAllText(BaselineFile),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return file?.Checks is null
            ? new Dictionary<string, CheckFloors>(StringComparer.Ordinal)
            : new Dictionary<string, CheckFloors>(file.Checks, StringComparer.Ordinal);
    }

    /// <summary>
    /// The baseline as it sits on disk. One entry per check, holding a floor per scope that carries
    /// the property and the names of the scopes that are context, so raising a floor is a one-line
    /// commit and the reason for it is that commit's message.
    /// </summary>
    public sealed record BaselineFileShape(string RecordedAt, string Note, IReadOnlyDictionary<string, CheckFloors> Checks);

    /// <summary>
    /// One check's floors: a number under each scope carrying the property, and the names of the
    /// scopes whose size is a fact about the corpus. Context is a list of names rather than a
    /// number, because the whole point is that no number under it would mean anything.
    /// </summary>
    public sealed record CheckFloors(IReadOnlyDictionary<string, int>? Scopes, IReadOnlyList<string>? Context);

    /// <summary>One scope a check named, and whether its size is a fact about the corpus.</summary>
    public sealed record Scope(string What, int Count, bool IsContext);

    /// <summary>The source-scan assertions this check declared, with their backing.</summary>
    public IReadOnlyList<ScanAssertion> Scans => _scans;

    /// <summary>One assertion made by reading the shipped source, and what exercises it.</summary>
    public sealed record ScanAssertion(string What, Backing Backing);

    /// <summary>
    /// What exercises a source-scan assertion, in one of exactly three shapes.
    ///
    /// <b>A test</b>, which is the shape the rule asks for: a behavioural test that runs the path
    /// and fails when the behaviour is removed. The name has to resolve to a test that exists, on
    /// the same grounds <c>decision-resolves</c> demands an exact decision name. A backing that has
    /// gone stale is worse than none, because it reads as covered and nothing distinguishes it from
    /// one that still holds.
    ///
    /// <b>A runner</b>, for the properties a CI job exercises and no test can. <c>path-casing</c> is
    /// the whole of this category today: the bug it targets is invisible on both development
    /// machines because both filesystems are case-insensitive, so what actually exercises it is the
    /// rehearsal job opening the files on Linux. Recording that as "nothing backs it" would be false
    /// and would put a covered property on a list of gaps.
    ///
    /// <b>None</b>, where nothing exercises it. Reported, listed by the phase report, and scheduled
    /// rather than fixed in the pass that found it.
    ///
    /// The three are counted separately for the same reason <see cref="OutOfScopeReason"/>'s three
    /// are: the way this rule would be lost is everything drifting into the shape that asks least,
    /// and a count per shape makes that drift visible rather than absorbed.
    /// </summary>
    public sealed record Backing
    {
        private Backing(string? testName, string? jobName, string why)
        {
            TestName = testName;
            JobName = jobName;
            Why = why;
        }

        /// <summary>The backing test, as <c>TypeName.MethodName</c>, or null.</summary>
        public string? TestName { get; }

        /// <summary>The backing workflow job, or null.</summary>
        public string? JobName { get; }

        /// <summary>Why this is the backing, which is additional to naming it and never instead.</summary>
        public string Why { get; }

        /// <summary>Whether nothing exercises the behaviour, which is the shape worth counting.</summary>
        public bool IsUnbacked => TestName is null && JobName is null;

        /// <summary>A behavioural test, named as <c>TypeName.MethodName</c>, that must exist.</summary>
        public static Backing Test(string testName, string why)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(testName);
            ArgumentException.ThrowIfNullOrWhiteSpace(why);
            return new Backing(testName, null, why);
        }

        /// <summary>A workflow job that exercises it, named as the job id in the workflow file.</summary>
        public static Backing Runner(string jobName, string why)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
            ArgumentException.ThrowIfNullOrWhiteSpace(why);
            return new Backing(null, jobName, why);
        }

        /// <summary>Nothing exercises it. Reported rather than failed, and scheduled.</summary>
        public static Backing None(string why)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(why);
            return new Backing(null, null, why);
        }

        public override string ToString() =>
            TestName is not null ? $"backed by {TestName}: {Why}"
            : JobName is not null ? $"backed by the {JobName} job: {Why}"
            : $"nothing exercises it: {Why}";
    }

    /// <summary>
    /// What is wrong with a check's scan declarations, or nothing.
    ///
    /// Pure, and separated from the run so the guard can be proved against declarations written by
    /// hand. Three things fail: declaring neither a scan nor <see cref="NoSourceScan"/>, declaring
    /// both, and naming a test or a job that does not exist. An unbacked scan is not among them.
    /// </summary>
    public static IReadOnlyList<string> ScanProblems(
        string check,
        IReadOnlyList<ScanAssertion> scans,
        string? noSourceScan,
        Func<string, bool> testExists,
        Func<string, bool> jobExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(testExists);
        ArgumentNullException.ThrowIfNull(jobExists);

        var problems = new List<string>();

        if (scans.Count == 0 && noSourceScan is null)
        {
            problems.Add(
                $"{check} declares neither a source-scan assertion nor NoSourceScan. A check concludes something "
                + "about the shipped system by reading its source or it does not, and which one has to be written "
                + "down: an assertion that survives the removal of its own subject is the defect this corpus has "
                + "now shipped four times, and every one of them was in something nobody had asked the question of.");
        }

        if (scans.Count > 0 && noSourceScan is not null)
        {
            problems.Add(
                $"{check} declares {scans.Count} source-scan assertion(s) and also declares NoSourceScan. One of the "
                + "two is wrong, and leaving both would let the check read as exempt while it scans.");
        }

        foreach (ScanAssertion scan in scans)
        {
            if (scan.Backing.TestName is string test && !testExists(test))
            {
                problems.Add(
                    $"{check} says \"{scan.What}\" is backed by {test}, and no test by that name exists. A backing "
                    + "that has gone stale is worse than none, because it reads as covered.");
            }

            if (scan.Backing.JobName is string job && !jobExists(job))
            {
                problems.Add(
                    $"{check} says \"{scan.What}\" is backed by the {job} job, and the workflow has no job by that "
                    + "name. A property whose only exercise is a runner has none once that runner is renamed.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Every test in this assembly, as <c>TypeName.MethodName</c>. What a backing has to resolve to.
    ///
    /// Read from the assembly rather than from the source text, so a name that compiles but is no
    /// longer a test, or a test renamed by an IDE, fails here rather than passing a grep.
    /// </summary>
    public static IReadOnlySet<string> TestNames { get; } = ReadTestNames();

    /// <summary>The job ids in the workflow. What a runner backing has to resolve to.</summary>
    public static IReadOnlySet<string> WorkflowJobs { get; } = ReadWorkflowJobs();

    private static IReadOnlySet<string> ReadTestNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Type type in typeof(CheckCoverage).Assembly.GetTypes())
        {
            foreach (System.Reflection.MethodInfo method in type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttributes(inherit: true).Any(a =>
                        a is FactAttribute or TheoryAttribute))
                {
                    names.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        return names;
    }

    private static IReadOnlySet<string> ReadWorkflowJobs()
    {
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        string workflow = Path.Combine(RepositoryLayout.Root, ".github", "workflows", "ci.yml");

        if (!File.Exists(workflow))
        {
            return jobs;
        }

        string text = File.ReadAllText(workflow).Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = text.IndexOf("\njobs:\n", StringComparison.Ordinal);
        if (start < 0)
        {
            return jobs;
        }

        // Only the jobs block, because "push:" under "on:" sits at the same indent and would
        // resolve as a job that never existed.
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            text[(start + "\njobs:\n".Length)..],
            @"^  (?<job>[A-Za-z][A-Za-z0-9_-]*):\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            jobs.Add(match.Groups["job"].Value);
        }

        return jobs;
    }

    /// <summary>What this check deferred, and what would end the deferral.</summary>
    public IReadOnlyList<Deferred> Deferrals => _reasons;

    /// <summary>One deferred item with its structured reason.</summary>
    public sealed record Deferred(string What, int Count, OutOfScopeReason Reason);

    /// <summary>
    /// Why something is out of scope, in one of exactly two shapes.
    ///
    /// <b>A checkpoint</b>, which is the ordinary case and obeys the rule an out-of-scope
    /// architecture claim already obeys: the checkpoint has to exist in BUILD_PLAN and has to be
    /// one PROGRESS does not yet record, so an item still deferred to a landed checkpoint is one
    /// that checkpoint shipped without coming back to.
    ///
    /// <b>A price</b>, for the ones that close on a decision nobody has scheduled. This is the half
    /// that does not transfer from the claim rule, and it needs its own shape rather than a
    /// checkpoint field left empty: an item resting on a purchase is not waiting for anything, and
    /// writing it as prose is what made two of `fixture-replay`'s exemptions read as equivalent
    /// when one costs 1,900 vendor calls and about 130 MB committed for ever and the other costs a
    /// single per-ticker call at the next capture. The price is the thing a later session needs and
    /// is exactly what prose loses.
    /// </summary>
    public sealed record OutOfScopeReason
    {
        private OutOfScopeReason(string? checkpoint, string? price, string why)
        {
            Checkpoint = checkpoint;
            Price = price;
            Why = why;
        }

        /// <summary>The checkpoint that ends it, or null where it rests on a decision instead.</summary>
        public string? Checkpoint { get; }

        /// <summary>What taking the decision would cost, or null where a checkpoint ends it.</summary>
        public string? Price { get; }

        /// <summary>The reason, in words, which is additional to the two above and never instead of them.</summary>
        public string Why { get; }

        /// <summary>Deferred to a checkpoint, which the report groups by and which has to be open.</summary>
        public static OutOfScopeReason UntilCheckpoint(string checkpoint, string why)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);
            ArgumentException.ThrowIfNullOrWhiteSpace(why);
            return new OutOfScopeReason(checkpoint, null, why);
        }

        /// <summary>Resting on a decision nobody has taken, with what taking it would cost.</summary>
        public static OutOfScopeReason UntilDecided(string price, string why)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(price);
            ArgumentException.ThrowIfNullOrWhiteSpace(why);
            return new OutOfScopeReason(null, price, why);
        }

        /// <summary>
        /// Exempt permanently and deliberately, with the reason nothing could close it.
        ///
        /// A third shape, found while converting the call sites at 2.2 and added rather than forced
        /// into the other two. The obligation named a checkpoint and a purchase, and several real
        /// exemptions are neither: citations inside a dated record, whose correction would rewrite
        /// history rather than the corpus; a runner set asserted against the workflow rather than
        /// against a test; columns exempted by name in a migration. Recording those as "priced at
        /// nothing" would be a lie about the shape, and recording them against a checkpoint would
        /// invent one.
        ///
        /// The risk is obvious and is worth naming, because it is how this rule would be lost: if
        /// everything becomes by-design the naming rule is decoration. What holds it is that the
        /// three counts are reported separately, so by-design growing is visible in the report
        /// rather than absorbed into one out-of-scope number.
        /// </summary>
        public static OutOfScopeReason ByDesign(string why)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(why);
            return new OutOfScopeReason(null, null, why);
        }

        /// <summary>Whether nothing will ever close this, which is the shape worth counting.</summary>
        public bool IsPermanent => Checkpoint is null && Price is null;

        public override string ToString() =>
            Checkpoint is not null ? $"closed by {Checkpoint}: {Why}"
            : Price is not null ? $"rests on a decision nobody has taken, priced at {Price}: {Why}"
            : $"exempt by design: {Why}";
    }

    /// <summary>
    /// What is wrong with a set of deferrals, or nothing.
    ///
    /// Pure, and separated from the run so it can be proved against deferrals written by hand. The
    /// checkpoint half is the same assertion an out-of-scope architecture claim goes through, and
    /// it is asserted here rather than trusted because the failure it catches is silent: an item
    /// deferred to a checkpoint that has landed reads exactly like one deferred to a checkpoint
    /// that has not.
    /// </summary>
    public static IReadOnlyList<string> DeferralProblems(
        string check,
        IReadOnlyList<Deferred> deferrals,
        Func<string, bool> checkpointExists,
        Func<string, bool> checkpointHasLanded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);
        ArgumentNullException.ThrowIfNull(deferrals);
        ArgumentNullException.ThrowIfNull(checkpointExists);
        ArgumentNullException.ThrowIfNull(checkpointHasLanded);

        var problems = new List<string>();

        foreach (Deferred deferred in deferrals)
        {
            if (deferred.Reason.Checkpoint is not string checkpoint)
            {
                continue;
            }

            if (!checkpointExists(checkpoint))
            {
                problems.Add(
                    $"{check} defers \"{deferred.What}\" to checkpoint {checkpoint}, and BUILD_PLAN.md has no such "
                    + "checkpoint. A deferral to a checkpoint nobody scheduled never ends.");
                continue;
            }

            if (checkpointHasLanded(checkpoint))
            {
                problems.Add(
                    $"{check} defers \"{deferred.What}\" to checkpoint {checkpoint}, and PROGRESS.md already records "
                    + $"{checkpoint}. That checkpoint shipped without coming back to it, and nothing said so at the time.");
            }
        }

        return problems;
    }

    private sealed record Unexamined(string What, int Count, string Why);

    private sealed record Record(
        string Check,
        int Examined,
        int Context,
        int Unexamined,
        int OutOfScope,
        IReadOnlyList<Scope> ExaminedDetail,
        IReadOnlyList<Unexamined> UnexaminedDetail,
        IReadOnlyList<Unexamined> OutOfScopeDetail,
        IReadOnlyList<ScanRecord> Scans,
        string? NoSourceScan);

    private sealed record ScanRecord(string What, string? BackedByTest, string? BackedByJob, string Why);
}
