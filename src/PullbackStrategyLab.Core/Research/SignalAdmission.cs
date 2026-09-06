using System.Globalization;

namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// Whether a candidate signal earns a place in the library, judged on the rows already stored and
/// on nothing else.
///
/// <b>The judgement needs no variant and no forward period, which is the whole reason it exists.</b>
/// A signal admitted through a rule is a signal judged by whatever rule happened to use it, so the
/// library would grow by whichever candidates a version's thresholds liked. This asks a question
/// about the evidence instead: do setups that ended up in the same place look more alike once this
/// number is part of what "alike" means?
/// see: Signals are admitted on whether they tighten outcome-similar neighbourhoods, independently of any rule using them
///
/// <b>Two tests in one order, and the order is not a preference.</b> Correlation is asked first,
/// because a candidate above the limit is refused whatever it does to a neighbourhood: it is the
/// signal already present wearing a different name, and admitting it would let one quantity be
/// screened twice and pay the correction threshold once. Tightening is asked second, of what is
/// left.
/// </summary>
public static class SignalAdmission
{
    /// <summary>
    /// Above this, in absolute value, a candidate is refused as something already present.
    ///
    /// <b>Absolute, and that is the half a bare 0.70 does not say.</b> A candidate correlating
    /// −0.95 with an admitted signal carries exactly as little new information as one correlating
    /// +0.95; the sign says which way it points and not how much of it is already known. A limit
    /// read as signed would admit every inverted restatement of the library.
    /// see: Signals are admitted on whether they tighten outcome-similar neighbourhoods, independently of any rule using them
    /// </summary>
    public const double CorrelationLimit = 0.70;

    /// <summary>
    /// The fewest setups a verdict may be reached over.
    ///
    /// <b>Below this the answer is `undecided` and not `rejected`.</b> Both statistics here are
    /// means over pairs, and a mean over a handful of pairs moves further on one row than any real
    /// discrimination would move it, so a verdict taken there would be a verdict about the sample.
    /// The lab is under this floor today and will be for months, which is why the undecided state
    /// is a recorded outcome carrying its reason rather than a silence.
    /// see: Admission compares outcome-similar pairs against all pairs, and a population too small for the comparison is undecided rather than rejected
    /// </summary>
    public const int MinimumPopulation = 20;

    /// <summary>
    /// How tightly outcome-similar setups sit together in a signal space, as a ratio.
    ///
    /// <b>A ratio rather than a distance, because a distance grows on any column at all.</b>
    /// Euclidean distance in a wider space is larger whatever the extra dimension holds, so
    /// comparing the active set against the active set plus a candidate on raw distance would refuse
    /// a perfectly discriminating candidate for exactly the reason it refuses a noise one, and both
    /// refusals would look like a working test. Dividing the outcome-similar mean by the all-pairs
    /// mean removes the part of that growth which is arithmetic rather than evidence, because both
    /// means are taken over the same space.
    ///
    /// <b>What it does not do is ignore the width, and that is deliberate.</b> An independent column
    /// adds the same variance to a near pair as to a far one, so it lifts the near pair
    /// proportionally more and the ratio moves toward one. A column carrying nothing about the
    /// outcome genuinely makes the space worse at separating outcomes, and the criterion is right to
    /// say so; the same column would have passed a criterion built to be blind to the width.
    ///
    /// Below one means outcome-similar setups sit closer than a typical pair, which is what the
    /// library is for. At one the space says nothing about outcomes. Above one it says something
    /// backwards.
    ///
    /// <b>The space and the distance come from <see cref="SignalSpace"/>.</b> TwinPairFinder takes
    /// the same distance over the same standardised axes, and two copies of it would put the
    /// admission test and the twin finder on subtly different geometries with nothing able to say
    /// which was right.
    ///
    /// <b>Outcome-similar is the median split and carries no parameter.</b> A pair is
    /// outcome-similar when its outcomes differ by less than the median pairwise outcome
    /// difference over the same population, so the threshold is derived from the population rather
    /// than chosen for it and cannot be tuned to make a candidate pass.
    /// </summary>
    public static double? NeighbourhoodTightness(
        IReadOnlyList<IReadOnlyList<double>> rows,
        IReadOnlyList<double> outcomes)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(outcomes);

