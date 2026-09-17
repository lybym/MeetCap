namespace MeetCap.Core.Speakers;

/// <summary>
/// Pure domain logic that resolves an anonymous speaker label to an identity by
/// applying the priority <c>manual &gt; high-confidence voiceprint &gt; unknown</c>
/// (<c>docs/ARCHITECTURE.md</c> section 17.3, <c>docs/CONFIGURATION.md</c> section 9).
/// </summary>
/// <remarks>
/// This logic is MeetCap-owned and has no infrastructure dependency, so the
/// matching policy is explicit and testable without any provider or store
/// (<c>docs/ARCHITECTURE.md</c> section 23). The provider produces ranked
/// candidates; this policy decides whether to accept them.
/// </remarks>
public static class SpeakerMatchingPolicy
{
    /// <summary>
    /// Resolves one anonymous speaker label. Manual assignments always win and are
    /// locked against automatic rematching. A voiceprint match is accepted only when
    /// the best candidate's score is at or above the threshold AND the best leads the
    /// runner-up by at least the margin (a single candidate needs no margin). Everything
    /// else stays unknown.
    /// </summary>
    public static SpeakerResolution Resolve(
        string speakerLabel,
        SpeakerAssignment? manual,
        IReadOnlyList<SpeakerCandidate> candidates,
        SpeakerMatchPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerLabel);

        // Manual assignment is authoritative. A locked manual assignment can never be
        // overwritten by automatic inference.
        if (manual is { Locked: true } or { Source: SpeakerAssignmentSource.Manual })
        {
            return new SpeakerResolution
            {
                SpeakerLabel = speakerLabel,
                SpeakerId = manual.SpeakerId,
                SpeakerName = manual.SpeakerName,
                Confidence = manual.Confidence,
                Source = SpeakerAssignmentSource.Manual,
                Locked = true,
            };
        }

        // High-confidence historical match. The best candidate must clear the threshold,
        // and unless it is the only candidate it must also lead the runner-up by the margin.
        // This is what keeps a close second place from being silently named.
        if (candidates.Count > 0 && candidates[0].Score >= policy.Threshold)
        {
            var best = candidates[0];
            var runnerUpScore = candidates.Count > 1 ? candidates[1].Score : (double?)null;

            if (runnerUpScore is null || best.Score - runnerUpScore.Value >= policy.Margin)
            {
                return new SpeakerResolution
                {
                    SpeakerLabel = speakerLabel,
                    SpeakerId = best.SpeakerId,
                    SpeakerName = best.DisplayName,
                    Confidence = best.Score,
                    Source = SpeakerAssignmentSource.Voiceprint,
                    Locked = false,
                };
            }
        }

        // Unknown / low-confidence. The label stays anonymous rather than being forcibly named.
        return new SpeakerResolution
        {
            SpeakerLabel = speakerLabel,
            SpeakerId = null,
            SpeakerName = null,
            Confidence = null,
            Source = SpeakerAssignmentSource.Unknown,
            Locked = false,
        };
    }
}
