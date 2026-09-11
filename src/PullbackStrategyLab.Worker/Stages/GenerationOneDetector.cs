using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Generation 1's two gate lists run over one night's universe, writing nothing, from 7.5.
///
/// <b>Built and proved long before it is live, and registered nowhere.</b> Generation 1 registers once,
/// at 7.11, when its execution rule exists and has been measured, and that is when this becomes the
/// detector that writes `setup` and generation 0's moves beside it. Until then it runs over the golden
/// fixture and over a store copy, and what it produces is a count per clause and the verdicts it gave,
/// held in memory. Nothing in the nightly schedule calls it and no verb dispatches it.
/// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
///
/// <b>It reads what generation 0's detectors read, through their own evidence methods</b>, and adds the
/// figures generation 1's new clauses need: the three averages, the session's own bar, the weekly
/// closes and the per-level distances. A clause both generations share is generation 0's check called,
/// so a name the two disagree about is one whose rule changed.
/// </summary>
public sealed class GenerationOneDetector
{
    private readonly StoreConnectionFactory _connections;
    private readonly PullbackStrategyLabOptions _options;

    public GenerationOneDetector(StoreConnectionFactory connections, IOptions<PullbackStrategyLabOptions> options)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>One night, both sides, over the universe the night's snapshot holds.</summary>
    public GenerationOneNight Evaluate(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyList<string> members = UniverseSnapshotReader.Members(connection, asOf);
        var source = new StoredFigures(connection, _options.SessionZone);

        var longs = new List<GenerationOneVerdict>();
        var shorts = new List<GenerationOneVerdict>();

        foreach (string ticker in members)
        {
            IReadOnlyList<StoredDailyBar> bars =
                DailyBarReader.Read(connection, ticker, asOf, LongSetupDetector.HistorySessions, _options.SessionZone);

            LongPullbackRules.LongEvidence? longEvidence = LongSetupDetector.Evidence(ticker, asOf, bars, source);
            ShortPullbackRules.ShortEvidence? shortEvidence = ShortSetupDetector.Evidence(ticker, asOf, bars, source);

            if (longEvidence is null && shortEvidence is null)
            {
                continue;
            }

            IReadOnlyList<StoredDailyBar> extended =
                DailyBarReader.Read(connection, ticker, asOf, SourcedForms.ExtendedHistorySessions, _options.SessionZone);

            GenerationOneFigures figures = Figures(ticker, asOf, bars, extended, source, shortEvidence);

            if (longEvidence is not null)
            {
                longs.Add(new GenerationOneVerdict(ticker, GenerationOneRules.EvaluateLong(longEvidence, figures)));
            }

            if (shortEvidence is not null)
            {
                shorts.Add(new GenerationOneVerdict(ticker, GenerationOneRules.EvaluateShort(shortEvidence, figures)));
            }
        }

        return new GenerationOneNight(asOf, longs, shorts);
    }

    /// <summary>
    /// The figures generation 1 reads beyond the evidence, for one name's session, on the adjusted
    /// basis. The weekly closes come from the longer read, as the vectorizer's do, so the clause and
    /// the frozen signal recording it are one number.
    /// </summary>
    public static GenerationOneFigures Figures(
        string ticker,
        DateOnly asOf,
        IReadOnlyList<StoredDailyBar> bars,
        IReadOnlyList<StoredDailyBar> extended,
        ISessionFigures source,
        ShortPullbackRules.ShortEvidence? shortEvidence)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(extended);
        ArgumentNullException.ThrowIfNull(source);

        if (bars.Count == 0)
        {
            return new GenerationOneFigures(null, null, null, null, null, null, null, null, null);
        }

        StoredDailyBar last = bars[^1];
        decimal factor = last.Close == 0m ? 1m : last.AdjustedClose / last.Close;
        StoredIndicators? indicators = source.Indicators(ticker, asOf, bars);

        IReadOnlyList<decimal> weekly = SourcedForms.WeeklyCloses(
            [.. extended.Select(b => b.BarDate)], [.. extended.Select(b => b.AdjustedClose)]);

        decimal? dailyRange = RangeDistance.InPrice(indicators?.AverageDailyRange, last.Close);

        decimal? confluence = indicators is null
            ? null
            : SourcedForms.ConfluenceDistance(
            [
                RangeDistance.Between(last.AdjustedClose, indicators.EmaMedium, dailyRange),
                RangeDistance.Between(last.AdjustedClose, indicators.EmaLong, dailyRange),
                shortEvidence?.DistanceToAnchoredRanges,
            ]);

        return new GenerationOneFigures(
            indicators?.EmaShort,
            indicators?.EmaMedium,
            indicators?.EmaLong,
            last.High * factor,
            last.Low * factor,
            last.AdjustedClose,
            SourcedForms.DistanceFromWeeklyMedium(weekly),
            SourcedForms.WeeklySqueezeRatio(weekly),
            confluence);
    }
}

/// <summary>One name's generation 1 verdicts on one side.</summary>
public sealed record GenerationOneVerdict(string Ticker, IReadOnlyList<CheckResult> Results)
{
    public bool Recorded(bool isLong) => GenerationOneChecks.ClearsRecordingFloor(Results, isLong);

    public bool PassedAll => GenerationOneChecks.PassedAll(Results);

    public bool Passed(string check) => Results.Any(r => r.Name == check && r.Passed);
}

/// <summary>What generation 1 made of one night, per side and never pooled.</summary>
public sealed record GenerationOneNight(
    DateOnly AsOf,
    IReadOnlyList<GenerationOneVerdict> Long,
    IReadOnlyList<GenerationOneVerdict> Short)
{
    /// <summary>How many names one side examined passed one clause.</summary>
    public int PassedOn(string direction, string check) =>
        (direction == SetupDirection.Long ? Long : Short).Count(v => v.Passed(check));

    /// <summary>How many names one side would have recorded under generation 1's floor.</summary>
    public int RecordedOn(string direction) =>
        direction == SetupDirection.Long ? Long.Count(v => v.Recorded(true)) : Short.Count(v => v.Recorded(false));

    /// <summary>How many recorded names one side passed on every gating clause.</summary>
    public int CandidatesOn(string direction) =>
        direction == SetupDirection.Long
            ? Long.Count(v => v.Recorded(true) && v.PassedAll)
            : Short.Count(v => v.Recorded(false) && v.PassedAll);
}
