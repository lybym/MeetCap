namespace MeetCap.Core.Speakers;

/// <summary>
/// Persistence contract for the local Speaker Registry
/// (<c>docs/DATA_MODEL.md</c> sections 8-10). Implemented by
/// <c>MeetCap.Persistence</c> over SQLite. Voiceprints and name mappings are
/// sensitive local data stored by default and never uploaded
/// (<c>docs/ARCHITECTURE.md</c> section 22).
/// </summary>
/// <remarks>
/// This store is the authoritative registry. It is deliberately not a cloud
/// service: speaker entities, embeddings, and per-session assignments all live
/// in the local <c>meetcap.db</c>.
/// </remarks>
public interface ISpeakerStore
{
    // --- Speakers (section 8) ---

    void CreateSpeaker(Speaker speaker);

    /// <summary>Returns the speaker, or null when no such id exists.</summary>
    Speaker? GetSpeaker(string speakerId);

    /// <summary>All speakers, ordered by display name. Inactive speakers are included.</summary>
    IReadOnlyList<Speaker> ListSpeakers();

    /// <summary>Only active speakers, ordered by display name.</summary>
    IReadOnlyList<Speaker> ListActiveSpeakers();

    void UpdateSpeaker(Speaker speaker);

    // --- Embeddings (section 9) ---

    void AddEmbedding(SpeakerEmbedding embedding);

    /// <summary>All embeddings for one speaker, ordered by creation time.</summary>
    IReadOnlyList<SpeakerEmbedding> ListEmbeddings(string speakerId);

    /// <summary>Every embedding in the registry, ordered by speaker then creation time.</summary>
    IReadOnlyList<SpeakerEmbedding> ListAllEmbeddings();

    // --- Assignments (section 10) ---

    /// <summary>Inserts or replaces the assignment for (session, label). Manual assignments set Locked.</summary>
    void UpsertAssignment(SpeakerAssignment assignment);

    IReadOnlyList<SpeakerAssignment> ListAssignments(string sessionId);

    /// <summary>The assignment for one (session, label), or null.</summary>
    SpeakerAssignment? GetAssignment(string sessionId, string speakerLabel);
}
