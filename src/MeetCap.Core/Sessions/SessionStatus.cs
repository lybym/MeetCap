namespace MeetCap.Core.Sessions;

/// <summary>
/// Session lifecycle values stored in <c>sessions.status</c>. The state machine is
/// defined in docs/ARCHITECTURE.md section 20.
/// </summary>
/// <remarks>
/// M1 uses <see cref="Created"/> to <see cref="Recording"/> to
/// <see cref="Finalizing"/> to <see cref="Completed"/> for a clean offline session,
/// and <see cref="Interrupted"/> for a session that startup recovery found was never
/// cleanly stopped. Since M4 a live recording that owns post-capture work enters
/// <see cref="Processing"/> on a clean stop and reaches <see cref="Completed"/> when its
/// ASR queue is terminal. Degraded conditions (ASR offline, device lost, low disk) are
/// orthogonal flags/events, never terminal states.
/// </remarks>
public static class SessionStatus
{
    public const string Created = "CREATED";
    public const string Recording = "RECORDING";
    public const string Finalizing = "FINALIZING";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";

    /// <summary>
    /// Terminal state for a session that did not complete cleanly: either startup
    /// recovery found it was never stopped, or the recording process itself had to
    /// abandon it because capture never started or storage failed while the final
    /// chunk was being closed.
    /// </summary>
    /// <remarks>
    /// docs/RELIABILITY.md section 6 forbids presenting such a session as clean.
    /// </remarks>
    public const string Interrupted = "INTERRUPTED";

    /// <summary>Every status this application may write.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Created, Recording, Finalizing, Processing, Completed, Interrupted,
    };

    /// <summary>
    /// Statuses that mean "this session still owns the recording surface".
    /// Mirrors the M0 active-session query so <c>meetcap status</c> stays truthful.
    /// </summary>
    public static readonly IReadOnlyList<string> Active = new[]
    {
        Created, Recording, Finalizing, Processing,
    };

    /// <summary>
    /// Statuses that mean a recording process still owns this session's capture surface:
    /// either it is recording, or it is closing the artifacts it just captured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PROCESSING</c> is deliberately not one of them. It means "capture is over and
    /// post-capture work is outstanding" (<c>docs/ARCHITECTURE.md</c> section 20), which no
    /// recording process owns: the audio is closed and durable, and only the ASR queue is
    /// still moving. Treating it as recording-owned would make <c>meetcap stop</c> wait for a
    /// transcription queue and would let startup recovery mistake a finished recording for a
    /// live one.
    /// </para>
    /// <para>
    /// <c>FINALIZING</c> is one of them, because the last chunk is still being closed and a
    /// stop request has to keep waiting for that process rather than report that no session
    /// is active.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> RecordingOwned = new[]
    {
        Created, Recording, Finalizing,
    };

    /// <summary>
    /// Statuses that startup recovery must treat as "was not cleanly stopped".
    /// </summary>
    public static readonly IReadOnlyList<string> Recoverable = new[]
    {
        Created, Recording, Finalizing,
    };

    public static bool IsKnown(string? status) => status is not null && All.Contains(status);

    public static bool IsActive(string? status) => status is not null && Active.Contains(status);

    /// <summary>True while a recording process still owns this session's capture surface.</summary>
    public static bool IsRecordingOwned(string? status) =>
        status is not null && RecordingOwned.Contains(status);

    public static bool NeedsRecovery(string? status) => status is not null && Recoverable.Contains(status);
}

/// <summary>How a session's audio was obtained (<c>docs/DATA_MODEL.md</c> section 1).</summary>
public static class SessionSourceType
{
    public const string Live = "live";
    public const string Import = "import";
}

/// <summary>Session mode. <c>import</c> is command-driven and never a capture default.</summary>
public static class SessionMode
{
    public const string Offline = "offline";
    public const string Online = "online";
    public const string Import = "import";
}

/// <summary>Audio track names used across sessions, chunks, ASR jobs, and transcripts.</summary>
public static class AudioTrackName
{
    public const string Mic = "mic";
    public const string Loopback = "loopback";

    /// <summary>The single logical track produced by an imported file.</summary>
    public const string Import = "import";
}
