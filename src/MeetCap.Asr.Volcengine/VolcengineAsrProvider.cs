namespace MeetCap.Asr.Volcengine;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetCap.Core.Asr;
using Polly;
using Polly.Retry;
using Polly.Timeout;

/// <summary>
/// Volcengine file-ASR adapter: submit/query lifecycle for Seed-ASR 2.0 recording-file
/// Standard recognition.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place provider field names, endpoint paths, resource ids, headers,
/// status codes, and speaker-info flags appear
/// (<c>docs/ARCHITECTURE.md</c> sections 11 and 12). Domain code consumes
/// <see cref="AsrSubmission"/>, <see cref="AsrPollResult"/>, and normalized
/// <c>TranscriptSegment</c> objects only.
/// </para>
/// <para>
/// The wire contract is fixed by <c>docs/ASR_STRATEGY.md</c> section 2: Seed-ASR 2.0,
/// recording-file Standard HTTP, <c>X-Api-Key</c> authentication, and the resource id
/// <c>volc.seedasr.auc</c>. Legacy <c>X-Api-App-Key</c>/<c>X-Api-Access-Key</c> headers,
/// <c>volc.bigasr.auc</c>, idle routing and flash/turbo routing are not compatibility
/// modes: there is no auth-mode switch and no tier selector to route with.
/// </para>
/// <para>
/// Polly is used <b>only</b> for transient HTTP execution: retry with exponential
/// backoff and jitter, plus a per-request timeout. It is explicitly not the ASR job
/// queue; durable retry state lives in <c>asr_jobs</c>.
/// </para>
/// <para>
/// No streaming endpoint exists in this adapter. A failed file-ASR request is retried
/// or failed; it is never converted into a streaming request
/// (<c>docs/ASR_STRATEGY.md</c> section 7).
/// </para>
/// <para>
/// The provider task id is supplied by the caller (the persistent job) instead of
/// being generated per HTTP attempt, so a retry — including a retry after a process
/// restart — addresses the same task rather than creating a second billable one.
/// </para>
/// </remarks>
public sealed class VolcengineAsrProvider : IAsrProvider, IDisposable
{
    internal const string StatusCodeHeader = "X-Api-Status-Code";
    internal const string MessageHeader = "X-Api-Message";

    /// <summary>Provider-side trace id, retained for support diagnostics.</summary>
    internal const string LogIdHeader = "X-Tt-Logid";

    internal const string ResourceIdHeader = "X-Api-Resource-Id";
    internal const string RequestIdHeader = "X-Api-Request-Id";
    internal const string SequenceHeader = "X-Api-Sequence";
    internal const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Request accepted and completed.</summary>
    internal const string StatusSuccess = "20000000";

    /// <summary>Request accepted; the task is still queued or running.</summary>
    internal const string StatusQueued = "20000001";

    internal const string StatusProcessing = "20000002";

    /// <summary>The provider recognised the audio as silent and returned no text.</summary>
    internal const string StatusSilentAudio = "20000003";

    /// <summary>Provider error codes that will not succeed on retry.</summary>
    private static readonly HashSet<string> s_permanentStatusCodes = new(StringComparer.Ordinal)
    {
        // Invalid request parameters.
        "45000001",
        // Empty or unusable audio.
        "45000002",
    };

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly VolcengineAsrOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ResiliencePipeline<VolcengineResponse> _pipeline;
    private readonly IAsrAudioPublisher _audioPublisher;

    public VolcengineAsrProvider(VolcengineAsrOptions options, HttpMessageHandler? handler = null, IAsrAudioPublisher? audioPublisher = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);

        _ownsHttpClient = handler is not null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // Polly owns the request deadline, so HttpClient's own timeout is disabled to
        // keep exactly one timeout semantic in play.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _audioPublisher = audioPublisher ?? new VolcengineTosAudioPublisher(null);

