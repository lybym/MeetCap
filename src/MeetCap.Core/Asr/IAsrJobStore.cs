namespace MeetCap.Core.Asr;

/// <summary>
/// Persistence contract for the persistent ASR job queue. Implemented by
/// <c>MeetCap.Persistence</c> over SQLite.
/// </summary>
/// <remarks>
/// This store is the authoritative queue. It is deliberately not a generic job
/// framework: no external scheduler, broker, or in-memory task list may replace it
/// (<c>docs/ARCHITECTURE.md</c> section 12).
/// </remarks>
public interface IAsrJobStore
{
    void Create(AsrJob job);

    /// <summary>Returns the job, or <c>null</c> when no such id exists.</summary>
    AsrJob? Get(string jobId);

    IReadOnlyList<AsrJob> ListBySession(string sessionId);

    /// <summary>
    /// Jobs that still need work and are due at <paramref name="now"/>:
    /// <c>pending</c>, <c>submitting</c>, <c>submitted</c>, <c>polling</c>, and
    /// <c>retry_wait</c> whose <c>next_retry_at</c> has passed. Ordered oldest first.
    /// </summary>
    IReadOnlyList<AsrJob> ListResumable(DateTimeOffset now, int limit, string? sessionId = null);

    void Update(AsrJob job);

    int CountByStatus(AsrJobStatus status);
}
