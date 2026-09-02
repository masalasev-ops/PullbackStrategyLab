using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// A night's setups, both directions, each with every check's verdict and a small window to look at.
///
/// <b>Long and short are separate lists on the wire, not one list with a column.</b> A caller that
/// received them pooled would have to remember to split them, and a short carries a borrow
/// assumption a long does not. Two lists make the pooled version something a caller has to write out
/// rather than something they get by not thinking about it.
/// see: Long and short are never pooled into one figure
///
/// <b>Every check comes back, passed and failed alike.</b> The gallery's whole use is reading what
/// the detector decided and disagreeing with it, and a screen that showed only the failures could not
/// be disagreed with on a pass.
/// see: Failed checks are recorded rather than discarded
///
/// <b>This is the one place the read surface writes,</b> and it writes two columns of one table.
/// see: The agreement a person records is written through the read surface, and it is the only write it makes
/// </summary>
/// <remarks>
/// An instance registered with the container rather than a static class, which is what the other two
/// read-surface types are. The difference is that this one is a declared writer: SCHEMA names it, and
/// `writer-ownership` resolves a declared writer against the component catalogue, so it has to be a
/// component the catalogue holds and a component the catalogue holds has to be something the
/// container can build.
/// </remarks>
public sealed class LabSetups
{
    private readonly StoreConnectionFactory _connections;

