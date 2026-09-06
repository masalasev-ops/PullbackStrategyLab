namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// Setups as points, one axis per signal, standardised so a distance across them means something.
///
/// <b>One implementation, because two components take this distance and they must agree.</b>
/// SignalAdmission asks whether adding a candidate pulls outcome-similar setups closer together;
/// TwinPairFinder asks which two setups are close and ended far apart. Both are the same
/// arithmetic over the same space, and a second copy of it would put the admission test and the
/// twin finder on subtly different geometries with nothing able to say which was right.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>Standardising is what makes the distance mean anything.</b> Raw units are not comparable
/// between a percentage and a bar count, so an unstandardised distance is a distance over whichever
/// column happens to be measured in the largest numbers. A retrace depth of 0.33 and a pullback of
/// four bars are the same object described twice, and only one of them would move the answer.
/// </summary>
public static class SignalSpace
{
    /// <summary>
    /// Each column centred on its own mean and divided by its own standard deviation, over the
    /// rows given and no others.
    ///
    /// <b>The population is the argument, which is the half worth stating.</b> A z-score is a
    /// statement about a distribution, so the same setup standardised over a trailing 250 and over
    /// the whole store is two different points. Every caller passes the window it means, and none of
    /// them gets a default.
    ///
    /// A column that does not vary is left at nought rather than divided by nought. It contributes
    /// nothing to any distance, which is exactly what a constant column carries.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<double>> ZScore(IReadOnlyList<IReadOnlyList<double>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return [];
        }

        int width = rows[0].Count;
        var scaled = new List<double>[rows.Count];

        for (int r = 0; r < rows.Count; r++)
        {
            scaled[r] = new List<double>(width);
        }

        for (int c = 0; c < width; c++)
        {
            double mean = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                mean += rows[r][c];
            }

            mean /= rows.Count;

            double variance = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                double d = rows[r][c] - mean;
                variance += d * d;
            }

            double deviation = Math.Sqrt(variance / rows.Count);

            for (int r = 0; r < rows.Count; r++)
            {
                scaled[r].Add(deviation == 0 ? 0 : (rows[r][c] - mean) / deviation);
            }
        }

        return scaled;
    }

    /// <summary>Euclidean distance between two points of the same width.</summary>
    public static double Distance(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
        {
            throw new ArgumentException(
                $"{left.Count} axis/axes against {right.Count}: a distance is taken inside one space.",
                nameof(right));
        }

        double sum = 0;

        for (int i = 0; i < left.Count; i++)
        {
            double d = left[i] - right[i];
            sum += d * d;
        }

        return Math.Sqrt(sum);
    }
}
