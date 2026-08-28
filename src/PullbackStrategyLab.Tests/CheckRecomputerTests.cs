using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The repair, and the two conditions it runs under.
///
/// A setup row is corrected only where the correction uses no information the night did not have.
/// Both halves are asserted here rather than trusted, and the bound is the half that matters: it is
/// the one a repair is tempted to relax, because relaxing it is exactly what makes the repair work.
/// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
/// </summary>
public sealed class CheckRecomputerTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 27);

    /// <summary>The end of the night's own day, which is the bound every reader in the lab applies.</summary>
    private const string OnTheNight = "2026-08-27T22:12:03.201Z";

    /// <summary>
    /// Six hours on, which is when the walk was rerun after it died on 2026-08-27. Inside the
    /// lateness bound, so an answer stamped here is admitted and recorded as late.
    /// </summary>
    private const string LateButInsideTheBound = "2026-08-28T04:19:33.201Z";

    /// <summary>Four days on, which no bound stated in hours admits.</summary>
    private const string BeyondTheBound = "2026-08-31T04:19:33.201Z";

    /// <summary>How late <see cref="LateButInsideTheBound"/> is against the session's own end of day.</summary>
    private const int MinutesLate = 260;

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 28, 4, 30, 0, TimeSpan.Zero));

    public CheckRecomputerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private CheckRecomputer Recomputer()
    {
        var options = Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });
        return new CheckRecomputer(_connections, new RunLogger(_clock, options), _clock, options);
    }

    /// <summary>
    /// Two names in one industry on one scan, so the cluster count each of them should carry is two,
    /// with the industry resolved at the given instant.
    /// </summary>
    private void Night(string resolvedAt, params string[] tickers)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        foreach (string ticker in tickers)
        {
            using SqliteCommand security = connection.CreateCommand();
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen, sector, industry, sector_resolved_at)
                VALUES (@t, @t, 'US', 'Common Stock', @d, 'Technology', 'Software - Infrastructure', @r)
                """;
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
            security.Parameters.AddWithValue("@r", resolvedAt);
            security.ExecuteNonQuery();

            using SqliteCommand hit = connection.CreateCommand();
            hit.CommandText =
                "INSERT INTO scan_hit (as_of, ticker, scan, magnitude, rank) VALUES (@d, @t, 'gainer', '1.0', 1)";
            hit.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
            hit.Parameters.AddWithValue("@t", ticker);
            hit.ExecuteNonQuery();
        }
    }

    /// <summary>A setup carrying the verdicts given, exactly as a detector writes them.</summary>
    private void Setup(string ticker, string direction, params CheckResult[] results)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @d, @t, @dir, @results, 0, '10.00', '9.00', '0.5')
            """;
        command.Parameters.AddWithValue("@id", $"{AsOf:yyyy-MM-dd}-{ticker}-{direction}");
        command.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@dir", direction);
        command.Parameters.AddWithValue(
            "@results",
            JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.ExecuteNonQuery();
    }

    private (CheckResult Cluster, string? CorrectedAt, string? Because) Read(string ticker, string direction)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT check_results, corrected_at, corrected_because FROM setup WHERE ticker = @t AND direction = @dir";
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@dir", direction);

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());

        List<CheckResult> results = JsonSerializer.Deserialize<List<CheckResult>>(
            reader.GetString(0), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        return (
            results.Single(r => r.Name == "cluster"),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>The recorded lateness in minutes, or null on a row nothing corrected.</summary>
    private int? Lateness(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT correction_lateness_minutes FROM setup WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);
        object? value = command.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A row corrected once is not corrected again, which is the mark being read rather than only
    /// written.
    ///
    /// The superseded rule recorded a mark "so a later reader can exclude corrected rows" under a
    /// guard that made corrected rows impossible, so the mark had neither a producer nor a consumer.
    /// This is the consumer.
    /// </summary>
    [Fact]
    public void A_row_already_corrected_is_refused()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        Assert.Equal(1, Recomputer().Recompute(AsOf, "cluster", apply: true).Corrected);

        // The verdict now carries a number, so the row is no longer a candidate on that ground
        // alone. Blanked back to null, the mark is the only thing between it and a second
        // correction, which is what makes this an assertion about the mark.
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE setup SET check_results = @results WHERE ticker = 'AAA'";
            command.Parameters.AddWithValue(
                "@results",
                JsonSerializer.Serialize(
                    new[] { new CheckResult("cluster", false, null) },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.ExecuteNonQuery();
        }

        RecheckResult second = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(1, second.Candidates);
        Assert.Equal(0, second.Corrected);
        Assert.Equal(1, second.Refused);
    }

    /// <summary>The correction mark alone, for a row carrying no cluster verdict to read.</summary>
    private string? CorrectedAt(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT corrected_at FROM setup WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);
        return command.ExecuteScalar() as string;
    }

    /// <summary>How many runs this stage has opened, which is zero when it refused before reading.</summary>
    private int Runs()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM run_log WHERE stage = 'recheck'";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An input past the bound is refused and nothing is written.
    ///
    /// This is the half that keeps the rule a rule. The superseded form refused everything arriving
    /// after the session, which cost fifteen setups on the first night; the amended form refuses
    /// everything arriving more than the bound after it, which is a condition rather than a licence.
    /// </summary>
    [Fact]
    public void An_input_stamped_beyond_the_bound_is_refused_and_nothing_is_written()
    {
        Night(BeyondTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(0, result.Corrected);
        Assert.Equal(1, result.Refused);

        // The verdict is untouched and the row is unmarked. A row refused is not a row corrected,
        // and marking it would say a correction happened.
        (CheckResult cluster, string? correctedAt, string? because) = Read("AAA", "long");
        Assert.Null(cluster.Value);
        Assert.Null(correctedAt);
        Assert.Null(because);
        Assert.Null(Lateness("AAA"));
    }

    /// <summary>
    /// An input the session asked for, arriving inside the bound, is admitted and recorded as late.
    ///
    /// The case the amendment exists for, and exactly what happened: the sector walk died at 18:12
    /// on 2026-08-27 and the names it never fetched were fetched at 04:19 the next morning, four
    /// hours and twenty minutes past that session's own end of day.
    /// </summary>
    [Fact]
    public void An_input_inside_the_bound_is_admitted_and_its_lateness_is_recorded()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(1, result.Corrected);
        Assert.Equal(0, result.Refused);
        Assert.Equal(2m, Read("AAA", "long").Cluster.Value);

        // Countable, in minutes, so a figure resting on late answers can be summed or excluded.
        // A sentence saying the same words could do neither.
        Assert.Equal(MinutesLate, Lateness("AAA"));
    }

    /// <summary>And where the input did exist on the night, the repair goes through and is marked.</summary>
    [Fact]
    public void An_input_the_night_had_is_recomputed_and_the_row_records_that_it_was()
    {
        Night(OnTheNight, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(1, result.Corrected);
        Assert.Equal(0, result.Refused);

        (CheckResult cluster, string? correctedAt, string? because) = Read("AAA", "long");

        // Two names, one industry, one scan, so a cluster of two, which is the threshold.
        Assert.Equal(2m, cluster.Value);
        Assert.True(cluster.Passed);

        // Both marks, written together. A correction with no reason recorded is the shape the rule
        // exists to refuse, because it is indistinguishable from a plan quietly improved.
        Assert.NotNull(correctedAt);
        Assert.NotNull(because);
        Assert.Contains("cluster", because, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan is never rewritten, and the refusal happens before a row is read.
    ///
    /// Every gating check is outside this stage's permission at every date and with every input,
    /// which is what keeps the correction rule narrow enough to be safe.
    /// </summary>
    [Theory]
    [InlineData("trigger-near")]
    [InlineData("exit-tight")]
    [InlineData("uptrend")]
    [InlineData("tradable")]
    public void A_gating_check_is_refused_outright(string check)
    {
        Night(OnTheNight, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult(check, false, null));

        int exit = Recomputer().Run([AsOf.ToString("yyyy-MM-dd"), "--check", check, "--apply"]);

        Assert.Equal(2, exit);
        Assert.Null(CorrectedAt("AAA"));

        // Nothing was read either, which is what "before a row is read" means: the stage opened no
        // run at all, so there is no entry saying it considered the request.
        Assert.Equal(0, Runs());
    }

    /// <summary>Without the flag it reports and writes nothing, so the refusals can be read first.</summary>
    [Fact]
    public void Without_apply_it_reports_and_writes_nothing()
    {
        Night(OnTheNight, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: false);

        Assert.Equal(1, result.Corrected);
        Assert.False(result.Applied);
        Assert.Null(Read("AAA", "long").CorrectedAt);
    }

    /// <summary>
    /// A check that ran and produced a number is a measurement the night made, and this stage has no
    /// permission to revisit one. Only a verdict with no value at all is a candidate.
    /// </summary>
    [Fact]
    public void A_verdict_that_already_carries_a_number_is_not_a_candidate()
    {
        Night(OnTheNight, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, 1m));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(1m, Read("AAA", "long").Cluster.Value);
        Assert.Null(Read("AAA", "long").CorrectedAt);
    }

    /// <summary>
    /// The gating verdicts on a corrected row are left exactly as the night wrote them, which is the
    /// other half of the plan staying untouched: the stage rewrites one verdict in the JSON and
    /// carries the rest through unchanged.
    /// </summary>
    [Fact]
    public void Every_other_verdict_on_a_corrected_row_is_carried_through_unchanged()
    {
        Night(OnTheNight, "AAA", "BBB");
        Setup(
            "AAA",
            "long",
            new CheckResult("trigger-near", true, 0.51m),
            new CheckResult("exit-tight", false, 1.20m, "the give-up point is beyond the cap"),
            new CheckResult("cluster", false, null));

        Recomputer().Recompute(AsOf, "cluster", apply: true);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT check_results FROM setup WHERE ticker = 'AAA'";
        List<CheckResult> results = JsonSerializer.Deserialize<List<CheckResult>>(
            (string)command.ExecuteScalar()!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(3, results.Count);
        Assert.Equal(new CheckResult("trigger-near", true, 0.51m), results[0]);
        Assert.Equal(new CheckResult("exit-tight", false, 1.20m, "the give-up point is beyond the cap"), results[1]);
        Assert.Equal(2m, results[2].Value);
    }
}