        if (rows.Count != outcomes.Count)
        {
            throw new ArgumentException(
                $"{rows.Count} row(s) against {outcomes.Count} outcome(s): the two are one population and have to be the same length.",
                nameof(outcomes));
        }

        if (rows.Count < 2 || rows[0].Count == 0)
        {
            return null;
        }

        IReadOnlyList<IReadOnlyList<double>> z = SignalSpace.ZScore(rows);

        var distances = new List<double>();
        var differences = new List<double>();

        for (int i = 0; i < z.Count; i++)
        {
            for (int j = i + 1; j < z.Count; j++)
            {
                distances.Add(SignalSpace.Distance(z[i], z[j]));
                differences.Add(Math.Abs(outcomes[i] - outcomes[j]));
            }
        }

        double split = Median(differences);
        double similar = 0;
        int similarCount = 0;

        for (int p = 0; p < distances.Count; p++)
        {
            if (differences[p] < split)
            {
                similar += distances[p];
                similarCount++;
            }
        }

        double all = distances.Average();

        // No pair is on the near side of the split, which happens where every outcome difference is
        // the same number, and an all-pairs mean of nought happens where every row is identical.
        // Both are populations the ratio has nothing to say about rather than populations that
        // score badly, so the answer is absent rather than a figure.
        // see: A gate handed an absent or degenerate quantity fails rather than passing
        return similarCount == 0 || all == 0 ? null : (similar / similarCount) / all;
    }

    /// <summary>
    /// Pearson correlation between two signals over the same rows, as an absolute value.
    ///
    /// Null where either side does not vary, because a constant column correlates with nothing and
    /// a correlation of nought would say the two are unrelated when what happened is that the
    /// question could not be asked.
    /// </summary>
    public static double? Correlation(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
        {
            throw new ArgumentException(
                $"{left.Count} value(s) against {right.Count}: a correlation is over one population.",
                nameof(right));
        }

        if (left.Count < 2)
        {
            return null;
        }

        double meanLeft = left.Average();
        double meanRight = right.Average();

        double covariance = 0;
        double varianceLeft = 0;
        double varianceRight = 0;

        for (int i = 0; i < left.Count; i++)
        {
            double a = left[i] - meanLeft;
            double b = right[i] - meanRight;
            covariance += a * b;
            varianceLeft += a * a;
            varianceRight += b * b;
        }

        return varianceLeft == 0 || varianceRight == 0
            ? null
            : Math.Abs(covariance / Math.Sqrt(varianceLeft * varianceRight));
    }

    /// <summary>
    /// The verdict on one candidate, given the active signals' values, the candidate's own, and the
    /// outcome each row ended at.
    ///
    /// <paramref name="active"/> is one column per admitted signal, each holding one value per row
    /// in the same order as <paramref name="outcomes"/>. The candidate is a column of the same
    /// length. Nothing here reads a store: the caller assembles the population and this decides,
    /// which is what lets the whole judgement be exercised over authored rows.
    /// </summary>
    public static SignalVerdict Judge(
        string candidate,
        IReadOnlyList<double> values,
        IReadOnlyDictionary<string, IReadOnlyList<double>> active,
        IReadOnlyList<double> outcomes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(outcomes);

        if (values.Count < MinimumPopulation)
        {
            return SignalVerdict.Undecided(candidate,
                $"the population is {values.Count} setup(s) with a closed outcome and a value for every "
                + $"signal compared, and a verdict is not taken below {MinimumPopulation}");
        }

        if (active.Count == 0)
        {
            return SignalVerdict.Undecided(candidate,
                "no signal is admitted yet, so there is neither a correlation to measure against nor a "
                + "space for the candidate to tighten");
        }

        // Correlation first, and against every admitted signal rather than against the mean of
        // them: what the limit refuses is a restatement of one signal, and an average over the
        // library would hide a perfect duplicate behind thirty unrelated columns.
        string? against = null;
        double worst = 0;

        foreach ((string name, IReadOnlyList<double> column) in active.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            double? measured = Correlation(values, column);

            if (measured is double c && c > worst)
            {
                worst = c;
                against = name;
            }
        }

        if (against is not null && worst > CorrelationLimit)
        {
            return SignalVerdict.Rejected(candidate, worst, against);
        }

        IReadOnlyList<string> order = [.. active.Keys.OrderBy(k => k, StringComparer.Ordinal)];

        double? before = NeighbourhoodTightness(Columns(order, active, values.Count, null), outcomes);
        double? after = NeighbourhoodTightness(Columns(order, active, values.Count, values), outcomes);

        if (before is not double b || after is not double a2)
        {
            return SignalVerdict.Undecided(candidate,
                "the tightness ratio is absent on one side of the comparison, so there is no pair of "
                + "figures to compare rather than a comparison that failed");
        }

        return a2 < b
            ? SignalVerdict.Admitted(candidate, b, a2, worst, against)
            : SignalVerdict.NotTightened(candidate, b, a2, worst, against);
    }

    /// <summary>The population as rows, the admitted signals in a fixed order with the candidate appended where there is one.</summary>
    private static IReadOnlyList<IReadOnlyList<double>> Columns(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, IReadOnlyList<double>> active,
        int count,
        IReadOnlyList<double>? candidate)
    {
        var rows = new List<IReadOnlyList<double>>(count);

        for (int i = 0; i < count; i++)
        {
            var row = new List<double>(order.Count + 1);

            foreach (string name in order)
            {
                row.Add(active[name][i]);
            }

            if (candidate is not null)
            {
                row.Add(candidate[i]);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The middle value, taking the lower of the two middles on an even count so the split is a value the population holds.</summary>
    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}

/// <summary>
/// What the admission test decided about one candidate, and what it decided it on.
///
/// <b>Four outcomes and not two.</b> Admitted and rejected are the two the library ends up
/// carrying; not-tightened and undecided are different from each other and neither is a rejection.
/// A signal that did not tighten was measured and found not to discriminate, and it can be asked
/// again over a wider population. A signal that could not be judged was never measured, and a
/// record that folded the two together would let months of an empty store read as months of
/// candidates failing.
/// </summary>
public sealed record SignalVerdict(
    string Signal,
    string Outcome,
    double? TightnessBefore,
    double? TightnessAfter,
    double? Correlation,
    string? CorrelatedWith,
    string? Because)
{
    public const string AdmittedOutcome = "admitted";
    public const string RejectedOutcome = "rejected_correlation";
    public const string NotTightenedOutcome = "not_tightened";
    public const string UndecidedOutcome = "undecided";

    public static SignalVerdict Admitted(string signal, double before, double after, double correlation, string? against) =>
        new(signal, AdmittedOutcome, before, after, correlation, against, null);

    public static SignalVerdict NotTightened(string signal, double before, double after, double correlation, string? against) =>
        new(signal, NotTightenedOutcome, before, after, correlation, against,
            $"the outcome-similar ratio was {Figure(before)} without it and {Figure(after)} with it, so it "
            + "does not pull outcome-similar setups closer together");

    public static SignalVerdict Rejected(string signal, double correlation, string against) =>
        new(signal, RejectedOutcome, null, null, correlation, against,
            $"it correlates {Figure(correlation)} with {against}, above the limit of "
            + $"{SignalAdmission.CorrelationLimit.ToString("0.00", CultureInfo.InvariantCulture)}");

    public static SignalVerdict Undecided(string signal, string because) =>
        new(signal, UndecidedOutcome, null, null, null, null, because);

    /// <summary>The status this verdict writes onto the library row, which is the specification's status wherever the verdict is not a rejection.</summary>
    public string? StatusOrNull => Outcome switch
    {
        AdmittedOutcome => SignalStatus.Active,
        RejectedOutcome => SignalStatus.RejectedCorrelation,
        _ => null,
    };

    private static string Figure(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
