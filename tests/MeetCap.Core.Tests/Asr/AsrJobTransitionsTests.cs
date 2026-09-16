using MeetCap.Core.Asr;
using Xunit;

namespace MeetCap.Core.Tests.Asr;

/// <summary>
/// The persistent ASR job state machine is the durable queue's correctness core
/// (docs/ARCHITECTURE.md section 12). These tests pin the legal edges, the retry
/// bookkeeping, and the crash-recovery rule that keeps a restart from submitting the
/// same audio twice.
/// </summary>
public class AsrJobTransitionsTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static AsrJob Job(AsrJobStatus status = AsrJobStatus.Pending, int attempts = 0) => new()
    {
        Id = "job_1",
        SessionId = "ses_1",
        Source = "import",
        Tier = "standard",
        Provider = "volcengine",
        InputArtifact = "audio/import/a.wav",
        Status = status,
        ProviderRequestId = "req-1",
        AttemptCount = attempts,
        CreatedAt = s_now,
        UpdatedAt = s_now,
    };

    [Theory]
    [InlineData(AsrJobStatus.Pending, AsrJobStatus.Submitting, true)]
    [InlineData(AsrJobStatus.Submitting, AsrJobStatus.Submitted, true)]
    [InlineData(AsrJobStatus.Submitted, AsrJobStatus.Polling, true)]
    [InlineData(AsrJobStatus.Polling, AsrJobStatus.Polling, true)]
    [InlineData(AsrJobStatus.Polling, AsrJobStatus.Succeeded, true)]
    [InlineData(AsrJobStatus.RetryWait, AsrJobStatus.Submitting, true)]
    [InlineData(AsrJobStatus.Pending, AsrJobStatus.Failed, true)]
    [InlineData(AsrJobStatus.Polling, AsrJobStatus.Cancelled, true)]
    [InlineData(AsrJobStatus.Pending, AsrJobStatus.Submitted, false)]
    [InlineData(AsrJobStatus.Pending, AsrJobStatus.Succeeded, false)]
    [InlineData(AsrJobStatus.Succeeded, AsrJobStatus.Submitting, false)]
    [InlineData(AsrJobStatus.Failed, AsrJobStatus.RetryWait, false)]
    [InlineData(AsrJobStatus.Cancelled, AsrJobStatus.Pending, false)]
    [InlineData(AsrJobStatus.RetryWait, AsrJobStatus.RetryWait, false)]
    public void CanTransition_MatchesTheDocumentedStateMachine(AsrJobStatus from, AsrJobStatus to, bool expected) =>
        Assert.Equal(expected, AsrJobTransitions.CanTransition(from, to));

    [Fact]
    public void BeginSubmit_CountsTheAttemptBeforeTheRequestIsSent()
    {
        // Counting here (not after a successful response) is what stops a submit that
        // never returns from retrying forever.
        var job = AsrJobTransitions.BeginSubmit(Job(), s_now);

        Assert.Equal(AsrJobStatus.Submitting, job.Status);
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public void MarkSubmitted_KeepsTheAttemptCountAndRecordsMetadataPath()
    {
        var submitted = AsrJobTransitions.MarkSubmitted(
            AsrJobTransitions.BeginSubmit(Job(), s_now),
            "C:/jobs/job_1/request.json",
            s_now);

        Assert.Equal(AsrJobStatus.Submitted, submitted.Status);
        Assert.Equal(1, submitted.AttemptCount);
        Assert.Equal("C:/jobs/job_1/request.json", submitted.RequestMetadataPath);
        Assert.Equal(s_now, submitted.SubmittedAt);
    }

    [Fact]
    public void ScheduleRetry_UsesExponentialBackoffCappedByThePolicy()
    {
        var policy = new AsrRetryPolicy(MaxAttempts: 8, InitialSeconds: 5, MaxSeconds: 20);

        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayFor(7));
    }

    [Fact]
    public void ScheduleRetry_PersistsTheNextAttemptTime()
    {
        var job = Job(AsrJobStatus.Polling, attempts: 2);
        var retried = AsrJobTransitions.ScheduleRetry(
            job,
            s_now,
            "http.503",
            "temporarily unavailable",
            new AsrRetryPolicy(8, 5, 300));

        Assert.Equal(AsrJobStatus.RetryWait, retried.Status);
        Assert.Equal(s_now.AddSeconds(10), retried.NextRetryAt);
        Assert.Equal("http.503", retried.ErrorCode);
    }

    [Fact]
    public void CanRetry_StopsAtTheConfiguredAttemptBudget()
    {
        var policy = new AsrRetryPolicy(MaxAttempts: 3, InitialSeconds: 1, MaxSeconds: 10);

        Assert.True(policy.CanRetry(0));
        Assert.True(policy.CanRetry(2));
        Assert.False(policy.CanRetry(3));
    }

    [Fact]
    public void MarkFailed_ClearsTheRetryScheduleAndStampsCompletion()
    {
        var failed = AsrJobTransitions.MarkFailed(Job(AsrJobStatus.Polling), s_now, "auth", "denied");

        Assert.Equal(AsrJobStatus.Failed, failed.Status);
        Assert.Null(failed.NextRetryAt);
        Assert.Equal(s_now, failed.CompletedAt);
    }

    [Fact]
    public void MarkSucceeded_RecordsBothArtifactPaths()
    {
        var job = Job(AsrJobStatus.Polling);
        var succeeded = AsrJobTransitions.MarkSucceeded(
            job,
            s_now,
            "jobs/job_1/response.json",
            "jobs/job_1/normalized.jsonl",
            speakerInfoReturned: true);

        Assert.Equal(AsrJobStatus.Succeeded, succeeded.Status);
        Assert.Equal("jobs/job_1/response.json", succeeded.RawResponsePath);
        Assert.Equal("jobs/job_1/normalized.jsonl", succeeded.NormalizedResultPath);
        Assert.True(succeeded.SpeakerInfoReturned);
    }

    [Fact]
    public void RecoverAfterRestart_TreatsInFlightSubmitAsAccepted()
    {
        // The provider request id is the provider's task id and was persisted before
        // the request, so recovery polls rather than re-submitting and paying twice.
        var recovered = AsrJobTransitions.RecoverAfterRestart(Job(AsrJobStatus.Submitting, attempts: 1), s_now);

        Assert.Equal(AsrJobStatus.Submitted, recovered.Status);
        Assert.Equal(1, recovered.AttemptCount);
    }

    [Fact]
    public void RecoverAfterRestart_LeavesPendingAndPollingJobsUnchanged()
    {
        Assert.Equal(
            AsrJobStatus.Pending,
            AsrJobTransitions.RecoverAfterRestart(Job(AsrJobStatus.Pending), s_now).Status);
        Assert.Equal(
            AsrJobStatus.Polling,
            AsrJobTransitions.RecoverAfterRestart(Job(AsrJobStatus.Polling), s_now).Status);
    }

    [Fact]
    public void IllegalTransition_ThrowsWithBothStatesNamed()
    {
        var ex = Assert.Throws<InvalidAsrJobTransitionException>(
            () => AsrJobTransitions.BeginPolling(Job(AsrJobStatus.Pending), s_now));

        Assert.Equal(AsrJobStatus.Pending, ex.From);
        Assert.Equal(AsrJobStatus.Polling, ex.To);
        Assert.Contains("pending", ex.Message, StringComparison.Ordinal);
        Assert.Contains("polling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusWireValues_RoundTripAndAreTerminalAware()
    {
        foreach (var status in Enum.GetValues<AsrJobStatus>())
        {
            Assert.Equal(status, AsrJobStatuses.Parse(AsrJobStatuses.ToWire(status)));
        }

        Assert.True(AsrJobStatuses.IsTerminal(AsrJobStatus.Succeeded));
        Assert.True(AsrJobStatuses.IsTerminal(AsrJobStatus.Failed));
        Assert.True(AsrJobStatuses.IsTerminal(AsrJobStatus.Cancelled));
        Assert.False(AsrJobStatuses.IsTerminal(AsrJobStatus.RetryWait));
        Assert.True(AsrJobStatuses.IsResumable(AsrJobStatus.RetryWait));
        Assert.False(AsrJobStatuses.IsResumable(AsrJobStatus.Failed));
    }

    [Fact]
    public void EstimatedCost_IsDurationTimesHourlyRate()
    {
        Assert.Equal(0.4, AsrCostEstimator.Estimate(1_800_000, 0.8), precision: 6);
        Assert.Equal(0, AsrCostEstimator.Estimate(0, 0.8));
        Assert.Equal(0, AsrCostEstimator.Estimate(1_800_000, 0));
    }
}
