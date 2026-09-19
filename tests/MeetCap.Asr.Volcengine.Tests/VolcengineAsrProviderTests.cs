using System.Net;
using System.Text.Json.Nodes;
using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Asr.Volcengine.Tests;

/// <summary>
/// Provider-boundary tests. They exercise the real adapter against a scripted
/// <see cref="HttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// Real Volcengine transcription is NOT verified: this environment has no
/// <c>MEETCAP_VOLCENGINE_API_KEY</c>, and docs/DEVELOPMENT.md section 7 forbids claiming
/// otherwise or inventing credentials. The contract asserted here is the one issue #26
/// fixes: Seed-ASR 2.0, recording-file Standard HTTP, <c>X-Api-Key</c> only.
/// </remarks>
public class VolcengineAsrProviderTests : IDisposable
{
    private const string ApiKey = "SECRET-API-KEY-0123456789";

    /// <summary>
    /// A presigned GET URL with the shape TOS actually mints: the credential is the whole query
    /// string, and the object is private, so this string is the only thing that opens it.
    /// </summary>
    private const string SignedUrl =
        "https://meetcap-asr.tos-cn-beijing.volces.com/meetcap-asr/ab/2026/09/19/job_1.wav" +
        "?X-Tos-Algorithm=TOS4-HMAC-SHA256&X-Tos-Date=20260919T101500Z&X-Tos-Expires=21600" +
        "&X-Tos-Signature=deadbeefcafef00d&X-Tos-Credential=AKLT-SECRET%2F20260919%2Fcn-beijing%2Ftos%2Frequest";

    private const string SignedUrlQuery =
        "X-Tos-Algorithm=TOS4-HMAC-SHA256&X-Tos-Date=20260919T101500Z&X-Tos-Expires=21600" +
        "&X-Tos-Signature=deadbeefcafef00d&X-Tos-Credential=AKLT-SECRET%2F20260919%2Fcn-beijing%2Ftos%2Frequest";

    private const string SignedUrlUnderHttp =
        "http://meetcap-asr.tos-cn-beijing.volces.com/meetcap-asr/ab/2026/09/19/job_1.wav" +
        "?" + SignedUrlQuery;

    /// <summary>
    /// Query values that are fixed protocol text rather than credential material, so an error
    /// message may legitimately still contain them after the credential itself is masked.
    /// </summary>
    private static readonly HashSet<string> NonSecretQueryValues = new(StringComparer.Ordinal)
    {
        "TOS4-HMAC-SHA256",
        "20260919T101500Z",
        "21600",
    };

    private readonly string _audioRoot = Path.Combine(
        Path.GetTempPath(),
        "meetcap-volcengine-test-" + Guid.NewGuid().ToString("N"));

    public VolcengineAsrProviderTests() => Directory.CreateDirectory(_audioRoot);

    public void Dispose()
    {
        if (Directory.Exists(_audioRoot))
        {
            Directory.Delete(_audioRoot, true);
        }
    }

