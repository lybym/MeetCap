using System.Net;
using System.Security.Cryptography;
using MeetCap.Asr;
using MeetCap.Asr.Transcripts;
using MeetCap.Asr.Volcengine;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Asr;
using MeetCap.Core.Capture;
using MeetCap.Core.Media;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeetCap.IntegrationTests;

/// <summary>Real provider/processor/SQLite recovery with an in-process HTTP boundary only.</summary>
public sealed class AsrAdapterRecoveryTests : IDisposable
{
    private const string Raw = """{"result":{"utterances":[{"text":"Recovered speech","start_time":0,"end_time":1000}]}}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meetcap-adapter-recovery-" + Guid.NewGuid().ToString("N"));
    private SessionArtifactPaths Paths => new(_root, "ses_1");
    private string DatabasePath => Path.Combine(_root, "meetcap.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private AsrJob CreateJob(AsrJobStatus status = AsrJobStatus.Pending, bool ambiguous = false)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new AsrJob
        {
            Id = "job_1", SessionId = "ses_1", Source = "mic", Provider = "volcengine",
            InputArtifact = "asr/batches/mic/batch-000001.wav", ProviderRequestId = "original-request",
            StartMs = 0, EndMs = ambiguous ? 1663 : 1000, DurationMs = ambiguous ? 1663 : 1000,
            Status = status, AttemptCount = status == AsrJobStatus.Pending ? 0 : 1,
            SubmittedAt = status == AsrJobStatus.Submitted ? now : null,
            CreatedAt = now, UpdatedAt = now,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ResolveRelative(job.InputArtifact))!);
        var format = new AudioFormat(16000, 1, 16, AudioSampleFormat.Pcm);
        File.WriteAllBytes(Paths.ResolveRelative(job.InputArtifact), WavHeader.Build(format, 32000).Concat(new byte[32000]).ToArray());
        File.WriteAllText(Path.ChangeExtension(Paths.ResolveRelative(job.InputArtifact), ".json"),
            $"{{\"chunks\":[{{\"start_ms\":0,\"end_ms\":{job.EndMs},\"data_bytes\":32000}}]}}");
        var db = new MeetCapDatabase(DatabasePath);
        db.EnsureMigrated();
        db.Sessions.Insert(new SessionRecord
        {
            Id = job.SessionId, Title = "Recovery", Mode = SessionModes.Offline, SourceType = "live",
            Status = SessionStatus.Processing, CreatedAt = now, UpdatedAt = now, Tracks = new[] { "mic" },
        });
        db.AsrJobs.Create(job);
        return job;
    }

    private static VolcengineAsrProvider Adapter(InProcessHandler handler, IAsrAudioPublisher? publisher = null) => new(
        new VolcengineAsrOptions
        {
            ApiKey = "in-process-test-key", BaseUrl = "https://asr.invalid/api/v3/auc/bigmodel",
            MaxTransientAttempts = 1, InitialBackoff = TimeSpan.Zero, HttpTimeout = TimeSpan.FromSeconds(5),
        }, handler, publisher);

    private AsrJobProcessor Processor(IAsrProvider provider)
    {
        var restarted = new MeetCapDatabase(DatabasePath);
        return new AsrJobProcessor(restarted.AsrJobs, provider, new VolcengineResponseNormalizer(),
            new FileTranscriptStore(), restarted.Sessions, new FileSessionArtifactWriter(),
            new AsrJobProcessorOptions
            {
                DataRoot = _root, RetryPolicy = new AsrRetryPolicy(3, 1, 1), PollTimeout = TimeSpan.Zero,
                PollInterval = TimeSpan.Zero, WriteMarkdown = false,
            });
    }

    private async Task<AsrJobProcessResult> RestartAndResume(IAsrProvider provider)
    {
        SqliteConnection.ClearAllPools();
        var jobs = new MeetCapDatabase(DatabasePath).AsrJobs;
        var due = jobs.Get("job_1")! with { NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        jobs.Update(due);
        return Assert.Single(await Processor(provider).RunDueAsync(1));
    }

    private void AssertDurableSuccess(AsrJobProcessResult result, byte[] sourceHash)
    {
        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        Assert.Equal("original-request", result.Job.ProviderRequestId);
        Assert.Equal(Raw, File.ReadAllText(Paths.JobResponseJson("job_1")));
        Assert.Equal(Paths.JobResponseJson("job_1"), new MeetCapDatabase(DatabasePath).AsrJobs.Get("job_1")!.RawResponsePath);
        Assert.Equal("Recovered speech", Assert.Single(new FileTranscriptStore().ReadJsonl(Paths.RawTranscriptJsonl)).RawText);
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(Paths.ResolveRelative(result.Job.InputArtifact))));
    }

    [Theory]
    [InlineData("tooling")]
    [InlineData("probe")]
    [InlineData("disk")]
    public async Task LocalPreparationFailureThenRepair_SubmitsOnceAfterSqliteRestart(string failure)
    {
        var job = CreateJob();
        var hash = SHA256.HashData(File.ReadAllBytes(Paths.ResolveRelative(job.InputArtifact)));
        var handler = new InProcessHandler();
        using var adapter = Adapter(handler);
        var broken = true;
        var mediaConstructions = 0;
        var provider = new NormalizingAsrProvider(adapter, () =>
        {
            mediaConstructions++;
            if (broken) throw failure switch
            {
                "tooling" => new MediaToolingException("Missing FFmpeg"),
                "probe" => new MediaProbeException("Probe failed"),
                _ => new IOException("Disk unavailable"),
            };
            return new ReadyMedia();
        }, _root);

        var first = await Processor(provider).ProcessAsync(job);
        Assert.Equal(AsrJobStatus.RetryWait, first.Job.Status);
        Assert.Equal("submit.not_sent.audio.normalization", first.Job.ErrorCode);
        Assert.Null(first.Job.SubmittedAt);
        Assert.Empty(handler.Requests);
        broken = false;
        var recovered = await RestartAndResume(provider);

        Assert.Equal(2, mediaConstructions);
        Assert.Equal(2, recovered.Job.AttemptCount);
        Assert.Equal(new[] { "/submit", "/query" }, handler.Requests.Select(r => r.Path));
        Assert.All(handler.Requests, r => Assert.Equal(job.ProviderRequestId, r.Id));
        AssertDurableSuccess(recovered, hash);
    }

    [Fact]
    public async Task LegacyLocalPreparationRetry_IsStillSafeToSubmit()
    {
        var job = CreateJob(AsrJobStatus.RetryWait);
        new MeetCapDatabase(DatabasePath).AsrJobs.Update(job with { ErrorCode = "audio.normalization" });
        var handler = new InProcessHandler();
        using var adapter = Adapter(handler);
        var provider = new NormalizingAsrProvider(adapter, () => new ReadyMedia(), _root);
        var result = await RestartAndResume(provider);
        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "/submit", "/query" }, handler.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task LaterLegacyPreparationFailure_CannotEraseAnEarlierUncertainSubmit()
    {
        var job = CreateJob(AsrJobStatus.RetryWait);
        new MeetCapDatabase(DatabasePath).AsrJobs.Update(job with { AttemptCount = 2, ErrorCode = "audio.normalization" });
        var handler = new InProcessHandler();
        using var adapter = Adapter(handler);
        var provider = new NormalizingAsrProvider(adapter, () => throw new Exception("Must query the earlier request"), _root);
        var result = await RestartAndResume(provider);
        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.Job.AttemptCount);
        Assert.Equal(new[] { ("/query", job.ProviderRequestId) }, handler.Requests);
    }

    [Fact]
    public async Task AudioStagingFailure_DoesNotCreateAnAsrTaskAndCanRetry()
    {
        var job = CreateJob();
        var handler = new InProcessHandler();
        using var failing = Adapter(handler, new FailingPublisher());
        var first = await Processor(failing).ProcessAsync(job);
        Assert.Equal("submit.not_sent.tos.unavailable", first.Job.ErrorCode);
        Assert.Empty(handler.Requests);

        using var repaired = Adapter(handler);
        var result = await RestartAndResume(repaired);
        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "/submit", "/query" }, handler.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task SubmitWithOnlyDnsFailures_RetriesSubmissionAfterNetworkRepair()
    {
        var job = CreateJob();
        var hash = SHA256.HashData(File.ReadAllBytes(Paths.ResolveRelative(job.InputArtifact)));
        var handler = new InProcessHandler { Reply = (_, _) => throw DnsFailure() };
        using var adapter = Adapter(handler);
        var first = await Processor(adapter).ProcessAsync(job);
        Assert.Equal("submit.not_sent.http.unreachable", first.Job.ErrorCode);
        Assert.Equal(AsrJobOutcome.AwaitingRetry, first.Outcome);
        var priorRequests = handler.Requests.Count;

        handler.Reply = null;
        var result = await RestartAndResume(adapter);
        Assert.Equal(new[] { "/submit", "/query" }, handler.Requests.Skip(priorRequests).Select(r => r.Path));
        Assert.All(handler.Requests, r => Assert.Equal(job.ProviderRequestId, r.Id));
        AssertDurableSuccess(result, hash);
    }

    [Fact]
    public async Task ConnectionResetThenDnsFailure_DoesNotEraseUncertainAcceptance()
    {
        var job = CreateJob();
        var handler = new InProcessHandler
        {
            Reply = (_, ordinal) => throw (ordinal == 1
                ? new HttpRequestException(HttpRequestError.ConnectionError, "Response lost") : DnsFailure()),
        };
        using var adapter = Adapter(handler);
        var first = await Processor(adapter).ProcessAsync(job);
        Assert.Equal("http.unreachable", first.Job.ErrorCode);
        var priorRequests = handler.Requests.Count;
        handler.Reply = null;

        var result = await RestartAndResume(adapter);
        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "/query" }, handler.Requests.Skip(priorRequests).Select(r => r.Path));
    }

    [Theory]
    [InlineData("45000001")]
    [InlineData("55000001")]
    [InlineData("404")]
    [InlineData("400")]
    [InlineData("missing-header")]
    [InlineData("malformed")]
    public async Task UncertainSubmitThenGenericQueryError_NeverUploadsAgain(string error)
    {
        var job = CreateJob();
        var hash = SHA256.HashData(File.ReadAllBytes(Paths.ResolveRelative(job.InputArtifact)));
        var handler = new InProcessHandler
        {
            Reply = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Lost submit response"),
        };
        using var adapter = Adapter(handler);
        var first = await Processor(adapter).ProcessAsync(job);
        Assert.Equal("http.unreachable", first.Job.ErrorCode);
        var priorRequests = handler.Requests.Count;
        handler.Reply = (_, _) => error switch
        {
            "404" => Response(null, HttpStatusCode.NotFound),
            "400" => Response(null, HttpStatusCode.BadRequest),
            "missing-header" => Response(null),
            "malformed" => Response("20000000", body: "not-json"),
            _ => Response(error),
        };

        var result = await RestartAndResume(adapter);
        if (result.Outcome == AsrJobOutcome.AwaitingRetry) result = await RestartAndResume(adapter);
        Assert.Equal(AsrJobOutcome.Failed, result.Outcome);
        Assert.NotEqual("provider.task_not_found", result.Job.ErrorCode);
        Assert.All(handler.Requests.Skip(priorRequests), r => Assert.Equal("/query", r.Path));
        Assert.All(handler.Requests, r => Assert.Equal(job.ProviderRequestId, r.Id));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Paths.ResolveRelative(job.InputArtifact))));
        Assert.False(File.Exists(Paths.JobNormalizedJsonl(job.Id)));
        if (error == "malformed") Assert.Equal("not-json", File.ReadAllText(Paths.JobResponseJson(job.Id)));
    }

    [Fact]
    public async Task AcceptedLegacyTaskWithQueryFailure_RetainsRawBeforeRefusingNormalization()
    {
        var job = CreateJob(AsrJobStatus.Submitted, ambiguous: true);
        var handler = new InProcessHandler
        {
            Reply = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Lost query response"),
        };
        using var adapter = Adapter(handler);
        var provider = new NormalizingAsrProvider(adapter, () => throw new Exception("Polling must not prepare audio"), _root);
        var first = await Processor(provider).ProcessAsync(job);
        Assert.Equal(AsrJobOutcome.AwaitingRetry, first.Outcome);
        handler.Reply = null;
        var recovered = await RestartAndResume(provider);
        Assert.Equal("asr.timeline_ambiguous", recovered.Job.ErrorCode);
        Assert.Equal(job.SubmittedAt, recovered.Job.SubmittedAt);
        Assert.All(handler.Requests, r => Assert.Equal(("/query", job.ProviderRequestId), r));
        Assert.Equal(Raw, File.ReadAllText(Paths.JobResponseJson(job.Id)));
        Assert.Equal(Paths.JobResponseJson(job.Id), new MeetCapDatabase(DatabasePath).AsrJobs.Get(job.Id)!.RawResponsePath);
        Assert.False(File.Exists(Paths.JobNormalizedJsonl(job.Id)));
    }

    private static HttpRequestException DnsFailure() => new(HttpRequestError.NameResolutionError, "DNS lookup failed");

    private static HttpResponseMessage Response(string? status, HttpStatusCode http = HttpStatusCode.OK, string body = Raw)
    {
        var response = new HttpResponseMessage(http) { Content = new StringContent(body) };
        if (status is not null) response.Headers.Add("X-Api-Status-Code", status);
        // A human-readable unknown-task phrase alone is not a verified absence signal.
        response.Headers.Add("X-Api-Message", "unknown task");
        return response;
    }

    private sealed class InProcessHandler : HttpMessageHandler
    {
        public List<(string Path, string Id)> Requests { get; } = new();
        public Func<string, int, HttpResponseMessage>? Reply { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.EndsWith("/submit", StringComparison.Ordinal) ? "/submit" : "/query";
            Assert.Equal("asr.invalid", request.RequestUri.Host);
            Requests.Add((path, request.Headers.GetValues("X-Api-Request-Id").Single()));
            return Task.FromResult(Reply?.Invoke(path, Requests.Count) ?? Response("20000000", body: path == "/query" ? Raw : "{}"));
        }
    }

    private sealed class ReadyMedia : IMediaPipeline
    {
        public MediaInfo Inspect(string path) => new()
        {
            Path = path, FormatName = "wav", DurationMs = 1000, ByteLength = new FileInfo(path).Length,
            AudioCodec = "pcm_s16le", SampleRateHz = 16000, Channels = 1, BitDepth = 16,
        };
        public Task NormalizeAsync(string input, string output, MediaNormalizationPlan plan, CancellationToken ct = default)
            => throw new InvalidOperationException("The source already satisfies the ASR format.");
    }

    private sealed class FailingPublisher : IAsrAudioPublisher
    {
        public Task<AsrPublishedAudio> PublishAsync(AsrFileRequest request, CancellationToken ct = default)
            => throw new AsrTransientException("tos.unavailable", "Staging unavailable");
        public Task DeleteAsync(AsrPublishedAudio audio, CancellationToken ct = default) => Task.CompletedTask;
    }
}
