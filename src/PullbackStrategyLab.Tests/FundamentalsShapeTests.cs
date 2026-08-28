using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Worker.Vendor;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The bodies the vendor can answer 200 with, and what the walk does with each.
///
/// <b>One captured shape is a sample of one from a space already proved wider than expected.</b> Two
/// shapes were predicted before the real one was seen and both were wrong: it was neither a non-200
/// nor a change in the vendor's field names, but a capitalisation delivered as the string "NA" in a
/// field declared decimal. So the captured instance is kept as the one the vendor actually sent, and
/// the space around it is authored, which is what the corpus asks authored cases to do.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class FundamentalsShapeTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 27);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 27, 22, 12, 0, TimeSpan.Zero));

    public FundamentalsShapeTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>The authored shapes, read from the fixture rather than restated here.</summary>
    public static TheoryData<string, string, bool> Shapes()
    {
        var data = new TheoryData<string, string, bool>();

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, "fixtures", "fundamentals-shapes.json")));

        foreach (JsonElement shape in document.RootElement.GetProperty("shapes").EnumerateArray())
        {
            data.Add(
                shape.GetProperty("name").GetString()!,
                shape.GetProperty("body").GetString()!,
                shape.GetProperty("holdsNothing").GetBoolean());
        }

        return data;
    }

    /// <summary>
    /// Every shape read through the real client, over the transport, with the walk behind it.
    ///
    /// A name the vendor holds nothing on is stamped, so it is never asked again, and counted as one
    /// the vendor had nothing on rather than as resolved. A name it holds something on is stamped
    /// with what it holds. Neither is a skip: none of these bodies is a failure any more, which is
    /// the whole change.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Every_authored_shape_is_read_rather_than_thrown_on(string name, string body, bool holdsNothing)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));

        Scanned("AAA");

        SectorResult result = await Resolver(Answering(body)).ResolveAsync(AsOf, limit: 200);

        // Nothing skipped, whatever the shape. A skip means the walk could not read it.
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Stamped);
        Assert.Equal("clean", result.Outcome.ToStorageText());

        Assert.Equal(holdsNothing ? 1 : 0, result.VendorHadNothing);
        Assert.Equal(holdsNothing ? 0 : 1, result.Resolved);

        // Stamped either way, so the question is not asked a second time.
        Assert.True(Stamped("AAA"));
    }

    /// <summary>
    /// A 200 the parse cannot read is refused as a working example, and the refusal names the parse
    /// rather than the status.
    ///
    /// This is the trigger stated as what it is. A guard on status would have stored the response
    /// that killed the sector walk as a good one, because it came back 200.
    /// </summary>
    [Fact]
    public void A_two_hundred_the_parse_cannot_read_is_refused_as_a_working_example()
    {
        var unreadable = new CapturedResponse(
            "fundamentals/AAA.US",
            "filter=General::Sector",
            "{\"General::Sector\":\"Technology\",\"Highlights::MarketCapitalization\":\"about ten billion\"}",
            200);

        string? why = EodhdClient.WhyUnreadable(unreadable);

        Assert.NotNull(why);
        Assert.Contains("will not shape", why, StringComparison.Ordinal);
    }

    /// <summary>And a body it can read is not refused, so the guard is not simply always on.</summary>
    [Fact]
    public void A_two_hundred_the_parse_can_read_is_not_refused()
    {
        var readable = new CapturedResponse(
            "fundamentals/AAA.US",
            "filter=General::Sector",
            "{\"General::Sector\":\"Technology\",\"Highlights::MarketCapitalization\":\"NA\"}",
            200);

        Assert.Null(EodhdClient.WhyUnreadable(readable));
    }

    /// <summary>A refusal is still a refusal, which is the other half of "whatever the status".</summary>
    [Fact]
    public void A_non_two_hundred_is_refused_and_says_so()
    {
        var refused = new CapturedResponse("fundamentals/AAA.US", string.Empty, "Forbidden", 403);

        Assert.Equal("the vendor answered 403", EodhdClient.WhyUnreadable(refused));
    }

    /// <summary>
    /// The capture refuses an unreadable body rather than storing it as a working example.
    ///
    /// This is the branch itself rather than the predicate behind it: with the refusal deleted, the
    /// capture stores whatever came back under a name that reads as a good response, and the fixture
    /// grows a body the parser has never seen. The message has to name the shape, because a capture
    /// that failed without saying what it read sends the next session to the network to find out.
    /// </summary>
    [Fact]
    public async Task The_capture_refuses_a_body_the_parse_cannot_read()
    {
        var options = Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });
        options.Value.Vendor.ApiKey = "answered-by-the-handler-below";

        // Unreadable at the first endpoint the capture asks for, so it throws before it can write
        // anything and the assertion is not about ordering.
        var capture = new FixtureCapture(
            Answering("{\"this\": \"is not a symbol list\"}"),
            _connections,
            new RunLogger(_clock, options),
            _clock,
            options);

        using var destination = new TemporaryDirectory();

        VendorException thrown = await Assert.ThrowsAsync<VendorException>(
            () => capture.CaptureAsync(AsOf, destination.Path));

        Assert.Contains("will not shape", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("exchange-symbol-list", thrown.Message, StringComparison.Ordinal);

        // And nothing was stored, so a refused capture leaves no half-written fixture behind.
        Assert.Empty(Directory.EnumerateFiles(destination.Path, "exchange-symbol-list.json"));
    }

    /// <summary>
    /// The captured response is one of the authored shapes, so the two cannot drift apart.
    ///
    /// Without this the authored file could describe a space the real capture is no longer in, and
    /// both would keep passing.
    /// </summary>
    [Fact]
    public void The_captured_response_is_one_of_the_authored_shapes()
    {
        string captured = File.ReadAllText(
            Path.Combine(RepositoryLayout.Fixtures, "fundamentals-MUZ.json")).Trim();

        Assert.Contains(Shapes().Select(row => (string)row[1]!), body => body == captured);
    }

    private SectorResolver Resolver(IMarketDataVendor vendor)
    {
        var options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = 5000,
        });

        return new SectorResolver(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    /// <summary>The real client over a transport that answers every request with one body.</summary>
    private static EodhdClient Answering(string body)
    {
        var options = new PullbackStrategyLabOptions();
        options.Vendor.ApiKey = "answered-by-the-handler-below";

        var http = new HttpClient(new OneBodyHandler(body))
        {
            BaseAddress = new Uri(options.Vendor.BaseAddress),
        };

        return new EodhdClient(http, Options.Create(options));
    }

    private void Scanned(string ticker)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using SqliteCommand security = connection.CreateCommand();
        security.CommandText = """
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@t, @t, 'US', 'Common Stock', @d)
            """;
        security.Parameters.AddWithValue("@t", ticker);
        security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
        security.ExecuteNonQuery();

        using SqliteCommand hit = connection.CreateCommand();
        hit.CommandText =
            "INSERT INTO scan_hit (as_of, ticker, scan, magnitude, rank) VALUES (@d, @t, 'gainer', '1.0', 1)";
        hit.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
        hit.Parameters.AddWithValue("@t", ticker);
        hit.ExecuteNonQuery();
    }

    private bool Stamped(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sector_resolved_at FROM security WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);
        return command.ExecuteScalar() is string;
    }

    /// <summary>Answers every request with one body and a 200, so the parse is the only thing under test.</summary>
    private sealed class OneBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
