namespace MeetCap.Core.Sessions;

/// <summary>
/// The session row as the domain sees it (docs/DATA_MODEL.md section 1). Persistence
/// owns the SQLite mapping; nothing here knows about SQL.
/// </summary>
public sealed class SessionRecord
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Mode { get; init; }

    public required string SourceType { get; init; }

    /// <summary>Mutable: a session moves through the state machine while it is open.</summary>
    public required string Status { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }

    public long DurationMs { get; set; }

    public int ConfigVersion { get; init; }

    /// <summary>
    /// The capture-relevant configuration this session started with, as a JSON
    /// object (docs/DATA_MODEL.md section 1). Secrets are never included: the
    /// snapshot is written into local artifacts, and docs/ARCHITECTURE.md section 22
    /// forbids copying credentials there.
    /// </summary>
    public string ConfigSnapshot { get; init; } = "{}";

    /// <summary>Wire names of the captured tracks, e.g. <c>["mic"]</c> for offline mode.</summary>
    public IReadOnlyList<string> Tracks { get; init; } = Array.Empty<string>();

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
