using System.Net;
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
/// <c>VOLCENGINE_APP_ID</c> and no access token, and docs/DEVELOPMENT.md section 7
/// forbids claiming otherwise or inventing credentials.
/// </remarks>
public class VolcengineAsrProviderTests : IDisposable
{
    private const string Token = "SECRET-ACCESS-TOKEN-0123456789";

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

    private VolcengineAsrProvider Create(StubHttpHandler handler, string tier = VolcengineTiers.Standard) =>
        new(
            new VolcengineAsrOptions
            {
                AppId = "app-123",
                AccessToken = Token,
                ResourceId = "volc.bigasr.auc",
                BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel",
                MaxTransientAttempts = 3,
                InitialBackoff = TimeSpan.FromMilliseconds(1),
                HttpTimeout = TimeSpan.FromSeconds(5),
            },
            handler);

    private AsrFileRequest Request(string path, string tier = VolcengineTiers.Standard) => new()
    {
        JobId = "job_1",
        SessionId = "ses_1",
        Source = "import",
        InputArtifactPath = path,
        AudioFormat = "wav",
        DurationMs = 754_000,
        ProviderRequestId = "req-0001",
        ServiceTier = tier,
        RequestSpeakerInfo = true,
    };

    [Fact]
    public async Task Submit_SendsTheDocumentedHeadersAndBody()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        var submission = await provider.SubmitFileAsync(Request(WriteAudio()));

