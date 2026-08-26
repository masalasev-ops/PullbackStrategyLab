using System.Net;
using System.Text;
using System.Text.Json;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Serves the captured fixture as if it were the vendor, at the transport rather than at the
/// interface.
///
/// This is the whole point of putting it here. A fake implementing <c>IMarketDataVendor</c>
/// would hand the stages objects some test author built, so the replay would exercise the
/// stages and skip the parsing, the field names, the number formats and the URL the client
/// actually asks for. Handing back the captured bytes runs the real client over the real
/// response, and the only thing replaced is the network.
/// see: Fixture inputs record where they came from, and a path a live run exercises needs a captured one
///
/// A request the fixture has no response for comes back as an empty JSON array, which is what
/// the vendor genuinely returns for a date it has nothing on. Every one of those is counted and
/// named, because a silent empty answer is how a replay convinces itself it covered a path it
/// never touched.
/// </summary>
public sealed class FixtureVendorHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _coveredEndpoints = new(StringComparer.Ordinal);
    private readonly List<string> _misses = [];
    private readonly List<string> _served = [];

    public FixtureVendorHandler(string fixtureDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);
        Directory = fixtureDirectory;

        string manifestFile = Path.Combine(fixtureDirectory, "manifest.json");
        if (!File.Exists(manifestFile))
        {
            throw new InvalidOperationException(
                $"No captured fixture at {manifestFile}. Capture one with the capture-fixture stage; the replay has "
                + "nothing to run against without it.");
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestFile));
        JsonElement root = manifest.RootElement;

        Tier = root.GetProperty("tier").GetString() ?? "UNKNOWN";
        AsOf = DateOnly.ParseExact(
            root.GetProperty("asOf").GetString() ?? throw new InvalidOperationException("The manifest states no as-of date."),
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);

        foreach (JsonElement response in root.GetProperty("responses").EnumerateArray())
        {
            string file = response.GetProperty("file").GetString()!;
            string endpoint = response.GetProperty("endpoint").GetString()!;
            string query = response.GetProperty("query").GetString() ?? string.Empty;

            string key = Key(endpoint, query);
            _bodies[key] = File.ReadAllText(Path.Combine(fixtureDirectory, file));
            _coveredEndpoints.Add(EndpointOf(key));
        }
    }

    /// <summary>
    /// Which endpoint a key belongs to: the path with the per-ticker part and the bulk
    /// endpoint's question stripped off. <c>eod/IESC.US</c> and <c>eod/AAPL.US</c> are the same
    /// endpoint asked about different names.
    /// </summary>
    private static string EndpointOf(string key)
    {
        string path = key.Split('|')[0];
        int slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? path : path[..slash];
    }

    public string Directory { get; }

    /// <summary>The tier the manifest records these inputs at. The replay reports it rather than assuming it.</summary>
    public string Tier { get; }

    /// <summary>The date the fixture was captured for. Every replay runs as of this date and no other.</summary>
    public DateOnly AsOf { get; }

    public int Responses => _bodies.Count;

    /// <summary>Requests the fixture answered from a captured response.</summary>
    public IReadOnlyList<string> Served => _served;

    /// <summary>
    /// Requests the fixture had nothing for, answered with an empty array. Named rather than
    /// counted, so the report can say which paths the replay only appeared to cover.
    /// </summary>
    public IReadOnlyList<string> Misses => _misses;

    /// <summary>
    /// A miss on an endpoint the fixture does cover, asked for something outside what it holds:
    /// another market day, or a ticker that is not one of the fixture's names. The endpoint has
    /// captured evidence and the fixture has a boundary, which is not the same thing as a gap.
    /// </summary>
    public IReadOnlyList<string> MissesInsideACoveredEndpoint => _misses
        .Where(m => _coveredEndpoints.Contains(EndpointOf(m)))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A miss on an endpoint with no captured response at all. This one is a gap: a live run
    /// exercised the path and the fixture answered it with nothing.
    /// </summary>
    public IReadOnlyList<string> MissesOnAnUncoveredEndpoint => _misses
        .Where(m => !_coveredEndpoints.Contains(EndpointOf(m)))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri uri = request.RequestUri ?? throw new InvalidOperationException("A request with no URI reached the fixture.");
        string path = uri.AbsolutePath.TrimStart('/');

        // The client's base address ends in /api/, so the endpoint the manifest recorded is what
        // follows it. Matched on the path rather than on the whole URL, because the whole URL
        // carries the token.
        const string ApiPrefix = "api/";
        if (path.StartsWith(ApiPrefix, StringComparison.Ordinal))
        {
            path = path[ApiPrefix.Length..];
        }

        string key = KeyFromRequest(path, uri.Query);
        bool hit = _bodies.TryGetValue(key, out string? body);

        if (hit)
        {
            _served.Add(key);
        }
        else
        {
            _misses.Add(key);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(hit ? body! : "[]", Encoding.UTF8, "application/json"),
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// What identifies one captured response. The path, plus the two parameters that make the
    /// bulk endpoint answer a different question: which kind of row, and which market day.
    ///
    /// The per-ticker endpoint is identified by its path alone. Its window is part of the
    /// capture rather than part of the question: the fixture holds the sessions it holds, and
    /// trimming them here would put a second reading of the vendor's shape inside the harness
    /// that exists to avoid exactly that.
    /// </summary>
    private static string Key(string endpoint, string query)
    {
        if (!endpoint.StartsWith("eod-bulk-last-day/", StringComparison.Ordinal))
        {
            return endpoint;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                parameters[pair[..equals]] = pair[(equals + 1)..];
            }
        }

        // No type parameter is the prices question, which is what the client asks when it wants
        // the whole market's closes.
        string type = parameters.GetValueOrDefault("type", "prices");
        string date = parameters.GetValueOrDefault("date", string.Empty);

        return $"{endpoint}|{type}|{date}";
    }

    private static string KeyFromRequest(string path, string rawQuery) =>
        Key(Uri.UnescapeDataString(path), rawQuery.TrimStart('?'));
}
