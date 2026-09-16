namespace MeetCap.Core.Asr;

/// <summary>
/// Legal ASR job state transitions (<c>docs/ARCHITECTURE.md</c> section 12 and
/// <c>docs/ASR_STRATEGY.md</c> section 6). Every transition is validated, so an
/// illegal lifecycle change fails loudly instead of silently corrupting queue state.
/// </summary>
/// <remarks>
/// <code>
/// pending     -> submitting | failed | cancelled
/// submitting  -> submitted  | retry_wait | failed | cancelled
/// submitted   -> polling    | retry_wait | failed | cancelled
/// polling     -> polling    | succeeded | retry_wait | failed | cancelled
/// retry_wait  -> submitting | failed | cancelled
/// succeeded / failed / cancelled -> (terminal)
/// </code>
/// Any non-terminal state may fail or be cancelled, so a configuration error detected
/// before the first submit has somewhere legal to land.
/// </remarks>
public static class AsrJobTransitions
{
    private static readonly Dictionary<AsrJobStatus, AsrJobStatus[]> s_allowed = new()
    {
        [AsrJobStatus.Pending] = new[]
        {
            AsrJobStatus.Submitting, AsrJobStatus.Failed, AsrJobStatus.Cancelled,
        },
        [AsrJobStatus.Submitting] = new[]
        {
            AsrJobStatus.Submitted, AsrJobStatus.RetryWait, AsrJobStatus.Failed, AsrJobStatus.Cancelled,
        },
        [AsrJobStatus.Submitted] = new[]
        {
            AsrJobStatus.Polling, AsrJobStatus.RetryWait, AsrJobStatus.Failed, AsrJobStatus.Cancelled,
        },
        [AsrJobStatus.Polling] = new[]
        {
            AsrJobStatus.Polling, AsrJobStatus.Succeeded, AsrJobStatus.RetryWait,
            AsrJobStatus.Failed, AsrJobStatus.Cancelled,
        },
        [AsrJobStatus.RetryWait] = new[]
        {
            AsrJobStatus.Submitting, AsrJobStatus.Failed, AsrJobStatus.Cancelled,
        },
        [AsrJobStatus.Succeeded] = Array.Empty<AsrJobStatus>(),
        [AsrJobStatus.Failed] = Array.Empty<AsrJobStatus>(),
        [AsrJobStatus.Cancelled] = Array.Empty<AsrJobStatus>(),
    };

