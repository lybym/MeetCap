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
