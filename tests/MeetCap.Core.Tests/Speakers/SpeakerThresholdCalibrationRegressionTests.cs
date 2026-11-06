using MeetCap.Core.Speakers;
using Xunit;

namespace MeetCap.Core.Tests.Speakers;

/// <summary>
/// Regression guard for issue #49: the shipped default <c>match_threshold = 0.82</c> is
/// unattainable on real meeting audio. These tests encode the 2026-11-05 field measurement
/// (<c>docs/ASR_STRATEGY.md</c> section 10, <c>docs/SPEAKER_CALIBRATION.md</c>) as constants
/// and assert the matching policy's behaviour at the measured score points, so the
/// same/different separation and the over-filter defect are tracked in the test suite.
/// </summary>
/// <remarks>
/// <para>
/// The default value itself is <b>not</b> changed by issue #49 (per the 2026-11-06 review
/// update): it stays <c>0.82 / 0.08</c> until issue #50 (candidate-score diagnostics) lands
/// and held-out different-day calibration across multiple known speakers/impostors is
/// documented. <c>0.50 / 0.06</c> is exercised here only as the documented opt-in experiment
/// profile, never as a new default.
/// </para>
/// <para>
/// These are documentation guardrails built on the real <see cref="SpeakerMatchingPolicy"/>:
/// they will need revisiting when the default is recalibrated after issue #50. The
/// <c>ShippedDefaultOverFiltersSameSpeakerProbe</c> test in particular records the core
/// defect — that the shipped default rejects a same-speaker same-recording probe — and will
/// flip from rejected to accepted only when a recalibrated default lands.
/// </para>
/// </remarks>
public class SpeakerThresholdCalibrationRegressionTests
{
    // Shipped default (src/MeetCap.Core/Configuration/Sections.cs) — retained unchanged.
    private static readonly SpeakerMatchPolicy s_shippedDefault =
        new(Threshold: 0.82, Margin: 0.08);

    // Opt-in experiment profile only (issue #49 review update). NOT a new default.
    private static readonly SpeakerMatchPolicy s_optInExperiment =
        new(Threshold: 0.50, Margin: 0.06);

    // 2026-11-05 measured cosine through the shipped sherpa-onnx + ERes2Net runtime.
    private const double SamePersonSameRecording = 0.656;       // different 15s spans, same recording
    private const double SamePersonCrossRecording = 0.57;       // midpoint of the observed 0.55–0.58
    private const double DifferentPersonVs18SpeakerLibrary = 0.38; // worst-case observed different-person

    private static SpeakerCandidate Candidate(string id, string name, double score) => new()
    {
        SpeakerId = id, DisplayName = name, Score = score,
    };

    [Fact]
    public void MeasuredSeparation_SameSpeakerScoresAboveDifferentPerson()
    {
        // The same/different gap the threshold has to sit inside. Same-recording same-speaker
        // must score above cross-recording same-speaker, which must score above the
        // worst-case different-person probe. If this invariant breaks, the measured basis for
        // the calibration guidance is stale and must be re-measured.
        Assert.True(SamePersonSameRecording > SamePersonCrossRecording,
            "same-recording same-speaker cosine must exceed cross-recording same-speaker cosine");
        Assert.True(SamePersonCrossRecording > DifferentPersonVs18SpeakerLibrary,
            "cross-recording same-speaker cosine must exceed different-person cosine");
    }

    [Fact]
    public void ShippedDefault_SitsAboveMeasuredSameSpeakerCosine()
    {
        // The core defect (issue #49): the shipped default threshold is above even the
        // same-speaker same-recording cosine this pipeline produces, so a correctly enrolled
        // person cannot be matched at all. This documents why voiceprint attribution resolves
        // nothing out of the box and why the default is gated on recalibration.
        Assert.True(s_shippedDefault.Threshold > SamePersonSameRecording,
            "the shipped 0.82 default must be documented as above the measured ~0.656 same-speaker cosine; " +
            "update this test only when a recalibrated default lands after issue #50");
    }

