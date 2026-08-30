using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// An authored run of nights whose scoring horizon has closed, driven through the real fill and the
/// real scoreboard build.
///
/// <b>Why it is owed.</b> The captured fixture holds one market day. Nothing in it can close a
/// ten-session horizon, so `forward.written` is legitimately nought, every band 1 panel is withheld,
/// and the whole of the measurement path past the flag was exercised by nothing. That is how a stage
/// binding its subject kind to a literal survived: no fixture ever reached the query that was empty.
/// The interval cases do not reach it either, because they hand authored nightly means straight to
/// `PairedInterval` and never touch `ScoreboardBuilder.Series`.
///
/// <b>Why it is a store of its own rather than rows in the replay's.</b> Inserting authored setup
/// rows into the captured store would move `calibration.setupRowsOutsideTheForwardNight`, which is
/// frozen at nought and stands for the evidence rule, and it would pool authored rows into
/// `controls.*`, `cap.*`, `journal.*`, `gallery.*` and every `check.*` sidedness figure, which are
/// stated to be the detector-written population. A figure over a mixed population is not stated at
/// all, and this is exactly the shape that produced the corpus's fifth defect.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// see: Long and short are never pooled into one figure
///
/// <b>Every figure it emits is namespaced `accumulation.` and none of them is added to a captured
/// one.</b> The captured night's counts stay exactly what they were and stay true of a one-night
/// fixture.
///
/// <b>The population is authored and deterministic.</b> Bars come from splitmix64 at a published
/// seed, so the same series is produced on both platforms and by any restatement. What is authored
/// is the market; the arithmetic applied to it is the shipped stages, unmodified.
/// </summary>
public sealed class AccumulationPopulation : IDisposable
{
    /// <summary>
    /// Enough nights for the block bootstrap, which needs twice its block length before it will say
    /// anything at all. Four above the floor, so a night lost to a weekend cannot silently take the
    /// population below it and turn every figure here into "withheld" for a reason unrelated to
    /// whatever a later session changed.
    /// </summary>
    public const int Nights = 24;

    /// <summary>
    /// Six a night on each side, so neither side is the other's leftovers and a night carries enough
    /// pairs to say how its own pairs dispersed.
    ///
    /// Two was the first figure here and it put every panel's effective count at one: with two pairs
    /// a night the design effect cannot be told from clustering, so the measurement sat in its
    /// pessimistic corner on every panel and the row count was never credited. A fixture whose every
    /// figure is at one corner exercises the arithmetic and holds none of it.
    /// </summary>
    public const int SetupsPerNightPerDirection = 6;

    private const string Observed = "2026-01-01T00:00:00.000Z";

    private static readonly DateOnly FirstNight = new(2026, 1, 5);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock;
    private readonly IOptions<PullbackStrategyLabOptions> _options;
    private readonly List<DateOnly> _sessions = [];

    public AccumulationPopulation()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();

