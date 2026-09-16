namespace MeetCap.Core.Sessions;

/// <summary>
/// Writes the durable session artifacts: the directory layout, <c>session.json</c>,
/// and the append-only <c>events.jsonl</c> operational log
/// (<c>docs/DATA_MODEL.md</c> sections 2 and 4).
/// </summary>
/// <remarks>
/// The filesystem is a first-class persistence layer, so these writes are owned by
/// the persistence layer; the ASR subsystem depends on this contract rather than on
/// file layout code. <c>events.jsonl</c> is append-only and is never rewritten.
/// </remarks>
public interface ISessionArtifactWriter
{
    /// <summary>Creates the session artifact directories. Idempotent.</summary>
    void EnsureLayout(SessionArtifactPaths paths);

    /// <summary>Writes (replacing) <c>session.json</c> and returns its absolute path.</summary>
    string WriteSessionDocument(SessionArtifactPaths paths, SessionDocument document);

    /// <summary>Reads <c>session.json</c>, or <c>null</c> when it does not exist.</summary>
    SessionDocument? ReadSessionDocument(SessionArtifactPaths paths);

    /// <summary>Appends one operational event to <c>events.jsonl</c>.</summary>
    void AppendEvent(
        SessionArtifactPaths paths,
        string name,
        long atMs,
        IReadOnlyDictionary<string, object?>? details = null);
}

/// <summary>Event names used in <c>events.jsonl</c>.</summary>
public static class SessionEvents
{
    public const string SessionCreated = "session.created";
    public const string SessionCompleted = "session.completed";
    public const string SourceImported = "session.source.imported";
    public const string MediaNormalized = "session.media.normalized";
    public const string AsrJobQueued = "asr.job.queued";
    public const string AsrJobSubmitted = "asr.job.submitted";
    public const string AsrJobRetryWait = "asr.job.retry_wait";
    public const string AsrJobCompleted = "asr.job.completed";
    public const string AsrJobFailed = "asr.job.failed";
}