    public LabSetups(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>Sessions in a gallery thumbnail. Enough to see the thrust and the pullback, and no more.</summary>
    public const int ThumbnailSessions = 40;

    /// <summary>The two values `agreement` accepts, and the third is clearing it.</summary>
    public static IReadOnlySet<string> Agreements { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "agree", "disagree" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One night, both directions, ordered by rank and then by ticker.
    ///
    /// Rank is null on a setup that failed a gating check, because the cap never ranked it. Those
    /// sort last and keep their place in the record: the gallery exists to be disagreed with, and the
    /// setups the detector rejected are the ones a disagreement is most likely to be about.
    /// </summary>
    public SetupsResponse Read(
        DateOnly asOf,
        DateTimeOffset observedBefore,
        string? failedCheck = null)
    {
        StoreConnectionFactory connections = _connections;
        string session = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!connections.StoreExists)
        {
            return SetupsResponse.Empty(session, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();
        IReadOnlyList<StoredSetup> stored = SetupReader.Read(connection, asOf);

        if (stored.Count == 0)
        {
            return SetupsResponse.Empty(session, $"no setups were flagged on {session}");
        }

        // The night's plans, keyed by setup, so a row can say how many shares the lab committed to.
        //
        // <b>Read here rather than per setup.</b> One statement for the night against sixty, and the
        // reader already bounds itself on the as-of it is given, so the point-in-time property is the
        // reader's rather than a second one written out at this level.
        //
        // <b>Written on this evening, not live in this session.</b> A plan is written on the evening
        // of N for session N+1, and the gallery and the watchlist are both reading the evening of N.
        // Reading by live session would return the plans written last night, which is the set every
        // row on this page is not about.
        IReadOnlyDictionary<string, int> planned = TradePlanReader
            .WrittenOn(connection, asOf, asOf)
            .ToDictionary(plan => plan.SetupId, plan => plan.Shares, StringComparer.Ordinal);

        SetupView[] all =
        [
            .. stored
                .Select(s => View(connection, s, asOf, observedBefore, planned))
                .OrderBy(s => s.Rank ?? int.MaxValue)
                .ThenBy(s => s.Ticker, StringComparer.Ordinal),
        ];

        // Filtering by a failed check is how a person asks the question the gallery is for: show me
        // the ones this gate rejected. A name nothing failed on comes back empty rather than as an
        // error, and the response says which check was asked for so an empty page can say so too.
        SetupView[] shown = failedCheck is null
            ? all
            : [.. all.Where(s => s.Checks.Any(c => !c.Passed && string.Equals(c.Name, failedCheck, StringComparison.Ordinal)))];

        return new SetupsResponse(
            session,
            failedCheck,
            all.Length,
            [.. shown.Where(s => s.Direction == SetupDirection.Long)],
            [.. shown.Where(s => s.Direction == SetupDirection.Short)],
            [.. all.SelectMany(s => s.Checks).Select(c => c.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            null);
    }

    /// <summary>
    /// Records what a person thought of one setup, or clears it.
    ///
    /// The only write the read surface makes. It touches two columns of one row and nothing else, it
    /// is driven by a person rather than by a schedule, and it never runs while the evening's job is
    /// working unless somebody is sitting at the gallery during it.
    ///
    /// Null clears the agreement, which is not the same as disagreeing. "I have not looked at this
    /// one" and "I looked and I disagree" are different facts, and the column can hold both.
    /// </summary>
    public AgreementResult RecordAgreement(string setupId, string? agreement, string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupId);
        StoreConnectionFactory connections = _connections;

        if (agreement is not null && !Agreements.Contains(agreement))
        {
            return new AgreementResult(setupId, false, $"\"{agreement}\" is not one of: {string.Join(", ", Agreements)}");
        }

        if (!connections.StoreExists)
        {
            return new AgreementResult(setupId, false, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE setup SET agreement = @agreement, agreement_note = @note
             WHERE setup_id = @setup_id
            """;
        command.Parameters.AddWithValue("@agreement", (object?)agreement ?? DBNull.Value);
        command.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("@setup_id", setupId);

        int rows = command.ExecuteNonQuery();

        return rows == 1
            ? new AgreementResult(setupId, true, null)
            : new AgreementResult(setupId, false, $"no setup with the id {setupId}");
    }

    private static SetupView View(
        SqliteConnection connection,
        StoredSetup setup,
        DateOnly asOf,
        DateTimeOffset observedBefore,
        IReadOnlyDictionary<string, int> planned)
    {
        CheckResult[] checks = JsonSerializer.Deserialize<CheckResult[]>(setup.CheckResults, Json) ?? [];

        IReadOnlyList<StoredDailyBar> bars =
            DailyBarReader.Read(connection, setup.Ticker, asOf, ThumbnailSessions, observedBefore);

        // The adjusted basis, the same crossing the chart page makes and for the same reason: the
        // store holds an adjusted close beside a raw open, high and low, and a picture that mixed
        // them shows a split as a cliff.
        var candles = new SetupCandle[bars.Count];

        for (int i = 0; i < bars.Count; i++)
        {
            StoredDailyBar bar = bars[i];
            decimal factor = bar.Close == 0m ? 1m : bar.AdjustedClose / bar.Close;

            candles[i] = new SetupCandle(
                bar.BarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                bar.Open * factor,
                bar.High * factor,
                bar.Low * factor,
                bar.AdjustedClose);
        }

        return new SetupView(
            setup.SetupId,
            setup.Ticker,
            setup.Direction,
            setup.Rank,
            setup.CappedOut,
            setup.PassedAll,
            setup.TriggerPrice,
            setup.StopPrice,
            setup.StopDistanceRanges,
            setup.Agreement,
            setup.AgreementNote,
            setup.DegradedBecause,
            planned.TryGetValue(setup.SetupId, out int shares) ? shares : null,
            [.. checks.Select(c => new SetupCheckView(
                c.Name, c.Passed, c.Value, c.Note,
                [.. c.FailedClauses.Select(f => f.Name)]))],
            candles);
    }
}

/// <summary>
/// A night as the read surface answers it. Two lists, never one.
///
/// <paramref name="Shown"/> is what survived the filter and <paramref name="Flagged"/> is what the
/// night held, so a filter that hides everything says how much it hid rather than looking like an
/// empty night.
/// </summary>
public sealed record SetupsResponse(
    string AsOf,
    string? FailedCheck,
    int Flagged,
    IReadOnlyList<SetupView> Long,
    IReadOnlyList<SetupView> Short,
    IReadOnlyList<string> CheckNames,
    string? Nothing)
{
    public static SetupsResponse Empty(string asOf, string why) => new(asOf, null, 0, [], [], [], why);
}

/// <summary>
/// One setup, with every check's verdict and the window to read it against.
///
/// <b><c>PlannedShares</c> is null on a setup no plan was written for, and that is not a defect.</b>
/// PlanBuilder refuses a candidate whose geometry is absent, whose trigger and give-up point are the
/// same price, or whose risk budget cannot buy one share at that distance, and it plans only the
/// capped set. So a null here means the lab committed to nothing on that row, which is a different
/// fact from a size of nought and is why the column is nullable rather than defaulted.
/// </summary>
public sealed record SetupView(
    string SetupId,
    string Ticker,
    string Direction,
    int? Rank,
    bool? CappedOut,
    bool PassedAll,
    decimal? TriggerPrice,
    decimal? StopPrice,
    decimal? StopDistanceRanges,
    string? Agreement,
    string? AgreementNote,
    string? DegradedBecause,
    int? PlannedShares,
    IReadOnlyList<SetupCheckView> Checks,
    IReadOnlyList<SetupCandle> Candles);

/// <summary>One check's verdict, with the number it turned on.</summary>
/// <summary>
/// One check's verdict on the wire, and the clauses it failed on where it has more than one.
///
/// <b>The failing clauses rather than every clause</b>, because that is the question the watchlist
/// asks: a greyed row wants to say which of `tradable-shortable`'s four floors it missed. The full
/// clause list with every number is in the store and reaches a threshold experiment through
/// `check_results`, which is where a distribution is computed rather than read off a screen.
/// </summary>
public sealed record SetupCheckView(
    string Name,
    bool Passed,
    decimal? Value,
    string? Note,
    IReadOnlyList<string> FailedClauses);

/// <summary>One session in a thumbnail, on the adjusted basis.</summary>
public sealed record SetupCandle(string Date, decimal Open, decimal High, decimal Low, decimal Close);

/// <summary>Whether the agreement landed, and why not when it did not.</summary>
public sealed record AgreementResult(string SetupId, bool Recorded, string? Why);
