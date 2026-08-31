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
/// <b>Two of the four tight dimensions eliminate and two only rank, and the funnel says which.</b>
/// The ladder grade and the market mood are equality clauses in <see cref="ControlMatching.Nearest"/>:
/// a candidate differing on either is dropped. Turnover and daily range are distances that order
/// the survivors and exclude nobody, so a pool size "after turnover" is the pool size before it.
/// Turnover does eliminate once, and earlier: a name below the liquidity floor never enters the
/// pool at all. That is a floor on membership rather than a matching dimension, and it is counted
/// here as the pool it produces rather than as a stage of the match.
///
/// <b>It predicts what the draw will write and the prediction is checked against what was written.</b>
/// A counting pass that re-states a filter can drift from the filter, which is the shape this corpus
/// keeps finding: the assertion says what it always said while its subject moves. So the last stage
/// of the funnel is a prediction of the drawn count, and <see cref="ReconstructedRead"/> compares it
/// against the rows `ControlSampler` wrote, per subject. A disagreement is reported as a defect in
/// this class rather than absorbed.
/// see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
/// </summary>
public sealed class TightDrawDiagnosis
{
    private const string Ungraded = "(ungraded)";
    private const string Unlabelled = "(unlabelled)";

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<DateOnly, string?> _moods = [];

    // The accumulating tight pool, sliced the three ways a subject asks about it. Maintained
    // incrementally as sessions are observed oldest first, so at any session the slices hold every
    // reach session at or before it, which is exactly the population `ControlSampler.MoodPool`
    // rebuilds per night.
    private readonly Dictionary<(string Mood, string Ladder), Slice> _byMoodAndLadder = [];
    private readonly Dictionary<string, Slice> _byMood = [];
    private readonly Dictionary<string, Slice> _byLadder = [];
    private readonly Slice _all = new();

    /// <summary>Every subject observed, with the pool it faced counted at each stage.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Each observed session's market mood, which is the dimension the pool is sliced on.</summary>
    public IReadOnlyDictionary<DateOnly, string?> Moods => _moods;

    /// <summary>
    /// One session in the reach: its candidates join the pool, then its subjects are counted
    /// against the pool as it then stands.
    ///
    /// In that order, because `MoodPool` takes sessions at or <b>before</b> the as-of, so a night's
    /// own unflagged names are available to its own tight draw.
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

        string? mood = source.Mood(session);
        _moods[session] = mood;

        IReadOnlyList<StoredSetup> setups = tables.IsEvidence
            ? SetupReader.Read(connection, session)
            : SetupReader.ReadCalibration(connection, session);

        var flagged = new HashSet<string>(setups.Select(s => s.Ticker), StringComparer.Ordinal);
        IReadOnlyDictionary<string, ControlMatching.Candidate> figures = source.Candidates(session, sessionZone);

        foreach (ControlMatching.Candidate candidate in figures.Values)
        {
            // Flagged on its own session, which is the session it would be drawn from. The same rule
            // the pool itself holds, and for the same reason: whether a name was a setup is a
            // question about the night the row comes from and not about tonight.
            if (flagged.Contains(candidate.Ticker))
            {
                continue;
            }

            string moodKey = candidate.MarketMood ?? Unlabelled;
            string ladderKey = candidate.LadderGrade ?? Ungraded;

            Add(_all, candidate.Ticker);
            Add(Slot(_byMood, moodKey), candidate.Ticker);
            Add(Slot(_byLadder, ladderKey), candidate.Ticker);
            Add(Slot(_byMoodAndLadder, (moodKey, ladderKey)), candidate.Ticker);
        }

