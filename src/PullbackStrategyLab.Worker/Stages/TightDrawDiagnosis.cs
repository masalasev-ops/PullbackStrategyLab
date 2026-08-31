using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Why a tight set came up short, per subject rather than totalled.
///
/// <b>A total says the set is thin. It cannot say whether it is thin everywhere or empty for most
/// subjects and full for a few, and those are different defects with different repairs.</b> The
/// first is a pool too small for anybody; the second is a dimension eliminating most subjects
/// outright while the rest match easily. `ControlSampler` counts `ShortOfFive` and that count is
/// the totalled form.
///
/// <b>It measures and changes nothing.</b> No threshold, gate, matching rule or control definition
/// is touched here, and nothing this class produces is read by any stage. It is a second reading of
/// the pool the draw was handed, taken beside the draw.
///
/// <b>One of the four tight dimensions eliminates and three do not, and the funnel says which.</b>
/// The trend ladder is an equality clause in <see cref="ControlMatching.Nearest"/> over a property
/// of the name, so a night's pool holds all three grades and a candidate on the wrong one is
/// dropped. The market mood is an equality clause over a property of the session, so within the
/// night every candidate already carries the subject's own and it drops nobody: it is held exactly
/// rather than enforced, and `WithoutMood` equalling `DistinctNames` on every row is the measured
/// form of that. Turnover and daily range are distances that order the survivors and exclude
/// nobody, so a pool size "after turnover" is the pool size before it. Turnover eliminates once and
/// earlier, as the liquidity floor on pool membership, and that is counted here as the pool it
/// produces rather than as a stage of the match.
/// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
///
/// <b>It predicts what the draw will write and the prediction is checked against what was written.</b>
/// A counting pass that re-states a filter can drift from the filter, which is the shape this corpus
/// keeps finding: the assertion says what it always said while its subject moves. So the last stage
/// of the funnel is a prediction of the drawn count, and <see cref="ReconstructedRead"/> compares it
/// against the rows `ControlSampler` wrote, per subject. A disagreement is reported as a defect in
/// this class rather than absorbed.
/// </summary>
public sealed class TightDrawDiagnosis
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<DateOnly, string?> _moods = [];

    /// <summary>Every subject observed, with the pool it faced counted at each stage.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Each observed session's market mood, reported rather than used to select a pool.</summary>
    public IReadOnlyDictionary<DateOnly, string?> Moods => _moods;

    /// <summary>
    /// One session: its subjects counted against the pool its own night offers them.
    ///
    /// <b>The night's pool and nothing before it.</b> That is the population `ControlSampler` draws
    /// both sets from, so it is the population a funnel beside the draw has to count. This class
    /// accumulated candidates across sessions for one day, while the tight draw could reach into
    /// earlier ones, and the accumulator went when the reach did.
    /// </summary>
    public void Observe(
        SqliteConnection connection,
        ISessionFigures source,
        DateOnly session,
        SubjectTables tables,
        string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tables);

        _moods[session] = source.Mood(session);

        IReadOnlyList<StoredSetup> setups = tables.IsEvidence
            ? SetupReader.Read(connection, session)
            : SetupReader.ReadCalibration(connection, session);

        var flagged = new HashSet<string>(setups.Select(s => s.Ticker), StringComparer.Ordinal);
        IReadOnlyDictionary<string, ControlMatching.Candidate> figures = source.Candidates(session, sessionZone);

        // A name flagged on the night is not a control for it. The same rule the pool itself holds,
        // and within the night there is only one night it could be asked about.
        ControlMatching.Candidate[] pool =
            [.. figures.Values.Where(c => !flagged.Contains(c.Ticker))];

        foreach (StoredSetup setup in setups)
        {
            _entries.Add(Count(setup, figures, pool));
        }
    }

    /// <summary>The pool one subject faced, stage by stage, and with each equality clause removed.</summary>
    private static Entry Count(
        StoredSetup setup,
        IReadOnlyDictionary<string, ControlMatching.Candidate> figures,
        IReadOnlyList<ControlMatching.Candidate> pool)
    {
        if (!figures.TryGetValue(setup.Ticker, out ControlMatching.Candidate? subject))
        {
            // A name with no figures on its own night cannot be matched on them, and draws nothing
            // on either set. `ControlSampler` counts this as two short sets and moves on; it is a
            // separate cause from a pool that eliminated everybody and is kept separate here.
            return new Entry(
                setup.SetupId, setup.Ticker, setup.AsOf, setup.Direction, null, null,
                NoFigures: true, PoolOnTheNight: pool.Count, PoolAfterMood: 0, PoolAfterLadder: 0,
                DistinctNames: 0, Predicted: 0, WithoutMood: 0, WithoutLadder: 0);
        }

        int Drawable(IEnumerable<ControlMatching.Candidate> candidates) => candidates.Count(
            c => !string.Equals(c.Ticker, setup.Ticker, StringComparison.Ordinal));

        // The two equality clauses, in the order `Nearest` applies them. Both compare against the
        // subject's own value, so a null mood on an unlabelled night matches the null the candidates
        // carry and the tight set is drawn as usual: within the night the label is not what does the
        // controlling, the session is.
        ControlMatching.Candidate[] afterMood =
            [.. pool.Where(c => string.Equals(c.MarketMood, subject.MarketMood, StringComparison.Ordinal))];

        ControlMatching.Candidate[] afterLadder =
            [.. afterMood.Where(c => string.Equals(c.LadderGrade, subject.LadderGrade, StringComparison.Ordinal))];

        int distinct = Drawable(afterLadder);

        return new Entry(
            setup.SetupId, setup.Ticker, setup.AsOf, setup.Direction, subject.LadderGrade, subject.MarketMood,
            NoFigures: false,
            PoolOnTheNight: pool.Count,
            PoolAfterMood: afterMood.Length,
            PoolAfterLadder: afterLadder.Length,
            DistinctNames: distinct,
            Predicted: Math.Min(distinct, MeasurementParameters.ControlsPerSet),

            // Each clause removed with the other kept, which is what names a dimension rather than
            // showing that something eliminated. Dropping the mood leaves the count unchanged, and
            // that equality is the assertion rather than a redundancy.
            WithoutMood: Drawable(
                pool.Where(c => string.Equals(c.LadderGrade, subject.LadderGrade, StringComparison.Ordinal))),
            WithoutLadder: Drawable(afterMood));
    }

    /// <summary>
    /// One subject and the pool its own night offered it.
    ///
    /// <paramref name="PoolOnTheNight"/> is every unflagged candidate on the subject's session,
    /// which is the widest set anything could be drawn from.
    /// <paramref name="WithoutMood"/> and <paramref name="WithoutLadder"/> are the drawable names
    /// with one equality clause removed and the other kept.
    /// </summary>
    public sealed record Entry(
        string SetupId,
        string Ticker,
        DateOnly AsOf,
        string Direction,
        string? LadderGrade,
        string? Mood,
        bool NoFigures,
        int PoolOnTheNight,
        int PoolAfterMood,
        int PoolAfterLadder,
        int DistinctNames,
        int Predicted,
        int WithoutMood,
        int WithoutLadder);
}
