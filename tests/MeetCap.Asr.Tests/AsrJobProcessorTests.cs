using MeetCap.Asr;
using MeetCap.Asr.Transcripts;
using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;
using MeetCap.Core.Transcripts;
using Xunit;

namespace MeetCap.Asr.Tests;

/// <summary>
/// The persistent job processor is the M3 reliability core: durable retry state, raw
/// response retention before normalization, crash recovery that does not re-submit,
/// and no streaming fallback anywhere.
/// </summary>
public class AsrJobProcessorTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly FakeAsrProvider _provider = new();
    private readonly FakeNormalizer _normalizer = new();
    private readonly InMemoryAsrJobStore _jobs = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly RecordingArtifactWriter _artifacts = new();
    private readonly FileTranscriptStore _transcripts = new();

    public AsrJobProcessorTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "meetcap-asr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    private SessionArtifactPaths Paths => new(_dataRoot, "ses_1");

    private AsrJobProcessor CreateProcessor(
        AsrRetryPolicy? retryPolicy = null,
        TimeSpan? pollTimeout = null,
        bool writeMarkdown = true) =>
        new(
            _jobs,
            _provider,
            _normalizer,
            _transcripts,
            _sessions,
            _artifacts,
            new AsrJobProcessorOptions
            {
                DataRoot = _dataRoot,
                RetryPolicy = retryPolicy ?? new AsrRetryPolicy(8, 5, 300),
                PollInterval = TimeSpan.Zero,
                PollTimeout = pollTimeout ?? TimeSpan.FromMinutes(15),
                WriteMarkdown = writeMarkdown,
                TimeProvider = TimeProvider.System,
                Delay = (_, _) => Task.CompletedTask,
            });

    private AsrJob CreateJob(
        AsrJobStatus status = AsrJobStatus.Pending,
        int attempts = 0,
        DateTimeOffset? nextRetryAt = null,
        string provider = "volcengine")
    {
        var job = new AsrJob
        {
            Id = "job_1",
            SessionId = "ses_1",
            Source = AudioSource.Import,
            Tier = "standard",
            Provider = provider,
            StartMs = 0,
            EndMs = 754_000,
            InputArtifact = "audio/import/normalized.wav",
            Status = status,
            ProviderRequestId = "req-0001",
            AttemptCount = attempts,
            NextRetryAt = nextRetryAt,
            DurationMs = 754_000,
            SpeakerInfoRequested = true,
            CreatedAt = s_now,
            UpdatedAt = s_now,
        };

        _jobs.Create(job);
        var artifactPath = Paths.ResolveRelative(job.InputArtifact);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllBytes(artifactPath, new byte[64]);

        if (_sessions.Get("ses_1") is null)
        {
            _sessions.Create(new Session
            {
                Id = "ses_1",
                Title = "Imported",
                Mode = SessionMode.Import,
                SourceType = SessionSourceType.Import,
                Status = SessionStatus.Processing,
                CreatedAt = s_now,
                UpdatedAt = s_now,
            });
        }

        return job;
    }

    private static AsrPollResult Completed(string body = """{"result":{"utterances":[]}}""") =>
        AsrPollResult.Completed(new AsrCompletion(body, "20000000"));

    [Fact]
    public async Task SuccessPath_RetainsRawResponseAndWritesEveryTranscriptArtifact()
    {
        var job = CreateJob();
        _provider.EnqueuePoll(Completed());

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.SegmentCount);
        Assert.Equal(AsrJobStatus.Succeeded, result.Job.Status);

        // Raw provider response is retained, and the normalized derivative sits beside it.
        Assert.True(File.Exists(Paths.JobResponseJson("job_1")));
        Assert.True(File.Exists(Paths.JobNormalizedJsonl("job_1")));
        Assert.True(File.Exists(Paths.JobRequestJson("job_1")));
        Assert.True(File.Exists(Paths.RawTranscriptJsonl));
        Assert.True(File.Exists(Paths.LiveTranscriptMarkdown));
        Assert.Equal(Paths.JobResponseJson("job_1"), result.Job.RawResponsePath);
        Assert.Equal(Paths.JobNormalizedJsonl("job_1"), result.Job.NormalizedResultPath);

        var markdown = File.ReadAllText(Paths.LiveTranscriptMarkdown);
        Assert.Contains("speaker_1", markdown, StringComparison.Ordinal);
        Assert.Contains("anonymous", markdown, StringComparison.OrdinalIgnoreCase);

        Assert.True(result.Job.SpeakerInfoReturned);
        Assert.Equal(0.1675, result.Job.EstimatedCostCny, precision: 3);
    }

    [Fact]
    public async Task SuccessPath_CompletesTheSession()
    {
        var job = CreateJob();
        _provider.EnqueuePoll(Completed());

        await CreateProcessor().ProcessAsync(job);

        Assert.Equal(SessionStatus.Completed, _sessions.Get("ses_1")!.Status);
        Assert.Contains(SessionEvents.SessionCompleted, _artifacts.Events);
        Assert.Contains(SessionEvents.AsrJobCompleted, _artifacts.Events);
    }

    [Fact]
    public async Task TransientSubmitFailure_SchedulesADurableRetry()
    {
        var job = CreateJob();
        _provider.OnSubmit = _ => throw new AsrTransientException("http.503", "unavailable");

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.AwaitingRetry, result.Outcome);
        Assert.Equal(AsrJobStatus.RetryWait, result.Job.Status);
        Assert.Equal(1, result.Job.AttemptCount);
        Assert.NotNull(result.Job.NextRetryAt);
        Assert.Equal("http.503", result.Job.ErrorCode);

        // The job stays resumable: nothing was lost.
        Assert.Equal(SessionStatus.Processing, _sessions.Get("ses_1")!.Status);
        Assert.Contains(SessionEvents.AsrJobRetryWait, _artifacts.Events);
    }

    [Fact]
    public async Task RetryWaitJob_IsResumedByRunDueAndSucceeds()
    {
        var job = CreateJob();
        _provider.OnSubmit = _ => throw new AsrTransientException("http.503", "unavailable");
        var processor = CreateProcessor();

        var first = await processor.ProcessAsync(job);
        Assert.Equal(AsrJobStatus.RetryWait, first.Job.Status);

        // Clear the durable backoff as if the clock had advanced past next_retry_at.
        var due = _jobs.Get("job_1")! with { NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        _jobs.Update(due);

        _provider.OnSubmit = null;
        _provider.EnqueuePoll(Completed());

        var results = await processor.RunDueAsync(10);

        var resumed = Assert.Single(results);
        Assert.Equal(AsrJobOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(2, resumed.Job.AttemptCount);
        Assert.Equal(2, _provider.Submissions.Count);
    }

    [Fact]
    public async Task PermanentFailure_FailsTheJobAndLeavesTheSessionRecoverable()
    {
        var job = CreateJob();
        _provider.OnSubmit = _ => throw new AsrPermanentException("http.401", "bad credentials");

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.Failed, result.Outcome);
        Assert.Equal(AsrJobStatus.Failed, result.Job.Status);
        Assert.Equal("http.401", result.Job.ErrorCode);

        // Audio is safe and the session is recoverable, not silently completed.
        Assert.Equal(SessionStatus.Processing, _sessions.Get("ses_1")!.Status);
        Assert.Contains(SessionEvents.AsrJobFailed, _artifacts.Events);
    }

    [Fact]
    public async Task RestartWithAnInFlightSubmit_PollsInsteadOfSubmittingAgain()
    {
        // A process killed between "submitting" and "submitted" must not pay twice.
        var job = CreateJob(AsrJobStatus.Submitting, attempts: 1);
        _provider.EnqueuePoll(Completed());

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        Assert.Empty(_provider.Submissions);
        Assert.Single(_provider.PolledRequestIds);
        Assert.Equal("req-0001", _provider.PolledRequestIds[0]);
    }

    [Fact]
    public async Task RestartWithADueRetry_ResubmitsWithTheSameProviderRequestId()
    {
        var job = CreateJob(AsrJobStatus.RetryWait, attempts: 1, nextRetryAt: s_now.AddMinutes(-1));
        _provider.EnqueuePoll(Completed());

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.Succeeded, result.Outcome);
        var submission = Assert.Single(_provider.Submissions);
        Assert.Equal("req-0001", submission.ProviderRequestId);
        Assert.Equal(2, result.Job.AttemptCount);
    }

    [Fact]
    public async Task PollTimeout_LeavesTheJobPollingSoALaterCommandCanContinue()
    {
        var job = CreateJob(AsrJobStatus.Submitted, attempts: 1);
        _provider.OnPoll = _ => AsrPollResult.Pending();

        var result = await CreateProcessor(pollTimeout: TimeSpan.Zero).ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.StillRunning, result.Outcome);
        Assert.Equal(AsrJobStatus.Polling, result.Job.Status);
        Assert.Contains("asr resume", result.Message!, StringComparison.Ordinal);
        Assert.Equal(AsrJobStatus.Polling, _jobs.Get("job_1")!.Status);
    }

    [Fact]
    public async Task UnknownProviderTask_FallsBackToARetryInsteadOfStalling()
    {
        var job = CreateJob(AsrJobStatus.Submitted, attempts: 1);
        _provider.OnPoll = _ => AsrPollResult.TaskNotFound();

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.AwaitingRetry, result.Outcome);
        Assert.Equal("provider.task_not_found", result.Job.ErrorCode);
    }

    [Fact]
    public async Task TransientPollFailure_RetriesAndExhaustionFailsTheJob()
    {
        var job = CreateJob(AsrJobStatus.Submitted, attempts: 1);
        _provider.OnPoll = _ => AsrPollResult.Failed(new AsrProviderError("55000001", "boom", IsTransient: true));

        var first = await CreateProcessor(retryPolicy: new AsrRetryPolicy(2, 1, 5)).ProcessAsync(job);
        Assert.Equal(AsrJobOutcome.AwaitingRetry, first.Outcome);

        var exhausted = await CreateProcessor(retryPolicy: new AsrRetryPolicy(2, 1, 5))
            .ProcessAsync(first.Job with { NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        Assert.Equal(AsrJobOutcome.Failed, exhausted.Outcome);
        Assert.Equal(AsrJobStatus.Failed, exhausted.Job.Status);
        Assert.Contains("exhausted", exhausted.Job.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawResponseIsRetainedEvenWhenNormalizationFails()
    {
        var job = CreateJob(AsrJobStatus.Submitted, attempts: 1);
        _provider.OnPoll = _ => Completed("""{"result":"unexpected-shape"}""");
        _normalizer.OnNormalize = (_, _) => throw new AsrNormalizationException("shape changed");

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.Failed, result.Outcome);
        Assert.Equal("asr.normalization_failed", result.Job.ErrorCode);
        Assert.True(File.Exists(Paths.JobResponseJson("job_1")));
        Assert.Contains("unexpected-shape", File.ReadAllText(Paths.JobResponseJson("job_1")), StringComparison.Ordinal);
        Assert.Contains("without re-billing", result.Job.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderErrorEnvelopeInANormalizedResponse_FailsTheJob()
    {
        var job = CreateJob(AsrJobStatus.Submitted, attempts: 1);
        _provider.EnqueuePoll(Completed("""{"error":{"code":"45000002"}}"""));
        _normalizer.OnNormalize = (_, _) => new AsrNormalizationResult
        {
            Segments = Array.Empty<TranscriptSegment>(),
            SpeakerInfoReturned = false,
            ErrorCode = "45000002",
            ErrorMessage = "empty audio",
        };

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.Failed, result.Outcome);
        Assert.Equal("45000002", result.Job.ErrorCode);
    }

    [Fact]
    public async Task RunDue_SkipsJobsOwnedByAnotherProvider()
    {
        CreateJob(provider: "some-other-provider");

        var results = await CreateProcessor().RunDueAsync(10);

        var skipped = Assert.Single(results);
        Assert.Equal(AsrJobOutcome.NoWork, skipped.Outcome);
        Assert.Empty(_provider.Submissions);
    }

    [Fact]
    public async Task RunDue_ReportsNoWorkWhenThereIsNothingToDo()
    {
        Assert.Empty(await CreateProcessor().RunDueAsync(10));
        Assert.Empty(await CreateProcessor().RunDueAsync(0));
    }

    [Fact]
    public async Task JobWithoutAProviderRequestId_IsFailedRatherThanResubmittedBlindly()
    {
        var job = CreateJob() with { ProviderRequestId = null };
        _jobs.Update(job);

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.Failed, result.Outcome);
        Assert.Equal("asr.configuration", result.Job.ErrorCode);
        Assert.Empty(_provider.Submissions);
    }

    [Fact]
    public async Task TerminalJobs_AreLeftAlone()
    {
        var job = CreateJob(AsrJobStatus.Succeeded);

        var result = await CreateProcessor().ProcessAsync(job);

        Assert.Equal(AsrJobOutcome.NoWork, result.Outcome);
        Assert.Empty(_provider.Submissions);
        Assert.Empty(_provider.PolledRequestIds);
    }

    [Fact]
    public async Task MarkdownIsOptionalButRawJsonlIsAlwaysWritten()
    {
        var job = CreateJob();
        _provider.EnqueuePoll(Completed());

        await CreateProcessor(writeMarkdown: false).ProcessAsync(job);

        Assert.True(File.Exists(Paths.RawTranscriptJsonl));
        Assert.False(File.Exists(Paths.LiveTranscriptMarkdown));
    }
}
