namespace MeetCap.Core.Sessions;

/// <summary>
/// A durable mapping between an imported source artifact and the copy MeetCap
/// keeps inside the session directory (<c>docs/ARCHITECTURE.md</c> section 21:
/// "materializes or links the source under the session artifact directory").
/// </summary>
/// <remarks>
/// The original file is never modified or deleted: MeetCap copies it, so the
/// read-only-source and provenance guarantees hold regardless of what the user
/// does with the original afterwards.
/// </remarks>
public sealed record SourceArtifact
{
    /// <summary>Role of this artifact, e.g. <c>original</c> or <c>normalized</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Absolute path the user supplied.</summary>
    public required string OriginalPath { get; init; }

    /// <summary>Absolute path inside the session directory.</summary>
    public required string StoredPath { get; init; }

    /// <summary>File name inside the session directory.</summary>
    public required string FileName { get; init; }

    public long ByteLength { get; init; }

    /// <summary>SHA-256 of the stored artifact, for provenance and re-verification.</summary>
    public required string Sha256 { get; init; }

    public static class Roles
    {
        public const string Original = "original";
        public const string Normalized = "normalized";
    }
}

/// <summary>
/// The <c>session.json</c> document (<c>docs/DATA_MODEL.md</c> section 3), extended
/// with the import source artifact mapping.
/// </summary>
/// <remarks>
/// The mapping lives in the durable session artifact rather than a new SQLite
/// table: <c>docs/DATA_MODEL.md</c> section 11 forbids creating tables ahead of
/// their feature, and section 6 already records <c>input_artifact</c> per ASR job,
/// so the indexed half of the mapping is the job row and the authoritative half is
/// this document.
/// </remarks>
public sealed record SessionDocument
{
    public required string SessionId { get; init; }

    public required string Title { get; init; }

    public required string Mode { get; init; }

    public required string SourceType { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    public int ConfigVersion { get; init; } = Configuration.SchemaVersion.Current;

    public IReadOnlyList<string> Tracks { get; init; } = Array.Empty<string>();

    /// <summary>Source artifact mapping. Empty for live sessions.</summary>
    public IReadOnlyList<SourceArtifact> SourceArtifacts { get; init; } = Array.Empty<SourceArtifact>();

    public static SessionDocument From(Session session, IReadOnlyList<SourceArtifact> sourceArtifacts)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceArtifacts);

        return new SessionDocument
        {
            SessionId = session.Id,
            Title = session.Title,
            Mode = session.Mode,
            SourceType = session.SourceType,
            StartedAt = session.StartedAt,
            StoppedAt = session.StoppedAt,
            ConfigVersion = session.ConfigVersion,
            Tracks = session.Tracks,
            SourceArtifacts = sourceArtifacts,
        };
    }
}
