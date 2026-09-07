using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The versioned evidence pack, and the two properties the phase report claims of it.
///
/// <b>Byte-stability is the claim, and it is the thinnest one this phase makes.</b> An almost-empty
/// pack is easier to make byte-identical than a full one, so a green here proves less than it will
/// when outcomes close. The tests are written to say so rather than to let a pass stand for more
/// than it holds: the stability assertions run over a populated pack as well as an empty one, and
/// the empty case is named as the weaker of the two.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
///
/// <b>Every population is authored, on the same terms 6.2's and 6.3's were.</b> No setup's ten-day
/// horizon has closed in the live store, so a pack built over captured rows would exercise the five
/// outcome-dependent sections not at all.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class ContextPackerTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 6, 22, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Flagged = new(2026, 8, 20);
    private static readonly DateOnly Today = new(2026, 9, 6);

    private const string First = "retrace_depth";
    private const string Second = "pullback_bars";

    public ContextPackerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private ContextPacker Packer() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    private SignalAdmissionTest Admitter() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    // ---- the deliverable ----------------------------------------------------------------------

    [Fact]
    public void The_first_section_is_the_rule_in_force_and_states_every_threshold_per_side()
    {
        // The value a proposal moves from, which no section carried until 6.5. A model asked for a
        // proposal against a pack without it abstained twice, correctly, and said so.
        // see: The rule in force is the pack's first section, because a proposal moves a threshold from a value
        Admitter().Admit(Today);

        RenderedSection rule = Packer().Build(Today).Pack.Sections[0];

        Assert.Equal("Rule in force", rule.Name);

        // Every threshold on both sides, named with the gate and the family that own it, because a
        // proposal naming an execution threshold is refused for a reason the model can read here.
        foreach (SelectionRule side in (SelectionRule[])[SelectionRule.Long, SelectionRule.Short])
        {
            foreach (RuleThreshold threshold in side.Thresholds)
            {
                Assert.Contains(
                    rule.Lines,
                    line => line.StartsWith($"{side.Direction} {threshold.Name} (gate {threshold.Gate}, ", StringComparison.Ordinal));
            }
        }

        // And the value a proposal has to move from, in the store's own text form so it is one
        // string on both machines.
        Assert.Contains(
            rule.Lines,
            line => line.EndsWith(
                SelectionRule.Long.Value(SelectionRule.MaximumRetrace).ToString("F6", CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
    }

    [Fact]
    public void All_ten_sections_are_rendered_and_each_carries_its_own_count()
    {
        Admitter().Admit(Today);

        PackResult result = Packer().Build(Today);

        Assert.Equal(PackSections.Declared.Count, result.Pack.Sections.Count);
        Assert.Equal(
            PackSections.Names,
            result.Pack.Sections.Select(s => s.Name).ToArray());

        // Every section renders a count, including the ones holding nothing. A section left out
        // would be indistinguishable on the page from a section over an empty population.
        string body = result.Pack.Render();
        foreach (PackSection declared in PackSections.Declared)
        {
            Assert.Contains($"## {declared.Name}\nrows: ", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_empty_section_says_which_shape_of_nothing_it_was_rather_than_showing_a_nought()
    {
        Admitter().Admit(Today);

        // Setups flagged and no horizon closed, which is the state the live store is actually in
        // and the state the five-of-nine figure describes. Without the setups, seven sections read
        // empty and this would pass for the wrong reason: a store with nothing flagged is a
        // different absence from a store whose horizons have not closed.
        SeedFlaggedOnly(SetupDirection.Long, 1);
        SeedFlaggedOnly(SetupDirection.Short, 41);

        PackResult result = Packer().Build(Today);

        // The five that rest on closed outcomes are all empty today, and each says why. A nought
        // with no sentence beside it cannot say whether the evidence refused or was absent.
        foreach (RenderedSection empty in result.Pack.Sections.Where(s => s.Count == 0))
        {
            Assert.False(string.IsNullOrWhiteSpace(empty.EmptyBecause));
            Assert.Contains($"empty: {empty.EmptyBecause}", result.Pack.Render(), StringComparison.Ordinal);
        }

        // All five here, because no upstream research stage has run in this store either. Over the
        // golden fixture the twin finder has run before the packer, so its section carries the
        // window reading and four are empty rather than five. The flag says the section's substance
        // needs closed outcomes; it does not predict how many render empty, and asserting it as
        // though it did would be a figure over a population other than the one beside it.
        Assert.Equal(
            PackSections.Declared.Where(s => s.RestsOnClosedOutcomes).Select(s => s.Name).ToArray(),
            result.Pack.Sections.Where(s => s.Count == 0).Select(s => s.Name).ToArray());
    }

    [Fact]
    public void The_multiple_comparison_section_states_signals_screened_and_never_signals_shown()
    {
        Admitter().Admit(Today);

        string body = Packer().Build(Today).Pack.Render();

        Assert.Contains("signals screened: ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("signals shown", body, StringComparison.OrdinalIgnoreCase);

        // The family-wise threshold is stated even though nothing has been measured against it,
        // because it depends on the count screened rather than on any p-value. The false-discovery
        // bar is not stated as a number, because there is none.
        Assert.Contains("family-wise threshold: 0.00", body, StringComparison.Ordinal);
        Assert.Contains(
            "false-discovery threshold: none, nothing was measured against one",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_null_control_is_planted_and_the_row_says_so()
    {
        Admitter().Admit(Today);

        PackResult result = Packer().Build(Today);

        Assert.True(result.NullControlPlanted);
        Assert.Contains(
            $"control: {SignalLibrary.NullControl}",
            result.Pack.Render(),
            StringComparison.Ordinal);
    }

    // ---- byte-stability -----------------------------------------------------------------------

    [Fact]
    public void Two_runs_at_one_commit_over_one_store_state_produce_a_byte_identical_pack()
    {
        Admitter().Admit(Today);

        PackResult first = Packer().Build(Today);

        // The clock moves between the two cuts, which is the whole point: the run row is keyed on
        // the instant, so a second cut is a second generation. If the pack body were stable only
        // because the clock stood still, this test would be asserting nothing.
        _clock.Advance(TimeSpan.FromMinutes(7));

        PackResult second = Packer().Build(Today);

        Assert.Equal(first.Pack.Render(), second.Pack.Render());
        Assert.Equal(first.BodyDigest, second.BodyDigest);
        Assert.Equal(first.BodyBytes, second.BodyBytes);

        // The same tuple, so the same version rather than a second one beside it.
        Assert.Equal(first.Version, second.Version);
        Assert.True(first.VersionIsNew);
        Assert.False(second.VersionIsNew);
    }

    /// <summary>
    /// The stability claim over a pack with content in it, which is the half the empty case cannot
    /// reach.
    ///
    /// An almost-empty pack is byte-stable almost for free: there are no orderings to get wrong
    /// because there is nothing to order. This authors enough rows for the population, conditional
    /// and library sections to hold lines, so the sorts and the number formatting are exercised.
    /// </summary>
    [Fact]
    public void A_populated_pack_is_byte_stable_too_which_the_empty_one_could_not_have_shown()
    {
        Admitter().Admit(Today);

        for (int i = 0; i < 12; i++)
        {
            Seed(SetupDirection.Long, i, outcome: i * 1.5, first: i * 0.25, second: 12 - i);
            Seed(SetupDirection.Short, 40 + i, outcome: i * -0.75, first: i * 0.5, second: i);
        }

        PackResult first = Packer().Build(Today);
        _clock.Advance(TimeSpan.FromMinutes(7));
        PackResult second = Packer().Build(Today);

        Assert.Equal(first.Pack.Render(), second.Pack.Render());
        Assert.Equal(first.BodyDigest, second.BodyDigest);

        // The pack really did have content, so the equality above is a statement about orderings
        // rather than about two empty documents.
        Assert.Contains("Signal conditionals", first.Pack.Render(), StringComparison.Ordinal);
        Assert.True(first.Pack.Sections.Single(s => s.Name == "Signal conditionals").Count > 0);
        Assert.Equal(12, first.LongSetups);
        Assert.Equal(12, first.ShortSetups);
    }

    [Fact]
    public void The_pack_body_carries_no_instant_of_generation()
    {
        Admitter().Admit(Today);

        string body = Packer().Build(Today).Pack.Render();

        // The as-of is an input and belongs in the body. The instant the pack was cut is not, and
        // it is the one thing that would differ between two runs of one commit.
        Assert.Contains("as of: 2026-09-06", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            _clock.UtcNow.Year.ToString(CultureInfo.InvariantCulture) + "-09-06T",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Line_endings_are_LF_rather_than_the_platform_s()
    {
        Admitter().Admit(Today);

        string body = Packer().Build(Today).Pack.Render();

        // A pack rendered through Environment.NewLine would be stable on each machine and different
        // between them, which is the shape that passes every test run on one platform.
        Assert.DoesNotContain('\r', body);
        Assert.Contains('\n', body);
    }

    // ---- the version tuple --------------------------------------------------------------------

    [Fact]
    public void A_pack_cut_on_two_dates_over_different_evidence_is_one_version()
    {
        Admitter().Admit(Today);

        PackResult first = Packer().Build(Today);

        Seed(SetupDirection.Long, 1, outcome: 4.0, first: 0.5, second: 3);

        PackResult second = Packer().Build(Today.AddDays(7));

        // The evidence moved and the version did not, which is the whole point of holding the
        // version fixed while the evidence accumulates.
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.BodyDigest, second.BodyDigest);
    }

    [Fact]
    public void The_realised_false_discovery_bar_moves_with_the_evidence_and_stays_out_of_the_fingerprint()
    {
        // The bar depends on p-values and the family-wise threshold does not, which is why one is
        // in the tuple and the other is not. Asserted on the arithmetic rather than through the
        // stage, because the lab has measured no claim and cannot produce a p-value yet.
        CorrectionReading quiet = MultipleComparison.Read(33, []);
        CorrectionReading measured = MultipleComparison.Read(33, [0.0001, 0.02, 0.4]);

        Assert.Null(quiet.FalseDiscoveryThreshold);
        Assert.NotNull(measured.FalseDiscoveryThreshold);
        Assert.Equal(quiet.FamilyWiseThreshold, measured.FamilyWiseThreshold);

        var tuple = new PackVersionTuple(
            PackSections.Names, ["a", "b"], MultipleComparison.Form,
            MultipleComparison.Level, quiet.FamilyWiseThreshold, PackVersions.ModelNotChosen);

        Assert.Equal(PackVersions.Fingerprint(tuple), PackVersions.Fingerprint(tuple with { }));
        Assert.DoesNotContain("falseDiscovery", PackVersions.Canonical(tuple), StringComparison.Ordinal);
    }

    [Fact]
    public void Reordering_the_signal_library_does_not_fork_the_version_and_reordering_the_sections_does()
    {
        var tuple = new PackVersionTuple(
            ["Population", "Planted null"], ["beta", "alpha"], MultipleComparison.Form,
            MultipleComparison.Level, 0.001, PackVersions.ModelNotChosen);

        // The screened set is sorted, so its order is not part of the identity.
        Assert.Equal(
            PackVersions.Fingerprint(tuple),
            PackVersions.Fingerprint(tuple with { SignalsScreened = ["alpha", "beta"] }));

        // The section order is what the model saw, so a reordered pack is a different pack.
        Assert.NotEqual(
            PackVersions.Fingerprint(tuple),
            PackVersions.Fingerprint(tuple with { Sections = ["Planted null", "Population"] }));
    }

    [Fact]
    public void Changing_the_model_forks_the_version()
    {
        var tuple = new PackVersionTuple(
            PackSections.Names, ["alpha"], MultipleComparison.Form,
            MultipleComparison.Level, 0.05, PackVersions.ModelNotChosen);

        // The model is a confounder for the phase's own success criterion, so a change to it is a
        // change of what a proposal against the pack means.
        Assert.NotEqual(
            PackVersions.Fingerprint(tuple),
            PackVersions.Fingerprint(tuple with { ModelIdentifier = "some-model" }));
    }

    // ---- the tripwire -------------------------------------------------------------------------

    [Fact]
    public void A_proposal_citing_the_planted_null_fails_its_pack_version_and_a_proposal_that_does_not_passes()
    {
        // The claim has two clauses and both are asserted: the version fails immediately, and a
        // proposal from a failed version is not admitted. Over an authored proposal, because the
        // seat that writes real ones lands at 6.5.
        Assert.True(PackVersions.CitesTheNullControl([SignalLibrary.NullControl, "retrace_depth"]));
        Assert.False(PackVersions.CitesTheNullControl(["retrace_depth", "pullback_bars"]));

        // A near-match is not a citation. Failing a version over a name that merely resembles the
        // control would be a tripwire firing at nothing.
        Assert.False(PackVersions.CitesTheNullControl(["day_of_month_squared"]));
        Assert.False(PackVersions.CitesTheNullControl([]));
    }

    // ---- a pack that cannot be built ----------------------------------------------------------

    /// <summary>
    /// A section that cannot be built refuses the whole pack rather than shortening it.
    ///
    /// <b>A pack missing a section is not a smaller pack.</b> The correction is computed over the
    /// signals screened, so a pack that dropped a section would carry a threshold for a set it did
    /// not screen and every claim against it would be judged against the wrong number. So nothing
    /// is written, no version is registered, and the night records which section failed.
    ///
    /// The failure is provoked by dropping a table a section reads, which is the cheapest way to
    /// make one genuinely unbuildable without a stub the shipped code would have to know about.
    /// </summary>
    [Fact]
    public void A_section_that_cannot_be_built_refuses_the_whole_pack_and_the_night_records_which()
    {
        Admitter().Admit(Today);
        SeedFlaggedOnly(SetupDirection.Long, 1);

        Execute("DROP TABLE ceiling_bound");

        PackResult result = Packer().Build(Today);

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.NotNull(result.RefusedBecause);
        Assert.Contains("Ceiling gap", result.RefusedBecause, StringComparison.Ordinal);

        // Nothing written: no version, and the run row says why rather than showing a short pack.
        Assert.Equal(0, RowCount("SELECT COUNT(*) FROM pack_version"));

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredPackRun? run = PackVersionReader.LatestRun(connection, Today, "America/New_York");

        Assert.NotNull(run);
        Assert.False(run.PackWasBuilt);
        Assert.Null(run.Version);
        Assert.Null(run.BodyDigest);
        Assert.Equal(0, run.SectionsRendered);
        Assert.Contains("Ceiling gap", run.RefusedBecause!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_produced_a_pack_carries_no_reason_it_did_not()
    {
        Admitter().Admit(Today);

        PackResult result = Packer().Build(Today);

        Assert.Null(result.RefusedBecause);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredPackRun? run = PackVersionReader.LatestRun(connection, Today, "America/New_York");

        // The store holds this as a biconditional, so the assertion runs in both directions rather
        // than only on the refusing side.
        Assert.NotNull(run);
        Assert.True(run.PackWasBuilt);
        Assert.Null(run.RefusedBecause);
        Assert.NotNull(run.Version);
    }

    // ---- the run row --------------------------------------------------------------------------

    [Fact]
    public void The_run_row_records_what_the_cut_held_and_the_two_populations_are_never_added()
    {
        Admitter().Admit(Today);
        Seed(SetupDirection.Long, 1, outcome: 3.0, first: 0.4, second: 2);
        Seed(SetupDirection.Long, 2, outcome: 5.0, first: 0.6, second: 3);
        Seed(SetupDirection.Short, 41, outcome: -2.0, first: 0.2, second: 5);

        PackResult result = Packer().Build(Today);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredPackRun? run = PackVersionReader.LatestRun(connection, Today, "America/New_York");

        Assert.NotNull(run);
        Assert.Equal(result.Version, run.Version);
        Assert.Equal(result.BodyDigest, run.BodyDigest);
        Assert.Equal(PackSections.Declared.Count, run.SectionsRendered);
        Assert.True(run.NullControlPlanted);

        // Two populations, stored apart, with no column for a figure over both.
        Assert.Equal(2, run.LongSetups);
        Assert.Equal(1, run.ShortSetups);
    }

    [Fact]
    public void A_second_cut_of_one_date_is_a_new_run_row_beside_the_old_and_not_a_second_version()
    {
        Admitter().Admit(Today);

        Packer().Build(Today);
        _clock.Advance(TimeSpan.FromMinutes(7));
        Packer().Build(Today);

        Assert.Equal(2, RowCount("SELECT COUNT(*) FROM pack_run"));
        Assert.Equal(1, RowCount("SELECT COUNT(*) FROM pack_version"));
    }

    // ---- seeding ------------------------------------------------------------------------------

    private void Seed(string direction, int index, double outcome, double? first, double? second) =>
        new Seeder(_connections, _clock).Seed(direction, index, outcome, first, second);

    /// <summary>
    /// A setup flagged with no closed horizon, which is every setup the live store holds.
    ///
    /// The distinction matters to one test above: a section empty because nothing was flagged and a
    /// section empty because no horizon has closed are different absences, and only the second is
    /// what "five of the nine" describes.
    /// </summary>
    private void SeedFlaggedOnly(string direction, int index) =>
        new Seeder(_connections, _clock).Seed(direction, index, outcome: null, first: null, second: null);

    private int RowCount(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>One authored setup with a closed outcome and up to two frozen signal values.</summary>
    private sealed class Seeder(StoreConnectionFactory connections, FixedClock clock)
    {
        public void Seed(string direction, int index, double? outcome, double? first, double? second)
        {
            string setupId = $"{Flagged:yyyy-MM-dd}-{direction}-{index:00}";
            string ticker = $"T{index:00}";

            Execute("""
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@ticker, @ticker, 'US', 'Common Stock', '2020-01-02')
                ON CONFLICT (ticker) DO NOTHING
                """, ("@ticker", ticker));

            Execute("""
                INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
                VALUES (@setup_id, @as_of, @ticker, @direction, '{}', 1)
                """,
                ("@setup_id", setupId),
                ("@as_of", StoreText.DateToStorageText(Flagged)),
                ("@ticker", ticker),
                ("@direction", direction));

            // A setup with no forward return is a setup whose horizon has not closed, which is
            // every setup the live store holds today.
            if (outcome is double closed)
            {
                Execute("""
                    INSERT INTO forward_return (subject_id, subject_kind, horizon_days, intended_date,
                                                actual_date, return_signed, mfe_atr, mae_atr, filled_at)
                    VALUES (@setup_id, 'setup', @horizon, @date, @date, @return, '1.0', '1.0', @filled_at)
                    """,
                    ("@setup_id", setupId),
                    ("@horizon", MeasurementParameters.ScoringHorizonSessions),
                    ("@date", StoreText.DateToStorageText(Flagged.AddDays(14))),
                    ("@return", StoreText.StatisticToStorageText(closed)),
                    ("@filled_at", StoreText.TimestampToStorageText(clock.UtcNow.AddDays(-1))));
            }

            Freeze(setupId, First, first);
            Freeze(setupId, Second, second);
        }

        private void Freeze(string setupId, string name, double? value)
        {
            if (value is not double held)
            {
                return;
            }

            Execute("""
                INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
                VALUES (@setup_id, @signal_name, @value, @computed_at)
                """,
                ("@setup_id", setupId),
                ("@signal_name", name),
                ("@value", StoreText.StatisticToStorageText(held)),
                ("@computed_at", StoreText.TimestampToStorageText(clock.UtcNow.AddDays(-1))));
        }

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = connections.OpenWrite();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;

            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }
    }
}
