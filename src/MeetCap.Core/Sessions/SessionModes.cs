namespace MeetCap.Core.Sessions;

/// <summary>
/// Session modes stored in <c>sessions.mode</c> (docs/PRD.md section 4: a session has
/// exactly one mode and it cannot change after the session starts).
/// </summary>
public static class SessionModes
{
    public const string Offline = "offline";
    public const string Online = "online";
    public const string Import = "import";

    public static readonly IReadOnlyList<string> All = new[] { Offline, Online, Import };

    public static bool IsKnown(string? mode) => mode is not null && All.Contains(mode);
}

/// <summary>
/// Where a session's audio came from, stored in <c>sessions.source_type</c>.
/// </summary>
public static class SessionSourceTypes
{
    /// <summary>Captured live by this machine.</summary>
    public const string Live = "live";

    /// <summary>Materialized from a file the user supplied (M3).</summary>
    public const string Import = "import";

    public static readonly IReadOnlyList<string> All = new[] { Live, Import };
}
