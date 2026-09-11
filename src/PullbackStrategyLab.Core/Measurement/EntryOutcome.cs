using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// Where a calibration entry went over the scoring horizon, measured from the entry and not from the
/// setup's close, from 7.9.
///
/// <b>From the entry because the stop is.</b> The win-rate ceiling asks whether a path that ended ahead
/// stayed inside its own give-up point, and a give-up point resolved at the entry minute is a distance
/// from the entry price. The stored reconstructed outcome runs from the setup session's close and holds
/// no excursion at all, so it is the wrong population twice over for this question; this measures both
/// halves from the entry, over the rest of the entry session's minutes and the horizon's daily bars.
///
/// <b>The excursion is in ATR</b>, the figure <see cref="WinRateCeiling.Survived"/> converts, with the
/// entry minute's own bar included, which is the pessimistic reading of a minute nothing orders.
/// </summary>
public static class EntryOutcome
{
    public sealed record Reading(decimal ReturnSigned, decimal MaximumAdverseExcursionAtr);

    /// <summary>
    /// The direction-signed return from <paramref name="entryPrice"/> to the last close of
    /// <paramref name="horizonBars"/>, and the least favourable price the path reached in ATR.
    /// Null where no horizon bar exists or the ATR is not a distance.
    /// </summary>
    public static Reading? Of(
        string direction,
        decimal entryPrice,
        decimal averageTrueRange,
        IReadOnlyList<(decimal High, decimal Low)> restOfEntrySession,
        IReadOnlyList<(decimal High, decimal Low, decimal Close)> horizonBars)
    {
        ArgumentNullException.ThrowIfNull(restOfEntrySession);
        ArgumentNullException.ThrowIfNull(horizonBars);

        if (horizonBars.Count == 0 || averageTrueRange <= 0m || entryPrice <= 0m)
        {
            return null;
        }

        bool isLong = direction == SetupDirection.Long;
        IEnumerable<(decimal High, decimal Low)> path = restOfEntrySession.Concat(horizonBars.Select(b => (b.High, b.Low)));

        // The least favourable point, signed so that a path that never went against the entry is
        // positive, which is the convention the stored excursions and Survived share.
        decimal leastFavourable = isLong
            ? path.Min(p => p.Low) - entryPrice
            : entryPrice - path.Max(p => p.High);

        decimal close = horizonBars[^1].Close;
        decimal signed = isLong ? (close - entryPrice) / entryPrice : (entryPrice - close) / entryPrice;

        return new Reading(signed, leastFavourable / averageTrueRange);
    }
}
