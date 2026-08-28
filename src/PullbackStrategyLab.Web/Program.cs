using System.Globalization;
using System.Text.Json;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web;

/// <summary>
/// The pages. Server-rendered with no build step, and no reference to the Data assembly:
/// everything it shows arrives from the Api over HTTP through a typed client whose base
/// address is configured.
/// see: Pages are server-rendered with no build step, and any script is local rather than fetched
/// see: The Web project reads through the Api and never opens the store
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        // The content root is where the binary sits, for the same reason the Worker's is: a
        // configuration file found by the current directory is found on one machine and missed
        // on the other.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.AddPullbackStrategyLab();

        PullbackStrategyLabOptions options = builder.Configuration
            .GetSection(PullbackStrategyLabOptions.SectionName)
            .Get<PullbackStrategyLabOptions>() ?? new PullbackStrategyLabOptions();

        builder.Services.AddRazorPages();
        builder.Services.AddHttpClient<LabApiClient>(client =>
        {
            client.BaseAddress = new Uri(options.Api.BaseAddress, UriKind.Absolute);

            // Every page load reads the status band, so a read surface that is down must cost a
            // page a moment rather than the client's hundred-second default. The band says so
            // and the page renders; a page that hung until the default expired would be a page
            // nobody could use to find out what was wrong.
            client.Timeout = TimeSpan.FromSeconds(LabApiClient.ReadTimeoutSeconds);
        });

        WebApplication app = builder.Build();
        app.UseStaticFiles();
        app.MapRazorPages();
        app.Run();
    }
}

/// <summary>
/// The one way a page reaches the store's contents. No page holds a store connection,
/// so a page cannot become a second connection to a file the Worker is writing.
/// </summary>
public sealed class LabApiClient
{
    /// <summary>How long a page waits for the read surface before rendering without its figures.</summary>
    public const int ReadTimeoutSeconds = 3;

    private readonly HttpClient _http;

    public LabApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public Uri? BaseAddress => _http.BaseAddress;

