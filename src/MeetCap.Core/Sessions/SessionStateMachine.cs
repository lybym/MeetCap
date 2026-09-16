namespace MeetCap.Core.Sessions;

/// <summary>
/// Session lifecycle transition rules (<c>docs/ARCHITECTURE.md</c> section 20):
/// <c>CREATED -&gt; RECORDING -&gt; FINALIZING -&gt; PROCESSING -&gt; COMPLETED</c>.
/// </summary>
/// <remarks>
/// An import session has no capture phase: it is created directly in
/// <see cref="SessionStatus.Processing"/> and completed once its transcript
/// artifacts exist. A failed ASR job leaves the session in
/// <see cref="SessionStatus.Processing"/> so it stays recoverable
/// (<c>docs/RELIABILITY.md</c> section 9), which is why that state has no
/// outgoing edge to a terminal failure state.
/// </remarks>
public static class SessionStateMachine
{
    private static readonly Dictionary<string, string[]> s_allowed = new(StringComparer.Ordinal)
    {
        [SessionStatus.Created] = new[] { SessionStatus.Recording, SessionStatus.Processing },
        [SessionStatus.Recording] = new[] { SessionStatus.Finalizing },
        [SessionStatus.Finalizing] = new[] { SessionStatus.Processing },
        [SessionStatus.Processing] = new[] { SessionStatus.Completed },
        [SessionStatus.Completed] = Array.Empty<string>(),
    };

    public static bool IsTerminal(string status) =>
        string.Equals(status, SessionStatus.Completed, StringComparison.Ordinal);

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
               "Allowed: CREATED -> RECORDING|PROCESSING, RECORDING -> FINALIZING, " +
               "FINALIZING -> PROCESSING, PROCESSING -> COMPLETED.")
    {
        From = from;
        To = to;
    }

    public string From { get; }

    public string To { get; }
}
