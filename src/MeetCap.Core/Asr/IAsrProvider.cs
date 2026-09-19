namespace MeetCap.Core.Asr;

/// <summary>
/// A file-ASR request handed to a provider adapter
/// (<c>docs/ARCHITECTURE.md</c> section 11). Contains no provider-specific fields:
/// endpoint, resource id, headers, hotword table, and speaker-info flags stay
/// inside the adapter.
/// </summary>
public sealed record AsrFileRequest
{
    public required string JobId { get; init; }

    public required string SessionId { get; init; }

    public required string Source { get; init; }

    /// <summary>Absolute path of the audio artifact to transcribe.</summary>
    public required string InputArtifactPath { get; init; }

    /// <summary>Provider format label for the artifact, e.g. <c>wav</c>, <c>m4a</c>.</summary>
    public required string AudioFormat { get; init; }

    /// <summary>
    /// Where this artifact begins on the session timeline, in milliseconds.
    /// </summary>
    /// <remarks>
    /// A provider reports timestamps relative to the audio it was given. An imported file
    /// starts at zero, but a live ASR batch is a window into a longer recording, so its
    /// segments only land on the session timeline once this offset is added
    /// (<c>docs/DATA_MODEL.md</c> section 6). Zero for anything that is the whole session.
    /// </remarks>
    public long StartOffsetMs { get; init; }

    public int DurationMs { get; init; }

    /// <summary>
    /// Provider task id allocated by the job store before the first submit. Adapters
    /// send it as the provider's task identifier so a retry after a restart reuses the
    /// same task instead of creating a second billable one.
    /// </summary>
    public required string ProviderRequestId { get; init; }

    /// <summary>Whether the provider is asked for anonymous speaker information.</summary>
    public bool RequestSpeakerInfo { get; init; }
}

/// <summary>The accepted submission, plus sanitized metadata safe to persist.</summary>
public sealed record AsrSubmission
{
    /// <summary>
    /// Provider task id. Equals the job's pre-allocated request id, so it is stable
    /// across restarts and across retries of the same job.
    /// </summary>
    public required string ProviderRequestId { get; init; }

    /// <summary>
    /// Sanitized request metadata to persist as <c>request.json</c>. Credentials,
    /// authorization headers, and signed URLs must never appear here.
    /// </summary>
    public required string SanitizedRequestJson { get; init; }

    /// <summary>
    /// Provider-side trace id for this exchange, when the provider reports one
    /// (Volcengine: <c>X-Tt-Logid</c>). Diagnostic only, never a credential.
    /// </summary>
    public string? ProviderLogId { get; init; }

    /// <summary>
    /// The audio transport this submission actually used, and the stable identity of any
    /// remote copy. The caller persists it before the request is answered so a restart can
    /// recover, and release, the copy (<c>docs/DATA_MODEL.md</c> section 6.2).
    /// </summary>
    /// <remarks>
    /// Deliberately the identity and not the payload: the base64 audio and the signed URL are
    /// both usable secrets and neither is durable state.
    /// </remarks>
    public AsrPublishedAudio? Audio { get; init; }
}

/// <summary>
/// The outcome of one attempt to release a job's published audio copy.
/// </summary>
/// <param name="Attempted">False when the job needed no release, so no call was made.</param>
/// <param name="Released">True when the copy is gone, or when there was nothing to release.</param>
/// <param name="Error">Why the release failed, when it did. Never contains a credential or a signed URL.</param>
public sealed record AsrAudioRelease(bool Attempted, bool Released, string? Error = null)
{
    /// <summary>Nothing to do: the job never used a transport that owns a remote copy.</summary>
    public static AsrAudioRelease NotNeeded { get; } = new(Attempted: false, Released: true);

    /// <summary>The copy is gone.</summary>
    public static AsrAudioRelease Succeeded { get; } = new(Attempted: true, Released: true);

