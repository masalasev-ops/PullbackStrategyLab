using System.Text.Json;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Subjects with an elapsed horizon, which the captured fixture cannot supply on its own.
///
/// <b>Why it is owed.</b> The fixture holds one night and its as-of is the last session in the
/// window, so no horizon has elapsed and the nightly fill honestly writes nothing. A stage whose
/// only exercise is a run producing nought rows is a stage nothing has tested, and this one carries
/// the sign convention, the horizons and the holiday handling.
///
/// <b>Authored in one respect only.</b> The bars are the fixture's own; what is authored is which
/// session a subject sits on and which way it was taken. Same shape as the gate cases and the
/// geometry cases, and for the same reason.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public static class ForwardCases
{
    public const string FileName = "forward-cases.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One subject: a session, a name and a direction.</summary>
    public sealed record ForwardCase(
        string Name, string Ticker, string AsOf, string Direction, string AverageTrueRange, string Why)
    {
        public DateOnly Date => DateOnly.ParseExact(AsOf, "yyyy-MM-dd");

        public bool IsLong => string.Equals(Direction, "long", StringComparison.Ordinal);

        public string Id => $"forward.{Name}";
    }

    private sealed record CaseFile(string Tier, string ObservedBefore, IReadOnlyList<ForwardCase> Cases);

    private static CaseFile Read() =>
        JsonSerializer.Deserialize<CaseFile>(
            File.ReadAllText(System.IO.Path.Combine(RepositoryLayout.Root, "fixtures", FileName)), Json)
        ?? throw new InvalidOperationException($"{FileName} did not parse into a case file.");

    public static string Tier => Read().Tier;

    public static IReadOnlyList<ForwardCase> All => Read().Cases;

    /// <summary>
    /// The instant these cases read as of, declared in the case file so both implementations take
    /// the same one.
    ///
    /// Everything the fixture holds was observed by then except the row planted after it, and that
    /// row is the subject of the point-in-time check rather than evidence about any outcome.
    /// </summary>
    public static string ObservedBefore => Read().ObservedBefore;

    /// <summary>
    /// One case's path, on the adjusted basis, from its own session forward to the end of what the
    /// fixture holds.
    ///
    /// Adjusted throughout for the reason the whole corpus keeps repeating: a return read across a
    /// split on the raw basis is a collapse, and it is a plausible-looking one.
    /// </summary>
    public static IReadOnlyList<ForwardOutcome.Bar> Path(SqliteConnection connection, ForwardCase forwardCase)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(forwardCase);

        var path = new List<ForwardOutcome.Bar>();

        // Bounded on the fixture's own as-of, and the bound is not decoration.
        //
        // The fixture carries a deliberately future-dated observation: IESC's last session has a
        // second row observed the following day, which is what `point-in-time` exists to catch. An
        // unbounded read takes it and reports a return of 1.6601 where the bounded answer is
        // -0.1369, and the two differ by more than a factor of ten with nothing on the surface
        // saying which is which. This read had no bound until the independent restatement disagreed
        // with it, and it was returning the right answer only because the replay happens to write
        // that row at a later stage than this method runs.
        // see: A reader's signature does not establish point-in-time; the query does
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.bar_date, b.high, b.low, b.close, b.adj_close
              FROM daily_bar b
             WHERE b.ticker = @ticker
               AND b.bar_date >= @from
               AND b.observed_at <= @observed_before
               AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                     WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                       AND l.observed_at <= @observed_before)
             ORDER BY b.bar_date
            """;
        command.Parameters.AddWithValue("@ticker", forwardCase.Ticker);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(forwardCase.Date));
        command.Parameters.AddWithValue("@observed_before", ObservedBefore);

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            decimal close = StoreText.StorageTextToPrice(reader.GetString(3));
            decimal adjusted = StoreText.StorageTextToPrice(reader.GetString(4));
            decimal factor = close == 0m ? 1m : adjusted / close;

            path.Add(new ForwardOutcome.Bar(
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToPrice(reader.GetString(1)) * factor,
                StoreText.StorageTextToPrice(reader.GetString(2)) * factor,
                adjusted));
        }

        return path;
    }

    /// <summary>
    /// The range the case's excursions are expressed in, taken from the case rather than the store.
    ///
    /// The fixture computes indicators for its as-of night only, so a subject placed earlier in the
    /// window has no indicator row and every excursion would come back undefined, leaving half the
    /// arithmetic unexercised. Stating it in the case isolates what is under test, the excursion
    /// arithmetic, from what is not, the ATR, which has DERIVED expectations of its own at 1.6.
    /// </summary>
    public static decimal AverageTrueRange(ForwardCase forwardCase)
    {
        ArgumentNullException.ThrowIfNull(forwardCase);
        return decimal.Parse(forwardCase.AverageTrueRange, System.Globalization.CultureInfo.InvariantCulture);
    }
}