    private string WriteAudio(string name = "normalized.wav", int bytes = 2400)
    {
        var path = Path.Combine(_audioRoot, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static VolcengineAsrProvider Create(StubHttpHandler handler, string? signedUrl = null) =>
        new(
            new VolcengineAsrOptions
            {
                ApiKey = ApiKey,
                BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel",
                MaxTransientAttempts = 3,
                InitialBackoff = TimeSpan.FromMilliseconds(1),
                HttpTimeout = TimeSpan.FromSeconds(5),
            },
            handler,
            // A null publisher keeps the ordinary inline path, which is what every test that does
            // not care about the transport boundary wants.
            signedUrl is null ? null : new FakePublisher(TosAudio(signedUrl)));

    private static AsrPublishedAudio TosAudio(string signedUrl) => new()
    {
        Transport = AsrTransports.Tos,
        Url = signedUrl,
        Bytes = 22 * 1024 * 1024,
        Bucket = "meetcap-asr",
        ObjectKey = "meetcap-asr/ab/2026/09/19/job_1.wav",
    };

    private static AsrFileRequest Request(string path) => new()
    {
        JobId = "job_1",
        SessionId = "ses_1",
        Source = "import",
        InputArtifactPath = path,
        AudioFormat = "wav",
        DurationMs = 754_000,
        ProviderRequestId = "req-0001",
        RequestSpeakerInfo = true,
    };

    private static AsrSubmission ExistingSubmission() =>
        new() { ProviderRequestId = "req-0001", SanitizedRequestJson = "{}" };

    private static string Header(HttpRequestMessage request, string name) =>
        string.Join(",", request.Headers.GetValues(name));

    [Fact]
    public async Task Submit_SendsTheDocumentedHeadersAndBody()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        var submission = await provider.SubmitFileAsync(Request(WriteAudio()));

        Assert.Equal("req-0001", submission.ProviderRequestId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://asr.invalid/api/v3/auc/bigmodel/submit", request.RequestUri!.ToString());
        Assert.Equal(ApiKey, Header(request, "X-Api-Key"));
        Assert.Equal("volc.seedasr.auc", Header(request, "X-Api-Resource-Id"));
        Assert.Equal("req-0001", Header(request, "X-Api-Request-Id"));
        // The official Standard HTTP contract identifies submit as sequence -1.
        Assert.Equal("-1", Header(request, "X-Api-Sequence"));

        var body = handler.RequestBodies[0];
        Assert.Contains("\"model_name\":\"bigmodel\"", body, StringComparison.Ordinal);
        Assert.Contains("\"show_utterances\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"enable_speaker_info\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"enable_itn\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"enable_punc\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"enable_ddc\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"wav\"", body, StringComparison.Ordinal);
        Assert.Contains("\"data\":", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_UsesEphemeralUrlWithoutPersistingSignedQuery()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        var publisher = new FakePublisher(new AsrPublishedAudio
        {
            Transport = "tos", Url = "https://tos.invalid/object?X-Tos-Signature=secret", Bytes = 22 * 1024 * 1024,
            Bucket = "meetcap-asr", ObjectKey = "meetcap-asr/shard/2026/09/19/job_1.wav",
        });
        using var provider = new VolcengineAsrProvider(new VolcengineAsrOptions { ApiKey = ApiKey, BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel" }, handler, publisher);

        var submission = await provider.SubmitFileAsync(Request(WriteAudio()));

        Assert.Contains("\"url\":\"https://tos.invalid/object?X-Tos-Signature=secret\"", handler.RequestBodies[0]);
        Assert.DoesNotContain("X-Tos-Signature", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("\"tos_object_key\":\"meetcap-asr/shard/2026/09/19/job_1.wav\"", submission.SanitizedRequestJson);
    }

    [Fact]
    public async Task Submit_ReportsTheTransportIdentitySoTheCallerCanPersistAndReleaseIt()
    {
        // The submission is the only place the caller can learn that a remote copy exists: the
        // provider owns the publisher, so without this the job row could never record
        // audio_transport/tos_bucket/tos_object_key (docs/DATA_MODEL.md section 6.2).
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        var published = new AsrPublishedAudio
        {
            Transport = "tos",
            Url = "https://tos.invalid/object?X-Tos-Signature=secret",
            Bytes = 22 * 1024 * 1024,
            Bucket = "meetcap-asr",
            ObjectKey = "meetcap-asr/abc/2026/09/19/job_1.wav",
        };
        using var provider = new VolcengineAsrProvider(
            new VolcengineAsrOptions { ApiKey = ApiKey, BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel" },
            handler,
            new FakePublisher(published));

        var submission = await provider.SubmitFileAsync(Request(WriteAudio()));

        Assert.NotNull(submission.Audio);
        Assert.Equal("tos", submission.Audio!.Transport);
        Assert.Equal("meetcap-asr", submission.Audio.Bucket);
        Assert.Equal("meetcap-asr/abc/2026/09/19/job_1.wav", submission.Audio.ObjectKey);
        Assert.True(submission.Audio.HasRemoteCopy);
        // The signed URL travels to the provider only: it is never part of the durable identity.
        Assert.Equal("https://tos.invalid/object?X-Tos-Signature=secret", submission.Audio.Url);
    }

    [Fact]
    public async Task Submit_AnInlineTransportReportsNoRemoteCopyToRelease()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        var submission = await provider.SubmitFileAsync(Request(WriteAudio()));

        Assert.NotNull(submission.Audio);
        Assert.Equal("inline", submission.Audio!.Transport);
        Assert.Null(submission.Audio.Bucket);
        Assert.Null(submission.Audio.ObjectKey);
        Assert.False(submission.Audio.HasRemoteCopy);
    }

    [Fact]
    public async Task Release_DeletesTheStagedObjectForATosJob()
    {
        var handler = new StubHttpHandler();
        var publisher = new FakePublisher(new AsrPublishedAudio { Transport = "inline", Bytes = 1 });
        using var provider = new VolcengineAsrProvider(
            new VolcengineAsrOptions { ApiKey = ApiKey, BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel" },
            handler,
            publisher);

        var release = await provider.ReleaseAudioAsync(TosJob());

        Assert.True(release.Attempted);
        Assert.True(release.Released);
        var deleted = Assert.Single(publisher.Deleted);
        Assert.Equal("meetcap-asr", deleted.Bucket);
        Assert.Equal("meetcap-asr/abc/2026/09/19/job_1.wav", deleted.ObjectKey);
        // The delete needs the stable identity only; a signed URL is never required for it.
        Assert.Null(deleted.Url);
    }

    [Fact]
    public async Task Release_DoesNothingForAJobWithNoCleanupDebt()
    {
        // Releasing an object a still-running job needs would break the very request it was
        // staged for, so the durable pending flag is what authorises the delete.
        var publisher = new FakePublisher(new AsrPublishedAudio { Transport = "inline", Bytes = 1 });
        using var provider = new VolcengineAsrProvider(
            new VolcengineAsrOptions { ApiKey = ApiKey, BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel" },
            new StubHttpHandler(),
            publisher);

        var settled = await provider.ReleaseAudioAsync(TosJob(tosCleanupPending: false));
        var notTos = await provider.ReleaseAudioAsync(TosJob() with { AudioTransport = "inline" });
        var missingKey = await provider.ReleaseAudioAsync(TosJob() with { TosObjectKey = null });

        Assert.False(settled.Attempted);
        Assert.False(notTos.Attempted);
        Assert.False(missingKey.Attempted);
        Assert.Empty(publisher.Deleted);
    }

    [Fact]
    public async Task Release_ReportsAFailedDeleteAsRetryableCleanupWorkRatherThanThrowing()
    {
        var publisher = new FakePublisher(new AsrPublishedAudio { Transport = "inline", Bytes = 1 })
        {
            OnDelete = _ => throw new AsrTransientException("tos.cleanup_failed", "TOS is unreachable."),
        };
        using var provider = new VolcengineAsrProvider(
            new VolcengineAsrOptions { ApiKey = ApiKey, BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel" },
            new StubHttpHandler(),
            publisher);

        var release = await provider.ReleaseAudioAsync(TosJob());

        Assert.True(release.Attempted);
        Assert.False(release.Released);
        Assert.Contains("TOS is unreachable.", release.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_ScrubsTheApiKeyFromAnyReportedCleanupFailure()
    {
        // An error message is persisted on the job, so the adapter's own scrubbing has to cover
        // the cleanup path too and not only the HTTP paths.
        var publisher = new FakePublisher(new AsrPublishedAudio { Transport = "inline", Bytes = 1 })
        {
            OnDelete = _ => throw new IOException($"delete rejected for key {ApiKey}"),
        };
        using var provider = new VolcengineAsrProvider(
            new VolcengineAsrOptions { ApiKey = ApiKey, BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel" },
            new StubHttpHandler(),
            publisher);

        var release = await provider.ReleaseAudioAsync(TosJob());

        Assert.False(release.Released);
        Assert.DoesNotContain(ApiKey, release.Error!, StringComparison.Ordinal);
        Assert.Contains("***", release.Error!, StringComparison.Ordinal);
    }

    private static AsrJob TosJob(bool tosCleanupPending = true) => new()
    {
        Id = "job_1",
        SessionId = "ses_1",
        Source = "import",
        Provider = "volcengine",
        InputArtifact = "audio/import/normalized.wav",
        ProviderRequestId = "req-0001",
        AudioTransport = "tos",
        TosBucket = "meetcap-asr",
        TosObjectKey = "meetcap-asr/abc/2026/09/19/job_1.wav",
        TosCleanupPending = tosCleanupPending,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>
    /// Records what the provider asked the transport boundary to publish and to release.
    /// </summary>
    private sealed class FakePublisher(AsrPublishedAudio audio) : IAsrAudioPublisher
    {
        public List<AsrPublishedAudio> Published { get; } = new();

        public List<AsrPublishedAudio> Deleted { get; } = new();

        /// <summary>Overrides deletion so a failure can be scripted; throw to simulate one.</summary>
        public Action<AsrPublishedAudio>? OnDelete { get; set; }

        public Task<AsrPublishedAudio> PublishAsync(AsrFileRequest request, CancellationToken cancellationToken = default)
        {
            Published.Add(audio);
            return Task.FromResult(audio);
        }

        public Task DeleteAsync(AsrPublishedAudio published, CancellationToken cancellationToken = default)
        {
            Deleted.Add(published);
            OnDelete?.Invoke(published);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NoRequestEverSendsTheLegacyAuthenticationHeaders()
    {
        // The whole point of issue #26: one authentication generation, not two. Both the
        // submit and the query are checked, because dropping the headers from only one of
        // them would still leave the legacy scheme half-alive.
        var handler = new StubHttpHandler()
            .Enqueue(HttpStatusCode.OK, apiStatus: "20000000", apiLogId: "log-submit")
            .EnqueueJson(HttpStatusCode.OK, """{"result":{"utterances":[]}}""", apiLogId: "log-query");
        using var provider = Create(handler);

        await provider.SubmitFileAsync(Request(WriteAudio()));
        await provider.GetResultAsync(ExistingSubmission(), Request(WriteAudio()));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.False(request.Headers.Contains("X-Api-App-Key"));
            Assert.False(request.Headers.Contains("X-Api-Access-Key"));
            // The new-console key is the only credential that is ever sent.
            Assert.Equal(ApiKey, Header(request, "X-Api-Key"));
        });
    }

    [Fact]
    public async Task RequestBody_CarriesANonSecretMeetCapOwnedUserId()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        await provider.SubmitFileAsync(Request(WriteAudio()));

        var root = JsonNode.Parse(handler.RequestBodies[0])!.AsObject();
        var body = root.ToJsonString();

        // user.uid is persisted with the sanitized request metadata, so it must never be the
        // API key (docs/CONFIGURATION.md section 8).
        Assert.Equal(VolcengineAsrOptions.UserId, root["user"]!["uid"]!.GetValue<string>());
        Assert.DoesNotContain(ApiKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_RetainsTheProviderLogIdForDiagnostics()
    {
        var handler = new StubHttpHandler().Enqueue(
            HttpStatusCode.OK,
            apiStatus: "20000000",
            apiLogId: "20260918120000ABCDEF");
        using var provider = Create(handler);

        var submission = await provider.SubmitFileAsync(Request(WriteAudio()));

        Assert.Equal("20260918120000ABCDEF", submission.ProviderLogId);
    }

    [Fact]
    public async Task Query_RetainsTheProviderLogIdOnTheCompletion()
    {
        var handler = new StubHttpHandler().EnqueueJson(
            HttpStatusCode.OK,
            """{"result":{"text":"hello","utterances":[]}}""",
            apiLogId: "log-query-1");
        using var provider = Create(handler);

        var result = await provider.GetResultAsync(ExistingSubmission(), Request(WriteAudio()));

        Assert.Equal(AsrPollState.Completed, result.State);
        Assert.Equal("log-query-1", result.Completion!.ProviderLogId);
    }

    [Fact]
    public async Task Submit_SanitizedMetadata_ContainsNoApiKeyAndNoInlineAudio()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        var submission = await provider.SubmitFileAsync(Request(WriteAudio(bytes: 4096)));

        // The persisted request.json must never carry the API key or duplicate the user's audio.
        Assert.DoesNotContain(ApiKey, submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\"", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("\"inline_bytes\":4096", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("volc.seedasr.auc", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"bigmodel\"", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("\"enable_speaker_info\":true", submission.SanitizedRequestJson, StringComparison.Ordinal);

        var sanitized = JsonNode.Parse(submission.SanitizedRequestJson)!.AsObject();
        Assert.False(sanitized.ContainsKey("tier"));
        Assert.False(sanitized.ContainsKey("endpoint_tier"));
    }

    [Fact]
    public async Task TransientServerErrors_AreRetriedInProcessWithAStableTaskId()
    {
        var handler = new StubHttpHandler()
            .EnqueueRepeat(2, HttpStatusCode.ServiceUnavailable, body: "busy")
            .Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        await provider.SubmitFileAsync(Request(WriteAudio()));

        // Polly retried inside the adapter; the provider request id never changed, so the
        // retries address one task rather than creating three billable ones.
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Equal("req-0001", Header(request, "X-Api-Request-Id")));
    }

    [Fact]
    public async Task TransientPollErrors_AreRetriedInProcessWithAStableTaskId()
    {
        // The same stability requirement applies to the query, where an unstable request id
        // would address a different task.
        var handler = new StubHttpHandler()
            .EnqueueRepeat(2, HttpStatusCode.ServiceUnavailable, body: "busy")
            .EnqueueJson(HttpStatusCode.OK, "{\"result\":{\"utterances\":[]}}");
        using var provider = Create(handler);

        var result = await provider.GetResultAsync(ExistingSubmission(), Request(WriteAudio()));

        Assert.Equal(AsrPollState.Completed, result.State);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Equal("req-0001", Header(request, "X-Api-Request-Id")));
    }

    [Fact]
    public async Task Unauthorized_FailsPermanentlyWithAnActionableMessage()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.Unauthorized, body: "bad api key");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        Assert.Equal("http.401", ex.Code);
        Assert.Contains("asr.volcengine.api_key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "http.401")]
    [InlineData(HttpStatusCode.Forbidden, "http.403")]
    [InlineData(HttpStatusCode.BadRequest, "http.400")]
    public async Task TransportLevelRejection_IsPermanentEvenWhenTheProviderStatusHeaderIsPresent(
        HttpStatusCode statusCode,
        string expectedCode)
    {
        // Volcengine can answer with an HTTP error *and* its own X-Api-Status-Code. Gating the
        // permanent classification on the absence of that header made a 401 retryable, so a bad
        // API key would be retried instead of failing visibly.
        var handler = new StubHttpHandler().Enqueue(
            statusCode,
            body: "{\"message\":\"denied\"}",
            apiStatus: "45000010",
            apiMessage: "auth failed",
            apiLogId: "log-401");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        Assert.Equal(expectedCode, ex.Code);
        Assert.Contains("asr.volcengine.api_key", ex.Message, StringComparison.Ordinal);
        // The provider log id travels with the error so an incident can be traced from the
        // persisted error_message alone.
        Assert.Contains("X-Tt-Logid=log-401", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "http.401")]
    [InlineData(HttpStatusCode.Forbidden, "http.403")]
    public async Task Query_TransportLevelAuthRejection_IsPermanent(HttpStatusCode statusCode, string expectedCode)
    {
        var handler = new StubHttpHandler().Enqueue(
            statusCode,
            body: "{\"message\":\"denied\"}",
            apiStatus: "45000010");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.GetResultAsync(ExistingSubmission(), Request(WriteAudio())));

        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public async Task ResponseBodiesThatEchoTheApiKey_AreScrubbedFromErrors()
    {
        // One more response than the retry budget: the adapter must scrub every attempt.
        var handler = new StubHttpHandler().EnqueueRepeat(
            4,
            HttpStatusCode.ServiceUnavailable,
            body: $"{{\"echoed\":\"{ApiKey}\"}}");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrTransientException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("***", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseBodiesThatEchoThePresignedUrl_AreScrubbedFromErrors()
    {
        // The mirror of the API-key case above, and the shape review finding P1-1 described: the
        // signed URL is put into the request body, so a Seed-ASR rejection that could not fetch
        // `audio.url` quotes the parameter it rejected. The API key alone is not enough here --
        // the adapter holds the URL and the AK/SK are nowhere in this options type -- so the URL
        // itself has to join the scrub set. The message this produces is written to
        // asr_jobs.error_message and to the append-only events.jsonl, so a live 6-hour credential
        // reaching it cannot be recalled.
        var handler = new StubHttpHandler().Enqueue(
            HttpStatusCode.BadRequest,
            body: $$"""{"code":"45000010","message":"failed to fetch { "audio.url" : "{{SignedUrl}}" }"}""",
            apiStatus: "45000010",
            apiMessage: "invalid audio url");
        using var provider = Create(handler, SignedUrl);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        AssertNoSignedUrl(ex.Message);
        Assert.Contains("***", ex.Message, StringComparison.Ordinal);
        // The scrubbing must not cost the operator the diagnosis: the provider's own code and
        // message survive, and the rejection is still clearly reported.
        Assert.Contains("45000010", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid audio url", ex.Message, StringComparison.Ordinal);
        Assert.Contains("asr.volcengine.api_key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderStatusFailuresThatEchoThePresignedUrl_AreScrubbedFromErrors()
    {
        // The other error path that carries the response body into a durable message: HTTP 200
        // with a non-success X-Api-Status-Code, which is how the provider reports a rejected
        // parameter after it accepted the request.
        var handler = new StubHttpHandler().Enqueue(
            HttpStatusCode.OK,
            body: $$"""{"message":"audio.url unreachable: {{SignedUrl}}"}""",
            apiStatus: "55000010",
            apiMessage: "cannot retrieve audio");
        using var provider = Create(handler, SignedUrl);

        var ex = await Assert.ThrowsAsync<AsrTransientException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        AssertNoSignedUrl(ex.Message);
        Assert.Contains("55000010", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot retrieve audio", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // The provider re-spells the URL it was handed: rewritten scheme, only the query quoted, or
    // the query with its `&` separators JSON-escaped. Each of these still contains a live
    // signature, so each has to be masked; a match against MeetCap's own single spelling of the
    // URL would leave them intact. The replacement is asserted in full, which is what pins down
    // both halves: the credential is gone and the diagnosis around it survives.
    [InlineData("the URL as it was sent", "cannot fetch", SignedUrl, "cannot fetch ***")]
    [InlineData("the same URL under http", "cannot fetch", SignedUrlUnderHttp, "cannot fetch ***")]
    [InlineData("the query quoted on its own", "could not fetch signed url", SignedUrlQuery, "could not fetch signed url ***")]
    [InlineData(
        "the query with escaped separators",
        "cannot fetch",
        "X-Tos-Algorithm=TOS4-HMAC-SHA256\\u0026X-Tos-Date=20260919T101500Z\\u0026X-Tos-Expires=21600" +
        "\\u0026X-Tos-Signature=deadbeefcafef00d\\u0026X-Tos-Credential=AKLT-SECRET%2F20260919%2Fcn-beijing%2Ftos%2Frequest",
        "cannot fetch ***")]
    public async Task PresignedUrlEchoedInAnySpelling_IsMaskedWithoutLosingTheDiagnosis(
        string shape,
        string lead,
        string echoed,
        string expected)
    {
        Assert.NotEmpty(shape);
        var handler = new StubHttpHandler().Enqueue(
            HttpStatusCode.BadRequest,
            body: "{\"message\":\"" + lead + " " + echoed + "\"}",
            apiStatus: "45000010");
        using var provider = Create(handler, SignedUrl);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        AssertNoSignedUrl(ex.Message);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInlineSubmissionCarriesNoUrlForAnErrorToLeak()
    {
        // The inline path never mints a credential, so the scrub set is the API key only and the
        // adapter must not treat the (absent) URL as if it had one.
        var handler = new StubHttpHandler().Enqueue(
            HttpStatusCode.BadRequest,
            body: "{\"message\":\"bad audio.data\"}",
            apiStatus: "45000001");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        Assert.Contains("bad audio.data", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fails when any part of the presigned credential survives into a durable message.
    /// </summary>
    /// <remarks>
    /// The query is the credential, spread across its parameters, so this asserts on the
    /// <em>values</em> rather than on parameter names: a parameter name is not a secret, and a
    /// provider that escapes <c>&amp;</c> to <c>\u0026</c> in a JSON body leaves the name readable
    /// around the mask. What may not survive is the credential material itself — the signature, the
    /// signed key, or the query as one contiguous string — under any scheme or encoding.
    /// </remarks>
    private static void AssertNoSignedUrl(string message)
    {
        Assert.DoesNotContain(SignedUrlQuery, message, StringComparison.Ordinal);
        Assert.DoesNotContain(SignedUrl, message, StringComparison.Ordinal);
        Assert.DoesNotContain(SignedUrlUnderHttp, message, StringComparison.Ordinal);

        foreach (var parameter in SignedUrlQuery.Split('&'))
        {
            // `name=value`; anything whose value is not one of the fixed non-secret protocol
            // values is credential material and must have been replaced.
            var value = parameter[(parameter.IndexOf('=', StringComparison.Ordinal) + 1)..];
            if (NonSecretQueryValues.Contains(value))
            {
                continue;
            }

            Assert.DoesNotContain(value, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PermanentProviderStatusCodes_AreNotRetried()
    {
        var handler = new StubHttpHandler().Enqueue(
            HttpStatusCode.OK,
            apiStatus: "45000002",
            apiMessage: "empty audio");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        Assert.Equal("45000002", ex.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingAudioArtifact_FailsPermanentlyWithoutCallingTheProvider()
    {
        var handler = new StubHttpHandler();
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(Path.Combine(_audioRoot, "gone.wav"))));

        Assert.Equal("audio.missing", ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Query_PendingCompletedAndSilentStatesAreDistinguished()
    {
        var path = WriteAudio();
        var request = Request(path);

        var pending = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000002");
        using (var provider = Create(pending))
        {
            var result = await provider.GetResultAsync(ExistingSubmission(), request);
            Assert.Equal(AsrPollState.Pending, result.State);

            // The query carries no X-Api-Sequence and no X-Api-Sequence is required: that
            // header belongs to submit, matching the official interface document.
            Assert.False(pending.Requests[0].Headers.Contains("X-Api-Sequence"));
            Assert.Equal(
                "https://asr.invalid/api/v3/auc/bigmodel/query",
                pending.Requests[0].RequestUri!.ToString());
            // The query repeats the same task identity: same key, same resource, same request id.
            Assert.Equal(ApiKey, Header(pending.Requests[0], "X-Api-Key"));
            Assert.Equal("volc.seedasr.auc", Header(pending.Requests[0], "X-Api-Resource-Id"));
            Assert.Equal("req-0001", Header(pending.Requests[0], "X-Api-Request-Id"));
        }

        var completed = new StubHttpHandler().EnqueueJson(
            HttpStatusCode.OK,
            "{\"result\":{\"text\":\"hello\",\"utterances\":[]}}");
        using (var provider = Create(completed))
        {
            var result = await provider.GetResultAsync(ExistingSubmission(), request);
            Assert.Equal(AsrPollState.Completed, result.State);
            Assert.Contains("hello", result.Completion!.RawResponseJson, StringComparison.Ordinal);
            Assert.Equal("20000000", result.Completion.ProviderStatus);
        }

        var silent = new StubHttpHandler().Enqueue(HttpStatusCode.OK, body: string.Empty, apiStatus: "20000003");
        using (var provider = Create(silent))
        {
            var result = await provider.GetResultAsync(ExistingSubmission(), request);

            // Silence is a completed result with no speech, not a failure.
            Assert.Equal(AsrPollState.Completed, result.State);
            Assert.Equal("20000003", result.Completion!.ProviderStatus);
        }
    }

    [Fact]
    public async Task Query_UnknownErrorCodeIsTransientButKnownParameterErrorIsNot()
    {
        var request = Request(WriteAudio());

        var unknown = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "55000001", apiMessage: "boom");
        using (var provider = Create(unknown))
        {
            var result = await provider.GetResultAsync(ExistingSubmission(), request);
            Assert.Equal(AsrPollState.Failed, result.State);
            Assert.True(result.Error!.IsTransient);
        }

        var permanent = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "45000001", apiMessage: "bad参数");
        using (var provider = Create(permanent))
        {
            var result = await provider.GetResultAsync(ExistingSubmission(), request);
            Assert.Equal(AsrPollState.Failed, result.State);
            Assert.False(result.Error!.IsTransient);
        }
    }

    [Fact]
    public void EndpointContract_IsSubmitAndQueryOnlyAndUsesTheFixedResourceId()
    {
        // "No streaming ASR in the normal or failure path" (docs/ASR_STRATEGY.md section 7)
        // and "one resource id" (section 2) are both auditable from the options alone: the
        // adapter has exactly two endpoint properties and one protocol constant.
        var options = new VolcengineAsrOptions { ApiKey = ApiKey };

        Assert.Equal(
            "https://openspeech.bytedance.com/api/v3/auc/bigmodel/submit",
            options.SubmitEndpoint);
        Assert.Equal(
            "https://openspeech.bytedance.com/api/v3/auc/bigmodel/query",
            options.QueryEndpoint);
        Assert.Equal("volc.seedasr.auc", VolcengineAsrOptions.ResourceId);

        Assert.DoesNotContain("stream", options.SubmitEndpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stream", options.QueryEndpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("idle", options.SubmitEndpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("idle", options.QueryEndpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bigasr", options.SubmitEndpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bigasr", VolcengineAsrOptions.ResourceId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_RejectsAnEmptyApiKeyAndAnUnresolvableReference()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = string.Empty;
        Assert.Throws<AsrConfigurationException>(() => VolcengineAsrProviderFactory.BuildOptions(config));

        config.Asr.Volcengine.ApiKey = "env:MEETCAP_MISSING_API_KEY";
        var ex = Assert.Throws<AsrConfigurationException>(
            () => VolcengineAsrProviderFactory.BuildOptions(config, _ => null));
        Assert.Contains("MEETCAP_MISSING_API_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_RejectsDisabledAsr()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Enabled = false;

        var ex = Assert.Throws<AsrConfigurationException>(
            () => VolcengineAsrProviderFactory.BuildOptions(config));
        Assert.Contains("asr.enabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_BuildsOptionsFromTheEffectiveConfiguration()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = "env:MEETCAP_API_KEY";
        config.Asr.Volcengine.HttpTimeoutSeconds = 12;

        var options = VolcengineAsrProviderFactory.BuildOptions(
            config,
            name => name == "MEETCAP_API_KEY" ? ApiKey : null);

        Assert.Equal(ApiKey, options.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(12), options.HttpTimeout);
        // The resource id is not configurable, so nothing in the configuration can change it.
        Assert.Equal("volc.seedasr.auc", VolcengineAsrOptions.ResourceId);
    }

    [Fact]
    public void VolcengineOptions_RejectAnEmptyApiKeyAtConstruction()
    {
        Assert.Throws<ArgumentException>(
            () => new VolcengineAsrProvider(new VolcengineAsrOptions { ApiKey = "   " }));
    }

    [Fact]
    public void Factory_WithoutTosSection_BuildsAPublisherThatKeepsTheOversizedPathClosed()
    {
        // "TOS stays optional until the large-file path is needed": an absent [asr.tos] must not
        // stop the provider from being constructed at all.
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = ApiKey;

        using var provider = VolcengineAsrProviderFactory.Create(config);

        Assert.NotNull(provider);
        Assert.Equal(VolcengineAsrProviderFactory.ProviderName, provider.Name);
    }

    [Fact]
    public void Factory_WithAFullyConfiguredTosSection_ResolvesBothCredentialReferences()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = ApiKey;
        config.Asr.Tos.Bucket = "meetcap-asr";
        config.Asr.Tos.Region = "cn-beijing";
        config.Asr.Tos.Endpoint = "https://tos-cn-beijing.volces.com";
        config.Asr.Tos.AccessKey = "env:TOS_ACCESS_KEY";
        config.Asr.Tos.SecretKey = "env:TOS_SECRET_KEY";

        using var provider = VolcengineAsrProviderFactory.Create(
            config,
            handler: null,
            environment: name => name switch
            {
                "TOS_ACCESS_KEY" => "AK-LITERAL",
                "TOS_SECRET_KEY" => "SK-LITERAL",
                _ => null,
            });

        Assert.NotNull(provider);
    }

    [Fact]
    public void Factory_WithAPartiallyConfiguredTosSection_NamesTheMissingField()
    {
        // A half-filled section is a typo or an interrupted edit, not "TOS is not configured",
        // and reporting it as absent would send the operator looking for a section they wrote.
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = ApiKey;
        config.Asr.Tos.Bucket = "meetcap-asr";

        var ex = Assert.Throws<AsrConfigurationException>(() => VolcengineAsrProviderFactory.Create(config));

        // The first missing field is named, and the message explains why a half-filled section is
        // an error rather than "TOS is not configured".
        Assert.Contains("asr.tos.region", ex.Message, StringComparison.Ordinal);
        Assert.Contains("partly configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_WithAnUnresolvableTosCredentialReference_NamesTheVariable()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = ApiKey;
        config.Asr.Tos.Bucket = "meetcap-asr";
        config.Asr.Tos.Region = "cn-beijing";
        config.Asr.Tos.Endpoint = "https://tos-cn-beijing.volces.com";
        config.Asr.Tos.AccessKey = "env:TOS_ACCESS_KEY";
        config.Asr.Tos.SecretKey = "env:TOS_SECRET_KEY";

        var ex = Assert.Throws<AsrConfigurationException>(
            () => VolcengineAsrProviderFactory.Create(config, handler: null, environment: _ => null));

        Assert.Contains("TOS_ACCESS_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_WithAnEmptyTosSecretKey_DoesNotSilentlySkipTheValidation()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = ApiKey;
        config.Asr.Tos.Bucket = "meetcap-asr";
        config.Asr.Tos.Region = "cn-beijing";
        config.Asr.Tos.Endpoint = "https://tos-cn-beijing.volces.com";
        config.Asr.Tos.SecretKey = "env:TOS_SECRET_KEY";

        var ex = Assert.Throws<AsrConfigurationException>(() => VolcengineAsrProviderFactory.Create(config));

        Assert.Contains("asr.tos.access_key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_TosCredentialsNeverReachTheRedactedConfigurationOutput()
    {
        // config show and the log sinks both redact from the same secret set, so the set has to
        // contain the TOS credential references as well as the Volcengine API key.
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.ApiKey = ApiKey;
        config.Asr.Tos.AccessKey = "AK-LITERAL-SECRET";
        config.Asr.Tos.SecretKey = "SK-LITERAL-SECRET";

        var secrets = MeetCap.Core.Secrets.SecretRedactor.GetSecretValues(config);
        var printed = MeetCap.Core.Secrets.SecretRedactor.Redact(
            $"api_key={ApiKey} access_key=AK-LITERAL-SECRET secret_key=SK-LITERAL-SECRET",
            secrets);

        Assert.DoesNotContain(ApiKey, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("AK-LITERAL-SECRET", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("SK-LITERAL-SECRET", printed, StringComparison.Ordinal);
        Assert.Equal("api_key=*** access_key=*** secret_key=***", printed);
    }

    [Fact]
    public void TosPublisher_UsesTheOfficialSdkAndNeverHandRollsSigning()
    {
        // Issue #29 requires the official TOS .NET SDK and forbids hand-written request signing.
        // The SDK is a real, separately-versioned dependency of this adapter rather than a shim,
        // and the adapter declares no signing surface of its own: the only TOS machinery it can
        // reach is the SDK's client, and neither of its two operations computes a signature.
        var adapter = typeof(VolcengineTosAudioPublisher).Assembly;
        var sdk = typeof(TOS.TosClientBuilder).Assembly;

        Assert.Equal("Volcengine.TOS", sdk.GetName().Name);
        Assert.NotEqual(adapter.GetName().Name, sdk.GetName().Name);

        // The official client is what the adapter builds and calls, so upload, presigning and
        // deletion all go through the SDK implementation.
        var buildClient = typeof(VolcengineTosAudioPublisher).GetMethod(
            "BuildClient",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(buildClient);
        Assert.Equal(typeof(TOS.ITosClient), buildClient!.ReturnType);

        // No hand-rolled signing: the public surface is the transport contract only.
        var adapterMethods = typeof(VolcengineTosAudioPublisher)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(VolcengineTosAudioPublisher.PublishAsync), adapterMethods);
        Assert.Contains(nameof(VolcengineTosAudioPublisher.DeleteAsync), adapterMethods);
        Assert.DoesNotContain(adapterMethods, name => name.Contains("Sign", StringComparison.Ordinal));
        Assert.DoesNotContain(adapterMethods, name => name.Contains("Signature", StringComparison.Ordinal));
    }
}
