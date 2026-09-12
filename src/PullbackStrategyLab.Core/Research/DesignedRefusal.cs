namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// A refusal the design requires, thrown where a stage cannot do its work because a condition the
/// corpus decided on is not met, and told apart from a stage that broke.
///
/// <b>The two arrive at the same catch and are not the same event.</b> A section that threw is a
/// fault: something is wrong and the run is red until somebody looks. A refusal the design requires
/// is the stage doing exactly what it was told to do, for as long as the condition holds, and that
/// can be months. Reported as a failure it puts a red beside a correct outcome every week, which is
/// how a red beside a wrong one stops being read. The first live instance was the pack of
/// 2026-09-12, refusing because generation 1 has no selection rule written down, which is a
/// condition with no date on it.
///
/// <b>It is a type rather than a message match</b>, because a reason compared as text is a citation
/// that stops resolving the day somebody rewords it, which is the same argument
/// `decision-resolves` makes about decision names.
/// </summary>
public sealed class DesignedRefusal : InvalidOperationException
{
    public DesignedRefusal(string message)
        : base(message)
    {
    }

    public DesignedRefusal(string message, Exception inner)
        : base(message, inner)
    {
    }
}
