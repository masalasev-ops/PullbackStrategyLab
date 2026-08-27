using System.Text.Json;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The authored geometry windows: thrust indices the captured fixture never produces.
///
/// <b>What it is for.</b> Over the captured fixture every name sits inside every scan on every
/// session, so the most recent hit is always the current bar, the extreme is always the last bar,
/// and <see cref="PullbackGeometry.Of"/> returns <c>PullbackBars</c> 0 and <c>RetraceDepth</c> 0 on
/// every row. <see cref="GateCases"/> does not help: it builds a
/// <see cref="PullbackGeometry.Pullback"/> by hand from a retrace depth and a bar count and never
/// calls <c>Of</c> at all. So the method behind four gates was exercised on no non-degenerate input
/// anywhere, and its own comment says why that is worse here than elsewhere: every figure it
/// returns is a plausible small number whichever way it was computed.
///
/// <b>Authored, and only in one respect.</b> The bars are the captured fixture's own. What this
/// file authors is which window and which thrust index, which is the same thing the gate cases
/// author and for the same reason.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
///
/// <b>It costs nothing to hold.</b> <c>Of</c> takes the thrust index as a parameter and the fixture
/// already holds 250 sessions for each of thirty names, so a non-degenerate case is an index inside
/// the stored window rather than at its end. No capture and no vendor call.
///
/// <b>The window is read through <see cref="DailyBarReader"/> rather than by a statement here.</b>
/// A case that selected its own bars would be bounding the observation stamp itself, and a
/// derivation that borrows the selection it is checking is checking less than it looks. The reader
/// is the one the detectors use, so a case sees the session a detector would have seen.
/// </summary>
public static class GeometryCases
{
    public const string FileName = "geometry-cases.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One window and one thrust index, with the branch it was chosen to reach.</summary>
    public sealed record GeometryCase(string Name, string Ticker, int ThrustIndex, string Direction, string Why)
    {
        /// <summary>Whether this case reads the geometry upward. The mirror is a parameter, not a second class.</summary>
        public bool IsLong => string.Equals(Direction, "long", StringComparison.Ordinal);

        /// <summary>
        /// The measurement id, naming the branch rather than the ticker.
        ///
        /// A reader scanning a diff wants "long-thrust-at-the-window-start moved", not "PFE index 0
        /// moved". Which bars produced it is in the case file, one line away.
        /// </summary>
        public string Id => $"geometry.{Name}";
    }

    /// <summary>The window every case is read over: one as-of and a session count.</summary>
    public sealed record CaseWindow(string AsOf, int Sessions, string Why)
    {
        public DateOnly Date => DateOnly.ParseExact(AsOf, "yyyy-MM-dd");
    }

    private sealed record CaseFile(string Tier, CaseWindow Window, IReadOnlyList<GeometryCase> Cases);

    private static CaseFile Read() =>
        JsonSerializer.Deserialize<CaseFile>(
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, "fixtures", FileName)), Json)
        ?? throw new InvalidOperationException($"{FileName} did not parse into a case file.");

    public static string Tier => Read().Tier;

    public static CaseWindow Window => Read().Window;

    public static IReadOnlyList<GeometryCase> All => Read().Cases;

    /// <summary>
    /// One case run through the shipped method, or null where the window cannot support a shape.
    ///
    /// The bars are shaped exactly as the detectors shape them, adjusted for the four shape prices
    /// and raw for the two that trade, because a second shaping routine here would be a second
    /// implementation of the thing the case is checking.
    /// </summary>
    public static PullbackGeometry.Pullback? Evaluate(SqliteConnection connection, GeometryCase geometryCase)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(geometryCase);

        CaseWindow window = Window;

        IReadOnlyList<StoredDailyBar> bars =
            DailyBarReader.Read(connection, geometryCase.Ticker, window.Date, window.Sessions);

        if (bars.Count == 0)
        {
            return null;
        }

        PullbackGeometry.Bar[] shaped = [.. bars.Select(Shape)];
        return PullbackGeometry.Of(shaped, geometryCase.ThrustIndex, geometryCase.IsLong);
    }

    /// <summary>
    /// One session as the geometry reads it, adjusted for shape and raw for the two prices.
    ///
    /// The same three lines the two detectors and the vectorizer each hold. It is duplicated here
    /// rather than shared because sharing it would let a change to the shaping move the case and
    /// the subject together, and the case exists to disagree with the subject.
    /// </summary>
    private static PullbackGeometry.Bar Shape(StoredDailyBar bar)
    {
        decimal factor = bar.Close == 0m ? 1m : bar.AdjustedClose / bar.Close;

        return new PullbackGeometry.Bar(
            bar.Open * factor, bar.High * factor, bar.Low * factor, bar.AdjustedClose, bar.High, bar.Low);
    }
}
