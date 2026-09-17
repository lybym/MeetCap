namespace MeetCap.Speakers;

using MeetCap.Core.Ids;
using MeetCap.Core.Speakers;

/// <summary>
/// The local Speaker Registry workflow: enrollment and identification
/// (<c>docs/ARCHITECTURE.md</c> section 17.3). The registry is the glue between the
/// <see cref="ISpeakerStore"/> (persistence) and the <see cref="ISpeakerIdentityProvider"/>
/// (embedding extraction and similarity). It owns the enrollment workflow and loads
/// enrolled speakers for identification; the threshold + margin policy is applied
/// separately by <see cref="SpeakerMatchingPolicy"/>.
/// </summary>
/// <remarks>
/// Voiceprints and name mappings are sensitive local data. The registry never uploads
/// them (<c>docs/ARCHITECTURE.md</c> section 22). Multiple embeddings per person are
/// stored rather than one permanent vector, so enrollment is additive.
/// </remarks>
public sealed class SpeakerRegistry
{
    private readonly ISpeakerStore _store;
    private readonly ISpeakerIdentityProvider _provider;

    public SpeakerRegistry(ISpeakerStore store, ISpeakerIdentityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        _store = store;
        _provider = provider;
    }

    /// <summary>
    /// Enrolls a named speaker from an audio sample. Creates the speaker if it does
    /// not exist, extracts an embedding via the provider, and persists it. Calling
    /// this multiple times for the same name adds embeddings rather than replacing
    /// one (<c>docs/DATA_MODEL.md</c> section 9).
    /// </summary>
    public async Task<Speaker> EnrollAsync(
        string displayName,
        SpeakerAudioSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(sample);

        var now = DateTimeOffset.UtcNow;
        var speaker = FindByName(displayName);
        if (speaker is null)
        {
            speaker = new Speaker
            {
                Id = Ids.NewSpeakerId(),
                DisplayName = displayName,
                Aliases = [],
                Active = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _store.CreateSpeaker(speaker);
        }

        var extracted = await _provider.ExtractAsync(sample, cancellationToken).ConfigureAwait(false);
        var embedding = new SpeakerEmbedding
        {
            Id = Ids.NewSpeakerEmbeddingId(),
            SpeakerId = speaker.Id,
            ModelName = extracted.ModelName,
            ModelVersion = extracted.ModelVersion,
            Dimension = extracted.Dimension,
            Values = extracted.Values,
            SourceSessionId = sample.SourceSessionId,
            SourceSegmentIds = sample.SourceSegmentIds,
            QualityScore = extracted.QualityScore,
            CreatedAt = now,
        };
        _store.AddEmbedding(embedding);

        return speaker;
    }

    /// <summary>
    /// Identifies a probe sample against all enrolled (active) speakers. Returns ranked
    /// candidates from the provider; the caller applies the threshold + margin policy
    /// via <see cref="SpeakerMatchingPolicy"/>.
    /// </summary>
    public async Task<IReadOnlyList<SpeakerCandidate>> IdentifyAsync(
        SpeakerAudioSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var probe = await _provider.ExtractAsync(sample, cancellationToken).ConfigureAwait(false);
        var enrolled = LoadEnrolledSpeakers();
        if (enrolled.Count == 0)
        {
            return [];
        }

        return await _provider.IdentifyAsync(probe, enrolled, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads all active speakers with their embeddings for identification.</summary>
    public IReadOnlyList<EnrolledSpeaker> LoadEnrolledSpeakers()
    {
        var speakers = _store.ListActiveSpeakers();
        var enrolled = new List<EnrolledSpeaker>(speakers.Count);
        foreach (var speaker in speakers)
        {
            var embeddings = _store.ListEmbeddings(speaker.Id);
            if (embeddings.Count == 0)
            {
                continue;
            }

            enrolled.Add(new EnrolledSpeaker
            {
                SpeakerId = speaker.Id,
                DisplayName = speaker.DisplayName,
                Embeddings = embeddings.Select(e => e.ToExtracted()).ToList(),
            });
        }

        return enrolled;
    }

    /// <summary>Manually binds an anonymous speaker label to a person for one session.</summary>
    /// <remarks>
    /// Manual assignment is authoritative and locked: it can never be overwritten by
    /// automatic inference (<c>docs/ARCHITECTURE.md</c> section 17.3).
    /// </remarks>
    public SpeakerAssignment AssignManually(
        string sessionId,
        string speakerLabel,
        Speaker speaker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerLabel);
        ArgumentNullException.ThrowIfNull(speaker);

        var now = DateTimeOffset.UtcNow;
        var assignment = new SpeakerAssignment
        {
            Id = Ids.NewSpeakerAssignmentId(),
            SessionId = sessionId,
            SpeakerLabel = speakerLabel,
            SpeakerId = speaker.Id,
            SpeakerName = speaker.DisplayName,
            Confidence = null,
            Source = SpeakerAssignmentSource.Manual,
            Locked = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _store.UpsertAssignment(assignment);
        return assignment;
    }

    private Speaker? FindByName(string displayName)
    {
        return _store.ListSpeakers().FirstOrDefault(s =>
            string.Equals(s.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
    }
}
