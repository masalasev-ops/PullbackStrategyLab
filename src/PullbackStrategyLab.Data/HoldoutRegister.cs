using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Research;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The register against the calendar: which windows should exist by a date, which the store holds,
/// which are spent, and why it holds nothing where it holds nothing.
///
/// <b>One implementation, and it sits here so two callers can have it.</b> It was a private method
/// on the stage at 5.4, and that entry said in its own words that this is the read the research
/// ledger makes at 5.5 and the one an operator makes on a morning the job did not fire. The read
/// surface cannot reference the Worker, which <c>api-isolation</c> asserts against the compiled
/// dependency file, so a ledger reading the register would either have copied this reasoning or
/// waited on a run row. A copy is the thing the corpus forbids and the wait is worse: nothing
/// schedules the registry, so the run row does not exist and the ledger would have reported an empty
/// register for a reason that is about the scheduler rather than about the calendar.
/// see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight
///
/// <b>It is a read and it writes nothing</b>, which is what lets a page call it. The stage's own
/// recording of matured windows happens before it and hands in what it wrote.
/// </summary>
public static class HoldoutRegister
{
    /// <summary>The evidence store holds no session at all, so no quarter of evidence has begun.</summary>
    public const string NoSessionRecorded =
        "the evidence store holds no session, so no quarter of forward-collected evidence has begun and "
        + "the first window has no start date yet";

    /// <summary>
    /// Sessions exist and no quarter has completed. The ordinary state for the first months, and
    /// not a fault.
    /// </summary>
    public const string NoQuarterMaturedYet =
        "no calendar quarter of forward-collected evidence has completed yet, which is the ordinary state "
        + "until the first one does and is not a failure to record anything";

    /// <summary>
    /// The calendar says a window should be here and the register does not hold it, which is a
    /// defect rather than a state and is why it is not one of the reasons above.
    /// </summary>
    public const string NotRecorded =
        "the calendar says a window has matured and the register does not hold it, so this register is "
        + "empty because nothing recorded one rather than because there is nothing to record";

    /// <summary>Every window that has matured has been spent, which is the designed dead end.</summary>
    public const string EveryMaturedWindowSpent =
        "every window that has matured has been spent, so no further pack-version decision can be made "
        + "by replay and the remaining channel is the forward hit rate. This is a designed dead end "
        + "rather than a bug";

    /// <summary>
    /// The register against the calendar, from a connection the caller holds.
    ///
    /// <paramref name="written"/> is how many windows the caller recorded in the act that led here,
    /// which is nought on every read. It is carried rather than derived because a run that recorded
    /// two windows and a read that found the same two already there are different facts and the run
    /// row stores both.
    /// </summary>
    public static HoldoutRegisterState Describe(
        SqliteConnection connection, DateOnly asOf, string sessionZone, int written)
    {
        ArgumentNullException.ThrowIfNull(connection);

        DateOnly? firstSession = HoldoutWindowReader.FirstSession(connection, asOf);

        IReadOnlyList<HoldoutWindow> matured = firstSession is DateOnly first
            ? HoldoutWindows.MaturedBy(first, asOf)
            : [];

        IReadOnlyList<StoredHoldoutWindow> register = HoldoutWindowReader.Read(connection, asOf, sessionZone);

        // What the calendar says should be there against what is. Nought is the ordinary answer and
        // a non-nought one is a defect rather than a state, which is why it is counted apart from
        // the empty reasons below.
        IReadOnlyList<string> missing =
            [.. matured.Select(w => w.WindowId)
                .Except(register.Select(w => w.Window.WindowId), StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

        int spent = register.Count(w => !w.IsAvailable);
        int available = register.Count(w => w.IsAvailable);

        // Three ordinary readings and one that is not ordinary at all, and the order is what keeps
        // them apart. A register empty because nothing has matured is not the same fact as one empty
        // because everything has been spent, and neither is the same as a lab that has recorded no
        // session.
        //
        // <b>A register missing a window it should hold gets its own reason rather than one of
        // those.</b> Without this clause, a lab whose registry never ran would report "no quarter has
        // completed yet" on a date when several had, which is the empty-and-correct sentence
        // covering for a defect.
        string? emptyBecause =
            available > 0 ? null
            : missing.Count > 0 ? NotRecorded
            : firstSession is null ? NoSessionRecorded
            : register.Count == 0 ? NoQuarterMaturedYet
            : EveryMaturedWindowSpent;

        RunOutcome outcome = missing.Count == 0 ? RunOutcome.Clean : RunOutcome.Partial;

        return new HoldoutRegisterState(
            asOf, firstSession, matured.Count, register.Count, written, spent, available,
            emptyBecause, missing, register, outcome);
    }
}

/// <summary>
/// What the register held at one moment, and why it held nothing where it held nothing.
/// </summary>
/// <param name="AsOf">The date the register was read as of.</param>
/// <param name="FirstSession">
/// The earliest session the evidence store holds, which is what the whole schedule is computed from.
/// Null where it holds none, which is a state of its own: no quarter has begun.
/// </param>
/// <param name="Matured">How many windows the calendar says have completed by <paramref name="AsOf"/>.</param>
/// <param name="Recorded">How many the register holds.</param>
/// <param name="Written">How many this act recorded, which is nought on a read.</param>
/// <param name="Spent">Of the recorded windows, how many carry a spend.</param>
/// <param name="Available">Of the recorded windows, how many do not.</param>
/// <param name="Missing">
/// The matured windows the register does not hold. Nought is the ordinary answer; anything else is
/// a failure to record and is a different state from a register that is empty because nothing has
/// matured.
/// </param>
/// <param name="EmptyBecause">
/// Why no window is available to spend, present exactly when none is. Four readings and they are
/// different facts.
/// </param>
public sealed record HoldoutRegisterState(
    DateOnly AsOf,
    DateOnly? FirstSession,
    int Matured,
    int Recorded,
    int Written,
    int Spent,
    int Available,
    string? EmptyBecause,
    IReadOnlyList<string> Missing,
    IReadOnlyList<StoredHoldoutWindow> Register,
    RunOutcome Outcome)
{
    /// <summary>
    /// Whether the budget is exhausted, which is the designed dead end rather than a fault.
    ///
    /// <b>Distinct from having nothing yet</b>, which is why it reads the reason rather than the
    /// count: both states have nought available and only one of them is permanent.
    /// </summary>
    public bool IsExhausted =>
        string.Equals(EmptyBecause, HoldoutRegister.EveryMaturedWindowSpent, StringComparison.Ordinal);

    /// <summary>
    /// Whether the register is short of a window the calendar says it should hold, which is the one
    /// state here that is a defect rather than a reading.
    /// </summary>
    public bool IsShortOfAWindow => Missing.Count > 0;
}
