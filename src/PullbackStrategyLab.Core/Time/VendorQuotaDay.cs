namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// The vendor's quota day: the window the daily call ceiling is counted over.
///
/// <b>It is a different quantity from a trading session and it now says so.</b> The ceiling is a
/// fact about the vendor, which resets its allowance on a UTC boundary; a night is a fact about the
/// lab, which starts its stages at 17:15 Eastern and finishes them after the UTC date has rolled.
/// The two overlap for most of an evening and diverge at the end of every one of them, so a stage
/// running at 21:50 Eastern belongs to the session of that afternoon and to the quota day of the
/// following morning. Both readings are correct and they are answers to different questions.
///
/// <b>Why it is a type rather than a <see cref="DateOnly"/> and a comment.</b> Until 4.3 the quota
/// day and the session night were computed by the same expression, <c>substr(started_at, 1, 10)</c>,
/// a few lines apart in one class, and the only thing telling them apart was a comment on each. The
/// phase 3 sign-off found that expression being used for the session night, where it was wrong, and
/// 3.12 repaired that read; what was left was a correct use of an expression indistinguishable from
/// an incorrect one. No guard could be written against it, because a pattern banning the truncation
/// would have failed the one remaining use on the first file it read, and <c>point-in-time</c> could
/// not separate them either, since the wrong version did bound the stamp, on the wrong calendar.
///
/// So the quantity is named and bounded like every other window in the lab, the truncation appears
/// nowhere in the source, and the check that says so is now able to say it without exceptions.
/// see: Averages are computed locally, never through the vendor's technical endpoint
///
/// <b>The boundary is asserted rather than assumed.</b> That the vendor's reset is exactly UTC
/// midnight is the vendor's claim; where it turns out to be an exchange-local boundary instead, this
/// is the one place that changes and every counter follows it.
/// </summary>
public readonly record struct VendorQuotaDay
{
    private VendorQuotaDay(DateOnly date) => Date = date;

    /// <summary>The UTC date the allowance is counted over.</summary>
    public DateOnly Date { get; }

    /// <summary>The quota day an instant falls in.</summary>
    public static VendorQuotaDay Containing(DateTimeOffset instant) =>
        new(DateOnly.FromDateTime(instant.UtcDateTime));

    /// <summary>
    /// The quota day of a named UTC date. Named for what it takes, so a caller holding a session
    /// date cannot reach it by accident: a session date and a UTC date are the same type and
    /// different quantities, and this is the boundary between them.
    /// </summary>
    public static VendorQuotaDay OfUtcDate(DateOnly utcDate) => new(utcDate);

    /// <summary>The first instant counted against this day.</summary>
    public DateTimeOffset Start => new(Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    /// <summary>
    /// The first instant of the next day, which bounds this one from above and is <b>exclusive</b>.
    ///
    /// Exclusive rather than the last millisecond of the day, unlike <see cref="SessionBoundaries"/>,
    /// and deliberately unlike it: a session bound is inclusive because it closes a day a person
    /// named, and this one abuts the next window with nothing between them. A stamp carrying more
    /// precision than the store's milliseconds would fall in the gap an inclusive bound leaves.
    /// </summary>
    public DateTimeOffset End => new(Date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    /// <summary>Whether an instant is counted against this day.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    public override string ToString() => Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
