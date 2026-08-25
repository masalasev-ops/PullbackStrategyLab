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

    public int TotalExamined => _examined.Sum(s => s.Count);

    public int TotalUnexamined => _unexamined.Sum(u => u.Count);

    /// <summary>
    /// Writes the coverage to the test output and to artifacts/checks, which is where the
    /// phase report harness reads it from at 1.7.
    /// </summary>
    public void Report()
    {
        var summary = new StringBuilder();
        summary.Append(CheckName).Append(": examined ").Append(TotalExamined);
        if (TotalUnexamined > 0)
        {
            summary.Append(", unexamined ").Append(TotalUnexamined);
        }

        _output.WriteLine(summary.ToString());

        foreach (Scope scope in _examined)
        {
            _output.WriteLine($"  examined   {scope.Count,6}  {scope.What}");
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
                new Record(CheckName, TotalExamined, TotalUnexamined, _examined, _unexamined),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Scope(string What, int Count);

    private sealed record Unexamined(string What, int Count, string Why);

    private sealed record Record(
        string Check,
        int Examined,
        int Unexamined,
        IReadOnlyList<Scope> ExaminedDetail,
        IReadOnlyList<Unexamined> UnexaminedDetail);
}
