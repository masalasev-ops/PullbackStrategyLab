using System.Globalization;
using System.Text.Json;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// That the geometry cases still reach the branches they were written for.
///
/// <b>Why this exists beside the expectations rather than instead of them.</b> `fixture-replay`
/// asserts that what <see cref="PullbackStrategyLab.Core.Indicators.PullbackGeometry"/> computes
/// matches what an independent restatement computed, which is the arithmetic. It cannot notice the
/// case file being replaced with eleven windows whose thrust is the last bar: every expectation
/// would be rewritten to the degenerate shape, every one would match, and the run would be green
/// over a method exercised on nothing. That is the shape this corpus has now shipped five times,
/// and the answer to it is an assertion that fails when the subject goes away.
///
/// So this reads the committed expectations and requires the branches to be present in them. It
/// fails if a case is deleted, if a thrust index is moved to the end of its window, or if the
/// authored file is narrowed to the shapes the captured fixture already produces.
/// </summary>
public sealed class GeometryCaseTests
{
    private const string Checkpoint = "3.0";

    private static IReadOnlyDictionary<string, string> Committed()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, "fixtures", "expectations.json")));

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (JsonElement expectation in document.RootElement.GetProperty("expectations").EnumerateArray())
        {
            string id = expectation.GetProperty("id").GetString() ?? string.Empty;

            if (id.StartsWith("geometry.", StringComparison.Ordinal))
            {
                values[id] = expectation.GetProperty("value").GetString() ?? string.Empty;
            }
        }

        return values;
    }

    private static string Value(IReadOnlyDictionary<string, string> committed, string name, string quantity) =>
        committed.TryGetValue($"geometry.{name}.{quantity}", out string? value)
            ? value
            : throw new InvalidOperationException(
                $"geometry.{name}.{quantity} has no committed expectation. A case that stops being "
                + "measured has narrowed to nothing, which is what this test exists to catch.");

    [Fact]
    public void The_case_file_is_authored_and_says_what_it_cannot_say()
    {
        Assert.Equal("AUTHORED", GeometryCases.Tier);

        // Stated in advance, because a case file that quietly shrinks is the failure here and a
        // count asserted after the fact is a count that agrees with whatever it found.
        Assert.Equal(14, GeometryCases.All.Count);

        foreach (GeometryCases.GeometryCase geometryCase in GeometryCases.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(geometryCase.Why),
                $"{geometryCase.Name} names no branch it was written to reach, so nothing says what "
                + "would be lost by deleting it.");

            Assert.True(geometryCase.Direction is "long" or "short",
                $"{geometryCase.Name} has direction \"{geometryCase.Direction}\".");
        }
    }

    /// <summary>
    /// Every case carries every quantity, so a caller reading half the record correctly cannot
    /// still be handed a wrong origin without anything noticing.
    /// </summary>
    [Fact]
    public void Every_case_pins_every_quantity_of_the_shape()
    {
        IReadOnlyDictionary<string, string> committed = Committed();

        string[] quantities =
        [
            "extremeIndex", "pullbackBars", "thrustOrigin", "thrustExtreme",
            "pullbackExtreme", "retraceDepth", "trigger", "stop",
        ];

        foreach (GeometryCases.GeometryCase geometryCase in GeometryCases.All)
        {
            foreach (string quantity in quantities)
            {
                Assert.False(string.IsNullOrWhiteSpace(Value(committed, geometryCase.Name, quantity)));
            }
        }

        Assert.Equal(GeometryCases.All.Count * quantities.Length, committed.Count);
    }

    /// <summary>
    /// The branches the captured fixture cannot reach on its own, each required to be reached by
    /// some case.
    ///
    /// Named individually rather than counted, because the useful failure is "no case has a thrust
    /// of no size any more" and not "one branch is missing".
    /// </summary>
    [Fact]
    public void Every_branch_the_captured_fixture_cannot_reach_is_reached_by_a_case()
    {
        IReadOnlyDictionary<string, string> committed = Committed();

        var shapes = GeometryCases.All
            .Select(c => new
            {
                c.Name,
                c.Direction,
                Bars = int.Parse(Value(committed, c.Name, "pullbackBars"), CultureInfo.InvariantCulture),
                Retrace = Value(committed, c.Name, "retraceDepth"),
                Origin = Value(committed, c.Name, "thrustOrigin"),
                ThrustIndex = c.ThrustIndex,
            })
            .ToList();

        Assert.True(shapes.Any(s => s.Bars > 0),
            "no case has a pullback at all, so every one of them is the degenerate shape the "
            + "captured fixture already produces and this file is buying nothing.");

        Assert.True(shapes.Any(s => s.Bars == 0),
            "no case has a thrust whose extreme is the last bar. That is the shape every captured "
            + "row returns today, and it is kept deliberately so the correction cannot move it "
            + "without the diff saying so.");

        Assert.True(shapes.Any(s => s.Retrace == "undefined"),
            "no case has a thrust of no size, so nothing asserts that a move of nought is reported "
            + "as undefined rather than as zero or as infinite.");

        Assert.True(shapes.Any(s => s.Retrace.StartsWith('-')),
            "no case has a thrust of the wrong sign, so nothing asserts what the method returns "
            + "when the extreme sits the wrong side of the origin.");

        Assert.True(shapes.Any(s => s.ThrustIndex == 0),
            "no case puts the thrust on the first bar of its window, so the origin fallback is "
            + "reached by nothing. That branch is why the two implementations disagreed at 3.0.");

        Assert.True(shapes.Any(s => s.Bars >= 2 && s.Bars <= 7 && !s.Retrace.StartsWith('-')
                                    && s.Retrace != "undefined"
                                    && decimal.Parse(s.Retrace, CultureInfo.InvariantCulture) <= 0.40m),
            "no case produces a shape the gates would pass, so every case is a rejection and the "
            + "passing branch of the quantity four gates read is asserted by nothing.");

        foreach (string direction in new[] { "long", "short" })
        {
            Assert.True(shapes.Any(s => s.Direction == direction && s.Bars > 0),
                $"no {direction} case has a pullback. The mirror is a parameter rather than a second "
                + "class, and a mirror asserted on one side only is a mirror nobody has checked.");
        }
    }

    /// <summary>
    /// At least one case where the adjusted basis and the raw one are far apart.
    ///
    /// Every quantity but the trigger and the stop is adjusted, and on a name with no corporate
    /// action the two bases are the same number. So a set of cases drawn only from such names would
    /// pin both bases and prove nothing about either: the expectations would be identical with the
    /// two swapped. The fixture's 2-for-1 split is the one place they are nearly a factor of two
    /// apart, which is the difference between a rounding and a plan that says buy at half price.
    /// </summary>
    [Fact]
    public void At_least_one_case_separates_the_adjusted_basis_from_the_raw_one()
    {
        IReadOnlyDictionary<string, string> committed = Committed();

        decimal widest = 0m;
        string where = "none";

        foreach (GeometryCases.GeometryCase geometryCase in GeometryCases.All)
        {
            decimal adjusted = decimal.Parse(
                Value(committed, geometryCase.Name, "thrustExtreme"), CultureInfo.InvariantCulture);
            decimal raw = decimal.Parse(
                Value(committed, geometryCase.Name, "trigger"), CultureInfo.InvariantCulture);

            if (adjusted == 0m)
            {
                continue;
            }

            decimal apart = Math.Abs(raw - adjusted) / adjusted;

            if (apart > widest)
            {
                widest = apart;
                where = geometryCase.Name;
            }
        }

        Assert.True(widest > 0.5m,
            $"the widest gap between the adjusted basis and the raw one is {widest:0.0000} in "
            + $"{where}. With every case on a name whose adjustment factor is near one, the two "
            + "bases are the same number and pinning both asserts nothing about either.");
    }
}
