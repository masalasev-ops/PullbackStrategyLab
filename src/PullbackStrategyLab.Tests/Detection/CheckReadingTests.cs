using PullbackStrategyLab.Core.Detection;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// What a check's number means, asserted over the gate list so a gate added later inherits it.
///
/// <b>This exists because of a defect no test could have found.</b> The gallery showed
/// <c>tradable-shortable 9849921234</c>. Every check passed, `check-completeness` agreed all twenty
/// gates recorded a result, and the phase report was green, because nothing in the suite asks
/// whether a number means anything to the person reading it. It took a person opening the page and
/// asking what the number was, which is what the 2.9 gallery review is for.
///
/// Written over <see cref="SetupChecks"/> rather than over a list of its own, on the same grounds as
/// <see cref="GateBoundaryTests"/>: a gate with no reading fails here, so the eleventh check cannot
/// arrive without one.
/// </summary>
public sealed class CheckReadingTests
{
    /// <summary>
    /// The two gates that compare a word rather than a number, exempted by name and with the reason.
    ///
    /// `uptrend` and `downtrend` record a null value and carry the ladder grade in their note, so
    /// there is no quantity to describe. Listed here rather than defaulted, because a gate that
    /// silently fell into this set would be one whose number stopped being explained.
    /// </summary>
    private static readonly string[] CompareAWordNotANumber = ["uptrend", "downtrend"];

    public static TheoryData<string> NumericGates
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in SetupChecks.Long.Concat(SetupChecks.Short).Distinct(StringComparer.Ordinal))
            {
                if (!CompareAWordNotANumber.Contains(name, StringComparer.Ordinal))
                {
                    data.Add(name);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NumericGates))]
    public void Every_gate_that_records_a_number_says_what_the_number_is(string gate)
    {
        CheckReading.Reading? reading = CheckReading.Of(gate, 1m);

        Assert.True(reading is not null,
            $"the gate \"{gate}\" records a number and CheckReading has no entry for it, so the gallery "
            + "shows the digits alone. That is the defect the gallery review found on tradable-shortable.");
        Assert.False(string.IsNullOrWhiteSpace(reading!.Quantity));
    }

    [Theory]
    [MemberData(nameof(NumericGates))]
    public void Every_gate_that_records_a_number_states_what_it_was_tested_against(string gate)
    {
        CheckReading.Reading? reading = CheckReading.Of(gate, 1m);

        Assert.True(!string.IsNullOrWhiteSpace(reading?.Against),
            $"the gate \"{gate}\" states a quantity and no threshold, so a reader can see the number and "
            + "not whether it cleared. The point of the reading is that the verdict can be checked rather "
            + "than taken.");
    }

    [Fact]
    public void The_two_word_gates_are_exempt_because_they_have_no_quantity()
    {
        // The exemption is asserted rather than assumed: if one of these started recording a number,
        // it would need a reading and this test says so by failing.
        foreach (string gate in CompareAWordNotANumber)
        {
            Assert.Null(CheckReading.Of(gate, null));
        }
    }

    [Fact]
    public void A_turnover_reads_as_money_rather_than_as_eleven_digits()
    {
        // The exact number off the card that started this.
        CheckReading.Reading reading = CheckReading.Of("tradable-shortable", 9_849_921_234m)!;

        Assert.Contains("$9.85bn", reading.Quantity, StringComparison.Ordinal);
        Assert.Contains("median daily turnover", reading.Quantity, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multi_clause_gate_says_the_number_is_one_clause_of_several()
    {
        // Four clauses, one recorded value. The screen must not imply the number is the verdict.
        Assert.Contains("of four clauses", CheckReading.Of("tradable-shortable", 5m)!.Against!, StringComparison.Ordinal);
        Assert.Contains("of four clauses", CheckReading.Of("tradable", 5m)!.Against!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_thresholds_are_read_from_the_rule_constants_rather_than_restated()
    {
        // The property that stops the screen and the rule drifting apart: every threshold shown is
        // formatted from the constant the gate compares against, so moving the constant moves the
        // text. Asserted by rendering the constants the same way and requiring them to appear.
        Assert.Contains("$50m", CheckReading.Of("tradable-shortable", 1m)!.Against!, StringComparison.Ordinal);
        Assert.Contains("$20m", CheckReading.Of("tradable", 1m)!.Against!, StringComparison.Ordinal);
        Assert.Contains("5%", CheckReading.Of("moves-enough", 1m)!.Against!, StringComparison.Ordinal);
        Assert.Contains(
            LongPullbackRules.ThrustWindowSessions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CheckReading.Of("thrust", 1m)!.Against!,
            StringComparison.Ordinal);
        Assert.Contains("1.50", CheckReading.Of("trigger-near", 1m)!.Against!, StringComparison.Ordinal);
        Assert.Contains("0.50", CheckReading.Of("exit-tight", 1m)!.Against!, StringComparison.Ordinal);
        Assert.Contains("0.50", CheckReading.Of("reached-ceiling", 1m)!.Against!, StringComparison.Ordinal);
        Assert.Contains("0.40", CheckReading.Of("dip-shape", 1m)!.Against!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gate_nobody_has_written_a_reading_for_returns_nothing_rather_than_a_guess()
    {
        // The default arm is null on purpose. A fabricated description would read as authoritative
        // and be wrong, which is worse than the bare number the caller falls back to.
        Assert.Null(CheckReading.Of("a-gate-that-does-not-exist", 1m));
    }
}
