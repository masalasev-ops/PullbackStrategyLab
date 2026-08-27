namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// How far apart names move over the scoring horizon once the market's own move is taken out.
///
/// <b>This is the quantity a minimum sample rests on, and it was never measured.</b> The corpus
/// states a minimum of paired setup observations "detecting about a two-point difference in ten-day
/// forward return", which is a sample-size calculation with three inputs: the difference worth
/// detecting, the confidence, and the dispersion of the statistic. The first two are judgements and
/// are written down. The third is a fact about the market, and a figure asserted about it rather
/// than measured is the one input that can be wrong without anybody noticing.
///
/// <b>The market factor is removed by subtraction rather than by modelling.</b> Within one session
/// every name carries the same market move, so the cross-sectional sample variance of that session's
/// forward returns estimates the idiosyncratic variance directly: the common term cancels exactly and
/// the <c>n - 1</c> denominator makes the estimate unbiased. That is the same cancellation the paired
/// difference buys on the scoreboard, which is why this measures the right quantity rather than a
/// near neighbour of it.
/// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
///
/// <b>Statistics are double here, deliberately.</b> CLAUDE.md's rule is that prices are decimal and
/// statistics are double, and these are neither prices nor money: they are variances of ratios, and
/// forcing them through decimal would buy nothing and cost the ability to reproduce the figure from
/// any other tool.
/// </summary>
public static class ForwardDispersion
{
    /// <summary>
    /// One session's forward returns across names, which is the unit the market factor cancels in.
    /// </summary>
    public sealed record Session(DateOnly Date, IReadOnlyList<double> Returns);

    /// <summary>
    /// The two dispersions, and the population they were taken over.
    ///
    /// <paramref name="Idiosyncratic"/> is one name's forward return with the session's move removed.
    /// <paramref name="PairedDifference"/> is what a setup's difference against the mean of its own
    /// matched controls disperses by, which is larger: the control mean carries its own noise.
    /// </summary>
    public sealed record Measured(
        double Idiosyncratic, double PairedDifference, int Sessions, int Observations, int Names);

    /// <summary>
    /// The pooled dispersion over the sessions given, or null where no session carries enough names
    /// for a cross-section to mean anything.
    ///
    /// <b>Sessions thinner than <paramref name="minimumNames"/> are dropped rather than pooled in.</b>
    /// A session with three names has a cross-sectional mean that is mostly one of those three names,
    /// so removing it removes part of the very dispersion being measured and the estimate comes back
    /// too small. Too small is the direction that matters: it shrinks the minimum sample and fires a
    /// decision early.
    /// </summary>
    public static Measured? Of(
        IReadOnlyList<Session> sessions, int minimumNames, int controlsPerSet, int names)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumNames, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(controlsPerSet, 1);

        double sumSquares = 0d;
        int degreesOfFreedom = 0;
        int used = 0;
        int observations = 0;

        foreach (Session session in sessions.OrderBy(s => s.Date))
        {
            int count = session.Returns.Count;

            if (count < minimumNames)
            {
                continue;
            }

            double mean = session.Returns.Sum() / count;

            foreach (double value in session.Returns)
            {
                double centred = value - mean;
                sumSquares += centred * centred;
            }

            degreesOfFreedom += count - 1;
            observations += count;
            used++;
        }

        if (degreesOfFreedom == 0)
        {
            return null;
        }

        // Rounded here rather than at the end, so the paired figure is a function of the reported
        // single-name figure rather than of an unreported one behind it. A reader given both should
        // be able to get from the first to the second, and an independent restatement should not
        // have to guess which of the two rounding orders was used: the two differ in the last place
        // and would agree on most inputs, which is the way a reproducibility fault hides.
        double idiosyncratic = Math.Round(
            Math.Sqrt(sumSquares / degreesOfFreedom), 6, MidpointRounding.AwayFromZero);

        // A setup's paired difference is its own residual less the mean of its controls' residuals.
        // The control mean is an average of `controlsPerSet` independent residuals, so it carries a
        // variance of its own rather than being a clean subtraction, and the difference disperses by
        // sqrt(1 + 1/m) times the single-name figure.
        //
        // <b>This is the conservative direction and that is the point.</b> Matching on liquidity,
        // range and ladder should leave the controls positively correlated with the setup, which
        // would make the real difference tighter than this. Assuming that correlation and being
        // wrong would shrink the minimum sample; assuming it away and being wrong only asks for more
        // evidence than strictly needed.
        double paired = idiosyncratic * Math.Sqrt(1d + (1d / controlsPerSet));

        return new Measured(
            idiosyncratic,
            Math.Round(paired, 6, MidpointRounding.AwayFromZero),
            used,
            observations,
            names);
    }

    /// <summary>
    /// One name's forward return over the horizon, at every session that has one.
    ///
    /// Point in time is not this method's to enforce: it is handed a series a bounded read produced,
    /// and it looks forward by construction because a forward return is the one quantity in this
    /// system that is allowed to. The bound belongs at the read.
    /// </summary>
    public static IReadOnlyList<(DateOnly Date, double Return)> Returns(
        IReadOnlyList<(DateOnly Date, decimal AdjustedClose)> series, int horizonSessions)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonSessions, 1);

        var returns = new List<(DateOnly, double)>();

        for (int i = 0; i + horizonSessions < series.Count; i++)
        {
            decimal basis = series[i].AdjustedClose;

            if (basis <= 0m)
            {
                continue;
            }

            returns.Add((
                series[i].Date,
                (double)(series[i + horizonSessions].AdjustedClose / basis) - 1d));
        }

        return returns;
    }
}