        // Enough sessions past the last flagged night for every horizon to have closed, and a fixed
        // instant so the stored observation stamps never move between runs.
        for (DateOnly day = FirstNight; _sessions.Count < Nights + MeasurementParameters.ScoringHorizonSessions + 2;)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                _sessions.Add(day);
            }

            day = day.AddDays(1);
        }

        FillOn = _sessions[^1];
        _clock = new FixedClock(new DateTimeOffset(FillOn.Year, FillOn.Month, FillOn.Day, 22, 0, 0, TimeSpan.Zero));
        _options = Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

        Seed();
    }

    /// <summary>The session the fill and the build run for, by which every horizon has closed.</summary>
    public DateOnly FillOn { get; }

    public void Dispose() => _root.Dispose();

    /// <summary>The real fill, over both subject kinds.</summary>
    public FillResult Fill() =>
        new ForwardReturnFiller(_connections, Logger(), _clock, _options).Fill(FillOn);

    /// <summary>The real scoreboard build, over what the fill wrote.</summary>
    public ScoreboardResult Build() =>
        new ScoreboardBuilder(_connections, Logger(), _clock, _options).Build(FillOn);

    /// <summary>
    /// The same build for another date, which is what a rebuild for a past date is.
    ///
    /// Separate from the one above because the stamp bounds only ever bind on a rebuild: reading
    /// tonight, every row in the store was stamped by tonight and no bound can exclude anything. It
    /// is the second run over a past date that can see something the first could not.
    /// </summary>
    public ScoreboardResult Build(DateOnly asOf) =>
        new ScoreboardBuilder(_connections, Logger(), _clock, _options).Build(asOf);

    /// <summary>A writing connection, for a test that has to stamp a row late on purpose.</summary>
    public SqliteConnection OpenWrite() => _connections.OpenWrite();

    /// <summary>A reading connection, for a test that wants the rows a build left behind.</summary>
    public SqliteConnection OpenRead() => _connections.OpenReadOnly();

    /// <summary>One band 1 panel as the store holds it, or null where none was written.</summary>
    public Panel? Band1(string direction, string set)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT figure, low, high, n_rows, n_effective, withheld_because, n_sessions,
                   n_minimum_sessions
              FROM scoreboard
             WHERE panel = @panel AND direction = @direction
            """;
        command.Parameters.AddWithValue("@panel", $"band1.vs{char.ToUpperInvariant(set[0])}{set[1..]}");
        command.Parameters.AddWithValue("@direction", direction);

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? new Panel(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7))
            : null;
    }

    /// <summary>How many outcome rows exist of one subject kind.</summary>
    public int Outcomes(string subjectKind)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM forward_return WHERE subject_kind = @kind";
        command.Parameters.AddWithValue("@kind", subjectKind);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The same store with every control outcome removed, rebuilt, so the withheld reason can be
    /// read back from the producer rather than from a stub.
    ///
    /// <b>This is the state the defect actually produced</b>, and the panel's own words are what a
    /// person had to diagnose it from. Asserting the text here holds that the stage says the right
    /// thing; `surface-claims` holds that the page does not swallow it. Neither substitutes for the
    /// other, and the corpus's sixth defect is exactly the gap between them.
    /// </summary>
    public string? WithheldReasonWithNoControlOutcomes(string direction, string set)
    {
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            Execute(connection, "DELETE FROM forward_return WHERE subject_kind = 'control'");
            Execute(connection, "DELETE FROM scoreboard");
        }

        Build();

        return Band1(direction, set)?.WithheldBecause;
    }

    private RunLogger Logger() => new(_clock, _options);

    /// <summary>
    /// The authored market, its setups and their controls.
    ///
    /// <b>Setups outrun their controls on purpose.</b> A population where the two are the same makes
    /// every paired difference nought, the blocks stop differing, and the interval is withheld for a
    /// reason that has nothing to do with what is being exercised. The size of the edge is authored
    /// and is not a claim about anything.
    /// </summary>
    private void Seed()
    {
        var tickers = new List<string>();

        for (int i = 0; i < 40; i++)
        {
            tickers.Add($"ACC{i:00}");
        }

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string ticker in tickers)
        {
            Execute(connection, transaction,
                "INSERT INTO security VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2020-01-01', NULL, NULL, NULL, NULL)",
                ("@t", ticker));
        }

        // The flagged names lead the field. Which names those are is fixed by position rather than
        // drawn, so the population is the same on every run and on every machine.
        var flagged = new HashSet<string>(tickers.Take(SetupsPerNightPerDirection * 2), StringComparer.Ordinal);

        foreach (string ticker in tickers)
        {
            decimal price = 100m;
            ulong state = Hash((ulong)tickers.IndexOf(ticker) + 1UL);
            bool leads = flagged.Contains(ticker);

            foreach (DateOnly session in _sessions)
            {
                state = Next(state, out ulong drawn);

                // A move of at most one and a half points either way, plus a sixth of a point of
                // drift for the flagged names, which is the edge the paired difference should find.
                decimal move = ((decimal)(drawn % 1201UL) / 1000m) - 0.6m + (leads ? 0.07m : 0m);
                price += move;

                Execute(connection, transaction,
                    "INSERT INTO daily_bar VALUES (@t, @d, @o, @h, @l, @c, @c, 1000000, @obs)",
                    ("@t", ticker),
                    ("@d", PhaseReplay.Session(session)),
                    ("@o", Text(price - (move / 2m))),
                    ("@h", Text(price + 1m)),
                    ("@l", Text(price - 1m)),
                    ("@c", Text(price)),
                    ("@obs", Observed));

                Execute(connection, transaction,
                    """
                    INSERT INTO indicator_daily
                    VALUES (@t, @d, @obs, '1', '1', '1', '2.0', '2.0', '50000000', '2.0', 'rising')
                    """,
                    ("@t", ticker),
                    ("@d", PhaseReplay.Session(session)),
                    ("@obs", Observed));
            }
        }

        for (int night = 0; night < Nights; night++)
        {
            DateOnly session = _sessions[night];
            int index = 0;

            foreach (string direction in new[] { "long", "short" })
            {
                for (int i = 0; i < SetupsPerNightPerDirection; i++)
                {
                    string ticker = tickers[index++];
                    string setupId = $"{PhaseReplay.Session(session)}-{ticker}-{direction}";

                    Execute(connection, transaction,
                        """
                        INSERT INTO setup
                            (setup_id, as_of, ticker, direction, check_results, passed_all,
                             trigger_price, stop_price, stop_distance_ranges)
                        VALUES (@id, @d, @t, @dir, '[]', 1, '100.0', '97.0', '0.5')
                        """,
                        ("@id", setupId),
                        ("@d", PhaseReplay.Session(session)),
                        ("@t", ticker),
                        ("@dir", direction));

                    // The two sets draw different names. A population whose tight set is its loose
                    // set cannot tell the two panels apart, and a fixture that froze them equal
                    // would be blind to exactly the defect 3.3 found by hand.
                    int setOffset = 0;

                    foreach (string set in new[] { "loose", "tight" })
                    {
                        setOffset += 7;

                        for (int rank = 1; rank <= MeasurementParameters.ControlsPerSet; rank++)
                        {
                            // Controls come from the names no setup ever takes, so a control is
                            // never a flagged name, which is the property the real draw enforces.
                            string control = tickers[(SetupsPerNightPerDirection * 2)
                                + ((night + (rank * 3) + index + setOffset) % (tickers.Count - (SetupsPerNightPerDirection * 2)))];

                            Execute(connection, transaction,
                                """
                                INSERT INTO control_setup
                                    (control_id, setup_id, control_ticker, control_set,
                                     match_quality, rank, drawn_at, control_as_of)
                                VALUES (@cid, @sid, @ct, @set, '{}', @rank, @obs, @cas)
                                ON CONFLICT (control_id) DO NOTHING
                                """,
                                ("@cid", $"{setupId}-{set}-{control}"),
                                ("@sid", setupId),
                                ("@ct", control),
                                ("@set", set),
                                ("@rank", rank),
                                ("@obs", Observed),
                                // This population authors its controls on the setup's own session,
                                // for both sets. It is a population for exercising the measurement
                                // path past the flag, not the draw, and the cross-session tight set
                                // is asserted where it is built rather than here.
                                ("@cas", PhaseReplay.Session(session)));
                        }
                    }
                }
            }
        }

        transaction.Commit();
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static ulong Hash(ulong value) => value * 0x9E3779B97F4A7C15UL;

    private static ulong Next(ulong state, out ulong value)
    {
        state += 0x9E3779B97F4A7C15UL;

        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        value = z ^ (z >> 31);

        return state;
    }

    private static void Execute(SqliteConnection connection, string sql) =>
        Execute(connection, null, sql);

    private static void Execute(
        SqliteConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        Execute(connection, null, sql, parameters);

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>One band 1 panel as the store holds it.</summary>
    public sealed record Panel(
        string Figure, string? Low, string? High, int Rows, int? Effective, string? WithheldBecause,
        int? Sessions = null, int? MinimumSessions = null);
}
