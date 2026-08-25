using System.Text;
using System.Text.Json;
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
    /// Writes the coverage to the test output and to artifacts/checks, which is where the
    /// phase report harness reads it from at 1.7.
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
    }

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
