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

    /// <summary>
    /// A closed chunk was materialized into a durable ASR batch artifact
    /// (<c>docs/ROADMAP.md</c> M4). Emitted before
    /// <see cref="AsrJobQueued"/>, because the durable batch is what the job names.
    /// </summary>
    public const string AsrBatchClosed = "asr.batch.closed";

    /// <summary>
    /// An unfinished batch <c>.part</c> file was discarded during recovery. Its capture
    /// chunks remain durable, so this is a lost batch window, not lost audio.
    /// </summary>
    public const string AsrBatchDiscarded = "asr.batch.discarded";

    /// <summary>
    /// A batch window could not be materialized, so its audio never reached the provider.
    /// </summary>
    /// <remarks>
    /// The capture chunks stay durable under <c>audio/</c> and the recording is unaffected —
    /// this is a transcript-degradation record, not an audio-loss record — but the window has
    /// to be named explicitly, because the alternative is a silence in the transcript that
    /// nothing explains (<c>docs/RELIABILITY.md</c> section 2).
    /// </remarks>
    public const string AsrBatchFailed = "asr.batch.failed";

    public const string AsrJobQueued = "asr.job.queued";
    public const string AsrJobSubmitted = "asr.job.submitted";
    public const string AsrJobRetryWait = "asr.job.retry_wait";
    public const string AsrJobCompleted = "asr.job.completed";
    public const string AsrJobFailed = "asr.job.failed";

    /// <summary>
    /// The temporary transport copy staged for a job was released from object storage.
    /// </summary>
    /// <remarks>
    /// The local WAV or batch artifact is untouched: this records the removal of the temporary
    /// object only (<c>docs/DATA_MODEL.md</c> section 6.2).
    /// </remarks>
    public const string AsrAudioReleased = "asr.audio.released";

    /// <summary>
    /// Releasing a job's temporary transport copy failed, so the durable cleanup debt remains.
    /// </summary>
    /// <remarks>
    /// This is explicitly not a transcription failure. The transcript is already durable and
    /// the object stays retryable, so the event exists to keep the debt observable rather than
    /// to change any job's status (<c>docs/DATA_MODEL.md</c> section 6.2,
    /// <c>docs/RELIABILITY.md</c> section 9).
    /// </remarks>
    public const string AsrAudioReleaseFailed = "asr.audio.release_failed";
}
