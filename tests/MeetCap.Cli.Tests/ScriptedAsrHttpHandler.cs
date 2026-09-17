using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Transport stand-in for the Volcengine file-ASR adapter, used by the M4 live-transcription
/// CLI tests.
/// </summary>
/// <remarks>
/// <para>
/// Replacing the HTTP boundary rather than the provider keeps everything above it real: the
/// command composition, the SQLite job queue, the batch builder, the retained raw response,
/// the normalizer, and the transcript writer are all the shipped implementations
/// (<c>docs/DEVELOPMENT.md</c> section 7 requires the provider boundary to be mocked in CI,
/// because no Volcengine credentials exist here).
/// </para>
/// <para>
/// <see cref="Offline"/> models the documented network-loss behaviour: every request fails at
/// the transport layer, so the queue keeps its durable state without any recording impact
/// (<c>docs/RELIABILITY.md</c> section 9).
/// </para>
/// </remarks>
internal sealed class ScriptedAsrHttpHandler : HttpMessageHandler
{
    private const string SuccessStatus = "20000000";
    private const string QueuedStatus = "20000001";

    private readonly List<string> _requestBodies = new();

    /// <summary>When true, every request fails as if the network were down.</summary>
    public bool Offline { get; set; }

    /// <summary>Number of submit requests that reached the transport.</summary>
    public int Submits { get; private set; }

    /// <summary>Number of query requests that reached the transport.</summary>
    public int Queries { get; private set; }

    /// <summary>Inline audio payload sizes per submit, in bytes.</summary>
    public List<long> SubmittedAudioBytes { get; } = new();

    /// <summary>The text the provider "recognized", returned as one utterance.</summary>
    public string RecognizedText { get; set; } = "hello from the meeting";

    public void Reset()
    {
        Offline = false;
        Submits = 0;
        Queries = 0;
        SubmittedAudioBytes.Clear();
        _requestBodies.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        if (Offline)
        {
            // Transport-level failure, exactly what a lost network produces: the adapter
            // classifies it as transient and the durable job state records it.
            throw new HttpRequestException("the network is unavailable (scripted test outage)");
        }

        if (path.EndsWith("/submit", StringComparison.Ordinal))
        {
            Submits++;
            _requestBodies.Add(body);
            SubmittedAudioBytes.Add(InlineAudioBytes(body));
            return Build(HttpStatusCode.OK, """{"result":{"text":""}}""", SuccessStatus);
        }

        if (path.EndsWith("/query", StringComparison.Ordinal))
        {
            Queries++;
            _requestBodies.Add(body);
            var response = new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["text"] = RecognizedText,
                    ["audio_info"] = new JsonObject { ["duration"] = 1000 },
                    ["utterances"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["text"] = RecognizedText,
                            ["start_time"] = 0,
                            ["end_time"] = 1000,
                            ["speaker"] = "1",
                        },
                    },
                },
            };

            return Build(HttpStatusCode.OK, response.ToJsonString(), SuccessStatus);
        }

        // The adapter only speaks submit/query; anything else is a scripted surprise.
        return Build(HttpStatusCode.OK, "{}", QueuedStatus);
    }

    private static long InlineAudioBytes(string body)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();
            var data = root?["audio"]?["data"]?.GetValue<string>();
            if (string.IsNullOrEmpty(data))
            {
                return 0;
            }

            // base64 length => decoded byte count, counting the padding characters.
            var padding = data.EndsWith("==", StringComparison.Ordinal)
                ? 2
                : data.EndsWith('=') ? 1 : 0;
            return (data.Length / 4 * 3) - padding;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return 0;
        }
    }

    private static HttpResponseMessage Build(HttpStatusCode statusCode, string body, string apiStatus)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        response.Headers.TryAddWithoutValidation("X-Api-Status-Code", apiStatus);
        return response;
    }

    /// <summary>Bodies seen by the transport, for asserting the sanitized request shape.</summary>
    public IReadOnlyList<string> RequestBodies => _requestBodies;
}
