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

    /// <summary>Records something the check actually looked at, and how many of them there were.</summary>
    public CheckCoverage Examined(string what, int count)
    {
        _examined.Add(new Scope(what, count));
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

    public int TotalExamined => _examined.Sum(s => s.Count);

    public int TotalOutOfScope => _outOfScope.Sum(u => u.Count);

    public int TotalUnexamined => _unexamined.Sum(u => u.Count);

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
        if (TotalOutOfScope > 0)
        {
            summary.Append(", out of scope ").Append(TotalOutOfScope);
        }

        if (TotalUnexamined > 0)
        {
            summary.Append(", unexamined ").Append(TotalUnexamined);
        }

        _output.WriteLine(summary.ToString());

        foreach (Scope scope in _examined)
        {
            _output.WriteLine($"  examined   {scope.Count,6}  {scope.What}");
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
                new Record(CheckName, TotalExamined, TotalUnexamined, TotalOutOfScope, _examined, _unexamined, _outOfScope),
                new JsonSerializerOptions { WriteIndented = true }));

        string? shortfall = Shortfall(CheckName, TotalExamined, Baseline);
        Assert.True(shortfall is null, shortfall);
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
    public static string? Shortfall(string check, int examined, IReadOnlyDictionary<string, int> baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);
        ArgumentNullException.ThrowIfNull(baseline);

        string seen = examined.ToString(CultureInfo.InvariantCulture);

        if (!baseline.TryGetValue(check, out int floor))
        {
            // A check with no floor is a check whose narrowing nothing would catch, which is the
            // hole this closes rather than an inconvenience it can wave through. The message
            // carries the line to add, so recording a floor is copying it rather than deriving a
            // number by hand and mistyping it.
            return $"{check} has no floor in fixtures/checks-baseline.json, so nothing would notice it narrowing. "
                + $"It examined {seen} on this run; add it to the checks object as {check} {seen}, in a commit whose "
                + "message says what the check examines.";
        }

        if (examined >= floor)
        {
            return null;
        }

        string recorded = floor.ToString(CultureInfo.InvariantCulture);
        string lost = (floor - examined).ToString(CultureInfo.InvariantCulture);

        return $"{check} examined {seen} against a floor of {recorded} in fixtures/checks-baseline.json, so it is "
            + $"looking at {lost} fewer things than when that floor was recorded. Either the narrowing is a defect "
            + "and the check is wrong, or the scope genuinely shrank, and lowering a floor carries what changing a "
            + "fixture expectation carries: the new figure, how it was produced, and why the old one no longer holds.";
    }

    /// <summary>
    /// The committed floor per check. Read once: every check in a run reads the same file and it
    /// does not change underneath them.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Baseline { get; } = ReadBaseline();

    /// <summary>Where the floor lives: a fixture, committed, never an artefact of a run.</summary>
    public static string BaselineFile => Path.Combine(RepositoryLayout.Root, "fixtures", "checks-baseline.json");

    private static IReadOnlyDictionary<string, int> ReadBaseline()
    {
        if (!File.Exists(BaselineFile))
        {
            // Empty rather than thrown, so a missing file fails every check by name with the line
            // each one needs, rather than failing whichever test happened to touch this class
            // first with a message about a file.
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        BaselineFileShape? file = JsonSerializer.Deserialize<BaselineFileShape>(
            File.ReadAllText(BaselineFile),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return file?.Checks is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(file.Checks, StringComparer.Ordinal);
    }

    /// <summary>
    /// The baseline as it sits on disk. One line per check, so raising a floor is a one-line
    /// commit and the reason for it is that commit's message.
    /// </summary>
    public sealed record BaselineFileShape(string RecordedAt, string Note, IReadOnlyDictionary<string, int> Checks);

    private sealed record Scope(string What, int Count);

    private sealed record Unexamined(string What, int Count, string Why);

    private sealed record Record(
        string Check,
        int Examined,
        int Unexamined,
        int OutOfScope,
        IReadOnlyList<Scope> ExaminedDetail,
        IReadOnlyList<Unexamined> UnexaminedDetail,
        IReadOnlyList<Unexamined> OutOfScopeDetail);
}
