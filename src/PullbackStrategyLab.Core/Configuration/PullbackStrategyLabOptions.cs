using System.ComponentModel.DataAnnotations;

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
    public string SessionZone { get; init; } = "America/New_York";

    /// <summary>
    /// The hard ceiling on vendor calls in one day. The job counts as it goes and stops
    /// rather than overrunning, writing a partial run entry and marking the affected
    /// setups degraded. About seven times expected nightly usage.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int DailyCallCeiling { get; init; } = 5000;

    public VendorOptions Vendor { get; init; } = new();

    public ApiOptions Api { get; init; } = new();
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
    /// see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

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
