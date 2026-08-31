using System.ComponentModel.DataAnnotations;

using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Core.Configuration;

/// <summary>
/// Everything the lab is configured with, under one section named in full. A shortened
/// form in one place and the full form in another is the kind of inconsistency that
/// survives for years and then bites during a rename.
/// </summary>
public sealed record PullbackStrategyLabOptions
{
    public const string SectionName = "PullbackStrategyLab";

    /// <summary>
    /// The one root under which every file the lab writes lives. Set per machine and
    /// kept outside the repository and outside any synced folder. Every path is composed
    /// from here through the platform API, so the store stays a directory that can be
    /// copied to another machine.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string DataRoot { get; init; } = string.Empty;

    /// <summary>
    /// The IANA identifier every session boundary resolves through. Never a Windows
    /// identifier: <see cref="Time.SystemClock"/> rejects those rather than translating them.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string SessionZone { get; init; } = SessionBoundaries.UsEquities;

    /// <summary>
    /// The hard ceiling on vendor calls in one day. The job counts as it goes and stops
    /// rather than overrunning, writing a partial run entry and marking the affected
    /// setups degraded.
    ///
    /// <b>Under twice expected nightly usage, not the seven times this comment claimed.</b>
    /// A night is 2,803 calls and up to 4,003 when holidays fall inside the universe screen's
    /// search window, because `universe-build` alone spends 2,005 and RUNBOOK priced it at 5
    /// until 3.10. The margin is real but it is one bad week rather than a comfortable multiple,
    /// and `daily-bars` runs after `universe`, so the stage that stops short on a full day is the
    /// one that stores the night's bars.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int DailyCallCeiling { get; init; } = 5000;

    /// <summary>
    /// How many snapshots the lab keeps. Older ones are removed after a new one is verified.
    ///
    /// <b>There was no retention at all until 3.11, and the store is 290 MB a night.</b> Twenty-four
    /// snapshots had accumulated in four days against a store holding one session of setups, which
    /// is 4.6 GB of recovery points for an evidence base of forty-four rows. A recovery path that
    /// fills the disk it recovers onto is not one.
    ///
    /// <b>Seven, because the thing being recovered from is a night.</b> A snapshot exists so that a
    /// store a stage corrupted, or a migration went wrong on, can be put back. Both are noticed
    /// within a night or two, and the pre-migration copy that `migrate` takes itself is the newest
    /// of the set at the moment it matters. Seven is a week of them, which covers a fault found on
    /// a Monday that happened before the weekend.
    ///
    /// It bounds the directory at about 2 GB at today's store size, and the bound moves with the
    /// store rather than staying put, which is worth knowing before the store is ten times larger.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int SnapshotsKept { get; init; } = 7;

    public VendorOptions Vendor { get; init; } = new();

    public ApiOptions Api { get; init; } = new();

    public UniverseOptions Universe { get; init; } = new();

    /// <summary>
    /// The market trackers the regime label is read from. Trackers rather than the indexes
    /// themselves, because a tracker has a bar with a volume and an index has a level.
    /// </summary>
    public IReadOnlyList<string> IndexSymbols { get; init; } = ["SPY", "QQQ", "IWM"];
}

/// <summary>
/// The floors that decide the tradable list. Every value here is an authored parameter, and
/// pinned-constants asserts each against the row in the architecture document that states it.
/// </summary>
public sealed record UniverseOptions
{
    /// <summary>
    /// Below this, spreads widen enough to swallow the stop, and it is outside the range the
    /// strategy is actually traded in. Both sides.
    /// </summary>
    public decimal PriceFloor { get; init; } = 5m;

    /// <summary>
    /// Median daily dollar volume on the long side. The filter doing the real work: price is a
    /// weak proxy for liquidity.
    /// </summary>
    public decimal LiquidityFloorLong { get; init; } = 20_000_000m;

    /// <summary>
    /// How many trading sessions the median is taken over. Twenty is what the backfill order in
    /// RUNBOOK screens on, and the screen is what keeps the per-ticker history inside the ceiling.
    /// </summary>
    public int LiquidityWindowSessions { get; init; } = 20;

    /// <summary>The vendor's own word for the only instrument type that survives the filter.</summary>
    public string SecurityType { get; init; } = "Common Stock";

    /// <summary>
    /// The venues the delisted purchase covers. It bounds one one-time operation and nothing
    /// nightly: the listed universe is screened on price and liquidity rather than on venue, and
    /// this list is not applied to it.
    ///
    /// <b>Two venues rather than every venue, and the gap is the whole reason it exists.</b> The
    /// delisted list holds 32,851 common stocks and 15,983 of them are NASDAQ or NYSE, so covering
    /// the rest costs about four extra nights of the ceiling. What those nights buy is the delisted
    /// history of venues the current universe holds 30 names on out of 2,005: 9,425 delisted names
    /// on PINK for 14 members, and a comparable ratio on AMEX, OTCQX, NYSE ARCA and BATS. A name
    /// there could in principle have cleared the price and liquidity floors while it traded, so
    /// this is a bound on the purchase and not a claim that nothing was missed, which is why the
    /// venues are configured rather than compiled in.
    /// see: Delisted daily history is bought so a reconstructed walk is not confined to survivors
    /// </summary>
    public IReadOnlyList<string> DelistedExchanges { get; init; } = ["NASDAQ", "NYSE"];
}

/// <summary>
/// The market data vendor. Named rather than left as "the vendor", because the whole
/// backfill order depends on this vendor's split between two differently priced endpoints.
/// see: The vendor is EODHD, and the endpoint mix is what the call budget is built on
/// </summary>
public sealed record VendorOptions
{
    /// <summary>Recorded so a store written against one vendor is not silently read as another's.</summary>
    public string Name { get; init; } = "EODHD";

    public string BaseAddress { get; init; } = "https://eodhd.com/api/";

    /// <summary>The exchange code the symbol list and the bulk endpoints are asked for.</summary>
    public string Exchange { get; init; } = "US";

    /// <summary>
    /// Lives only in appsettings.Secrets.json, which is gitignored and travels between
    /// machines by deliberate copy rather than by clone. Empty on a machine that has no
    /// secrets file, which is a working state for everything that does not call the vendor.
    ///
    /// It is read from <see cref="VendorTokenKey"/> rather than from this section, because the
    /// secrets file on a machine holds keys for more than this lab and grouping them under one
    /// heading is what makes it copyable as a unit. Registered before environment variables all
    /// the same, so <c>Secrets__EodhdApiToken</c> in the environment still wins.
    /// see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The one configuration key the vendor token is read from. Named once, here.</summary>
    public const string VendorTokenKey = "Secrets:EodhdApiToken";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Where the read surface listens, and where the Web project finds it. Both are
/// configured rather than defaulted into launchSettings.json, so neither host carries a
/// hardcoded port and local loopback stays plain HTTP.
/// </summary>
public sealed record ApiOptions
{
    /// <summary>The address the Api binds. Read by the Api host.</summary>
    public string BindAddress { get; init; } = "http://127.0.0.1:5180";

    /// <summary>The address the Web project calls. Read by the Web host.</summary>
    public string BaseAddress { get; init; } = "http://127.0.0.1:5180";
}
