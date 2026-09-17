namespace MeetCap.Core.Speakers;

/// <summary>
/// Local speaker identity provider boundary
/// (<c>docs/ARCHITECTURE.md</c> section 17.3). The default implementation uses
/// sherpa-onnx + 3D-Speaker ERes2Net-base behind the
/// <c>MeetCap.Speakers.SherpaOnnx</c> boundary. MeetCap owns the registry
/// persistence, thresholds, candidate policy, manual confirmation, and manual locks.
/// </summary>
/// <remarks>
/// The provider extracts embeddings and ranks enrolled speakers. The threshold +
/// margin policy and manual-lock priority are applied by MeetCap's
/// <see cref="SpeakerMatchingPolicy"/>, not by the provider, so the product
/// semantics stay explicit and testable (<c>docs/ARCHITECTURE.md</c> section 23).
/// </remarks>
public interface ISpeakerIdentityProvider
{
    /// <summary>Stable provider name, e.g. <c>sherpa_onnx_3dspeaker</c>.</summary>
    string Name { get; }

    /// <summary>The embedding model name, e.g. <c>3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k</c>.</summary>
    string ModelName { get; }

    /// <summary>The embedding model version.</summary>
    string ModelVersion { get; }

    /// <summary>The dimension of embeddings this provider produces (192 for ERes2Net-base).</summary>
    int EmbeddingDimension { get; }

    /// <summary>
    /// Extracts a speaker embedding from a clean audio sample. The provider must be
    /// resilient: a missing model or runtime failure surfaces as an exception that the
    /// caller contains, never as a silent null (<c>docs/ARCHITECTURE.md</c> section 20).
    /// </summary>
    Task<ExtractedEmbedding> ExtractAsync(
        SpeakerAudioSample sample,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ranks enrolled speakers by similarity to the probe embedding. The provider
    /// owns the model-specific similarity metric; MeetCap owns the threshold and
    /// margin policy applied to the returned candidates.
    /// </summary>
    Task<IReadOnlyList<SpeakerCandidate>> IdentifyAsync(
        ExtractedEmbedding probe,
        IReadOnlyList<EnrolledSpeaker> enrolled,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when the identity provider cannot be constructed because its model is
/// missing or its configuration is invalid. Raised before any session state is
/// created, so a misconfiguration cannot leave a half-written session behind
/// (<c>docs/DEVELOPMENT.md</c> section 7).
/// </summary>
public sealed class SpeakerProviderConfigurationException : InvalidOperationException
{
    public SpeakerProviderConfigurationException(string message)
        : base(message)
    {
    }
}