        Assert.Equal("req-0001", submission.ProviderRequestId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://asr.invalid/api/v3/auc/bigmodel/submit", request.RequestUri!.ToString());
        Assert.Equal("app-123", string.Join(",", request.Headers.GetValues("X-Api-App-Key")));
        Assert.Equal(Token, string.Join(",", request.Headers.GetValues("X-Api-Access-Key")));
        Assert.Equal("volc.bigasr.auc", string.Join(",", request.Headers.GetValues("X-Api-Resource-Id")));
        Assert.Equal("req-0001", string.Join(",", request.Headers.GetValues("X-Api-Request-Id")));
        Assert.Equal("-1", string.Join(",", request.Headers.GetValues("X-Api-Sequence")));

        var body = handler.RequestBodies[0];
        Assert.Contains("\"show_utterances\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"enable_speaker_info\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"wav\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_SanitizedMetadata_ContainsNoCredentialAndNoInlineAudio()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        var submission = await provider.SubmitFileAsync(Request(WriteAudio(bytes: 4096)));

        // The persisted request.json must never carry the access token or duplicate the
        // user's audio.
        Assert.DoesNotContain(Token, submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("data", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("\"inline_bytes\":4096", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("volc.bigasr.auc", submission.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.Contains("\"enable_speaker_info\":true", submission.SanitizedRequestJson, StringComparison.Ordinal);
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
            request => Assert.Equal("req-0001", string.Join(",", request.Headers.GetValues("X-Api-Request-Id"))));
    }

    [Fact]
    public async Task Unauthorized_FailsPermanentlyWithAnActionableMessage()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.Unauthorized, body: "bad app id");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        Assert.Equal("http.401", ex.Code);
        Assert.Contains("asr.volcengine.app_id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseBodiesThatEchoTheCredential_AreScrubbedFromErrors()
    {
        // One more response than the retry budget: the adapter must scrub every attempt.
        var handler = new StubHttpHandler().EnqueueRepeat(
            4,
            HttpStatusCode.ServiceUnavailable,
            body: $"{{\"echoed\":\"{Token}\"}}");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrTransientException>(
            () => provider.SubmitFileAsync(Request(WriteAudio())));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Contains("***", ex.Message, StringComparison.Ordinal);
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
            var result = await provider.GetResultAsync(
                new AsrSubmission { ProviderRequestId = "req-0001", SanitizedRequestJson = "{}" },
                request);
            Assert.Equal(AsrPollState.Pending, result.State);

            // The query carries no X-Api-Sequence: that header belongs to submit.
            Assert.False(pending.Requests[0].Headers.Contains("X-Api-Sequence"));
            Assert.Equal(
                "https://asr.invalid/api/v3/auc/bigmodel/query",
                pending.Requests[0].RequestUri!.ToString());
        }

        var completed = new StubHttpHandler().EnqueueJson(
            HttpStatusCode.OK,
            "{\"result\":{\"text\":\"hello\",\"utterances\":[]}}");
        using (var provider = Create(completed))
        {
            var result = await provider.GetResultAsync(
                new AsrSubmission { ProviderRequestId = "req-0001", SanitizedRequestJson = "{}" },
                request);
            Assert.Equal(AsrPollState.Completed, result.State);
            Assert.Contains("hello", result.Completion!.RawResponseJson, StringComparison.Ordinal);
            Assert.Equal("20000000", result.Completion.ProviderStatus);
        }

        var silent = new StubHttpHandler().Enqueue(HttpStatusCode.OK, body: string.Empty, apiStatus: "20000003");
        using (var provider = Create(silent))
        {
            var result = await provider.GetResultAsync(
                new AsrSubmission { ProviderRequestId = "req-0001", SanitizedRequestJson = "{}" },
                request);

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
            var result = await provider.GetResultAsync(
                new AsrSubmission { ProviderRequestId = "req-0001", SanitizedRequestJson = "{}" },
                request);
            Assert.Equal(AsrPollState.Failed, result.State);
            Assert.True(result.Error!.IsTransient);
        }

        var permanent = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "45000001", apiMessage: "bad参数");
        using (var provider = Create(permanent))
        {
            var result = await provider.GetResultAsync(
                new AsrSubmission { ProviderRequestId = "req-0001", SanitizedRequestJson = "{}" },
                request);
            Assert.Equal(AsrPollState.Failed, result.State);
            Assert.False(result.Error!.IsTransient);
        }
    }

    [Fact]
    public async Task IdleTier_UsesTheIdleEndpoints()
    {
        var handler = new StubHttpHandler().Enqueue(HttpStatusCode.OK, apiStatus: "20000000");
        using var provider = Create(handler);

        await provider.SubmitFileAsync(Request(WriteAudio(), VolcengineTiers.Idle));

        Assert.Equal(
            "https://asr.invalid/api/v3/auc/bigmodel/idle/submit",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task TurboTier_IsRejectedInsteadOfBeingSilentlyMapped()
    {
        var handler = new StubHttpHandler();
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<AsrConfigurationException>(
            () => provider.SubmitFileAsync(Request(WriteAudio(), VolcengineTiers.Turbo)));

        Assert.Contains("turbo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not implemented", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void NoStreamingEndpointExistsAnywhereInThisAdapter()
    {
        // "No streaming ASR in the normal or failure path" (docs/ASR_STRATEGY.md section 7)
        // is auditable: the adapter's only endpoints are submit/query.
        var paths = new[]
        {
            VolcengineTiers.SubmitPath("https://x", VolcengineTiers.Standard),
            VolcengineTiers.QueryPath("https://x", VolcengineTiers.Standard),
            VolcengineTiers.SubmitPath("https://x", VolcengineTiers.Idle),
            VolcengineTiers.QueryPath("https://x", VolcengineTiers.Idle),
        };

        Assert.All(paths, path => Assert.DoesNotContain("stream", path, StringComparison.OrdinalIgnoreCase));
        Assert.All(paths, path => Assert.DoesNotContain("sauc", path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Factory_RejectsMissingAppIdAndUnresolvableCredential()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.AppId = string.Empty;
        Assert.Throws<AsrConfigurationException>(() => VolcengineAsrProviderFactory.BuildOptions(config));

        config.Asr.Volcengine.AppId = "app-123";
        config.Asr.Volcengine.Credential = "env:MEETCAP_MISSING_TOKEN";
        var ex = Assert.Throws<AsrConfigurationException>(
            () => VolcengineAsrProviderFactory.BuildOptions(config, _ => null));
        Assert.Contains("MEETCAP_MISSING_TOKEN", ex.Message, StringComparison.Ordinal);
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
        config.Asr.Volcengine.AppId = "app-123";
        config.Asr.Volcengine.Credential = "env:TOKEN";
        config.Asr.Volcengine.HttpTimeoutSeconds = 12;

        var options = VolcengineAsrProviderFactory.BuildOptions(config, name => name == "TOKEN" ? Token : null);

        Assert.Equal("app-123", options.AppId);
        Assert.Equal(Token, options.AccessToken);
        Assert.Equal(TimeSpan.FromSeconds(12), options.HttpTimeout);
    }
}
