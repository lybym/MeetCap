namespace MeetCap.Core.Speakers;

/// <summary>
/// A ranked identity candidate returned by <see cref="ISpeakerIdentityProvider.IdentifyAsync"/>.
/// The provider owns the similarity metric; MeetCap owns the threshold and margin
/// policy applied to the ranked list (<c>docs/ARCHITECTURE.md</c> section 17.3).
/// </summary>
public sealed record SpeakerCandidate
{
    public required string SpeakerId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Similarity score in [0, 1] (cosine similarity for ERes2Net).</summary>
    public double Score { get; init; }
}

/// <summary>
/// An enrolled speaker with their stored embeddings, handed to the provider for
/// identification. The registry loads these from the store; the provider computes
/// similarities and returns ranked <see cref="SpeakerCandidate"/> values.
/// </summary>
public sealed record EnrolledSpeaker
{
    public required string SpeakerId { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyList<ExtractedEmbedding> Embeddings { get; init; }
}
