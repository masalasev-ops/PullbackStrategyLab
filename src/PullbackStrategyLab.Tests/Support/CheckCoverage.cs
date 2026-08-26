using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private readonly ITestOutputHelper _output;

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
    /// Records something this check was not asked to look at, with the reason: a component a
    /// later phase builds, or a case the check deliberately exempts.
    ///
    /// Separate from <see cref="NotExamined"/> on purpose, and the separation is the discipline.
    /// Unexamined means a claim this phase should have been able to assert and could not, and it
    /// is not a pass. Out of scope means nobody owed it yet. Collapsing them would let forty
    /// later-phase rows hide the one row nobody can check, which is the failure this whole idea
    /// exists to catch, arrived at from the other direction.
    /// </summary>
    public CheckCoverage OutOfScope(string what, int count, string why)
    {
        _outOfScope.Add(new Unexamined(what, count, why));
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

        _output.WriteLine(summary.ToString());

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
                new Record(CheckName, TotalExamined, TotalContext, TotalUnexamined, TotalOutOfScope, _examined, _unexamined, _outOfScope),
                new JsonSerializerOptions { WriteIndented = true }));

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

    private sealed record Unexamined(string What, int Count, string Why);

    private sealed record Record(
        string Check,
        int Examined,
        int Context,
        int Unexamined,
        int OutOfScope,
        IReadOnlyList<Scope> ExaminedDetail,
        IReadOnlyList<Unexamined> UnexaminedDetail,
        IReadOnlyList<Unexamined> OutOfScopeDetail);
}