        _pipeline = new ResiliencePipelineBuilder<VolcengineResponse>()
            .AddRetry(new RetryStrategyOptions<VolcengineResponse>
            {
                MaxRetryAttempts = options.MaxTransientAttempts,
                Delay = options.InitialBackoff,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<VolcengineResponse>()
                    .Handle<AsrTransientException>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>(),
            })
            .AddTimeout(options.HttpTimeout)
            .Build();
    }

    public string Name => VolcengineAsrProviderFactory.ProviderName;

    public async Task<AsrSubmission> SubmitFileAsync(
        AsrFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var submitPath = _options.SubmitEndpoint;

        var audio = new FileInfo(request.InputArtifactPath);
        if (!audio.Exists)
        {
            throw new AsrPermanentException(
                "audio.missing",
                $"Audio artifact '{request.InputArtifactPath}' no longer exists, so job '{request.JobId}' " +
                "cannot be submitted. Re-import the recording.");
        }

        var published = await _audioPublisher.PublishAsync(request, cancellationToken).ConfigureAwait(false);
        var body = BuildRequestBody(request, published);
        var sanitized = BuildSanitizedRequestJson(request, published);

        var response = await SendAsync(
            submitPath,
            body,
            request.ProviderRequestId,
            // The official Standard HTTP contract identifies the submit as sequence -1.
            sequence: -1,
            cancellationToken).ConfigureAwait(false);

        var status = response.ApiStatus;
        if (status == StatusSuccess)
        {
            return new AsrSubmission
            {
                ProviderRequestId = request.ProviderRequestId,
                SanitizedRequestJson = sanitized,
                ProviderLogId = response.LogId,
            };
        }

        throw Failure(status, response);
    }

    public async Task<AsrPollResult> GetResultAsync(
        AsrSubmission submission,
        AsrFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(request);

        // The query addresses the same task by the same request id and sends no
        // X-Api-Sequence, which is what the official document specifies for query.
        var response = await SendAsync(
            _options.QueryEndpoint,
            "{}",
            submission.ProviderRequestId,
            sequence: null,
            cancellationToken).ConfigureAwait(false);

        var status = response.ApiStatus;
        if (status == StatusSuccess)
        {
            return AsrPollResult.Completed(new AsrCompletion(response.Body, status, response.LogId));
        }

        if (status is StatusQueued or StatusProcessing)
        {
            return AsrPollResult.Pending();
        }

        if (status == StatusSilentAudio)
        {
            // The provider detected silence and returned no text. This is a completed
            // result with zero segments, not a failure: the recording is intact and the
            // transcript legitimately has no speech.
            return AsrPollResult.Completed(new AsrCompletion(response.Body, status, response.LogId));
        }

        var error = Failure(status, response);
        return error is AsrTransientException transient
            ? AsrPollResult.Failed(new AsrProviderError(transient.Code, transient.Message, IsTransient: true))
            : AsrPollResult.Failed(new AsrProviderError(
                ((AsrPermanentException)error).Code,
                error.Message,
                IsTransient: false));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private async Task<VolcengineResponse> SendAsync(
        string path,
        string body,
        string requestId,
        int? sequence,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async token => await SendOnceAsync(path, body, requestId, sequence, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is (HttpRequestException or TimeoutRejectedException or TaskCanceledException)
                && !cancellationToken.IsCancellationRequested)
        {
            // The Polly pipeline retries transport failures and then rethrows the original
            // exception, which is an implementation detail of the HTTP stack. The domain side
            // must see the adapter's own classification — "this request failed and may succeed
            // later" — or a lost network would surface as an unhandled exception instead of a
            // durable `retry_wait` job (docs/RELIABILITY.md section 9).
            throw new AsrTransientException(
                "http.unreachable",
                Scrub(
                    $"Provider '{Name}' could not be reached for {path} after " +
                    $"{_options.MaxTransientAttempts} attempt(s): {ex.Message} " +
                    "The job keeps its durable state and is retried when the provider is reachable."),
                ex);
        }
    }

    private async Task<VolcengineResponse> SendOnceAsync(
        string path,
        string body,
        string requestId,
        int? sequence,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, s_utf8, "application/json"),
        };

        // Provider-specific headers stay inside this method. The new-console API key is the
        // only credential, and X-Api-App-Key / X-Api-Access-Key are deliberately never sent:
        // carrying both authentication generations would be the compatibility mode issue #26
        // removes (docs/ASR_STRATEGY.md section 2).
        httpRequest.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.ApiKey);
        httpRequest.Headers.TryAddWithoutValidation(ResourceIdHeader, VolcengineAsrOptions.ResourceId);
        httpRequest.Headers.TryAddWithoutValidation(RequestIdHeader, requestId);
        if (sequence is not null)
        {
            httpRequest.Headers.TryAddWithoutValidation(SequenceHeader, sequence.Value.ToString());
        }

        using var response = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var apiStatus = ReadHeader(response, StatusCodeHeader);
        var apiMessage = ReadHeader(response, MessageHeader);
        var logId = ReadHeader(response, LogIdHeader);

        var statusCode = (int)response.StatusCode;

        // A transport-level rejection is classified from the HTTP status alone. Gating this
        // on the absence of X-Api-Status-Code would make a 401/403 that happens to carry the
        // header retryable, so bad credentials would be retried instead of failing visibly --
        // the opposite of the acceptance criterion this adapter is supposed to satisfy.
        if (!response.IsSuccessStatusCode
            && response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.BadRequest)
        {
            throw new AsrPermanentException(
                $"http.{statusCode}",
                Scrub(
                    $"Volcengine rejected the request with HTTP {statusCode} for {path}" +
                    Describe(apiStatus, apiMessage, logId) +
                    $": {Snippet(responseBody)} Check asr.volcengine.api_key and the resolved " +
                    "new-console API key."));
        }

        if (statusCode is >= 500 or 408 or 429)
        {
            // Transport-level server problems are the classic transient case that Polly
            // exists for.
            throw new AsrTransientException(
                $"http.{statusCode}",
                Scrub(
                    $"Volcengine returned HTTP {statusCode}{Describe(apiStatus, apiMessage, logId)}: " +
                    Snippet(responseBody)));
        }

        if (!response.IsSuccessStatusCode && string.IsNullOrEmpty(apiStatus))
        {
            throw new AsrTransientException(
                $"http.{statusCode}",
                Scrub(
                    $"Volcengine returned HTTP {statusCode} for {path}" +
                    Describe(apiStatus, apiMessage, logId) +
                    $": {Snippet(responseBody)}"));
        }

        return new VolcengineResponse(responseBody, apiStatus ?? string.Empty, apiMessage ?? string.Empty, logId);
    }

    private static string? ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private Exception Failure(string status, VolcengineResponse response)
    {
        var code = string.IsNullOrEmpty(status) ? "provider.unknown" : status;
        var message = Scrub(
            $"Volcengine reported status {code}" +
            Describe(null, response.ApiMessage, response.LogId) +
            $". {Snippet(response.Body)}");
        return s_permanentStatusCodes.Contains(code)
            ? new AsrPermanentException(code, message)
            : new AsrTransientException(code, message);
    }

    private string BuildRequestBody(AsrFileRequest request, AsrPublishedAudio audio)
    {
        var body = new JsonObject
        {
            // MeetCap-owned, non-secret: request.json is persisted locally, so the API key
            // must never be copied into user.uid (docs/CONFIGURATION.md section 8).
            ["user"] = new JsonObject { ["uid"] = VolcengineAsrOptions.UserId },
            ["audio"] = new JsonObject
            {
                ["format"] = request.AudioFormat,
                [audio.Transport == "inline" ? "data" : "url"] = audio.Transport == "inline" ? audio.InlineBase64 : audio.Url,
            },
            ["request"] = BuildProviderRequest(request.RequestSpeakerInfo),
        };

        return body.ToJsonString();
    }

    /// <summary>
    /// The official Standard HTTP <c>request</c> object (Seed-ASR 2.0 recording-file).
    /// </summary>
    private static JsonObject BuildProviderRequest(bool requestSpeakerInfo) => new()
    {
        ["model_name"] = VolcengineAsrOptions.ModelName,
        ["enable_itn"] = true,
        ["enable_punc"] = true,
        ["enable_ddc"] = true,
        ["show_utterances"] = true,
        // Anonymous speaker labels, requested per docs/ASR_STRATEGY.md section 10.
        ["enable_speaker_info"] = requestSpeakerInfo,
    };

    /// <summary>
    /// The request metadata persisted as <c>request.json</c>.
    /// </summary>
    /// <remarks>
    /// The inline audio payload is replaced by its size: the artifact itself already
    /// lives in the session directory, and copying it into the job metadata would
    /// duplicate user audio for no provenance benefit. Headers -- which carry the API
    /// key -- are never included, and the endpoint/resource id are recorded so a later
    /// reader can tell which fixed contract produced the artifact.
    /// </remarks>
    private string BuildSanitizedRequestJson(AsrFileRequest request, AsrPublishedAudio audio)
    {
        var body = new JsonObject
        {
            ["provider"] = Name,
            ["model"] = VolcengineAsrOptions.ModelName,
            ["resource_id"] = VolcengineAsrOptions.ResourceId,
            ["endpoint"] = _options.SubmitEndpoint,
            ["job_id"] = request.JobId,
            ["session_id"] = request.SessionId,
            ["source"] = request.Source,
            ["duration_ms"] = request.DurationMs,
            ["provider_request_id"] = request.ProviderRequestId,
            ["speaker_info_requested"] = request.RequestSpeakerInfo,
            ["audio"] = new JsonObject
            {
                ["format"] = request.AudioFormat,
                ["transport"] = audio.Transport,
                ["bytes"] = audio.Bytes,
                ["inline_bytes"] = audio.Transport == "inline" ? audio.Bytes : null,
                ["tos_bucket"] = audio.Bucket,
                ["tos_object_key"] = audio.ObjectKey,
            },
            ["request"] = BuildProviderRequest(request.RequestSpeakerInfo),
        };

        return Scrub(body.ToJsonString());
    }

    /// <summary>Removes any occurrence of the API key from text that will be stored or logged.</summary>
    private string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_options.ApiKey))
        {
            return text;
        }

        return text.Replace(_options.ApiKey, "***", StringComparison.Ordinal);
    }

    /// <summary>
    /// Formats the provider's status/message/log-id headers for an error message.
    /// </summary>
    /// <remarks>
    /// The log id is included so a provider-side incident can be traced from MeetCap's own
    /// persisted <c>error_message</c> without a second round trip; it is not a secret.
    /// </remarks>
    private static string Describe(string? status, string? message, string? logId)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(status))
        {
            parts.Add(status);
        }

        if (!string.IsNullOrEmpty(message))
        {
            parts.Add(message);
        }

        if (!string.IsNullOrEmpty(logId))
        {
            parts.Add($"X-Tt-Logid={logId}");
        }

        return parts.Count == 0 ? string.Empty : $" ({string.Join(" ", parts)})";
    }

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty body)";
        }

        var trimmed = body.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "...";
    }

    /// <summary>One provider HTTP exchange, already read into memory.</summary>
    internal sealed record VolcengineResponse(
        string Body,
        string ApiStatus,
        string ApiMessage,
        string? LogId)
    {
        /// <summary>Parses the response body as JSON, or returns null when it is not JSON.</summary>
        public JsonDocument? TryParseBody()
        {
            if (string.IsNullOrWhiteSpace(Body))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(Body);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
