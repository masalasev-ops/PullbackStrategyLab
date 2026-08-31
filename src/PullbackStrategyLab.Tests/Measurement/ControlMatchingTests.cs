using PullbackStrategyLab.Core.Measurement;
using Xunit;

namespace PullbackStrategyLab.Tests.Measurement;

/// <summary>
/// What a comparison is made of, asserted where it is decided.
///
/// <b>This class holds the dimensions and the tie-break, and nothing else does.</b>
/// <see cref="ControlMatching"/> is in Core so the nightly draw, the replay and a test share one
/// implementation, and its own doc names the two places it could disagree with itself: which
/// dimensions count, and how ties break. Both are here.
///
/// <b>It exists because the route through the sampler cannot hold them.</b> Deleting the mood
/// clause leaves every sampler test green, and that was measured rather than reasoned about: the
/// clause was removed and all seven passed. It was true when the sampler narrowed its pool by mood,
/// which excluded the same rows a second time, and it is true now that the pool is one night's,
/// which never contains them. A pool handed in directly is the only subject that can tell the two
/// apart, and it is the reason this file outlived the reach that prompted it.
/// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
/// </summary>
public sealed class ControlMatchingTests
{
    private static readonly DateOnly Tonight = new(2026, 8, 27);
    private static readonly DateOnly Earlier = new(2026, 8, 20);

    private static readonly ControlMatching.Candidate Subject =
        new("SUBJ", 100_000_000m, 2.0m, "rising", Tonight, "risk_on");

    /// <summary>
    /// The guard. A nearer candidate on the wrong mood is excluded and a further one on the right
    /// mood is drawn, so distance cannot be what produced the answer.
    /// </summary>
    [Fact]
    public void A_tight_draw_excludes_a_candidate_from_a_session_carrying_a_different_mood()
    {
        ControlMatching.Candidate[] pool =
        [
            new("WRONG", 100_100_000m, 2.01m, "rising", Earlier, "risk_off"),
            new("RIGHT", 160_000_000m, 3.10m, "rising", Earlier, "risk_on"),
        ];

        IReadOnlyList<ControlMatching.Draw> drawn = ControlMatching.Nearest(Subject, pool, 5, tight: true);

        ControlMatching.Draw only = Assert.Single(drawn);
        Assert.Equal("RIGHT", only.Ticker);
        Assert.Equal(Earlier, only.AsOf);
        Assert.Equal("same", only.MatchQuality["marketMood"]);
    }

    /// <summary>
    /// The loose set does not match on the mood, so the nearer candidate wins whatever mood it
    /// carries, and its row says the dimension was not matched rather than saying "same".
    /// </summary>
    [Fact]
    public void A_loose_draw_does_not_match_on_the_mood_and_records_that_it_did_not()
    {
        ControlMatching.Candidate[] pool =
        [
            new("WRONG", 100_100_000m, 2.01m, "falling", Tonight, "risk_off"),
            new("RIGHT", 160_000_000m, 3.10m, "rising", Tonight, "risk_on"),
        ];

        IReadOnlyList<ControlMatching.Draw> drawn = ControlMatching.Nearest(Subject, pool, 5, tight: false);

        Assert.Equal("WRONG", drawn[0].Ticker);
        Assert.Equal("not matched", drawn[0].MatchQuality["marketMood"]);
        Assert.Equal("0", drawn[0].MatchQuality["sessionsApart"]);
    }

    /// <summary>
    /// The ladder is still excluded rather than penalised, which the mood clause sits beside and
    /// must not have replaced.
    /// </summary>
    [Fact]
    public void A_tight_draw_still_excludes_a_different_ladder_grade()
    {
        ControlMatching.Candidate[] pool =
        [
            new("WRONG", 100_100_000m, 2.01m, "falling", Tonight, "risk_on"),
            new("RIGHT", 160_000_000m, 3.10m, "rising", Tonight, "risk_on"),
        ];

        ControlMatching.Draw only =
            Assert.Single(ControlMatching.Nearest(Subject, pool, 5, tight: true));

        Assert.Equal("RIGHT", only.Ticker);
    }

    /// <summary>
    /// One row per name however many sessions it qualifies on, and the row kept is the nearest.
    ///
    /// Five per set exists so a comparison does not inherit one name's idiosyncratic move. A set
    /// holding the same name on five sessions would inherit it while looking like five.
    /// </summary>
    [Fact]
    public void A_name_qualifying_on_several_sessions_is_drawn_once_at_its_nearest()
    {
        ControlMatching.Candidate[] pool =
        [
            new("REPEAT", 400_000_000m, 9.0m, "rising", Earlier, "risk_on"),
            new("REPEAT", 110_000_000m, 2.1m, "rising", Tonight, "risk_on"),
        ];

        ControlMatching.Draw only =
            Assert.Single(ControlMatching.Nearest(Subject, pool, 5, tight: true));

        Assert.Equal(Tonight, only.AsOf);
    }

    /// <summary>
    /// The session distance the ruling paid for is recorded per row, in sessions apart from the
    /// subject's own.
    /// </summary>
    [Fact]
    public void A_tight_draw_records_how_far_it_reached()
    {
        ControlMatching.Candidate[] pool =
            [new("RIGHT", 160_000_000m, 3.10m, "rising", Earlier, "risk_on")];

        ControlMatching.Draw only =
            Assert.Single(ControlMatching.Nearest(Subject, pool, 5, tight: true));

        Assert.Equal("7", only.MatchQuality["sessionsApart"]);
    }
}