    [Fact]
    public void ShippedDefaultOverFiltersSameSpeakerProbe()
    {
        // A same-speaker same-recording probe (the issue's control experiment) is rejected at
        // the shipped default even with no runner-up to confuse it. This is the over-filter
        // made executable: it will start accepting only once the default is recalibrated.
        var candidates = new List<SpeakerCandidate>
        {
            Candidate("person_alice", "Alice", SamePersonSameRecording),
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_shippedDefault);

        Assert.Null(resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, resolution.Source);
    }

    [Fact]
    public void ShippedDefault_RejectsDifferentPersonProbe()
    {
        // The one thing the unattainable default does correctly: a different-person probe is
        // rejected. Lowering the threshold must preserve this.
        var candidates = new List<SpeakerCandidate>
        {
            Candidate("person_alice", "Alice", DifferentPersonVs18SpeakerLibrary),
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_shippedDefault);

        Assert.Null(resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, resolution.Source);
    }

    [Fact]
    public void OptInExperimentProfile_AcceptsCrossRecordingSameSpeakerProbe()
    {
        // The opt-in 0.50/0.06 experiment profile accepts a cross-recording same-speaker
        // probe (0.57) when the runner-up is a different person (0.38): the best clears the
        // threshold and leads the runner-up by 0.19, well above the 0.06 margin. This is the
        // behaviour the field deployment observed ("five enrolled people recognized
        // immediately"), documented as an opt-in profile, not a shipped default.
        var candidates = new List<SpeakerCandidate>
        {
            Candidate("person_alice", "Alice", SamePersonCrossRecording),
            Candidate("person_bob", "Bob", DifferentPersonVs18SpeakerLibrary),
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_optInExperiment);

        Assert.Equal("person_alice", resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, resolution.Source);
        Assert.Equal(SamePersonCrossRecording, resolution.Confidence);
    }

    [Fact]
    public void OptInExperimentProfile_RejectsDifferentPersonProbeBelowThreshold()
    {
        // The opt-in profile still rejects a lone different-person probe (0.38 < 0.50).
        // This is the lower bound of safety for the experiment profile: it must not accept a
        // different-person score just because it is the only candidate.
        var candidates = new List<SpeakerCandidate>
        {
            Candidate("person_alice", "Alice", DifferentPersonVs18SpeakerLibrary),
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_optInExperiment);

        Assert.Null(resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, resolution.Source);
    }

    [Fact]
    public void Margin_RejectsAmbiguousMatchWhenRunnerUpIsClose()
    {
        // Margin interaction (docs/CONFIGURATION.md section 9.2.2): even at the opt-in
        // threshold, a best candidate that clears the threshold is rejected when a runner-up
        // is too close, because two enrolled people scoring similarly must not be silently
        // disambiguated. Best 0.57 vs runner-up 0.54 leaves a 0.03 lead, below the 0.06 margin.
        var candidates = new List<SpeakerCandidate>
        {
            Candidate("person_alice", "Alice", SamePersonCrossRecording),
            Candidate("person_bob", "Bob", 0.54),
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual: null, candidates, s_optInExperiment);

        Assert.Null(resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, resolution.Source);
    }

    [Fact]
    public void ManualAssignment_OverridesThresholdEvenForOverFilteredProbe()
    {
        // While the default remains uncalibrated, manual assignment is the authoritative
        // escape hatch (docs/ASR_STRATEGY.md section 10.2): a human-bound label is accepted
        // even when the voiceprint path would over-filter. A locked manual assignment wins
        // regardless of the shipped default.
        var manual = new SpeakerAssignment
        {
            Id = "asgn_1", SessionId = "ses_1", SpeakerLabel = "speaker_0",
            SpeakerId = "person_alice", SpeakerName = "Alice",
            Source = SpeakerAssignmentSource.Manual, Locked = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        // A probe that the shipped default would reject (0.656 < 0.82).
        var candidates = new List<SpeakerCandidate>
        {
            Candidate("person_alice", "Alice", SamePersonSameRecording),
        };

        var resolution = SpeakerMatchingPolicy.Resolve("speaker_0", manual, candidates, s_shippedDefault);

        Assert.Equal("person_alice", resolution.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Manual, resolution.Source);
        Assert.True(resolution.Locked);
    }
}
