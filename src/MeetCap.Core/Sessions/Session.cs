namespace MeetCap.Core.Sessions;

/// <summary>
/// The MeetCap session entity (<c>docs/DATA_MODEL.md</c> section 1). Immutable: a
/// session is advanced by producing an updated instance, never by mutating shared
/// state, so a caller cannot half-apply a lifecycle change.
/// </summary>
/// <remarks>
/// Import uses the same session abstraction as capture, with
/// <see cref="SourceType"/> = <c>import</c> and <see cref="Mode"/> = <c>import</c>
/// (<c>docs/ARCHITECTURE.md</c> sections 5 and 21).
/// </remarks>
public sealed record Session
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Allowed: <c>offline</c>, <c>online</c>, <c>import</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>Allowed: <c>live</c>, <c>import</c>.</summary>
    public required string SourceType { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    public long DurationMs { get; init; }

    /// <summary>JSON snapshot of the effective configuration at session creation.</summary>
    public string ConfigSnapshotJson { get; init; } = "{}";

    public int ConfigVersion { get; init; } = Configuration.SchemaVersion.Current;

    /// <summary>Logical audio tracks present in this session, e.g. <c>mic</c>, <c>import</c>.</summary>
    public IReadOnlyList<string> Tracks { get; init; } = Array.Empty<string>();

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Returns a copy with a new status, advancing <see cref="UpdatedAt"/>.</summary>
    public Session WithStatus(string status, DateTimeOffset now)
    {
        SessionStateMachine.EnsureTransitionAllowed(Status, status);
        return this with { Status = status, UpdatedAt = now };
    }

    /// <summary>Returns a copy carrying the recorded/imported duration.</summary>
    public Session WithDuration(long durationMs, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);
        return this with { DurationMs = durationMs, UpdatedAt = now };
    }

    /// <summary>Returns a copy marked stopped/finalized at <paramref name="now"/>.</summary>
    public Session WithStoppedAt(DateTimeOffset now) => this with { StoppedAt = now, UpdatedAt = now };
}