    public static bool CanTransition(AsrJobStatus from, AsrJobStatus to) =>
        s_allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureTransitionAllowed(AsrJobStatus from, AsrJobStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidAsrJobTransitionException(from, to);
        }
    }

    /// <summary>
    /// Moves the job into <c>submitting</c> and counts the attempt. The attempt is
    /// counted here, before the request, so a submit that never returns still consumes
    /// one attempt and cannot retry forever.
    /// </summary>
    public static AsrJob BeginSubmit(AsrJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureTransitionAllowed(job.Status, AsrJobStatus.Submitting);
        return job with
        {
            Status = AsrJobStatus.Submitting,
            AttemptCount = job.AttemptCount + 1,
            NextRetryAt = null,
            UpdatedAt = now,
        };
    }

    /// <summary>Records the sanitized request metadata path and moves to <c>submitted</c>.</summary>
    public static AsrJob MarkSubmitted(AsrJob job, string requestMetadataPath, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestMetadataPath);
        EnsureTransitionAllowed(job.Status, AsrJobStatus.Submitted);

        return job with
        {
            Status = AsrJobStatus.Submitted,
            RequestMetadataPath = requestMetadataPath,
            SubmittedAt = job.SubmittedAt ?? now,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAt = now,
        };
    }

    /// <summary>Enters polling. Polling is a self-transition, so repeated queries are legal.</summary>
    public static AsrJob BeginPolling(AsrJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureTransitionAllowed(job.Status, AsrJobStatus.Polling);
        return job with { Status = AsrJobStatus.Polling, UpdatedAt = now };
    }

    /// <summary>Records the retained raw provider response path.</summary>
    public static AsrJob RecordRawResponse(AsrJob job, string rawResponsePath, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawResponsePath);
        return job with { RawResponsePath = rawResponsePath, UpdatedAt = now };
    }

    /// <summary>
    /// Schedules a persistent retry. The delay is derived from the attempt that just
    /// failed, and the resulting <c>next_retry_at</c> is durable so a restart
    /// resumes the same backoff instead of restarting it.
    /// </summary>
    public static AsrJob ScheduleRetry(
        AsrJob job,
        DateTimeOffset now,
        string errorCode,
        string errorMessage,
        AsrRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(policy);
        EnsureTransitionAllowed(job.Status, AsrJobStatus.RetryWait);

        var attempts = Math.Max(1, job.AttemptCount);
        return job with
        {
            Status = AsrJobStatus.RetryWait,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            NextRetryAt = now + policy.DelayFor(attempts - 1),
            UpdatedAt = now,
        };
    }

    /// <summary>Terminal failure.</summary>
    public static AsrJob MarkFailed(AsrJob job, DateTimeOffset now, string errorCode, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureTransitionAllowed(job.Status, AsrJobStatus.Failed);

        return job with
        {
            Status = AsrJobStatus.Failed,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            NextRetryAt = null,
            CompletedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Terminal cancellation.</summary>
    public static AsrJob Cancel(AsrJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureTransitionAllowed(job.Status, AsrJobStatus.Cancelled);
        return job with
        {
            Status = AsrJobStatus.Cancelled,
            NextRetryAt = null,
            CompletedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Terminal success, with the retained raw response and normalized result paths.</summary>
    public static AsrJob MarkSucceeded(
        AsrJob job,
        DateTimeOffset now,
        string rawResponsePath,
        string normalizedResultPath,
        bool speakerInfoReturned)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawResponsePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedResultPath);
        EnsureTransitionAllowed(job.Status, AsrJobStatus.Succeeded);

        return job with
        {
            Status = AsrJobStatus.Succeeded,
            RawResponsePath = rawResponsePath,
            NormalizedResultPath = normalizedResultPath,
            SpeakerInfoReturned = speakerInfoReturned || job.SpeakerInfoReturned,
            NextRetryAt = null,
            CompletedAt = now,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAt = now,
        };
    }

    /// <summary>Records the estimated provider cost for this job.</summary>
    public static AsrJob WithEstimatedCost(AsrJob job, double estimatedCostCny, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job with { EstimatedCostCny = estimatedCostCny, UpdatedAt = now };
    }

    /// <summary>
    /// Crash recovery for a job left mid-flight by an earlier process.
    /// </summary>
    /// <remarks>
    /// A job in <c>submitting</c> may or may not have reached the provider. The
    /// provider request id is allocated before the first submit and is the provider's
    /// task identifier, so recovery treats the submission as accepted and resumes by
    /// polling rather than re-submitting — re-submitting would risk paying twice for
    /// the same audio. If the provider reports the task as unknown, the job falls back
    /// to <c>retry_wait</c> and is submitted again.
    /// </remarks>
    public static AsrJob RecoverAfterRestart(AsrJob job, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.Status switch
        {
            AsrJobStatus.Submitting => job with { Status = AsrJobStatus.Submitted, UpdatedAt = now },
            _ => job,
        };
    }
}

/// <summary>Raised when an ASR job lifecycle change is not a legal edge.</summary>
public sealed class InvalidAsrJobTransitionException : InvalidOperationException
{
    public InvalidAsrJobTransitionException(AsrJobStatus from, AsrJobStatus to)
        : base(
            $"Invalid ASR job transition '{AsrJobStatuses.ToWire(from)}' -> " +
            $"'{AsrJobStatuses.ToWire(to)}'.")
    {
        From = from;
        To = to;
    }

    public AsrJobStatus From { get; }

    public AsrJobStatus To { get; }
}
