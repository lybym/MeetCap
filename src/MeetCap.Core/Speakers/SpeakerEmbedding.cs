namespace MeetCap.Core.Speakers;

/// <summary>
/// An embedding extracted from audio by the identity provider
/// (<c>docs/ARCHITECTURE.md</c> section 17.3). This is what <see cref="ISpeakerIdentityProvider.ExtractAsync"/>
/// returns and what <see cref="ISpeakerIdentityProvider.IdentifyAsync"/> compares against.
/// </summary>
/// <remarks>
/// The float vector is the model's own representation. For 3D-Speaker ERes2Net-base
/// the dimension is 192. The provider owns the model name/version so a stored
/// embedding can be audited against the model that produced it.
/// </remarks>
public sealed record ExtractedEmbedding
{
    public required float[] Values { get; init; }

    public required string ModelName { get; init; }

    public required string ModelVersion { get; init; }

    public int Dimension => Values.Length;

    /// <summary>Optional provider-reported sample quality score in [0, 1].</summary>
    public double? QualityScore { get; init; }
}

/// <summary>
/// A persisted speaker embedding (<c>docs/DATA_MODEL.md</c> section 9). Multiple
/// embeddings per person are stored rather than one permanent vector.
/// </summary>
public sealed record SpeakerEmbedding
{
    public required string Id { get; init; }

    public required string SpeakerId { get; init; }

    public required string ModelName { get; init; }

    public required string ModelVersion { get; init; }

    public int Dimension { get; init; }

    /// <summary>The raw embedding vector, stored as a BLOB by the persistence layer.</summary>
    public required float[] Values { get; init; }

    /// <summary>The session the embedding was enrolled from, when known.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>The transcript segment ids the sample was drawn from.</summary>
    public string[] SourceSegmentIds { get; init; } = [];

    public double? QualityScore { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Projects this persisted embedding into the provider-facing extracted form.</summary>
    public ExtractedEmbedding ToExtracted() => new()
    {
        Values = Values,
        ModelName = ModelName,
        ModelVersion = ModelVersion,
        QualityScore = QualityScore,
    };
}
