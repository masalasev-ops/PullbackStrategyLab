using System.Globalization;

namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// What a check's recorded number actually is, in words, beside the threshold it was tested against.
///
/// <b>Found by the gallery review rather than by a test, which is the point of that review.</b> A
/// card showed <c>tradable-shortable 9849921234</c>. Every test passed, `check-completeness` agreed
/// all ten checks were recorded, and the phase report was green, because none of them asks what a
/// number means to the person reading it. Nine billion eight hundred forty nine million is a median
/// daily turnover in dollars and it is being compared against a fifty million dollar floor, and none
/// of that is recoverable from the digits.
///
/// <b>The thresholds are read from the rule constants and never restated here.</b> A second copy of
/// 50,000,000 in a display helper is the defect this corpus greps for, one layer out from where it
/// usually looks: the number on the screen would keep agreeing with itself while the rule moved.
/// Every figure below resolves to the same constant the gate compares against.
///
/// <b>A multi-clause gate says which clause its number belongs to.</b> `tradable-shortable` tests
/// four things and records one, so the screen names the one it recorded rather than implying the
/// number is the whole verdict. What it cannot yet say is which clause failed when the gate fails,
/// because <see cref="CheckResult"/> carries a single value; that is recorded as an obligation
/// rather than solved by inventing a second number here.
/// </summary>
public static class CheckReading
{
    /// <summary>
    /// One check's number said in words, and what it was tested against.
    ///
    /// <paramref name="Against"/> is null where the check has no threshold to state, which is the
    /// grade checks: `uptrend` and `downtrend` compare a word rather than a number.
    /// </summary>
    public sealed record Reading(string Quantity, string? Against);

    /// <summary>
    /// The reading for one check's recorded value, or null where there is no number to explain.
    ///
    /// A null value is not a failure to describe: the gate was handed nothing and the result's own
    /// note says what was absent, which the caller shows instead.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public static Reading? Of(string checkName, decimal? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkName);

        if (value is not decimal number)
        {
            return null;
        }

        return checkName switch
        {
            "tradable" => new Reading(
                $"{Money(number)} median daily turnover",
                $"floor {Money(LongPullbackRules.LiquidityFloor)}, of four clauses this is the one recorded"),

            "tradable-shortable" => new Reading(
                $"{Money(number)} median daily turnover",
                $"floor {Money(ShortPullbackRules.LiquidityFloor)}, of four clauses this is the one recorded"),

            "moves-enough" => new Reading(
                $"{Percent(number)} typical daily range",
                $"floor {Percent(LongPullbackRules.DailyRangeFloor)}"),

            "thrust" => new Reading(
                $"{Whole(number)} session(s) since the scan hit",
                $"window {LongPullbackRules.ThrustWindowSessions} session(s)"),

            "dip-shape" => new Reading(
                $"{Ratio(number)} of the thrust given back",
                $"cap {Ratio(LongPullbackRules.MaximumRetrace)}, over "
                    + $"{LongPullbackRules.MinimumPullbackBars} to {LongPullbackRules.MaximumPullbackBars} bars"),

            "bounce-shape" => new Reading(
                $"{Ratio(number)} of the drop recovered",
                $"cap {Ratio(ShortPullbackRules.MaximumRecovery)}, over "
                    + $"{ShortPullbackRules.MinimumBounceBars} to {ShortPullbackRules.MaximumBounceBars} bars"),

            "held-floor" => new Reading(
                $"{Whole(number)} close(s) below the 21-day average",
                "must be none"),

            "no-reclaim" => new Reading(
                $"{Whole(number)} close(s) above the 50-day average",
                "must be none"),

            "contraction" => new Reading(
                $"{Ratio(number)}x its own 20-session average range",
                "must be under 1.00"),

            "averages-squeezing" => new Reading(
                $"{Ratio(number)}x its own {ShortPullbackRules.SqueezeWindowSessions}-session average gap",
                "must be under 1.00"),

            "trigger-near" => new Reading(
                $"{Ranges(number)} daily range(s) to the trigger",
                $"cap {Ranges(LongPullbackRules.TriggerReachRanges)}"),

            "reached-ceiling" => new Reading(
                $"{Ranges(number)} daily range(s) to the nearer average",
                $"cap {Ranges(ShortPullbackRules.CeilingReachRanges)}"),

            "exit-tight" => new Reading(
                $"{Ranges(number)} daily range(s) to the give-up point",
                $"cap {Ranges(LongPullbackRules.GiveUpRanges)}"),

            "cluster" => new Reading(
                $"{Whole(number)} same-industry name(s) the same night",
                $"needs {LongPullbackRules.ClusterThreshold}, recorded and never gating"),

            // Named rather than defaulted. A check added later with no reading here shows its bare
            // number, which is what this file exists to stop, so the absence has to be visible.
            _ => null,
        };
    }

    /// <summary>Dollars at the scale a person reads, because 9849921234 is not a quantity anyone parses.</summary>
    private static string Money(decimal value) => value switch
    {
        >= 1_000_000_000m => $"${value / 1_000_000_000m:0.##}bn",
        >= 1_000_000m => $"${value / 1_000_000m:0.##}m",
        _ => value.ToString("$#,##0", CultureInfo.InvariantCulture),
    };

    private static string Percent(decimal value) =>
        (value * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Ratio(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Ranges(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Whole(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
