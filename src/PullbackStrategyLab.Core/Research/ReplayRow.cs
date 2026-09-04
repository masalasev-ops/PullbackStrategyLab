namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// What a replay made of one stored setup: whether the rule selects it, and what the rebuild could
/// not stand behind.
///
/// <b>Three outcomes rather than two, and the third is the one that matters.</b> A rule selects a
/// row, or it does not, or the record cannot say. Folding the third into the second would count a
/// row the harness could not judge as a row the rule rejected, and a screen whose rejections
/// include its own blind spots is a screen that flatters every rule that narrows.
/// </summary>
/// <param name="Selected">
/// Whether the rule selects the row, or null where the rebuild could not stand behind an answer.
/// </param>
/// <param name="Disagreed">
/// The judgeable gates whose verdict under the <i>baseline's own</i> rule differs from what the
/// night recorded. Non-empty means the harness and the detector disagree about this row, which is
/// the condition that makes every replay result over it worthless rather than merely uncertain.
/// </param>
/// <param name="Unjudged">
/// The judgeable gates the row carries no usable value for: a frozen signal the gate reads is
/// absent, or the night recorded no verdict of that name. A fact about the record rather than
/// about the name.
/// </param>
/// <param name="Unmeasured">
/// The judgeable gates the night recorded with no value, meaning it could not make the comparison
/// at all. Read back rather than rebuilt: a threshold cannot move a quantity that was never
/// measured, so the night's verdict is the verdict under every version of the rule.
/// </param>
/// <param name="FrozenYetUnmeasured">
/// The gates of <paramref name="Unmeasured"/> the row nonetheless froze a usable quantity for.
/// Nought is the ordinary state. A non-nought count is a row whose verdicts and whose signals
/// describe two different things, which is a fact about how that row was written and is reported
/// rather than absorbed into the read-back above.
/// </param>
/// <param name="GatesJudged">How many judgeable gates the rebuild actually reached a verdict on.</param>
public sealed record ReplayRow(
    bool? Selected,
    IReadOnlyList<string> Disagreed,
    IReadOnlyList<string> Unjudged,
    IReadOnlyList<string> Unmeasured,
    IReadOnlyList<string> FrozenYetUnmeasured,
    int GatesJudged);
