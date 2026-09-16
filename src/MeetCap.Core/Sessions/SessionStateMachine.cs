namespace MeetCap.Core.Sessions;

/// <summary>
/// Session lifecycle transition rules (<c>docs/ARCHITECTURE.md</c> section 20):
/// <c>CREATED -&gt; RECORDING -&gt; FINALIZING -&gt; PROCESSING -&gt; COMPLETED</c>, plus the
/// M1 recording path <c>FINALIZING -&gt; COMPLETED</c> and the terminal
/// <see cref="SessionStatus.Interrupted"/> outcome.
/// </summary>
/// <remarks>
/// <para>
/// M1 implements the subset that has no post-capture work yet, so a clean offline
/// recording goes <c>CREATED -&gt; RECORDING -&gt; FINALIZING -&gt; COMPLETED</c> and never
/// enters <see cref="SessionStatus.Processing"/>. <c>FINALIZING -&gt; PROCESSING</c> stays
/// in the machine because it is the documented edge once ASR work exists (M3/M4).
/// </para>
/// <para>
/// <see cref="SessionStatus.Interrupted"/> is terminal and is reachable from every
/// non-terminal state: startup recovery can find any of them abandoned, and the recording
/// process itself abandons a session when capture never started, the final chunk could not
/// be closed, or the endpoint came back at a different format. It is never used for a
/// degraded but complete recording.
/// </para>
/// <para>
/// An import session has no capture phase: it is created directly in
/// <see cref="SessionStatus.Processing"/> and completed once its transcript
/// artifacts exist. A failed ASR job leaves the session in
/// <see cref="SessionStatus.Processing"/> so it stays recoverable
/// (<c>docs/RELIABILITY.md</c> section 9), which is why that state has no
/// outgoing edge to a terminal failure state beyond <see cref="SessionStatus.Interrupted"/>.
/// </para>
/// </remarks>
public static class SessionStateMachine
{
    private static readonly Dictionary<string, string[]> s_allowed = new(StringComparer.Ordinal)
    {
        [SessionStatus.Created] = new[]
        {
            SessionStatus.Recording,
            SessionStatus.Processing,
            SessionStatus.Interrupted,
        },
        [SessionStatus.Recording] = new[]
        {
            SessionStatus.Finalizing,
            SessionStatus.Interrupted,
        },
        [SessionStatus.Finalizing] = new[]
        {
            SessionStatus.Completed,
            SessionStatus.Processing,
            SessionStatus.Interrupted,
        },
        [SessionStatus.Processing] = new[]
        {
            SessionStatus.Completed,
            SessionStatus.Interrupted,
        },
        [SessionStatus.Completed] = Array.Empty<string>(),
        [SessionStatus.Interrupted] = Array.Empty<string>(),
    };

    public static bool IsTerminal(string status) =>
        string.Equals(status, SessionStatus.Completed, StringComparison.Ordinal)
        || string.Equals(status, SessionStatus.Interrupted, StringComparison.Ordinal);

    public static bool CanTransition(string from, string to) =>
        !string.Equals(from, to, StringComparison.Ordinal)
        && s_allowed.TryGetValue(from, out var targets)
        && targets.Contains(to, StringComparer.Ordinal);

    /// <summary>Throws <see cref="InvalidSessionTransitionException"/> unless the edge exists.</summary>
    public static void EnsureTransitionAllowed(string from, string to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidSessionTransitionException(from, to);
        }
    }
}

/// <summary>Raised when a session status change is not a legal lifecycle edge.</summary>
public sealed class InvalidSessionTransitionException : InvalidOperationException
{
    public InvalidSessionTransitionException(string from, string to)
        : base($"Invalid session transition '{from}' -> '{to}'. " +
               "Allowed: CREATED -> RECORDING|PROCESSING|INTERRUPTED, " +
               "RECORDING -> FINALIZING|INTERRUPTED, " +
               "FINALIZING -> COMPLETED|PROCESSING|INTERRUPTED, " +
               "PROCESSING -> COMPLETED|INTERRUPTED.")
    {
        From = from;
        To = to;
    }

    public string From { get; }

    public string To { get; }
}
