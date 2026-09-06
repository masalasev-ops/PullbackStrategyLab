using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Measurement;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The setups of one side whose scoring-horizon outcome the lab could have had by a date, with the
/// numeric signals every one of them carries.
///
/// <b>One implementation, because two stages ask the same question of the store.</b>
/// SignalAdmissionTest asks whether adding a candidate pulls outcome-similar setups closer
/// together, and TwinPairFinder asks which two setups are close and ended far apart. Both need the
/// same population and the same columns, and two copies of the assembly would put the admission
/// test and the twin finder on populations that could differ by a bounding clause nobody compared.
///
/// <b>A signal is in the space only where every row of the population carries it as a number.</b>
/// The library holds words as well as numbers, and `regime_label`, `industry`, `thrust_scan` and
/// `ladder_grade` are categories rather than quantities: a distance over them would be a distance
/// over whatever their text happened to parse as. A signal missing on some rows is excluded for the
/// same reason, because filling the gap with nought would put every row that has no value at the
/// middle of the distribution and call that a measurement.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
///
/// <b>One side at a time and never both.</b> `forward_return.return_signed` is signed by direction,
/// so a long that rose and a short that fell read the same way, and a population holding both would
/// be two populations under one name.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class ScoredSetupReader
{
    /// <summary>
    /// One side's scored population as of <paramref name="asOf"/>, oldest session first, with each
    /// setup's numeric signals.
    ///
    /// The rows come back in session order with the setup id as the tiebreak, so a caller taking a
    /// trailing window takes the same window on both machines and a rerun produces the same answer.
    /// </summary>
    public static IReadOnlyList<ScoredSetup> Read(
        SqliteConnection connection, string direction, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        IReadOnlyDictionary<string, (DateOnly Session, double Outcome)> scored =
            Outcomes(connection, direction, asOf, sessionZone);

        if (scored.Count == 0)
        {
            return [];
        }

        // Read through the backfill-aware reader, which is what a research read of stored history
        // uses: a value computed later from the night's own inputs is admissible evidence about that
        // night, and bounding on the instant the arithmetic ran would make every backfilled signal
        // invisible to the research that asked for it.
        // see: A backfilled signal is read by a replay and not by a surface, because point-in-time is a property of the inputs
        var values = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        foreach (DateOnly session in scored.Values.Select(v => v.Session).Distinct().Order())
        {
            foreach (StoredSetupSignal signal in SetupSignalReader.ReadIncludingBackfilled(connection, session))
            {
                if (!scored.ContainsKey(signal.SetupId)
                    || !double.TryParse(signal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    continue;
                }

                if (!values.TryGetValue(signal.SetupId, out Dictionary<string, double>? row))
                {
                    row = new Dictionary<string, double>(StringComparer.Ordinal);
                    values[signal.SetupId] = row;
                }

                row[signal.SignalName] = value;
            }
        }

        return
        [
            .. scored
                .Select(s => new ScoredSetup(
                    s.Key,
                    s.Value.Session,
                    s.Value.Outcome,
                    values.TryGetValue(s.Key, out Dictionary<string, double>? row)
                        ? row
                        : new Dictionary<string, double>(StringComparer.Ordinal)))
                .OrderBy(s => s.Session)
                .ThenBy(s => s.SetupId, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The signal names every setup in <paramref name="population"/> carries as a number, in the
    /// library's own order.
    ///
    /// <b>The intersection rather than the union, and the order is not cosmetic.</b> A space whose
    /// axes differ from row to row is not a space, and an axis order that varied would make a
    /// distance depend on which setup the walk saw first. The library's order is the one both
    /// callers get.
    /// </summary>
    public static IReadOnlyList<string> CommonSignals(
        IReadOnlyList<ScoredSetup> population, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(population);
        ArgumentNullException.ThrowIfNull(candidates);

        return population.Count == 0
            ? []
            : [.. candidates.Where(name => population.All(s => s.Values.ContainsKey(name)))];
    }

    /// <summary>
    /// The scoring-horizon outcome of one direction's setups, with each setup's own session,
    /// bounded on `filled_at`.
    ///
    /// Bounded because a return filled tomorrow is not evidence this run could have had, and the
    /// whole point of judging stored rows is that the judgement is one a replay can reproduce.
    /// </summary>
    private static IReadOnlyDictionary<string, (DateOnly Session, double Outcome)> Outcomes(
        SqliteConnection connection, string direction, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.subject_id, s.as_of, f.return_signed
              FROM forward_return f
              JOIN setup s ON s.setup_id = f.subject_id
             WHERE f.subject_kind = 'setup'
               AND f.horizon_days = @horizon
               AND f.filled_at <= @filled_before
               AND s.direction = @direction
               AND s.as_of <= @as_of
            """;

        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@filled_before", StoreText.EndOfSession(asOf, sessionZone));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var outcomes = new Dictionary<string, (DateOnly, double)>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            outcomes[reader.GetString(0)] = (
                StoreText.StorageTextToDate(reader.GetString(1)),
                StoreText.StorageTextToStatistic(reader.GetString(2)));
        }

        return outcomes;
    }

}

/// <summary>
/// One setup with a closed outcome, its own session, and the signals it carries as numbers.
///
/// <c>Session</c> is on the row because a trailing window is taken in session order, and because a
/// figure computed over these rows states the sessions it covered rather than only the count.
/// </summary>
public sealed record ScoredSetup(
    string SetupId,
    DateOnly Session,
    double Outcome,
    IReadOnlyDictionary<string, double> Values);
