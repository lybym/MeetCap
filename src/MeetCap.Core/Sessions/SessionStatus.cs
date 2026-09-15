namespace MeetCap.Core.Sessions;

/// <summary>
/// Session lifecycle values stored in <c>sessions.status</c>. The state machine is
/// defined in docs/ARCHITECTURE.md section 19.
/// </summary>
/// <remarks>
/// M1 uses <see cref="Created"/> to <see cref="Recording"/> to
/// <see cref="Finalizing"/> to <see cref="Completed"/> for a clean offline session,
/// and <see cref="Interrupted"/> for a session that startup recovery found was never
/// cleanly stopped. <see cref="Processing"/> arrives with the ASR milestones.
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
    /// Statuses that startup recovery must treat as "was not cleanly stopped".
    /// </summary>
    public static readonly IReadOnlyList<string> Recoverable = new[]
    {
        Created, Recording, Finalizing,
    };

    public static bool IsKnown(string? status) => status is not null && All.Contains(status);

    public static bool IsActive(string? status) => status is not null && Active.Contains(status);

    public static bool NeedsRecovery(string? status) => status is not null && Recoverable.Contains(status);
}
