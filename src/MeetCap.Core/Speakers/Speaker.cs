namespace MeetCap.Core.Speakers;

/// <summary>
/// A persisted speaker entity (<c>docs/DATA_MODEL.md</c> section 8). The registry
/// stores multiple embeddings per person (<see cref="SpeakerEmbedding"/>) rather
/// than a single permanent vector, so enrollment is additive.
/// </summary>
/// <remarks>
/// Speaker entities, embeddings, and name mappings are sensitive local
/// identity-related data. They are never uploaded to cloud storage by default
/// (<c>docs/ARCHITECTURE.md</c> section 22).
/// </remarks>
public sealed record Speaker
{
    public required string Id { get; init; }

    /// <summary>Human-readable display name. Unique within the local registry.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Additional names/aliases serialized as a JSON array string for storage.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>An inactive speaker is retained but not returned as an identification candidate.</summary>
    public bool Active { get; init; } = true;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
