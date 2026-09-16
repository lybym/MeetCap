using System.Net;
using System.Text;

namespace MeetCap.Asr.Volcengine.Tests;

/// <summary>
/// Minimal in-memory <see cref="HttpMessageHandler"/>: every request is recorded and
/// answered from a queue of scripted responses.
/// </summary>
/// <remarks>
/// The provider boundary is mocked here exactly as docs/DEVELOPMENT.md section 7
/// requires: no Volcengine credentials are available in this environment, so no test
/// may reach the live service.
/// </remarks>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Request bodies captured as text, indexed like <see cref="Requests"/>.</summary>
    public List<string> RequestBodies { get; } = new();

    public StubHttpHandler Enqueue(HttpStatusCode statusCode, string body = "", string? apiStatus = null, string? apiMessage = null)
    {
        _responses.Enqueue(_ => Build(statusCode, body, apiStatus, apiMessage));
        return this;
    }

    public StubHttpHandler EnqueueJson(HttpStatusCode statusCode, string body, string apiStatus = "20000000", string? apiMessage = null)
    {
        _responses.Enqueue(_ => Build(statusCode, body, apiStatus, apiMessage));
        return this;
    }

    public StubHttpHandler EnqueueRepeat(int times, HttpStatusCode statusCode, string body = "", string? apiStatus = null)
    {
        for (var i = 0; i < times; i++)
        {
            Enqueue(statusCode, body, apiStatus);
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"StubHttpHandler has no scripted response left for {request.RequestUri}.");
        }

        return _responses.Dequeue()(request);
    }

    private static HttpResponseMessage Build(
        HttpStatusCode statusCode,
        string body,
        string? apiStatus,
        string? apiMessage)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (apiStatus is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Api-Status-Code", apiStatus);
        }

        if (apiMessage is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Api-Message", apiMessage);
        }

        return response;
    }
}
