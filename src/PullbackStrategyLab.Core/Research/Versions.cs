namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// The families a rule version belongs to, and the vocabulary the store constrains itself with.
///
/// <b>Two experiment families and a baseline, and they are not comparable.</b> A selection change
/// alters which stocks are picked and is scored on the forward return of what it selected against
/// what the baseline selected on the same nights. An execution change alters the size of the R unit
/// itself, so its results cannot be differenced against the baseline the same way. A version that
/// changes both is rejected, because if it did better you would have learned nothing about which
/// change caused it.
/// </summary>
public static class VariantFamily
{
    /// <summary>The frozen rule every other version is measured against.</summary>
    public const string Baseline = "baseline";

    /// <summary>Changes a check, leaves entry and exit alone. Scored on forward return.</summary>
    public const string Selection = "selection";

    /// <summary>
    /// Changes the stop, the trigger or the exit, on the same selections. Scored on R per trade.
    ///
    /// <b>None is admitted in this generation and that is recorded rather than merely true.</b>
    /// Both routes by which such a version earns its place are closed: it cannot be screened,
    /// because minute bars exist only from the night capture began at 4.2 and the vendor sells no
    /// history to buy the gap back, and it cannot accumulate, because R needs fills and the funnel
    /// passes a median of nought candidates a night.
    /// see: No execution variant is admitted in this generation, and the condition that would reopen it is named
    /// </summary>
    public const string Execution = "execution";

    public static IReadOnlyList<string> All { get; } = [Baseline, Selection, Execution];
}

/// <summary>Where a version stands, and the only two of these AcceptanceGate may write.</summary>
public static class VariantStatus
{
    /// <summary>Registered and accumulating. The only status VariantAdmitter may write.</summary>
    public const string Open = "open";

    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    /// <summary>
    /// Closed without an answer, which is what editing the baseline does to every open version.
    ///
    /// Not a rejection: a rejected version was measured against its target and fell short, and an
    /// unresolved one was never measured at all, because the thing it was being compared against
    /// stopped existing. Counting the two together would read as evidence the loop had produced.
    /// </summary>
    public const string Unresolved = "unresolved";

    public static IReadOnlyList<string> All { get; } = [Open, Accepted, Rejected, Unresolved];
}

/// <summary>
/// What a version's minimum sample counts, stated beside the figure because the two families count
/// different things.
///
/// A selection version's minimum is in effective observations, discounted for overlapping labels and
/// the shared market factor, because a row is worth less than its own number. An execution version's
/// is in rows, because the trade-level design effect cannot be measured until a trade exists and
/// stating one would invent the quantity the setup-level measurement refused to assume. One integer
/// with no unit beside it would make 1802 and 200 read as comparable.
/// see: The execution minimum is 200 paired trades and its conversion waits on a trade existing
/// </summary>
public static class MinimumSampleUnit
{
    public const string EffectivePairedSetupObservations = "effective_paired_setup_observations";
    public const string PairedTrades = "paired_trades";

    public static IReadOnlyList<string> All { get; } = [EffectivePairedSetupObservations, PairedTrades];

    /// <summary>The unit a family's minimum is counted in, which the store also constrains.</summary>
    public static string For(string family) => family switch
    {
        VariantFamily.Execution => PairedTrades,
        VariantFamily.Baseline or VariantFamily.Selection => EffectivePairedSetupObservations,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "not a version family"),
    };
}

/// <summary>
/// The identifier of a plan, which is one setup under one version.
///
/// <b>Composed here rather than in the stage that writes it, because five components read it back.</b>
/// Until 5.1 a plan's identifier was the setup's own, and everything below the plan keyed on that
/// with a uniqueness constraint. Two versions selecting one stock produce two plans, and every one
/// of those constraints would have refused the second.
/// </summary>
public static class PlanIdentity
{
    /// <summary>The separator, which no setup identifier or version identifier may contain.</summary>
    public const char Separator = '@';

    public static string For(string setupId, string variantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantId);

        if (setupId.Contains(Separator) || variantId.Contains(Separator))
        {
            throw new ArgumentException(
                $"A setup or version identifier containing '{Separator}' would make a plan identifier "
                + "nobody could split back, and the two halves are read apart.",
                nameof(setupId));
        }

        return $"{setupId}{Separator}{variantId}";
    }
}
