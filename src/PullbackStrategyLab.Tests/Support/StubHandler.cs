using System.Net;
using System.Text;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// A transport that answers from a function rather than from the network.
///
/// The shell reads the status band on every page load, so a test that let those requests reach
/// a socket would spend its time waiting for a port nobody is listening on, and would test the
/// timeout rather than the page. Answering here makes both states of the band, up and down,
/// something a test can ask for.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

    /// <summary>Answers every request with this JSON body and a 200.</summary>
    public static StubHandler Json(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    });

    /// <summary>Answers every request with this status and no body.</summary>
    public static StubHandler Status(HttpStatusCode code) => new(_ => new HttpResponseMessage(code));

    /// <summary>Fails the way a host that is not listening fails.</summary>
    public static StubHandler NotListening() =>
        new(_ => throw new HttpRequestException("connection refused"));

    /// <summary>Requests this handler was asked for, in order.</summary>
    public List<string> Asked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Asked.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
        return Task.FromResult(_answer(request));
    }
}
