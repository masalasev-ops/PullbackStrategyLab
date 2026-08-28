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
}
