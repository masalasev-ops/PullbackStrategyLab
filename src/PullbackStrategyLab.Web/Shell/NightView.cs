using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// What one session's slots did, as the morning screen renders it.
///
/// <b>This is the one figure in the interface that is about the running lab rather than about the
/// market.</b> Every check in this corpus takes its subject from the source, the documents, the
/// golden fixture or a store it builds itself, so a green build says nothing about whether the
/// night ran. Fifteen of the thirty-two slots had never fired while four lists declaring them all
/// agreed, and the cost was four flagged nights whose minute bars cannot be bought back at any
/// price.
///
/// <b>Four states rather than two, and the fourth is the one that matters most here.</b> A slot ran
/// cleanly, a slot never fired, a slot fired and did not end cleanly, or nothing in the store can
/// say. Folding the fourth into either of the others is the under-reporting shape: a count that
/// narrows its own scope and goes on reading as complete.
/// </summary>
public sealed record NightView(
    string AsOf,
    string? Absent,
    IReadOnlyList<SlotView> Slots,
    int Ran,
    int NeverRan,
    int NotClean,
    int Unobservable,
    IReadOnlyList<string> Unscheduled)
{
    public static NightView Empty(string asOf, string why) =>
        new(asOf, why, [], 0, 0, 0, 0, []);

    /// <summary>Whether the report has anything to say at all.</summary>
    public bool HasSlots => Slots.Count > 0;

    /// <summary>Whether the night needs a person to do something, which is what decides if the banner shows at all.</summary>
    public bool NeedsAttention => NeverRan > 0 || NotClean > 0;

    /// <summary>
    /// The slots that never fired, which is what an operator reruns.
    ///
    /// Ordered as the night runs them rather than by name, because the stages after a missing one
    /// read what it should have written and the order is what says which to rerun first.
    /// </summary>
    public IReadOnlyList<SlotView> Missing => [.. Slots.Where(s => s.NeverRan)];

    /// <summary>The slots that fired and did not end cleanly, which is a different morning from a slot that never fired.</summary>
    public IReadOnlyList<SlotView> Ragged => [.. Slots.Where(s => s.NotClean)];

    /// <summary>
    /// The count, with the unobservable slot stated apart from the rest.
    ///
    /// Four numbers rather than one, because they add up to the whole list only when the fourth is
    /// in the sum, and the fourth is not a verdict.
    /// </summary>
    public string Count =>
        $"{Ran.ToString("N0", CultureInfo.InvariantCulture)} clean, "
        + $"{NeverRan.ToString("N0", CultureInfo.InvariantCulture)} never ran, "
        + $"{NotClean.ToString("N0", CultureInfo.InvariantCulture)} not clean, "
        + $"{Unobservable.ToString("N0", CultureInfo.InvariantCulture)} the store cannot say, "
        + $"of {Slots.Count.ToString("N0", CultureInfo.InvariantCulture)} due";
}

/// <summary>One slot of the night, and each stage it runs.</summary>
public sealed record SlotView(
    string Slot,
    string At,
    bool InsideTheSession,
    string? Unobservable,
    IReadOnlyList<StageView> Stages,
    FetchView? Bought = null)
{
    public bool IsUnobservable => Unobservable is not null;

    public bool Ran =>
        !IsUnobservable && Stages.All(s => string.Equals(s.Outcome, "clean", StringComparison.Ordinal));

    public bool NeverRan => !IsUnobservable && Stages.All(s => s.StartedAt is null);

    public bool NotClean => !IsUnobservable && !NeverRan && !Ran;

    /// <summary>
    /// What is unrecoverable about this slot not having fired, or null where nothing is.
    ///
    /// <b>Two slots lose something that cannot be bought back and the rest do not.</b> A quote has
    /// no history at all, so a spread pass that does not fire is a sample that never existed; minute
    /// bars reach back a bounded number of days, so a session outside that window cannot be bought
    /// afterwards at any price. Everything else this lab fetches can be re-asked for, and a report
    /// that treated all thirty-two the same would put the two that matter in a list of thirty.
    /// </summary>
    public string? Unrecoverable => Slot switch
    {
        "intraday" => "minute bars reach back a bounded number of days. A session not captured "
            + "inside that window cannot be bought afterwards at any price",
        "spread-open" or "spread-close" => "a quote has no history to buy back at all, so a pass "
            + "that did not fire is a sample that never existed",
        "universe" => "the symbol list is read at run time, so a rerun stamps this session with "
            + "today's membership. This session is never rerun and the night it missed is gone",
        _ => null,
    };

    /// <summary>The stages, as a phrase, so a slot running two says which.</summary>
    public string Runs => string.Join(", ", Stages.Select(s => s.Stage));
}

/// <summary>
/// What the night's unrecoverable buy bought, as the morning screen renders it.
///
/// <b>It says nothing about whether the night was clean, because that is the stage's answer and the
/// point of showing this is to let a person read the counts it was reached from.</b>
/// </summary>
public sealed record FetchView(
    int Requested,
    int Fetched,
    int Empty,
    int BarsWritten,
    int Stored,
    int WindowSessions,
    int WindowAsks)
{
    /// <summary>Whether the night spent calls and the store holds nothing for the window it bought.</summary>
    public bool BoughtNothing => Fetched > 0 && Stored == 0;

    /// <summary>
    /// Whether the window this night bought was narrower than the anchor window asks for.
    ///
    /// True for every night before the width landed and for every night the store holds fewer
    /// sessions than the window wants, and the two read the same on the screen because they cost the
    /// same thing: the sessions not bought are anchors nothing will ever price.
    /// </summary>
    public bool Narrow => WindowSessions < WindowAsks;

    /// <summary>
    /// The counts, as one sentence, in the order 4.2's row names them: asked, returned, stored.
    ///
    /// Written out on every night rather than only on a bad one. A figure shown only when something
    /// is wrong is a figure nobody has a reading of when it appears.
    /// </summary>
    public string Reads =>
        $"{Requested.ToString("N0", CultureInfo.InvariantCulture)} asked, "
        + $"{Fetched.ToString("N0", CultureInfo.InvariantCulture)} answered, "
        + $"{Empty.ToString("N0", CultureInfo.InvariantCulture)} with no minutes, "
        + $"{BarsWritten.ToString("N0", CultureInfo.InvariantCulture)} bar(s) written, "
        + $"{Stored.ToString("N0", CultureInfo.InvariantCulture)} held for the window";

    /// <summary>The width, against the width the anchor window asks for.</summary>
    public string Window =>
        $"{WindowSessions.ToString("N0", CultureInfo.InvariantCulture)} of "
        + $"{WindowAsks.ToString("N0", CultureInfo.InvariantCulture)} session(s)";
}

/// <summary>One stage of one slot on one session.</summary>
public sealed record StageView(
    string Stage,
    string? StartedAt,
    string? EndedAt,
    string? Outcome,
    int CallsUsed,
    string? Unobservable)
{
    /// <summary>
    /// How this stage reads on the screen.
    ///
    /// A stage that began and has not ended is its own sentence rather than a blank outcome: the
    /// row exists, so it is not a stage that never ran, and it has no outcome, so it is not one that
    /// finished.
    /// </summary>
    public string Reads =>
        Unobservable is not null ? "the store cannot say"
        : StartedAt is null ? "never ran"
        : EndedAt is null ? "started and has not ended"
        : Outcome ?? "ended with no outcome recorded";
}