        foreach (StoredSetup setup in setups)
        {
            _entries.Add(Count(setup, figures, mood));
        }
    }

    /// <summary>The pool one subject faced, stage by stage, and with each eliminating dimension removed.</summary>
    private Entry Count(
        StoredSetup setup, IReadOnlyDictionary<string, ControlMatching.Candidate> figures, string? sessionMood)
    {
        if (!figures.TryGetValue(setup.Ticker, out ControlMatching.Candidate? subject))
        {
            // A name with no figures on its own night cannot be matched on them, and draws nothing
            // on either set. `ControlSampler` counts this as two short sets and moves on; it is a
            // separate cause from a pool that eliminated everybody and is kept separate here.
            return new Entry(
                setup.SetupId, setup.Ticker, setup.AsOf, setup.Direction, null, sessionMood,
                NoFigures: true, PoolAllSessions: 0, PoolAfterMood: 0, PoolAfterLadder: 0,
                DistinctNames: 0, Predicted: 0, WithoutMood: 0, WithoutLadder: 0);
        }

        string ladderKey = subject.LadderGrade ?? Ungraded;

        if (sessionMood is not string mood)
        {
            // No label, so no session can be said to share it and the tight pool is empty by the
            // rule `MoodPool` states. Counted as its own cause rather than as a mood that eliminated
            // everything, because nothing was eliminated: nothing was ever eligible.
            return new Entry(
                setup.SetupId, setup.Ticker, setup.AsOf, setup.Direction, subject.LadderGrade, null,
                NoFigures: false, PoolAllSessions: _all.Rows, PoolAfterMood: 0, PoolAfterLadder: 0,
                DistinctNames: 0, Predicted: 0,
                WithoutMood: Distinct(Slot(_byLadder, ladderKey), setup.Ticker),
                WithoutLadder: 0);
        }

        Slice afterMood = Slot(_byMood, mood);
        Slice afterLadder = Slot(_byMoodAndLadder, (mood, ladderKey));
        int distinct = Distinct(afterLadder, setup.Ticker);

        return new Entry(
            setup.SetupId, setup.Ticker, setup.AsOf, setup.Direction, subject.LadderGrade, mood,
            NoFigures: false,
            PoolAllSessions: _all.Rows,
            PoolAfterMood: afterMood.Rows,
            PoolAfterLadder: afterLadder.Rows,
            DistinctNames: distinct,
            Predicted: Math.Min(distinct, MeasurementParameters.ControlsPerSet),
            WithoutMood: Distinct(Slot(_byLadder, ladderKey), setup.Ticker),
            WithoutLadder: Distinct(afterMood, setup.Ticker));
    }

    /// <summary>
    /// Names in a slice the draw could take, which is the distinct names less the subject.
    ///
    /// Distinct rather than rows, because `Nearest` keeps one row per name however many sessions it
    /// qualifies on. A tight pool spanning a hundred sessions holds the same name a hundred times
    /// and can still offer the draw fewer than five.
    /// </summary>
    private static int Distinct(Slice slice, string subjectTicker) =>
        slice.Names.Count - (slice.Names.Contains(subjectTicker) ? 1 : 0);

    private static void Add(Slice slice, string ticker)
    {
        slice.Rows++;
        slice.Names.Add(ticker);
    }

    private static Slice Slot<TKey>(Dictionary<TKey, Slice> slices, TKey key) where TKey : notnull
    {
        if (!slices.TryGetValue(key, out Slice? slice))
        {
            slice = new Slice();
            slices[key] = slice;
        }

        return slice;
    }

    private sealed class Slice
    {
        public int Rows;

        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// One subject and the pool it faced.
    ///
    /// <paramref name="PoolAllSessions"/> is every unflagged candidate over every reach session at
    /// or before this one, which is the widest set anything could have been drawn from.
    /// <paramref name="WithoutMood"/> and <paramref name="WithoutLadder"/> are the drawable names
    /// with one equality clause removed and the other kept, which is what names the dimension doing
    /// the eliminating rather than merely showing that something did.
    /// </summary>
    public sealed record Entry(
        string SetupId,
        string Ticker,
        DateOnly AsOf,
        string Direction,
        string? LadderGrade,
        string? Mood,
        bool NoFigures,
        int PoolAllSessions,
        int PoolAfterMood,
        int PoolAfterLadder,
        int DistinctNames,
        int Predicted,
        int WithoutMood,
        int WithoutLadder);
}
