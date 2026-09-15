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
/// Volcengine file-ASR adapter: submit/query lifecycle for recording-file recognition.
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
    private const string StatusCodeHeader = "X-Api-Status-Code";
    private const string MessageHeader = "X-Api-Message";

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

    public VolcengineAsrProvider(VolcengineAsrOptions options, HttpMessageHandler? handler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ResourceId);

        _ownsHttpClient = handler is not null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // Polly owns the request deadline, so HttpClient's own timeout is disabled to
        // keep exactly one timeout semantic in play.
        _http.Timeout = Timeout.InfiniteTimeSpan;

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
        var submitPath = _options.ToSubmitPath(request.ServiceTier);

        var audio = new FileInfo(request.InputArtifactPath);
        if (!audio.Exists)
        {
            throw new AsrPermanentException(
                "audio.missing",
                $"Audio artifact '{request.InputArtifactPath}' no longer exists, so job '{request.JobId}' " +
                "cannot be submitted. Re-import the recording.");
        }

        if (audio.Length > _options.MaxInlineAudioBytes)
        {
            throw new AsrConfigurationException(
                $"Audio artifact '{request.InputArtifactPath}' is {audio.Length} bytes, above the " +
                $"{_options.MaxInlineAudioBytes}-byte inline upload limit. Import a shorter recording; " +
                "splitting long imports is not implemented yet.");
        }

        byte[] audioBytes;
        try
        {
            audioBytes = await File.ReadAllBytesAsync(request.InputArtifactPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new AsrTransientException(
                "audio.unreadable",
                $"Audio artifact '{request.InputArtifactPath}' could not be read: {ex.Message}",
                ex);
        }

        var body = BuildRequestBody(request, Convert.ToBase64String(audioBytes));
        var sanitized = BuildSanitizedRequestJson(request, audio.Length);

        var response = await SendAsync(
            submitPath,
            body,
            request.ProviderRequestId,
            sequence: -1,
            cancellationToken).ConfigureAwait(false);

        var status = response.ApiStatus;
        if (status == StatusSuccess)
        {
            return new AsrSubmission
            {
                ProviderRequestId = request.ProviderRequestId,
                SanitizedRequestJson = sanitized,
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

        var queryPath = _options.ToQueryPath(request.ServiceTier);
        var response = await SendAsync(
            queryPath,
            "{}",
            submission.ProviderRequestId,
            sequence: null,
            cancellationToken).ConfigureAwait(false);

        var status = response.ApiStatus;
        if (status == StatusSuccess)
        {
            return AsrPollResult.Completed(new AsrCompletion(response.Body, status));
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
            return AsrPollResult.Completed(new AsrCompletion(response.Body, status));
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
        return await _pipeline.ExecuteAsync(
            async token => await SendOnceAsync(path, body, requestId, sequence, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
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

        // Provider-specific headers stay inside this method.
        httpRequest.Headers.TryAddWithoutValidation("X-Api-App-Key", _options.AppId);
        httpRequest.Headers.TryAddWithoutValidation("X-Api-Access-Key", _options.AccessToken);
        httpRequest.Headers.TryAddWithoutValidation("X-Api-Resource-Id", _options.ResourceId);
        httpRequest.Headers.TryAddWithoutValidation("X-Api-Request-Id", requestId);
        if (sequence is not null)
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Api-Sequence", sequence.Value.ToString());
        }

        using var response = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var apiStatus = response.Headers.TryGetValues(StatusCodeHeader, out var statusValues)
            ? statusValues.FirstOrDefault()
            : null;
        var apiMessage = response.Headers.TryGetValues(MessageHeader, out var messageValues)
            ? messageValues.FirstOrDefault()
            : null;

        var statusCode = (int)response.StatusCode;
        if (statusCode is >= 500 or 408 or 429)
        {
            // Transport-level server problems are the classic transient case that Polly
            // exists for.
            throw new AsrTransientException(
                $"http.{statusCode}",
                Scrub(
                    $"Volcengine returned HTTP {statusCode}{Describe(apiStatus, apiMessage)}: " +
                    Snippet(responseBody)));
        }

        if (!response.IsSuccessStatusCode && string.IsNullOrEmpty(apiStatus))
        {
            var code = $"http.{statusCode}";
            var message = Scrub(
                $"Volcengine returned HTTP {statusCode} for {path}: {Snippet(responseBody)}" +
                (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? " Check asr.volcengine.app_id and the resolved credential."
                    : string.Empty));

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.BadRequest)
            {
                throw new AsrPermanentException(code, message);
            }

            throw new AsrTransientException(code, message);
        }

        return new VolcengineResponse(responseBody, apiStatus ?? string.Empty, apiMessage ?? string.Empty);
    }

    private Exception Failure(string status, VolcengineResponse response)
    {
        var code = string.IsNullOrEmpty(status) ? "provider.unknown" : status;
        var message = Scrub(
            $"Volcengine reported status {code} ({response.ApiMessage}). {Snippet(response.Body)}");
        return s_permanentStatusCodes.Contains(code)
            ? new AsrPermanentException(code, message)
            : new AsrTransientException(code, message);
    }

    private string BuildRequestBody(AsrFileRequest request, string audioBase64)
    {
        var body = new JsonObject
        {
            ["user"] = new JsonObject { ["uid"] = _options.AppId },
            ["audio"] = new JsonObject
            {
                ["format"] = request.AudioFormat,
                ["data"] = audioBase64,
            },
            ["request"] = new JsonObject
            {
                ["model_name"] = "bigmodel",
                ["enable_itn"] = true,
                ["enable_punc"] = true,
                ["enable_ddc"] = true,
                ["show_utterances"] = true,
                // Anonymous speaker labels where the configured tier supports them.
                ["enable_speaker_info"] = request.RequestSpeakerInfo,
            },
        };

        return body.ToJsonString();
    }

    /// <summary>
    /// The request metadata persisted as <c>request.json</c>.
    /// </summary>
    /// <remarks>
    /// The inline audio payload is replaced by its size: the artifact itself already
    /// lives in the session directory, and copying it into the job metadata would
    /// duplicate user audio for no provenance benefit. Headers -- which carry the
    /// access token -- are never included.
    /// </remarks>
    private string BuildSanitizedRequestJson(AsrFileRequest request, long audioBytes)
    {
        var body = new JsonObject
        {
            ["provider"] = Name,
            ["endpoint_tier"] = request.ServiceTier,
            ["resource_id"] = _options.ResourceId,
            ["job_id"] = request.JobId,
            ["session_id"] = request.SessionId,
            ["source"] = request.Source,
            ["duration_ms"] = request.DurationMs,
            ["provider_request_id"] = request.ProviderRequestId,
            ["speaker_info_requested"] = request.RequestSpeakerInfo,
            ["audio"] = new JsonObject
            {
                ["format"] = request.AudioFormat,
                ["inline_bytes"] = audioBytes,
            },
            ["request"] = new JsonObject
            {
                ["model_name"] = "bigmodel",
                ["enable_itn"] = true,
                ["enable_punc"] = true,
                ["enable_ddc"] = true,
                ["show_utterances"] = true,
                ["enable_speaker_info"] = request.RequestSpeakerInfo,
            },
        };

        return Scrub(body.ToJsonString());
    }

    /// <summary>Removes any occurrence of the access token from text that will be stored or logged.</summary>
    private string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_options.AccessToken))
        {
            return text;
        }

        return text.Replace(_options.AccessToken, "***", StringComparison.Ordinal);
    }

    private static string Describe(string? status, string? message) =>
        string.IsNullOrEmpty(status) && string.IsNullOrEmpty(message)
            ? string.Empty
            : $" ({status} {message})".TrimEnd();

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
    internal sealed record VolcengineResponse(string Body, string ApiStatus, string ApiMessage)
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