    public async Task<string> ReadHealthAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the status band reads, on every page load.
    ///
    /// It never throws. The Api and the pages are two hosts started separately, so one being
    /// down is an ordinary state of the machine, and a shell that would not render without the
    /// read surface would be a shell nobody could use to find out that the read surface was
    /// down.
    /// </summary>
    public async Task<LabStatusView> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("/status", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return LabStatusView.Down($"the read surface answered {(int)response.StatusCode}");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            StatusPayload? payload = await JsonSerializer
                .DeserializeAsync<StatusPayload>(body, Json, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                return LabStatusView.Down("the read surface answered with nothing");
            }

            return new LabStatusView(
                true,
                null,
                payload.Store,
                payload.SchemaVersion,
                payload.Session,
                payload.LastRun?.Stage,
                payload.LastRun?.Outcome,
                payload.UniverseMembers,
                payload.BarsStored,
                payload.CallsUsed,
                payload.DailyCallCeiling,
                payload.MarketMood,
                payload.PositionsOpen,
                payload.ShortPositionsOpen,
                payload.RiskAtStake);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return LabStatusView.Down($"the read surface at {BaseAddress} did not answer");
        }
    }

    /// <summary>
    /// One stock's window. Answers with a reason rather than throwing, for the same reason the
    /// status read does: a ticker the store has never held is an ordinary thing to ask for.
    /// </summary>
    public async Task<ChartView> ReadChartAsync(string ticker, int sessions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync($"/chart/{Uri.EscapeDataString(ticker)}?sessions={sessions}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ChartView.Empty(ticker, $"the read surface answered {(int)response.StatusCode}");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            ChartPayload? payload = await JsonSerializer
                .DeserializeAsync<ChartPayload>(body, Json, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                return ChartView.Empty(ticker, "the read surface answered with nothing");
            }

            if (payload.Nothing is not null)
            {
                return ChartView.Empty(payload.Ticker, payload.Nothing);
            }

            return new ChartView(
                payload.Ticker,
                payload.AsOf,
                payload.Drawn,
                payload.Read,
                [.. payload.Bars.Select(b => new Candle(
                    DateOnly.ParseExact(b.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    b.Open, b.High, b.Low, b.Close))],
                [.. payload.Averages.Select(a => new AverageLine(a.Name, a.Values))],
                payload.Readout is null
                    ? null
                    : new ChartReadoutView(
                        payload.Readout.AsOf,
                        payload.Readout.Ema9,
                        payload.Readout.Ema21,
                        payload.Readout.Ema50,
                        payload.Readout.Atr14,
                        payload.Readout.Adr20,
                        payload.Readout.DollarVolumeMedian,
                        payload.Readout.RangeAverage),
                null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            return ChartView.Empty(ticker, $"the read surface at {BaseAddress} did not answer");
        }
    }

    /// <summary>
    /// A night's setups, both directions. Answers with a reason rather than throwing, on the same
    /// terms as every other read here: a night with nothing flagged is an ordinary state.
    /// </summary>
    /// <summary>
    /// One day's scoreboard panels.
    ///
    /// It never throws, on the same terms as the status band: the Api and the pages are two hosts
    /// started separately, and a page that would not render without the read surface is a page
    /// nobody can use to find out the read surface is down.
    /// </summary>
    public async Task<ScoreboardView> ReadScoreboardAsync(
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        string session = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync($"/scoreboard/{session}", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ScoreboardView.Empty(session, $"the read surface answered {(int)response.StatusCode}");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            ScoreboardPayload? payload = await JsonSerializer
                .DeserializeAsync<ScoreboardPayload>(body, Json, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                return ScoreboardView.Empty(session, "the read surface answered with nothing");
            }

            if (payload.Absent is not null)
            {
                return ScoreboardView.Empty(session, payload.Absent);
            }

            return new ScoreboardView(
                session,
                null,
                Panels(payload.Health),
                Panels(payload.Long),
                Panels(payload.Short));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return ScoreboardView.Empty(session, "the read surface did not answer");
        }
    }

    private static IReadOnlyList<PanelView> Panels(IReadOnlyList<PanelPayload>? panels) =>
        panels is null
            ? []
            : [.. panels.Select(p => new PanelView(
                p.Name, p.Direction, p.Figure, p.Low, p.High, p.Rows, p.Effective,
                p.Population ?? "population not recorded", p.Minimum, p.WithheldBecause))];

    public async Task<SetupsView> ReadSetupsAsync(
        DateOnly asOf,
        string? failedCheck,
        CancellationToken cancellationToken = default)
    {
        string session = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string filter = string.IsNullOrWhiteSpace(failedCheck)
            ? string.Empty
            : "?failed=" + Uri.EscapeDataString(failedCheck);

        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync($"/setups/{session}{filter}", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return SetupsView.Empty(session, $"the read surface answered {(int)response.StatusCode}");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            SetupsPayload? payload = await JsonSerializer
                .DeserializeAsync<SetupsPayload>(body, Json, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                return SetupsView.Empty(session, "the read surface answered with nothing");
            }

            if (payload.Nothing is not null)
            {
                return SetupsView.Empty(payload.AsOf ?? session, payload.Nothing);
            }

            // The lists are read defensively, because a read surface answering with a different
            // shape is a thing that happens and a page that threw on it would take the whole
            // screen down rather than saying what it got.
            return new SetupsView(
                payload.AsOf ?? session,
                payload.FailedCheck,
                payload.Flagged,
                [.. (payload.Long ?? []).Select(Card)],
                [.. (payload.Short ?? []).Select(Card)],
                payload.CheckNames ?? [],
                null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            return SetupsView.Empty(session, $"the read surface at {BaseAddress} did not answer");
        }
    }

    /// <summary>
    /// What a person thought of one setup, on its way to the one column pair the read surface writes.
    ///
    /// Returns the reason rather than throwing, because the page that called it is mid-render of a
    /// night the person is working through and an exception would lose the rest of it.
    /// see: The agreement a person records is written through the read surface, and it is the only write it makes
    /// </summary>
    public async Task<string?> RecordAgreementAsync(
        string setupId,
        string? agreement,
        string? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupId);

        try
        {
            using HttpResponseMessage response = await _http
                .PostAsJsonAsync(
                    $"/setups/{Uri.EscapeDataString(setupId)}/agreement",
                    new { agreement, note },
                    Json,
                    cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? null
                : $"the read surface answered {(int)response.StatusCode}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"the read surface at {BaseAddress} did not answer";
        }
    }

    private static SetupCardView Card(SetupPayload s) => new(
        s.SetupId,
        s.Ticker,
        s.Direction,
        s.Rank,
        s.CappedOut,
        s.PassedAll,
        s.TriggerPrice,
        s.StopPrice,
        s.StopDistanceRanges,
        s.Agreement,
        s.AgreementNote,
        [.. s.Checks.Select(c => new SetupCheckRowView(c.Name, c.Passed, c.Value, c.Note))],
        [.. s.Candles.Select(c => new Candle(
            DateOnly.ParseExact(c.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            c.Open, c.High, c.Low, c.Close))]);

    private sealed record ScoreboardPayload(
        string AsOf,
        string? Absent,
        IReadOnlyList<PanelPayload>? Health,
        IReadOnlyList<PanelPayload>? Long,
        IReadOnlyList<PanelPayload>? Short);

    private sealed record PanelPayload(
        string Name, string? Direction, string Figure, string? Low, string? High, int Rows,
        int? Effective, string? Population, int? Minimum, string? WithheldBecause);

    private sealed record SetupsPayload(
        string? AsOf,
        string? FailedCheck,
        int Flagged,
        IReadOnlyList<SetupPayload>? Long,
        IReadOnlyList<SetupPayload>? Short,
        IReadOnlyList<string>? CheckNames,
        string? Nothing);

    private sealed record SetupPayload(
        string SetupId,
        string Ticker,
        string Direction,
        int? Rank,
        bool? CappedOut,
        bool PassedAll,
        decimal? TriggerPrice,
        decimal? StopPrice,
        decimal? StopDistanceRanges,
        string? Agreement,
        string? AgreementNote,
        IReadOnlyList<SetupCheckPayload> Checks,
        IReadOnlyList<SetupCandlePayload> Candles);

    private sealed record SetupCheckPayload(string Name, bool Passed, decimal? Value, string? Note);

    private sealed record SetupCandlePayload(string Date, decimal Open, decimal High, decimal Low, decimal Close);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ChartPayload(
        string Ticker,
        string AsOf,
        int Requested,
        int Drawn,
        int Read,
        IReadOnlyList<ChartBarPayload> Bars,
        IReadOnlyList<ChartAveragePayload> Averages,
        ChartReadoutPayload? Readout,
        string? Nothing);

    private sealed record ChartBarPayload(string Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

    private sealed record ChartAveragePayload(string Name, int Period, IReadOnlyList<decimal?> Values);

    private sealed record ChartReadoutPayload(
        string AsOf,
        decimal Ema9,
        decimal Ema21,
        decimal Ema50,
        decimal Atr14,
        decimal Adr20,
        decimal DollarVolumeMedian,
        decimal RangeAverage);

    /// <summary>The wire shape, which is all the two hosts share.</summary>
    private sealed record StatusPayload(
        string Store,
        int SchemaVersion,
        string? Session,
        RunPayload? LastRun,
        long UniverseMembers,
        long BarsStored,
        int CallsUsed,
        int DailyCallCeiling,
        string? MarketMood,
        int? PositionsOpen,
        int? ShortPositionsOpen,
        decimal? RiskAtStake);

    private sealed record RunPayload(string Stage, string StartedAt, string? EndedAt, string Outcome, int CallsUsed);
}
