namespace MeetCap.Core.Sessions;

/// <summary>
/// Session lifecycle states from <c>docs/ARCHITECTURE.md</c> section 20.
/// Degraded conditions (ASR offline, device lost, low disk) are orthogonal
/// flags/events, never terminal states.
/// </summary>
public static class SessionStatus
{
    public const string Created = "CREATED";
    public const string Recording = "RECORDING";
    public const string Finalizing = "FINALIZING";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";

    /// <summary>Non-terminal states: a session that may still be worked on.</summary>
    public static readonly IReadOnlyList<string> Active = new[]
    {
        Created,
        Recording,
        Finalizing,
        Processing,
    };

    public static bool IsActive(string status) =>
        Active.Any(s => string.Equals(s, status, StringComparison.Ordinal));
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
public static class AudioSource
{
    public const string Mic = "mic";
    public const string Loopback = "loopback";

    /// <summary>The single logical track produced by an imported file.</summary>
    public const string Import = "import";
}
