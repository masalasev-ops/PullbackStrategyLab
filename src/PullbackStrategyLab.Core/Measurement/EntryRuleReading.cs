using PullbackStrategyLab.Core.Trading;

namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// What generation 1's entry rule makes of the flagged calibration rows, per side, from 7.9: how many
/// produce an entry, the stops they resolve against the ceiling, and the win-rate ceiling's bound over
/// those stops.
///
/// <b>The only look at the sourced rule before it goes live</b>, and it is over the hourly averages
/// alone. The anchored average price is one of the levels the source names and its anchor is carried to
/// the operator, so this reading names the level set it was computed over and says the anchored level
/// was not evaluated, rather than reporting a figure over a level set it did not have.
/// see: Generation 1's entry is armed by the flush into the hourly zone and taken on the first break after it, and a session gets one such decision
///
/// <b>Long and short are two readings and never one.</b> Every figure is per side.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class EntryRuleReading
{
    /// <summary>The levels this reading's zone was made of, named on every report.</summary>
    public const string LevelSet = "hourly-ema-9, hourly-ema-21";

    /// <summary>Why the anchored average price is not among them, said on every report.</summary>
    public const string AnchoredLevelNotEvaluated =
        "the anchored average price was not evaluated: its anchor rule is carried to the operator from 7.0, "
        + "and the source's procedure tunes the anchor to a candle the price has already respected, which cannot "
        + "be computed at the entry minute";

    /// <summary>What became of one flagged row under the rule.</summary>
    public enum Outcome
    {
        /// <summary>The research table holds no minute of the row's entry session.</summary>
        NoMinutes,

        /// <summary>Too few hourly closes before the entry session to form the averages.</summary>
        NoLevels,

        /// <summary>The price never reached the zone.</summary>
        NoFlush,

        /// <summary>It reached the zone and never broke a previous candle's extreme after.</summary>
        NoReclaim,

        /// <summary>It broke, and the chase filter or the ceiling refused the entry.</summary>
        Refused,

        /// <summary>It broke, and the stop was inside the ceiling.</summary>
        Entered,
    }

    /// <summary>One row's result, with its stop where it reached one and its win-rate subject where it entered.</summary>
    public sealed record Row(
        string SetupId,
        string Direction,
        Outcome Outcome,
        string? RefusedBecause,
        string? StopBasis,
        decimal? StopFraction,
        decimal? Ceiling,
        WinRateCeiling.Subject? Subject);

    /// <summary>One side's reading.</summary>
    public sealed record Side(
        string Direction,
        int Rows,
        int NoMinutes,
        int NoLevels,
        int NoFlush,
        int NoReclaim,
        int Refused,
        int Entered,
        int StopsAtSessionExtreme,
        int StopsAtEntryCandle,
        int StopsPastCeiling,
        decimal? MedianStopFraction,
        decimal? MedianCeiling,
        int BoundSubjects,
        decimal? Bound,
        decimal? Achieved);

    /// <summary>The reading of one side over its rows.</summary>
    public static Side Of(string direction, IReadOnlyList<Row> all)
    {
        ArgumentNullException.ThrowIfNull(all);

        Row[] rows = [.. all.Where(r => r.Direction == direction)];
        Row[] stopped = [.. rows.Where(r => r.StopFraction is not null)];
        WinRateCeiling.Subject[] subjects = [.. rows.Where(r => r.Outcome == Outcome.Entered && r.Subject is not null).Select(r => r.Subject!)];
        WinRateCeiling.Bound? bound = WinRateCeiling.Of(subjects);

        return new Side(
            direction,
            rows.Length,
            rows.Count(r => r.Outcome == Outcome.NoMinutes),
            rows.Count(r => r.Outcome == Outcome.NoLevels),
            rows.Count(r => r.Outcome == Outcome.NoFlush),
            rows.Count(r => r.Outcome == Outcome.NoReclaim),
            rows.Count(r => r.Outcome == Outcome.Refused),
            rows.Count(r => r.Outcome == Outcome.Entered),
            stopped.Count(r => r.StopBasis == EntryStop.SessionExtremeBasis),
            stopped.Count(r => r.StopBasis == EntryStop.EntryCandleBasis),
            stopped.Count(r => r.StopFraction > r.Ceiling),
            Median([.. stopped.Select(r => r.StopFraction!.Value)]),
            Median([.. stopped.Select(r => r.Ceiling!.Value)]),
            bound?.Subjects ?? 0,
            bound?.Ceiling,
            bound?.Achieved);
    }

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        decimal[] sorted = [.. values.Order()];
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
