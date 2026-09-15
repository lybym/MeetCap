namespace MeetCap.Core.Asr;

/// <summary>
/// The persistent ASR job entity (<c>docs/DATA_MODEL.md</c> section 6) extended with
/// the observability fields required by <c>docs/ASR_STRATEGY.md</c> section 13.
/// Immutable: transitions produce a new instance so an illegal state change cannot
/// be half-applied to a job that a second caller also holds.
/// </summary>
public sealed record AsrJob
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Source track, e.g. <c>import</c>, <c>mic</c>, <c>loopback</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Provider service tier the job was queued with: <c>standard</c>, <c>idle</c>, <c>turbo</c>.</summary>
    public required string Tier { get; init; }

    /// <summary>Provider name, e.g. <c>volcengine</c>.</summary>
    public required string Provider { get; init; }

    public long StartMs { get; init; }

    public long EndMs { get; init; }

    /// <summary>Session-relative path (or absolute path) of the audio submitted for this job.</summary>
    public required string InputArtifact { get; init; }

    public AsrJobStatus Status { get; init; } = AsrJobStatus.Pending;

    /// <summary>
    /// Provider task id. Allocated once when the job is created and persisted
    /// before the first submit, so a restart never has to invent a new id (which
    /// would risk a second billable task for the same audio).
    /// </summary>
    /// <remarks>
    /// Non-nullable by design: without a stable provider task id a restart could not avoid
    /// submitting the same audio twice, so the invariant is expressed in the type instead of
    /// in a defensive branch that only a tolerant store implementation could reach. The
    /// SQLite store rejects a blank value on write, and fails loudly -- naming the job -- if
    /// a stored row is NULL.
    /// </remarks>
    public required string ProviderRequestId { get; init; }

    public int AttemptCount { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    public string? RequestMetadataPath { get; init; }

    public string? RawResponsePath { get; init; }

    public string? NormalizedResultPath { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Source duration in milliseconds, used for the cost estimate.</summary>
    public int DurationMs { get; init; }

    public bool SpeakerInfoRequested { get; init; }

    public bool SpeakerInfoReturned { get; init; }

    /// <summary>Estimated provider cost in CNY. An estimate, never a billing guarantee.</summary>
    public double EstimatedCostCny { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
