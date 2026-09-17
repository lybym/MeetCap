namespace MeetCap.Core.Speakers;

/// <summary>
/// Threshold and match-margin policy for speaker identification
/// (<c>docs/CONFIGURATION.md</c> section 9.2, <c>docs/ARCHITECTURE.md</c> section 17.3).
/// Both are configuration, not hard-coded product truth.
/// </summary>
/// <param name="Threshold">The minimum similarity for a candidate to be accepted.</param>
/// <param name="Margin">The required lead of the best candidate over the runner-up.</param>
public sealed record SpeakerMatchPolicy(double Threshold, double Margin);

/// <summary>
/// The outcome of applying the matching policy to one anonymous speaker label.
/// Priority: manual assignment &gt; high-confidence voiceprint &gt; unknown
/// (<c>docs/ARCHITECTURE.md</c> section 17.3).
/// </summary>
public sealed record SpeakerResolution
{
    public required string SpeakerLabel { get; init; }

    public string? SpeakerId { get; init; }

    public string? SpeakerName { get; init; }

    public double? Confidence { get; init; }

    public required SpeakerAssignmentSource Source { get; init; }

    public bool Locked { get; init; }

    public bool IsResolved => SpeakerId is not null;

    /// <summary>Projects this resolution into an attribution entry.</summary>
    public SpeakerAttributionEntry ToEntry() => new()
    {
        SpeakerLabel = SpeakerLabel,
        SpeakerId = SpeakerId,
        SpeakerName = SpeakerName,
        Confidence = Confidence,
        Source = Source,
        Locked = Locked,
    };
}
