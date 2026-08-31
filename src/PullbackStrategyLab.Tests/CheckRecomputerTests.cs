using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Checks;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The repair, and the conditions it runs under.
///
/// An answer the session itself asked for may be attributed to it up to a stated lateness bound,
/// recorded on the row and countable. Every condition is asserted here rather than trusted, and the
/// bound is the one that matters: it is what a repair is tempted to relax, because relaxing it is
/// exactly what makes the repair work.
/// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
/// </summary>
public sealed class CheckRecomputerTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 27);

    /// <summary>
    /// When the sector walk ran on the night, which is inside the session's own day and therefore not
    /// late at all. It is deliberately not the end of that day: the bound is
    /// <c>2026-08-27T23:59:59.999Z</c> and this instant is one an answer arrives at, well before it.
    /// </summary>
    private const string OnTheNight = "2026-08-27T22:12:03.201Z";

    /// <summary>
    /// When the walk was rerun after it died on 2026-08-27, which is 00:19 Eastern on the 28th.
    /// About six hours after the walk itself and 20 minutes after the session's own end of day, and
    /// those two origins are the reason this is spelled out: only the second is lateness. Inside the
    /// bound, so an answer stamped here is admitted and recorded as late.
    /// </summary>
    private const string LateButInsideTheBound = "2026-08-28T04:19:33.201Z";

    /// <summary>Four days on, which no bound stated in hours admits.</summary>
    private const string BeyondTheBound = "2026-08-31T04:19:33.201Z";

    /// <summary>
    /// How late <see cref="LateButInsideTheBound"/> is against the session's own end of day, which is
    /// the only origin lateness is ever measured from. Measured from the failed walk instead the same
    /// arrival reads as about six hours, and that figure is not lateness.
    ///
    /// <b>It was 260 until the session boundary was corrected, and that is worth keeping here.</b>
    /// The end of the session of 2026-08-27 was being computed as <c>T23:59:59.999Z</c>, which is
    /// 19:59:59 Eastern, so an arrival at 00:19 Eastern read as four hours and twenty minutes late.
    /// Against the session's real end of day it is twenty minutes. Nothing about the arrival changed;
    /// the origin it was measured from was in the wrong zone.
    /// </summary>
    private const int MinutesLate = 20;

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
    private void Setup(string ticker, string direction, params CheckResult[] results) =>
        Setup(ticker, direction, "gainer", AsOf, results);

    /// <summary>
    /// The same, naming the hit the detector selected as its thrust.
    ///
    /// <b>Every row that reaches this stage carries one.</b> The recording floor is tradable,
    /// moves-enough, uptrend and thrust, so a name with no thrust is never written at all, and the
    /// cluster verdict this stage repairs is the count on that one hit. The seeds left both columns
    /// null until the recompute was corrected to read them, which is why the defect could not have
    /// been caught here: the store the tests built was not a store a detector could have written.
    /// </summary>
    private void Setup(string ticker, string direction, string thrustScan, DateOnly thrustSession, params CheckResult[] results)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all,
                 trigger_price, stop_price, stop_distance_ranges, thrust_scan, thrust_session)
            VALUES (@id, @d, @t, @dir, @results, 0, '10.00', '9.00', '0.5', @scan, @session)
            """;
        command.Parameters.AddWithValue("@id", $"{AsOf:yyyy-MM-dd}-{ticker}-{direction}");
        command.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@dir", direction);
        command.Parameters.AddWithValue("@scan", thrustScan);
        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(thrustSession));
        command.Parameters.AddWithValue(
            "@results",
            JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.ExecuteNonQuery();
    }

    /// <summary>One more hit for a name, on a named scan and session.</summary>
    private void Hit(string ticker, string scan, DateOnly session, int rank = 1)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO scan_hit (as_of, ticker, scan, magnitude, rank) VALUES (@d, @t, @s, '1.0', @r)";
        command.Parameters.AddWithValue("@d", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@s", scan);
        command.Parameters.AddWithValue("@r", rank);
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

    /// <summary>
    /// A repaired row can be put back the way it was, from what the repair recorded.
    ///
    /// Auditable and reversible are different properties and the mark alone gives neither: a reader
    /// can see the row was touched and cannot see what it said, and nothing can undo it. The prior
    /// text is the whole JSON rather than the one verdict, because the column it restores is the
    /// whole JSON and a partial record would need the restore to reconstruct the rest.
    /// </summary>
    [Fact]
    public void A_repaired_row_can_be_restored_from_the_state_it_was_corrected_from()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup(
            "AAA",
            "long",
            new CheckResult("trigger-near", true, 0.51m),
            new CheckResult("cluster", false, null));

        Recomputer().Recompute(AsOf, "cluster", apply: true);
        Assert.Equal(2m, Read("AAA", "long").Cluster.Value);

        string prior = PriorState("AAA") ?? throw new InvalidOperationException("no prior state was recorded.");

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE setup
                   SET check_results = corrected_from,
                       corrected_at = NULL,
                       corrected_because = NULL,
                       correction_lateness_minutes = NULL,
                       corrected_from = NULL
                 WHERE ticker = 'AAA'
                """;
            command.ExecuteNonQuery();
        }

        // Back to the night's own record, verdict and all, with nothing left saying it was ever
        // corrected. Both halves matter: a restore that left the mark behind would report a
        // correction that no longer exists.
        (CheckResult cluster, string? correctedAt, _) = Read("AAA", "long");
        Assert.Null(cluster.Value);
        Assert.False(cluster.Passed);
        Assert.Null(correctedAt);
        Assert.Null(Lateness("AAA"));

        // And the verdict the correction never touched came back with it.
        Assert.Contains("trigger-near", prior, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exception is one column wide, asserted against the stage's own source.
    ///
    /// The lateness bound admits exactly one stamped column, <c>security.sector_resolved_at</c>, and
    /// every other input stays bounded to the session's own date. A repair that admitted a second
    /// late input would be reconstructing the night rather than completing it, and the difference
    /// between those two is the whole rule.
    ///
    /// <b>What this cannot say, stated rather than left to be assumed.</b> `scan_hit` carries no
    /// observation stamp at all, so a hit inserted for a past session after the fact is invisible to
    /// any bound, including this one. That is not a hole this exemption opened and it is one a
    /// reader of this test would otherwise assume closed, so it is carried as an obligation rather
    /// than implied to be covered.
    /// </summary>
    [Fact]
    public void Exactly_one_stamped_column_is_admitted_late()
    {
        string source = RepositoryLayout.Read(Path.Combine(
            RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "CheckRecomputer.cs"));

        string[] lateBound =
        [
            .. PointInTimeCheck.Stamped.Values
                .Distinct(StringComparer.Ordinal)
                .Where(stamp => source.Contains($"{stamp} <= @bound", StringComparison.Ordinal)),
        ];

        Assert.Equal(["sector_resolved_at"], lateBound);
    }

    /// <summary>The check results as they stood before the correction, or null.</summary>
    private string? PriorState(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT corrected_from FROM setup WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);
        return command.ExecuteScalar() as string;
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
    /// on 2026-08-27 and the names it never fetched were fetched at 00:19 Eastern the next morning,
    /// twenty minutes past that session's own end of day.
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
    public void Correcting_another_check_does_not_backfill_a_short_row_with_a_clause_set_it_did_not_have()
    {
        // The half of the seam that a correction could silently erase. Every short row carries the
        // clause set its `reached-ceiling` verdict actually ran, and 3.6 counts the short side's
        // twenty sessions from the first row that records the full disjunction. A recomputation
        // that rewrote the whole verdict array from a fresh detector run would stamp today's
        // clause set onto a row produced under a different one, and after 4.4 that would move the
        // seam backwards over every row anybody happened to correct.
        //
        // The recomputer replaces the one verdict it was asked for and maps the rest through
        // untouched, so the property holds by construction. This is the test that says so, and it
        // is a short row rather than the long one beside it because the clause record only exists
        // on this side.
        Night(OnTheNight, "AAA", "BBB");
        Setup(
            "AAA",
            SetupDirection.Short,
            new CheckResult("moves-enough", true, 0.06m),
            new CheckResult("reached-ceiling", true, 0.31m, ShortPullbackRules.ClausesRun),
            new CheckResult("cluster", false, null));

        Recomputer().Recompute(AsOf, "cluster", apply: true);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT check_results FROM setup WHERE ticker = 'AAA'";
        List<CheckResult> results = JsonSerializer.Deserialize<List<CheckResult>>(
            (string)command.ExecuteScalar()!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // The cluster verdict moved, which is what was asked for. The clause record did not.
        Assert.Equal(2m, results[2].Value);
        Assert.Equal(CeilingClauses.TwoOfThree, ShortPullbackRules.ClauseSetOf(results));
        Assert.Equal(
            new CheckResult("reached-ceiling", true, 0.31m, ShortPullbackRules.ClausesRun),
            results[1]);
    }

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

    /// <summary>
    /// The cluster a repaired row gets is formed over the night's whole scan population, not over
    /// the rows being repaired.
    ///
    /// <b>Why this is worth its own test.</b> If the count were taken over the repaired set, the
    /// figure would be an artefact of how many rows happened to be broken: two rows repaired
    /// together would each read two, and a row repaired alone would read one and fail. On
    /// 2026-08-27 two of the fifteen came back failing at a cluster of one, which is exactly the
    /// number that shape would produce, so the population the count was formed over is the
    /// difference between a real verdict and a self-fulfilling one.
    ///
    /// Both kinds of outsider are present. <c>BBB</c> has a setup whose verdict already carries a
    /// value, so it is not a candidate; <c>CCC</c> has no setup at all. Neither can be repaired and
    /// both must be counted, so the only count that passes is three.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    [Fact]
    public void The_cluster_is_formed_over_the_whole_scan_population_not_over_the_repaired_rows()
    {
        // Three names, one industry, one scan. Only AAA is repairable.
        Night(LateButInsideTheBound, "AAA", "BBB", "CCC");
        Setup("AAA", "long", new CheckResult("cluster", false, null));
        Setup("BBB", "long", new CheckResult("cluster", true, 3m));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        // One candidate, so the repaired set is a set of one and a count over it would be one.
        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Corrected);

        // Three. BBB is in the cluster although it was never a candidate, and CCC is in it
        // although it has no setup row at all.
        Assert.Equal(3m, Read("AAA", "long").Cluster.Value);
        Assert.True(Read("AAA", "long").Cluster.Passed);

        // And the row that was already valued is untouched, so the count did not come from
        // rewriting the population it was taken over.
        Assert.Null(Read("BBB", "long").CorrectedAt);
        Assert.Equal(3m, Read("BBB", "long").Cluster.Value);
    }

    /// <summary>
    /// A member of the cluster that is not a setup at all still counts, stated on its own because
    /// it is the strongest form of the property.
    ///
    /// A scan hit with no setup row can never be repaired by anything, so a count that includes it
    /// cannot have been formed over the repaired set under any reading. Five scan names, one
    /// setup, and the answer is five.
    /// </summary>
    [Fact]
    public void A_scan_name_with_no_setup_row_is_counted_in_the_cluster()
    {
        Night(LateButInsideTheBound, "AAA", "BBB", "CCC", "DDD", "EEE");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(5m, Read("AAA", "long").Cluster.Value);
    }

    /// <summary>
    /// The restore puts a corrected row back and clears every mark, through the stage rather than
    /// through a hand-written statement.
    ///
    /// <b>This test used to issue the UPDATE itself, and that was the defect.</b>
    /// <c>A_repaired_row_can_be_restored_from_the_state_it_was_corrected_from</c> asserted the
    /// property by performing the restore in the test, so the corpus claimed a corrected population
    /// could be put back while nothing an operator could run would do it. A property asserted only
    /// by the assertion that wants it is the shape this lab keeps meeting.
    /// </summary>
    [Fact]
    public void The_stage_restores_a_corrected_row_and_clears_every_mark()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup(
            "AAA",
            "long",
            new CheckResult("trigger-near", true, 0.51m),
            new CheckResult("cluster", false, null));

        Assert.Equal(1, Recomputer().Recompute(AsOf, "cluster", apply: true).Corrected);
        Assert.Equal(2m, Read("AAA", "long").Cluster.Value);

        RecheckResult restored = Recomputer().Restore(AsOf, "cluster", apply: true);

        Assert.Equal(1, restored.Candidates);
        Assert.Equal(1, restored.Corrected);
        Assert.Equal(0, restored.Refused);

        // The night's own record, verdict and all, with nothing left saying it was ever corrected.
        (CheckResult cluster, string? correctedAt, string? because) = Read("AAA", "long");
        Assert.Null(cluster.Value);
        Assert.False(cluster.Passed);
        Assert.Null(correctedAt);
        Assert.Null(because);
        Assert.Null(Lateness("AAA"));
        Assert.Null(PriorState("AAA"));

        // And the verdict the correction never touched came back with it.
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT check_results FROM setup WHERE ticker = 'AAA'";
        Assert.Contains("trigger-near", (string)command.ExecuteScalar()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restored row can be corrected again, which is the whole reason the restore is the supported
    /// way to redo a repair.
    ///
    /// A row is refused a second correction by design, so a correction computed against something
    /// since found wrong cannot be overwritten. It is undone and made again, and both operations
    /// leave a run entry.
    /// </summary>
    [Fact]
    public void A_restored_row_can_be_corrected_again()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        Assert.Equal(1, Recomputer().Recompute(AsOf, "cluster", apply: true).Corrected);

        // Not a candidate at all while the correction stands: its verdict now carries a number, and
        // the stage has no permission to revisit a measurement the night made. The mark is the
        // second guard, behind that one, and the test above exercises it directly.
        Assert.Equal(0, Recomputer().Recompute(AsOf, "cluster", apply: true).Candidates);

        Recomputer().Restore(AsOf, "cluster", apply: true);

        RecheckResult again = Recomputer().Recompute(AsOf, "cluster", apply: true);
        Assert.Equal(1, again.Corrected);
        Assert.Equal(0, again.Refused);
        Assert.Equal(2m, Read("AAA", "long").Cluster.Value);
        Assert.Equal(MinutesLate, Lateness("AAA"));
    }

    /// <summary>Without the flag the restore reports and writes nothing, like the repair.</summary>
    [Fact]
    public void Without_apply_the_restore_reports_and_writes_nothing()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));
        Recomputer().Recompute(AsOf, "cluster", apply: true);

        RecheckResult restored = Recomputer().Restore(AsOf, "cluster", apply: false);

        Assert.Equal(1, restored.Corrected);
        Assert.NotNull(Read("AAA", "long").CorrectedAt);
        Assert.Equal(2m, Read("AAA", "long").Cluster.Value);
    }

    /// <summary>
    /// A row marked corrected with no prior state is refused rather than guessed at.
    ///
    /// The stage writes the mark and the prior state in one statement, so it cannot produce that
    /// pair. Encountering it means something else wrote the mark, and the restore has nothing to
    /// put back.
    /// </summary>
    [Fact]
    public void A_row_marked_corrected_with_no_prior_state_is_refused()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));
        Recomputer().Recompute(AsOf, "cluster", apply: true);

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE setup SET corrected_from = NULL WHERE ticker = 'AAA'";
            command.ExecuteNonQuery();
        }

        RecheckResult restored = Recomputer().Restore(AsOf, "cluster", apply: true);

        Assert.Equal(0, restored.Corrected);
        Assert.Equal(1, restored.Refused);
        Assert.Equal("partial", restored.Outcome.ToStorageText());

        // Untouched, because a restore that cannot put the row back must not clear the mark saying
        // it was moved.
        Assert.NotNull(Read("AAA", "long").CorrectedAt);
    }

    /// <summary>Which check a row's correction was recorded against, or null where it records none.</summary>
    private string? CorrectedCheck(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT corrected_check FROM setup WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// A row corrected under a second check, written the way the store will hold one once a second
    /// check is admitted.
    ///
    /// <b>Seeded rather than recomputed, and the reason is the point of the test.</b>
    /// <c>SetupChecks.RecordedNotRequired</c> admits `cluster` and nothing else today, so the state
    /// this test is about cannot be produced by running the stage twice. The property under test is
    /// the restore's scope, not the admission list, and a defect that only appears once a list
    /// grows is one that has to be asserted before the list grows or it is discovered by the
    /// correction it destroys.
    /// </summary>
    private void CorrectedUnderAnotherCheck(string ticker, string direction, string check)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE setup
               SET check_results = @after,
                   corrected_at = '2026-08-28T04:30:00.000Z',
                   corrected_because = @because,
                   correction_lateness_minutes = 260,
                   corrected_from = @before,
                   corrected_check = @check
             WHERE ticker = @t AND direction = @dir
            """;
        // Both verdicts on the row, because a setup carries every check's and the one being
        // corrected sits among the others. A row holding only the corrected verdict would let the
        // restore look right while dropping everything beside it.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        command.Parameters.AddWithValue(
            "@after",
            JsonSerializer.Serialize(
                new[] { new CheckResult("cluster", true, 2m), new CheckResult(check, true, 3m) }, json));
        command.Parameters.AddWithValue(
            "@before",
            JsonSerializer.Serialize(
                new[] { new CheckResult("cluster", true, 2m), new CheckResult(check, false, null) }, json));
        command.Parameters.AddWithValue("@because", $"'{check}' recomputed, seeded by a test");
        command.Parameters.AddWithValue("@check", check);
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@dir", direction);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>
    /// A restore scoped to one check leaves another check's corrections standing.
    ///
    /// <b>The argument was validated and then discarded.</b> `Restore` took the check, checked it
    /// against the recomputable list in `Run`, and issued a statement bounded on the date alone, so
    /// it put back every corrected row of that date whatever check each was corrected for. Its own
    /// doc comment said "for one date and check". Harmless while `cluster` is the only admitted
    /// check, because there is only one thing a row can have been corrected for, and silently
    /// destructive on the day a second is admitted.
    ///
    /// So the case asserted here is the one that will exist rather than the one that does: two
    /// rows on one date corrected under two checks, one restored, the other read back.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    [Fact]
    public void A_restore_scoped_to_one_check_leaves_another_checks_corrections_standing()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));
        Setup("BBB", "long", new CheckResult("cluster", true, 2m));

        // The real path writes the column, so what the restore selects on is what the stage records
        // rather than something the test arranged.
        Assert.Equal(1, Recomputer().Recompute(AsOf, "cluster", apply: true).Corrected);
        Assert.Equal("cluster", CorrectedCheck("AAA"));

        CorrectedUnderAnotherCheck("BBB", "long", "thrust");

        RecheckResult restored = Recomputer().Restore(AsOf, "cluster", apply: true);

        // One candidate, not two. The other row is on the same date and is another check's.
        Assert.Equal(1, restored.Candidates);
        Assert.Equal(1, restored.Corrected);

        Assert.Null(Read("AAA", "long").CorrectedAt);
        Assert.Null(CorrectedCheck("AAA"));

        // And the row this call was never about is exactly as it was: mark, reason, lateness, prior
        // state and verdict.
        Assert.NotNull(Read("BBB", "long").CorrectedAt);
        Assert.Equal("thrust", CorrectedCheck("BBB"));
        Assert.Equal(260, Lateness("BBB"));
        Assert.NotNull(PriorState("BBB"));

        // The other direction, so what passes above is the scoping rather than the restore having
        // stopped working: asked for that check, it restores that row.
        RecheckResult other = Recomputer().Restore(AsOf, "thrust", apply: true);
        Assert.Equal(1, other.Candidates);
        Assert.Equal(1, other.Corrected);
        Assert.Null(CorrectedCheck("BBB"));
    }

    /// <summary>
    /// A row corrected before migration 033 records no check, so no scoped restore reaches it, and
    /// the stage says so rather than passing over it.
    ///
    /// The conservative direction. Backfilling the column from `corrected_because` would be correct
    /// for the fifteen rows that exist and would be this stage reading the sentence the column was
    /// added to stop anybody reading.
    /// </summary>
    [Fact]
    public void A_row_corrected_before_the_column_existed_is_reported_rather_than_swept_in()
    {
        Night(LateButInsideTheBound, "AAA", "BBB");
        Setup("AAA", "long", new CheckResult("cluster", false, null));

        Assert.Equal(1, Recomputer().Recompute(AsOf, "cluster", apply: true).Corrected);

        // The state migration 033 leaves behind: a mark, a prior state, and no check.
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE setup SET corrected_check = NULL WHERE ticker = 'AAA'";
            command.ExecuteNonQuery();
        }

        RecheckResult restored = Recomputer().Restore(AsOf, "cluster", apply: true);

        Assert.Equal(0, restored.Candidates);
        Assert.Equal(0, restored.Corrected);

        // Left exactly as it was rather than restored on a guess.
        Assert.NotNull(Read("AAA", "long").CorrectedAt);
        Assert.NotNull(PriorState("AAA"));
    }

    /// <summary>
    /// Every ordering of the arguments parses to the same command, and no flag's value can be
    /// taken as the date.
    ///
    /// <b>The date was whatever argument was neither a flag nor the check's own name.</b>
    /// `--check`'s value was excluded by naming it and `--expect`'s was not, so
    /// <c>recheck --check cluster --expect 15 2026-08-27</c> read <c>15</c> as the date and died on
    /// the format. Loud, and never a correctness fault; what it was is a command with one working
    /// ordering and nothing saying which one. The RUNBOOK documents
    /// <c>recheck &lt;date&gt; --check cluster</c>, which is the ordering that happened to work.
    ///
    /// Asserted across the orderings rather than on the one that failed, because a fix that named
    /// `--expect` too would pass a test written only about `--expect`.
    /// </summary>
    [Theory]
    [InlineData("2026-08-27", "--check", "cluster", "--expect", "15", "--apply")]
    [InlineData("--check", "cluster", "--expect", "15", "2026-08-27", "--apply")]
    [InlineData("--expect", "15", "--check", "cluster", "--apply", "2026-08-27")]
    [InlineData("--apply", "--expect", "15", "2026-08-27", "--check", "cluster")]
    [InlineData("--check", "cluster", "--as-of", "2026-08-27", "--expect", "15", "--apply")]
    [InlineData("--as-of", "2026-08-27", "--expect", "15", "--apply", "--check", "cluster")]
    public void Every_documented_ordering_parses_to_the_same_command(params string[] args)
    {
        CheckRecomputer.Arguments parsed = CheckRecomputer.Arguments.Parse(args);

        Assert.Equal("cluster", parsed.Check);
        Assert.Equal(new DateOnly(2026, 8, 27), parsed.AsOf);
        Assert.Equal(15, parsed.Expect);
        Assert.True(parsed.Applying);
        Assert.False(parsed.Restoring);
    }

    /// <summary>
    /// The parse refuses what it cannot read rather than defaulting, and the refusals are the ones
    /// that would otherwise run a repair against the wrong night.
    ///
    /// An unknown option is refused by name, which is what stops a flag added later from
    /// reintroducing the fault: it cannot be added without declaring whether it takes a value.
    /// </summary>
    [Fact]
    public void The_parse_refuses_what_it_cannot_read()
    {
        // A flag nobody declared. Refused rather than ignored, and rather than assumed to take a
        // value, which is how a new boolean would swallow the date.
        Assert.Contains(
            "is not an option this stage knows",
            Assert.Throws<ArgumentException>(
                () => CheckRecomputer.Arguments.Parse(["--dry-run", "2026-08-27"])).Message,
            StringComparison.Ordinal);

        // Two dates, which cannot both be meant.
        Assert.Contains(
            "both given as the date",
            Assert.Throws<ArgumentException>(
                () => CheckRecomputer.Arguments.Parse(["2026-08-27", "2026-08-28"])).Message,
            StringComparison.Ordinal);

        // The named and the positional form disagreeing.
        Assert.Contains(
            "given twice and the two disagree",
            Assert.Throws<ArgumentException>(
                () => CheckRecomputer.Arguments.Parse(["2026-08-27", "--as-of", "2026-08-28"])).Message,
            StringComparison.Ordinal);

        // A flag at the end with nothing after it, which used to leave the check null and read as
        // "no check given".
        Assert.Contains(
            "needs a value after it",
            Assert.Throws<ArgumentException>(
                () => CheckRecomputer.Arguments.Parse(["--check"])).Message,
            StringComparison.Ordinal);

        // A date that is not one, named as a date rather than as whatever it looked like.
        Assert.Contains(
            "is not a date",
            Assert.Throws<ArgumentException>(
                () => CheckRecomputer.Arguments.Parse(["--as-of", "15"])).Message,
            StringComparison.Ordinal);

        // And the same two forms agreeing is not an error.
        Assert.Equal(
            new DateOnly(2026, 8, 27),
            CheckRecomputer.Arguments.Parse(["2026-08-27", "--as-of", "2026-08-27"]).AsOf);
    }

    /// <summary>
    /// The command refuses a mis-parsed line rather than running against today, end to end through
    /// <c>Run</c> rather than through the parser alone.
    /// </summary>
    [Fact]
    public void The_command_exits_two_on_an_argument_it_cannot_read()
    {
        Assert.Equal(2, Recomputer().Run(["--check", "cluster", "--dry-run"]));
        Assert.Equal(2, Recomputer().Run(["--check", "cluster", "--as-of", "the-27th"]));
    }
    /// <summary>
    /// The repaired value is the count behind the hit the detector chose, not the largest count the
    /// name has anywhere.
    ///
    /// <b>Written against the case the live store already held.</b> On 2026-08-27 two of the fifteen
    /// rows the repair corrected carry a number the detector's rule cannot produce: PATH's thrust was
    /// a `leader` hit whose industry counted 6, and 13 was written from the `gainer` side; PURR's was
    /// a `gainer` hit counting 3, and 4 was written. The verdicts did not move, because the
    /// threshold is 2 and both numbers clear it, and that is the only reason this was a wrong value
    /// rather than a wrong gate: a name whose thrust scan counts 1 while another scan counts 2 is
    /// promoted from fail to pass by the same arithmetic. A maximum is never smaller than the value
    /// it replaces, so the direction of the error is always towards passing, which is the direction
    /// a repair of an already-recorded verdict must never have.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    [Fact]
    public void The_repaired_count_is_the_thrusts_own_scan_rather_than_the_largest_the_name_carries()
    {
        // Three names in one industry on `gainer`, two of them also on `leader`. The gainer side
        // counts three, the leader side two.
        Night("2026-08-27T21:00:00.000Z", "AAA", "BBB", "CCC");
        Hit("AAA", "leader", AsOf);
        Hit("BBB", "leader", AsOf);

        // AAA's detector took the `leader` hit, so its verdict was decided on two and not on three.
        Setup("AAA", "long", "leader", AsOf, new CheckResult("cluster", false, null, null));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(1, result.Corrected);
        Assert.Equal(2, Read("AAA", "long").Cluster.Value);
    }

    /// <summary>
    /// A row whose detector recorded no thrust is refused, naming that rather than the sector.
    ///
    /// It cannot arise from a detector, because thrust is on the recording floor, and it is asserted
    /// anyway: the recompute keys on two columns that are nullable in the schema, and reading a null
    /// key as "no industry" would have produced the sector message for a row whose sector is fine.
    /// </summary>
    [Fact]
    public void A_row_that_names_no_thrust_is_refused_rather_than_recomputed()
    {
        Night("2026-08-27T21:00:00.000Z", "AAA");

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO setup
                    (setup_id, as_of, ticker, direction, check_results, passed_all,
                     trigger_price, stop_price, stop_distance_ranges)
                VALUES (@id, @d, 'AAA', 'long', @results, 0, '10.00', '9.00', '0.5')
                """;
            command.Parameters.AddWithValue("@id", $"{AsOf:yyyy-MM-dd}-AAA-long");
            command.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
            command.Parameters.AddWithValue(
                "@results",
                JsonSerializer.Serialize(
                    new[] { new CheckResult("cluster", false, null, null) },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.ExecuteNonQuery();
        }

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(0, result.Corrected);
        Assert.Equal(1, result.Refused);
        Assert.Null(Read("AAA", "long").Cluster.Value);
    }

    /// <summary>
    /// A thrust on an earlier session is counted over that session, not over the setup's own.
    ///
    /// The window looks back twenty sessions, so the two dates differ on most rows. Reading the
    /// as-of would take a count over a night the verdict was never computed from.
    /// </summary>
    [Fact]
    public void A_thrust_on_an_earlier_session_is_counted_over_that_session()
    {
        DateOnly earlier = AsOf.AddDays(-3);

        Night("2026-08-27T21:00:00.000Z", "AAA", "BBB", "CCC");
        Hit("AAA", "gainer", earlier);
        Hit("BBB", "gainer", earlier);

        Setup("AAA", "long", "gainer", earlier, new CheckResult("cluster", false, null, null));

        RecheckResult result = Recomputer().Recompute(AsOf, "cluster", apply: true);

        Assert.Equal(1, result.Corrected);

        // Two on the earlier session, three on the as-of. The earlier one is the subject.
        Assert.Equal(2, Read("AAA", "long").Cluster.Value);
    }
}
