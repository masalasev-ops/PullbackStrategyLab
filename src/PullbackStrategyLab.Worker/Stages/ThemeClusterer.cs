using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// How many same-industry names appeared on the same scan the same night.
///
/// <b>Industry, not sector.</b> They are different columns giving different answers, and the check
/// exists to distinguish an industry shift from one company's news. Sector is too coarse for that:
/// two names in the same sector routinely have nothing to do with each other, so a sector count
/// would report grouped movement on almost every busy night and mean nothing.
/// see: The cluster grouping key is industry, not sector
///
/// <b>It counts over scan hits, not over flagged setups.</b> The catalogue's "flagged together" put
/// this stage at 18:15 downstream of detectors that run at 18:20, which cannot be. SCHEMA settles it
/// by putting `cluster_count` on `scan_hit`: the count is over the names a scan surfaced, which run
/// at 18:10, and the detectors read it afterwards.
///
/// Updates `cluster_count` and nothing else, which is what SCHEMA declares. A name whose industry
/// has never been resolved is left null rather than counted as its own cluster of one.
/// </summary>
public sealed class ThemeClusterer
{
    public const string Name = "clusters";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ThemeClusterer(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        ClusterResult result = Count(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Hits} hit(s), {result.WithIndustry} with a resolved industry");
        Console.WriteLine($"{Name}: {result.Counted} counted, {result.Clustered} in a cluster of two or more, {result.Industries} industry group(s)");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public ClusterResult Count(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.BeginUpdatingInPlace(connection, Name, "scan_hit");

        // One row per hit, with the name's industry beside it. Names with no industry are returned
        // too, so the stage can say how many it could not group rather than reporting a smaller
        // total as though it had grouped everything.
        //
        // The industry is bounded on when it was resolved, not merely read. `security` carries the
        // attributes as they stand today with one instant saying when the lookup was made, so a
        // rerun of an old night would otherwise group it by industries nobody knew at the time and
        // produce a different cluster count for the same evening.
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = """
            SELECT h.ticker, h.scan,
                   CASE WHEN s.sector_resolved_at IS NOT NULL AND s.sector_resolved_at <= @resolved_before
                        THEN s.industry END
              FROM scan_hit h
              JOIN security s ON s.ticker = h.ticker
             WHERE h.as_of = @as_of
               AND (h.observed_at <= @observed_before OR (h.observed_at IS NULL AND h.as_of = @as_of))
            """;
        read.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        read.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, _options.SessionZone));
        read.Parameters.AddWithValue("@resolved_before", StoreText.EndOfSession(asOf, _options.SessionZone));

        var hits = new List<(string Ticker, string Scan, string? Industry)>();
        using (SqliteDataReader reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                hits.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        // Grouped by scan as well as by industry. Two names in the same industry, one on the gainer
        // scan and one on the decliner scan, are not moving together in the sense the check means:
        // that is the industry splitting rather than shifting.
        var counts = hits
            .Where(h => h.Industry is not null)
            .GroupBy(h => (h.Scan, h.Industry), StringTupleComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Count(), StringTupleComparer.Instance);

        int counted = 0;
        int clustered = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach ((string ticker, string scan, string? industry) in hits)
            {
                if (industry is null)
                {
                    continue;
                }

                int count = counts[(scan, industry)];
                counted += Update(connection, transaction, ticker, asOf, scan, count);

                if (count >= 2)
                {
                    clustered++;
                }
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new ClusterResult(
            asOf, hits.Count, hits.Count(h => h.Industry is not null), counted, clustered,
            counts.Count, summary.RowsWritten, RunOutcome.Clean);
    }

    private static int Update(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ticker,
        DateOnly asOf,
        string scan,
        int count)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE scan_hit SET cluster_count = @count
             WHERE ticker = @ticker AND as_of = @as_of AND scan = @scan
            """;

        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@scan", scan);

        return command.ExecuteNonQuery();
    }

    /// <summary>Ordinal comparison for the (scan, industry) key, so grouping does not depend on culture.</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string Scan, string? Industry)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string Scan, string? Industry) x, (string Scan, string? Industry) y) =>
            string.Equals(x.Scan, y.Scan, StringComparison.Ordinal)
            && string.Equals(x.Industry, y.Industry, StringComparison.Ordinal);

        public int GetHashCode((string Scan, string? Industry) obj) =>
            HashCode.Combine(obj.Scan, obj.Industry);
    }
}

/// <summary>What one clustering run counted.</summary>
public sealed record ClusterResult(
    DateOnly AsOf,
    int Hits,
    int WithIndustry,
    int Counted,
    int Clustered,
    int Industries,
    int RowsWritten,
    RunOutcome Outcome);
