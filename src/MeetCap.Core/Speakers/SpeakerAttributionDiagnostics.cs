namespace MeetCap.Core.Speakers;

/// <summary>
/// Why a speaker label resolved the way it did
/// (<c>docs/DATA_MODEL.md</c> section 10, issue #50). Every label — resolved or not —
/// carries one of these reasons in its diagnostics block so an operator can tell
/// "genuinely not enrolled" apart from "no clean range", "audio unreadable",
/// "candidates existed but scored below threshold", and "best led the runner-up by
/// less than the margin".
/// </summary>
public enum SpeakerResolutionReason
{
    /// <summary>No speakers are enrolled, so no candidate can exist.</summary>
    RegistryEmpty,

    /// <summary>The label had speech, but no eligible clean sample range was found.</summary>
    NoCleanRanges,

    /// <summary>Clean ranges were selected, but audio was unavailable for all of them.</summary>
    AudioUnavailable,

    /// <summary>The identity provider threw while extracting or identifying.</summary>
    ProviderFailed,

    /// <summary>Candidates existed, but the best score was below the threshold.</summary>
    BelowThreshold,

    /// <summary>The best candidate cleared the threshold but led the runner-up by less than the margin.</summary>
    MarginTooSmall,

    /// <summary>
    /// The label is reused across provider jobs/tracks (or lacks job provenance), so it is
    /// never voiceprint-scored (issue #45 invariant). It stays unknown unless a manual
    /// assignment covers it.
    /// </summary>
    AmbiguousLabel,

    /// <summary>A manual assignment won and is locked against automatic rematching.</summary>
    ManualLocked,

    /// <summary>A voiceprint candidate cleared both the threshold and the margin.</summary>
    Matched,
}

/// <summary>Wire-format helpers for <see cref="SpeakerResolutionReason"/>.</summary>
public static class SpeakerResolutionReasons
{
    public static string ToWire(SpeakerResolutionReason reason) => reason switch
    {
        SpeakerResolutionReason.RegistryEmpty => "registry_empty",
        SpeakerResolutionReason.NoCleanRanges => "no_clean_ranges",
        SpeakerResolutionReason.AudioUnavailable => "audio_unavailable",
        SpeakerResolutionReason.ProviderFailed => "provider_failed",
        SpeakerResolutionReason.BelowThreshold => "below_threshold",
        SpeakerResolutionReason.MarginTooSmall => "margin_too_small",
        SpeakerResolutionReason.AmbiguousLabel => "ambiguous_label",
        SpeakerResolutionReason.ManualLocked => "manual_locked",
        SpeakerResolutionReason.Matched => "matched",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown speaker resolution reason."),
    };
}

/// <summary>
/// The outcome of the sample-extraction/identification attempt for one label
/// (issue #50). Distinct from <see cref="SpeakerResolutionReason"/>: this describes what
/// happened to the audio sample, while the resolution reason describes why the label
/// resolved the way it did.
/// </summary>
public enum SpeakerSampleOutcome
{
    /// <summary>No clean range was selected (registry empty, no eligible speech, or no attempt made).</summary>
    NoRanges,

    /// <summary>A sample was extracted and the provider returned ranked candidates.</summary>
    Extracted,

    /// <summary>Clean ranges were selected but audio was unavailable for all of them.</summary>
    NoAudio,

    /// <summary>The provider failed while extracting or identifying.</summary>
    ProviderFailed,
}

/// <summary>Wire-format helpers for <see cref="SpeakerSampleOutcome"/>.</summary>
public static class SpeakerSampleOutcomes
{
    public static string ToWire(SpeakerSampleOutcome outcome) => outcome switch
    {
        SpeakerSampleOutcome.NoRanges => "no_ranges",
        SpeakerSampleOutcome.Extracted => "extracted",
        SpeakerSampleOutcome.NoAudio => "no_audio",
        SpeakerSampleOutcome.ProviderFailed => "provider_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown speaker sample outcome."),
    };
}

/// <summary>
/// A clean speech range that was considered for embedding extraction
/// (<c>docs/DATA_MODEL.md</c> section 10, issue #50).
/// </summary>
public sealed record DiagnosticSampleRange
{
    public required long StartMs { get; init; }

    public required long EndMs { get; init; }
}

/// <summary>
/// One ranked identity candidate recorded in the attribution diagnostics
/// (<c>docs/DATA_MODEL.md</c> section 10, issue #50). Speaker names plus ranked
/// similarities are sensitive identity metadata: they live in the local
/// <c>attribution.json</c> artifact under the data root and are not printed to ordinary
/// logs unless an operator explicitly opts in.
/// </summary>
public sealed record DiagnosticCandidate
{
    public required string SpeakerName { get; init; }

    /// <summary>Similarity score in [0, 1] (cosine similarity for ERes2Net).</summary>
    public required double Similarity { get; init; }
}

/// <summary>
/// The per-label diagnostics block added additively to <c>attribution.json</c>
/// (issue #50). Existing top-level entry fields (<c>speaker_id</c>, <c>speaker_name</c>,
/// <c>confidence</c>, <c>source</c>, <c>locked</c>) are untouched; this block is new and
/// nullable so an older artifact without it still deserializes.
/// </summary>
public sealed record SpeakerLabelDiagnostics
{
    /// <summary>Wire string from <see cref="SpeakerResolutionReasons.ToWire"/>.</summary>
    public required string ResolutionReason { get; init; }

    /// <summary>The clean speech ranges the selector produced, empty when none were selected.</summary>
    public IReadOnlyList<DiagnosticSampleRange> RangesConsidered { get; init; } = [];

    /// <summary>Wire string from <see cref="SpeakerSampleOutcomes.ToWire"/>.</summary>
    public required string SampleOutcome { get; init; }

    /// <summary>Top-N ranked candidates with similarities; empty when the provider was not scored.</summary>
    public IReadOnlyList<DiagnosticCandidate> Candidates { get; init; } = [];

    /// <summary>The configured match threshold in effect at attribution time.</summary>
    public required double Threshold { get; init; }

    /// <summary>The configured match margin in effect at attribution time.</summary>
    public required double Margin { get; init; }

    /// <summary>The number of enrolled speakers (with embeddings) at attribution time.</summary>
    public required int EnrolledSpeakers { get; init; }

    /// <summary>The total number of enrolled embeddings at attribution time.</summary>
    public required int EnrolledEmbeddings { get; init; }

    /// <summary>
    /// The single provider job id the label is scoped to, when the label is unambiguous.
    /// Null when the label spans multiple jobs or lacks job provenance.
    /// </summary>
    public string? ProviderJob { get; init; }
}
