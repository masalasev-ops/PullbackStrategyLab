namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// Which of a session's captured quotes a fill is charged, and what a fill records about it.
///
/// <b>The widest usable sample of the session, whatever time the fill happened.</b> A session gets
/// two passes, at 10:15 and 15:45, and a fill can happen at any minute of it, so no sample is the
/// spread at the fill and the choice is between two approximations. The widest is taken for three
/// reasons and the first is the model's own stance: pessimism on purpose, which is the safe
/// direction for a lab asking whether edge exists at all. The second is that it removes the
/// within-day lookahead question entirely, since the choice does not depend on when the fill was:
/// a fill at 09:31 charged the 10:15 quote would be priced from a book the morning had not reached.
/// The third is that a nearest-in-time rule would claim a precision the data does not have, since
/// the feed is delayed by about fifteen minutes and the two sides of one quote are stamped seconds
/// apart (see: A fill is charged the widest usable quote of its session, not the nearest one).
///
/// <b>A name with no usable quote is not filled at all.</b> Not charged nought, which is a free
/// entry that clears every threshold written as a maximum, and not charged a figure derived from
/// other names, which would be a spread nobody measured wearing the authority of one that was. The
/// order stays on the record as unfilled with the reason, so the absence is countable rather than
/// silent (see: A fill with no usable quote for its name is refused and recorded, never charged
/// nought) (see: A gate handed an absent or degenerate quantity fails rather than passing).
/// </summary>
public static class SpreadCharge
{
    /// <summary>
    /// The sample a fill is charged, or null where the session quoted this name no usable book.
    ///
    /// Ties broken by pass name so two samples of equal width choose the same one on every machine,
    /// on the same grounds a tie in trigger time is broken by ticker: an answer that depends on the
    /// order a query returned is one nobody can reproduce.
    /// </summary>
    public static QuotedSpread? Widest(IEnumerable<QuotedSpread> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return samples
            .OrderByDescending(s => s.BasisPoints)
            .ThenBy(s => s.Pass, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}

/// <summary>
/// One pass's quote for one name, reduced to what a fill needs and what a fill records.
///
/// <b>The straddle is carried and never acted on.</b> <see cref="StraddleSeconds"/> is the gap
/// between the two stamps the vendor put on the two sides of one quote, and
/// <see cref="QuoteLagSeconds"/> is how stale the older side already was. Both are recorded on every
/// fill so a later session can exclude a straddled quote from a measurement, and neither widens or
/// refuses anything here: the corpus holds one measurement of a straddle, 32 seconds on one name on
/// one response, and a threshold set from that would be a number authored rather than derived.
/// </summary>
public sealed record QuotedSpread(
    string Pass, double BasisPoints, int? QuoteLagSeconds, int? StraddleSeconds);
