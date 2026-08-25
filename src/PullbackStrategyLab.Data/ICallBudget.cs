namespace PullbackStrategyLab.Data;

/// <summary>
/// What is left of the day's vendor allowance. Passed to anything that makes a request, so
/// counting is unavoidable rather than remembered: a component that could make a call without
/// one would be a component the ceiling does not constrain.
///
/// Requests are not all worth one. The vendor prices a whole-market bulk request far above a
/// single-ticker one, so the cost is stated per request by the caller and counted as given.
/// </summary>
public interface ICallBudget
{
    /// <summary>What is left of the day's ceiling, across every stage that has already run.</summary>
    int CallsRemaining { get; }

    /// <summary>
    /// Counts a request costing <paramref name="cost"/>. Returns false when the remainder will
    /// not cover it, so the stage stops and completes as partial rather than overrunning.
    /// </summary>
    bool TryCountCalls(int cost);
}
