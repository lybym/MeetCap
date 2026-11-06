using MeetCap.Cli.Commands;
using MeetCap.Core.Speakers;
using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Tests the opt-in per-label attribution diagnostics summary (issue #50). The summary is
/// off by default and printed to a writer (stderr in production) only when an operator
/// passes <c>--diagnostics</c>. Candidate names are intentionally omitted from the line:
/// the best similarity, threshold and reason answer "why unknown" and feed calibration
/// (#49) without printing sensitive identity metadata to the console.
/// </summary>
public class SpeakerDiagnosticsSummaryTests
{
    [Fact]
    public void Summary_PrintsOneLinePerUnresolvedLabel_WithScoreThresholdAndReason()
    {
        var artifact = new SpeakerAttributionArtifact
        {
            SessionId = "ses_1",
            Entries =
            [
                // A resolved label is skipped (no summary line).
                new SpeakerAttributionEntry
                {
                    SpeakerLabel = "speaker_0",
                    SpeakerId = "person_01",
                    SpeakerName = "Alice",
                    Confidence = 0.95,
                    Source = SpeakerAssignmentSource.Voiceprint,
                    Locked = false,
                    Diagnostics = new SpeakerLabelDiagnostics
                    {
                        ResolutionReason = "matched",
                        SampleOutcome = "extracted",
                        Candidates = [new DiagnosticCandidate { SpeakerName = "Alice", Similarity = 0.95 }],
                        Threshold = 0.82,
                        Margin = 0.08,
                        EnrolledSpeakers = 1,
                        EnrolledEmbeddings = 1,
                    },
                },
                // An unresolved label with candidates: best score, threshold, reason.
                new SpeakerAttributionEntry
                {
                    SpeakerLabel = "speaker_1",
                    SpeakerId = null,
                    SpeakerName = null,
                    Confidence = null,
                    Source = SpeakerAssignmentSource.Unknown,
                    Locked = false,
                    Diagnostics = new SpeakerLabelDiagnostics
                    {
                        ResolutionReason = "below_threshold",
                        SampleOutcome = "extracted",
                        Candidates = [new DiagnosticCandidate { SpeakerName = "Bob", Similarity = 0.5707 }],
                        Threshold = 0.82,
                        Margin = 0.08,
                        EnrolledSpeakers = 8,
                        EnrolledEmbeddings = 24,
                    },
                },
                // An unresolved label with no candidates: no candidates, threshold, reason.
                new SpeakerAttributionEntry
                {
                    SpeakerLabel = "speaker_2",
                    SpeakerId = null,
                    SpeakerName = null,
                    Confidence = null,
                    Source = SpeakerAssignmentSource.Unknown,
                    Locked = false,
                    Diagnostics = new SpeakerLabelDiagnostics
                    {
                        ResolutionReason = "no_clean_ranges",
                        SampleOutcome = "no_ranges",
                        Candidates = [],
                        Threshold = 0.82,
                        Margin = 0.08,
                        EnrolledSpeakers = 8,
                        EnrolledEmbeddings = 24,
                    },
                },
            ],
        };

        using var writer = new StringWriter();
        SpeakersCommand.WriteDiagnosticsSummary(writer, artifact);
        var output = writer.ToString();

        // The header is always printed.
        Assert.Contains("attribution diagnostics:", output, StringComparison.Ordinal);

        // speaker_0 was resolved, so it is NOT summarized.
        Assert.DoesNotContain("speaker_0:", output, StringComparison.Ordinal);

        // speaker_1: best score (3 decimals), threshold (2 decimals), reason. No candidate name.
        Assert.Contains("speaker_1: best 0.571 (threshold 0.82, reason: below_threshold)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", output, StringComparison.Ordinal);

        // speaker_2: no candidates, threshold, reason.
        Assert.Contains("speaker_2: no candidates (threshold 0.82, reason: no_clean_ranges)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_WithNullDiagnostics_SkipsEntriesWithoutDiagnostics()
    {
        // An older artifact (pre-issue-#50) has no diagnostics block; those entries are skipped
        // rather than crashing, so the opt-in flag is safe on legacy attribution.json files.
        var artifact = new SpeakerAttributionArtifact
        {
            SessionId = "ses_legacy",
            Entries =
            [
                new SpeakerAttributionEntry
                {
                    SpeakerLabel = "speaker_0",
                    SpeakerId = null,
                    SpeakerName = null,
                    Confidence = null,
                    Source = SpeakerAssignmentSource.Unknown,
                    Locked = false,
                    Diagnostics = null,
                },
            ],
        };

        using var writer = new StringWriter();
        SpeakersCommand.WriteDiagnosticsSummary(writer, artifact);
        var output = writer.ToString();

        Assert.Contains("attribution diagnostics:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("speaker_0:", output, StringComparison.Ordinal);
    }
}