    /// <summary>The copy may still exist, so the cleanup debt stays durable and retryable.</summary>
    public static AsrAudioRelease Failed(string error) => new(Attempted: true, Released: false, error);
}

/// <summary>Where a poll attempt landed.</summary>
public enum AsrPollState
{
    /// <summary>The provider is still working.</summary>
    Pending,

    /// <summary>The provider returned a final result.</summary>
    Completed,

    /// <summary>The provider does not know this task id (e.g. a submit that never landed).</summary>
    TaskNotFound,

    /// <summary>The provider reported a failure for this task.</summary>
    Failed,
}

/// <summary>The raw, unparsed provider result. Retained before normalization.</summary>
public sealed record AsrCompletion(
    string RawResponseJson,
    string? ProviderStatus = null,
    string? ProviderLogId = null);

/// <summary>A provider-reported failure, classified for retry purposes.</summary>
public sealed record AsrProviderError(string Code, string Message, bool IsTransient);

/// <summary>The outcome of one poll attempt.</summary>
public sealed record AsrPollResult
{
    private AsrPollResult(AsrPollState state, AsrCompletion? completion, AsrProviderError? error)
    {
        State = state;
        Completion = completion;
        Error = error;
    }

    public AsrPollState State { get; }

    public AsrCompletion? Completion { get; }

    public AsrProviderError? Error { get; }

    public static AsrPollResult Pending() => new(AsrPollState.Pending, null, null);

    public static AsrPollResult TaskNotFound() => new(AsrPollState.TaskNotFound, null, null);

    public static AsrPollResult Completed(AsrCompletion completion) =>
        new(AsrPollState.Completed, completion ?? throw new ArgumentNullException(nameof(completion)), null);

    public static AsrPollResult Failed(AsrProviderError error) =>
        new(AsrPollState.Failed, null, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>
/// File-ASR provider boundary. Domain code consumes normalized
/// <c>TranscriptSegment</c> objects through this interface and never sees
/// provider JSON (<c>docs/ARCHITECTURE.md</c> section 11).
/// </summary>
public interface IAsrProvider
{
    /// <summary>Stable provider name recorded on every job, e.g. <c>volcengine</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Submits a file-ASR task. Implementations must be idempotent with respect to
    /// the job id: the provider request id is derived from it.
    /// </summary>
    Task<AsrSubmission> SubmitFileAsync(AsrFileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Polls a previously submitted task. Never opens a streaming endpoint.</summary>
    Task<AsrPollResult> GetResultAsync(
        AsrSubmission submission,
        AsrFileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the audio copy this job staged for transport, once the job is terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional, and a no-op by default: a provider whose every request is inline has nothing
    /// to release, and neither has a provider that does not stage audio at all.
    /// </para>
    /// <para>
    /// An implementation must be idempotent and must report progress honestly, because the
    /// cleanup debt recorded on the job is cleared only when this returns
    /// <see cref="AsrAudioRelease.Released"/>. A failed delete is retryable cleanup work and
    /// never a transcription failure (<c>docs/DATA_MODEL.md</c> section 6.2).
    /// </para>
    /// </remarks>
    Task<AsrAudioRelease> ReleaseAudioAsync(AsrJob job, CancellationToken cancellationToken = default)
        => Task.FromResult(AsrAudioRelease.NotNeeded);
}

/// <summary>Thrown when the provider request failed for a reason that may succeed later.</summary>
public sealed class AsrTransientException : Exception
{
    public AsrTransientException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Thrown when the provider request failed permanently (auth, quota, bad request).</summary>
public sealed class AsrPermanentException : Exception
{
    public AsrPermanentException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Thrown when the provider cannot even be constructed because its configuration is
/// missing or invalid. Raised before any session state is created, so a
/// misconfiguration cannot leave a half-written session behind
/// (<c>docs/DEVELOPMENT.md</c> section 7).
/// </summary>
public sealed class AsrConfigurationException : InvalidOperationException
{
    public AsrConfigurationException(string message)
        : base(message)
    {
    }
}
