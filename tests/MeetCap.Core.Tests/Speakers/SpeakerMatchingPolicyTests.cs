using MeetCap.Core.Speakers;
using Xunit;

namespace MeetCap.Core.Tests.Speakers;

/// <summary>
/// Tests the matching policy that resolves an anonymous speaker label to an identity
/// by applying <c>manual &gt; high-confidence voiceprint &gt; unknown</c>
/// (<c>docs/ARCHITECTURE.md</c> section 17.3, Issue #8 acceptance criteria 4-5).
/// </summary>
public class SpeakerMatchingPolicyTests
{
    private static readonly SpeakerMatchPolicy s_policy = new(Threshold: 0.82, Margin: 0.08);

    [Fact]
    public void ManualAssignment_AlwaysWinsOverVoiceprint()
    {
        var manual = new SpeakerAssignment
        {
            Id = "asgn_1", SessionId = "ses_1", SpeakerLabel = "speaker_0",
            SpeakerId = "person_alice", SpeakerName = "Alice",
            Source = SpeakerAssignmentSource.Manual, Locked = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        var candidates = new List<SpeakerCandidate>
        {
            new() { SpeakerId = "person_bob", DisplayName = "Bob", Score = 0.99 },
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual, candidates, s_policy);

        Assert.Equal("person_alice", resolution.SpeakerId);
        Assert.Equal("Alice", resolution.SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Manual, resolution.Source);
        Assert.True(resolution.Locked);
    }

    [Fact]
    public void VoiceprintMatch_AboveThresholdAndMargin_IsAccepted()
    {
        var candidates = new List<SpeakerCandidate>
        {
            new() { SpeakerId = "person_alice", DisplayName = "Alice", Score = 0.95 },
            new() { SpeakerId = "person_bob", DisplayName = "Bob", Score = 0.70 },
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_policy);

        Assert.Equal("person_alice", resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, resolution.Source);
        Assert.False(resolution.Locked);
        Assert.Equal(0.95, resolution.Confidence);
    }

    [Fact]
    public void VoiceprintMatch_BelowThreshold_StaysUnknown()
    {
        var candidates = new List<SpeakerCandidate>
        {
            new() { SpeakerId = "person_alice", DisplayName = "Alice", Score = 0.50 },
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_policy);

        Assert.Null(resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, resolution.Source);
    }

    [Fact]
    public void VoiceprintMatch_MarginNotMet_StaysUnknown()
    {
        // Best is above threshold but the runner-up is too close: margin not met.
        var candidates = new List<SpeakerCandidate>
        {
            new() { SpeakerId = "person_alice", DisplayName = "Alice", Score = 0.90 },
            new() { SpeakerId = "person_bob", DisplayName = "Bob", Score = 0.86 },
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_policy);

        Assert.Null(resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, resolution.Source);
    }

    [Fact]
    public void SingleCandidate_AboveThreshold_NoMarginRequired()
    {
        var candidates = new List<SpeakerCandidate>
        {
            new() { SpeakerId = "person_alice", DisplayName = "Alice", Score = 0.83 },
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_policy);

        Assert.Equal("person_alice", resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, resolution.Source);
    }

    [Fact]
    public void NoCandidates_StaysUnknown()
    {
        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, Array.Empty<SpeakerCandidate>(), s_policy);

        Assert.Null(resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, resolution.Source);
    }

    [Fact]
    public void ManualAssignment_IsLockedAgainstAutomaticRematching()
    {
        // A manual assignment with no voiceprint candidates still resolves to the manual identity.
        var manual = new SpeakerAssignment
        {
            Id = "asgn_1", SessionId = "ses_1", SpeakerLabel = "speaker_0",
            SpeakerId = "person_bob", SpeakerName = "Bob",
            Source = SpeakerAssignmentSource.Manual, Locked = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        // Even with strong voiceprint evidence for a different person, manual wins.
        var candidates = new List<SpeakerCandidate>
        {
            new() { SpeakerId = "person_alice", DisplayName = "Alice", Score = 0.99 },
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual, candidates, s_policy);

        Assert.Equal("person_bob", resolution.SpeakerId);
        Assert.True(resolution.Locked);
    }
}
