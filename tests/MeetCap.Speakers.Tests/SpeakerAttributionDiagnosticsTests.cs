using MeetCap.Core.Speakers;
using MeetCap.Core.Transcripts;
using MeetCap.Persistence.Storage;
using MeetCap.Speakers.Tests.Fakes;
using Xunit;

namespace MeetCap.Speakers.Tests;

/// <summary>
/// Per-label attribution diagnostics (issue #50). Verifies that every resolution path —
/// resolved or not — records an explicit <c>resolution_reason</c>, sampled ranges, sample
/// outcome, top-N candidates with similarities, and the configured threshold/margin/registry
/// size; that the previously-silent non-match paths now emit one bounded per-label warning;
/// that the artifact extension is additive (existing fields and <c>raw_text</c> untouched);
/// and that an older artifact without the diagnostics block still deserializes.
/// </summary>
/// <remarks>
/// The <c>resolution_reason</c> values covered mirror the issue #50 review update (2026-11-06):
/// <c>registry_empty</c>, <c>no_clean_ranges</c>, <c>audio_unavailable</c>,
/// <c>provider_failed</c>, <c>below_threshold</c>, <c>margin_too_small</c>,
/// <c>ambiguous_label</c> (not scored, issue #45 invariant), <c>manual_locked</c>, <c>matched</c>.
/// The match threshold is intentionally unchanged (diagnostic-only PR).
/// </remarks>
public class SpeakerAttributionDiagnosticsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MeetCapDatabase _database;
    private readonly SpeakerMatchPolicy _policy = new(Threshold: 0.80, Margin: 0.05);

    public SpeakerAttributionDiagnosticsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "meetcap-iss50-diag-" + Guid.NewGuid().ToString("N"), "meetcap.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _database = new MeetCapDatabase(_dbPath);
        _database.EnsureMigrated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static TranscriptSegment Seg(string label, string text, long start = 0, long durationMs = 5000, string jobId = "job_1") => new()
    {
        SegmentId = $"seg_{label}_{start}_{jobId}", SessionId = "ses_1", Source = "mic",
        StartMs = start, EndMs = start + durationMs, RawText = text, SpeakerLabel = label,
        ProviderJobId = jobId,
    };

    private async Task EnrollSpeaker(string name, float[] embedding)
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding(embedding);
        var registry = new SpeakerRegistry(_database.Speakers, provider);
        await registry.EnrollAsync(name, new SpeakerAudioSample { Samples = [1], SampleRate = 16000 }, CancellationToken.None);
    }

    private SpeakerSampleExtractor ExtractorReturningSamples =>
        (label, startMs, endMs, ct) =>
            Task.FromResult<SpeakerAudioSample?>(new SpeakerAudioSample
            {
                Samples = [1, 2, 3, 4],
                SampleRate = 16000,
                SpeakerLabel = label,
                StartMs = startMs,
                EndMs = endMs,
            });

    // A provider that extracts a fixed embedding for every sample, so a test can make a
    // given label produce a known cosine against an enrolled embedding.
    private static FakeSpeakerIdentityProvider FixedProbeProvider(float[] probe) =>
        FakeSpeakerIdentityProvider.WithFixedEmbedding(probe);

    private static SpeakerLabelDiagnostics DiagFor(SpeakerAttributionArtifact artifact, string label)
    {
        var d = artifact.Entries.Single(e => e.SpeakerLabel == label).Diagnostics;
        Assert.NotNull(d);
        return d!;
    }

    // --- registry_empty ---

    [Fact]
    public async Task EmptyRegistry_RecordsRegistryEmptyReasonAndWarns()
    {
        // No speakers enrolled, so no candidate can exist. The label still gets a diagnostics block.
        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);

        var result = await service.BuildAsync("ses_1", [Seg("speaker_0", "hello")], ExtractorReturningSamples, CancellationToken.None);

        var entry = Assert.Single(result.Artifact.Entries);
        Assert.Equal(SpeakerAssignmentSource.Unknown, entry.Source);
        Assert.Null(entry.SpeakerId);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("registry_empty", d.ResolutionReason);
        Assert.Equal("no_ranges", d.SampleOutcome);
        Assert.Empty(d.Candidates);
        Assert.Empty(d.RangesConsidered);
        Assert.Equal(0.80, d.Threshold);
        Assert.Equal(0.05, d.Margin);
        Assert.Equal(0, d.EnrolledSpeakers);
        Assert.Equal(0, d.EnrolledEmbeddings);

        // The previously-silent "registry empty" path now emits one bounded per-label warning.
        Assert.Contains(result.Warnings, w => w.Contains("speaker_0", StringComparison.Ordinal) && w.Contains("registry is empty", StringComparison.Ordinal));
    }

    // --- no_clean_ranges ---

    [Fact]
    public async Task NoCleanRange_RecordsNoCleanRangesReasonAndWarns()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        // Zero-duration segments: CleanSampleSelector filters them (EndMs > StartMs is false),
        // so no eligible clean range is selected even though a speaker is enrolled.
        var segments = new[]
        {
            Seg("speaker_0", "a", 0, durationMs: 0),
            Seg("speaker_0", "b", 1000, durationMs: 0),
        };

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("no_clean_ranges", d.ResolutionReason);
        Assert.Equal("no_ranges", d.SampleOutcome);
        Assert.Empty(d.Candidates);
        Assert.Empty(d.RangesConsidered);
        Assert.Equal(1, d.EnrolledSpeakers);

        // AC1: an explicit per-label warning names the label and the segment count considered.
        Assert.Contains(result.Warnings, w => w.Contains("speaker_0", StringComparison.Ordinal)
            && w.Contains("no eligible clean speech range", StringComparison.Ordinal)
            && w.Contains("2 segment(s)", StringComparison.Ordinal));
    }

    // --- audio_unavailable ---

    [Fact]
    public async Task NoAudioForSampledRange_RecordsAudioUnavailableAndWarnsNamingSourceAndRange()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        // An 8s segment clears the clean-sample minimum, so a range is selected and sampled.
        var segments = new[] { Seg("speaker_0", "hello world", 0, durationMs: 8000) };

        SpeakerSampleExtractor nullExtractor = (_, _, _, _) => Task.FromResult<SpeakerAudioSample?>(null);

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", segments, nullExtractor, CancellationToken.None);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("audio_unavailable", d.ResolutionReason);
        Assert.Equal("no_audio", d.SampleOutcome);
        Assert.Empty(d.Candidates);
        // The sampled range is recorded even though audio was unavailable for it.
        var range = Assert.Single(d.RangesConsidered);
        Assert.Equal(0, range.StartMs);
        Assert.Equal(8000, range.EndMs);

        // AC2: an explicit per-label warning names the source and the sampled range.
        Assert.Contains(result.Warnings, w => w.Contains("speaker_0", StringComparison.Ordinal)
            && w.Contains("audio unavailable", StringComparison.Ordinal)
            && w.Contains("source 'mic'", StringComparison.Ordinal)
            && w.Contains("0-8000ms", StringComparison.Ordinal));
    }

    // --- provider_failed ---

    [Fact]
    public async Task ProviderFailure_RecordsProviderFailedReasonAndWarns()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        var failingProvider = new ThrowingProvider();
        var service = new SpeakerAttributionService(_database.Speakers, failingProvider, _policy, 5, 15);

        var result = await service.BuildAsync("ses_1", [Seg("speaker_0", "hello")], ExtractorReturningSamples, CancellationToken.None);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("provider_failed", d.ResolutionReason);
        Assert.Equal("provider_failed", d.SampleOutcome);
        Assert.Empty(d.Candidates);
        Assert.Equal(SpeakerAssignmentSource.Unknown, Assert.Single(result.Artifact.Entries).Source);

        // The provider failure keeps its single bounded warning (one per label, with the message).
        Assert.Contains(result.Warnings, w => w.Contains("speaker identity provider failed for speaker_0", StringComparison.Ordinal));
    }

    // --- below_threshold ---

    [Fact]
    public async Task BestCandidateBelowThreshold_RecordsBelowThresholdWithCandidates()
    {
        // Alice enrolled with [1,1,0,0]; the probe is [1,0,0,0]. Cosine = 1/sqrt(2) ~= 0.707,
        // below the 0.80 threshold, so the label stays unknown with a recorded candidate.
        await EnrollSpeaker("Alice", [1, 1, 0, 0]);

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", [Seg("speaker_0", "hello", 0, 8000)], ExtractorReturningSamples, CancellationToken.None);

        var entry = Assert.Single(result.Artifact.Entries);
        Assert.Equal(SpeakerAssignmentSource.Unknown, entry.Source);
        Assert.Null(entry.SpeakerId);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("below_threshold", d.ResolutionReason);
        Assert.Equal("extracted", d.SampleOutcome);
        Assert.Equal(0.80, d.Threshold);

        var candidate = Assert.Single(d.Candidates);
        Assert.Equal("Alice", candidate.SpeakerName);
        // Cosine([1,0,0,0],[1,1,0,0]) ~= 0.7071.
        Assert.InRange(candidate.Similarity, 0.70, 0.71);

        // below_threshold is a normal non-match: no per-label warning (only opt-in CLI summary).
        Assert.DoesNotContain(result.Warnings, w => w.Contains("speaker_0", StringComparison.Ordinal));
    }

    // --- margin_too_small ---

    [Fact]
    public async Task BestAboveThresholdButMarginTooSmall_RecordsMarginTooSmall()
    {
        // Alice [1,0,0,0] and Bob [1,0.1,0,0] are near-parallel. Probe [1,0,0,0] gives cosine
        // ~=1.0 for Alice and ~=0.995 for Bob: both clear the 0.80 threshold, but the lead is
        // ~0.005, below the 0.05 margin, so the label stays unknown (not silently named).
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);
        await EnrollSpeaker("Bob", [1, 0.1f, 0, 0]);

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", [Seg("speaker_0", "hello", 0, 8000)], ExtractorReturningSamples, CancellationToken.None);

        var entry = Assert.Single(result.Artifact.Entries);
        Assert.Equal(SpeakerAssignmentSource.Unknown, entry.Source);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("margin_too_small", d.ResolutionReason);
        Assert.Equal("extracted", d.SampleOutcome);

        // Top-N candidates are ranked by similarity descending.
        Assert.True(d.Candidates.Count >= 2);
        Assert.Equal("Alice", d.Candidates[0].SpeakerName);
        Assert.InRange(d.Candidates[0].Similarity, 0.99, 1.01);
        Assert.Equal("Bob", d.Candidates[1].SpeakerName);
        Assert.InRange(d.Candidates[1].Similarity, 0.99, 1.0);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("speaker_0", StringComparison.Ordinal));
    }

    // --- ambiguous_label (issue #45 invariant: not scored) ---

    [Fact]
    public async Task AmbiguousLabelAcrossJobs_RecordsAmbiguousLabelWithNoCandidates()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        // The same label reused across two provider jobs is never voiceprint-scored, so the
        // diagnostics block must record no candidates and the ambiguous reason.
        var segments = new[]
        {
            Seg("speaker_0", "first", 0, 5000, "job_1"),
            Seg("speaker_0", "second", 5000, 5000, "job_2"),
        };

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", segments,
            (_, _, _, _) => throw new Exception("ambiguous label must not reach extraction"));

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("ambiguous_label", d.ResolutionReason);
        Assert.Equal("no_ranges", d.SampleOutcome);
        Assert.Empty(d.Candidates);
        Assert.Empty(d.RangesConsidered);
        // No single provider job: provenance is null.
        Assert.Null(d.ProviderJob);

        Assert.Contains(result.Warnings, w => w.Contains("multiple provider jobs", StringComparison.Ordinal));
    }

    // --- manual_locked ---

    [Fact]
    public async Task ManualAssignment_RecordsManualLockedReasonWithNoCandidates()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);
        var alice = Assert.Single(_database.Speakers.ListSpeakers());
        new SpeakerRegistry(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]))
            .AssignManually("ses_1", "speaker_0", alice);

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", [Seg("speaker_0", "hello", 0, 8000)],
            (_, _, _, _) => throw new Exception("manual lock must not reach extraction"));

        var entry = Assert.Single(result.Artifact.Entries);
        Assert.Equal(SpeakerAssignmentSource.Manual, entry.Source);
        Assert.True(entry.Locked);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("manual_locked", d.ResolutionReason);
        Assert.Equal("no_ranges", d.SampleOutcome);
        Assert.Empty(d.Candidates);
        Assert.Empty(d.RangesConsidered);
        // Threshold/margin/registry size are still recorded for a manual-locked label.
        Assert.Equal(0.80, d.Threshold);
        Assert.Equal(1, d.EnrolledSpeakers);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("left unknown", StringComparison.Ordinal));
    }

    // --- matched ---

    [Fact]
    public async Task VoiceprintMatch_RecordsMatchedReasonWithCandidatesAndProvenance()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", [Seg("speaker_0", "hello", 0, 8000)], ExtractorReturningSamples, CancellationToken.None);

        var entry = Assert.Single(result.Artifact.Entries);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, entry.Source);
        Assert.Equal("Alice", entry.SpeakerName);

        var d = DiagFor(result.Artifact, "speaker_0");
        Assert.Equal("matched", d.ResolutionReason);
        Assert.Equal("extracted", d.SampleOutcome);

        // A matched label still records its top-N candidates (including the matched one).
        var candidate = Assert.Single(d.Candidates);
        Assert.Equal("Alice", candidate.SpeakerName);
        Assert.InRange(candidate.Similarity, 0.99, 1.01);

        // Provider-job provenance is recorded for the unambiguous matched label.
        Assert.Equal("job_1", d.ProviderJob);
        Assert.Equal(0.80, d.Threshold);
        Assert.Equal(1, d.EnrolledSpeakers);
        Assert.Equal(1, d.EnrolledEmbeddings);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("left unknown", StringComparison.Ordinal));
    }

    // --- additive artifact + raw_text untouched ---

    [Fact]
    public async Task DiagnosticsArtifact_IsAdditiveAndRoundTripsJson_WithRawTextUntouched()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        var originalText = "the original transcript text must survive attribution";
        var segments = new[]
        {
            Seg("speaker_0", originalText, 0, 8000),
            Seg("speaker_1", "another", 8000, 8000),
        };

        var service = new SpeakerAttributionService(_database.Speakers, FixedProbeProvider([1, 0, 0, 0]), _policy, 5, 15);
        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        // raw_text is never modified (PRD section 2.3, DATA_MODEL.md section 7).
        var attributedByLabel = result.AttributedSegments.ToDictionary(s => s.SpeakerLabel);
        Assert.Equal(originalText, attributedByLabel["speaker_0"].RawText);
        Assert.Equal("another", attributedByLabel["speaker_1"].RawText);

        // Every entry carries the diagnostics block.
        Assert.All(result.Artifact.Entries, e => Assert.NotNull(e.Diagnostics));

        var json = SpeakerAttributionJson.ToJson(result.Artifact);

        // Existing top-level fields are still present (additive: existing consumers unaffected).
        Assert.Contains("\"speaker_label\"", json, StringComparison.Ordinal);
        Assert.Contains("\"speaker_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\"", json, StringComparison.Ordinal);
        Assert.Contains("\"locked\"", json, StringComparison.Ordinal);

        // New additive diagnostics fields are present.
        Assert.Contains("\"diagnostics\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolution_reason\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sample_outcome\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ranges_considered\"", json, StringComparison.Ordinal);
        Assert.Contains("\"candidates\"", json, StringComparison.Ordinal);
        Assert.Contains("\"threshold\"", json, StringComparison.Ordinal);
        Assert.Contains("\"margin\"", json, StringComparison.Ordinal);
        Assert.Contains("\"enrolled_speakers\"", json, StringComparison.Ordinal);
        Assert.Contains("\"enrolled_embeddings\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider_job\"", json, StringComparison.Ordinal);
        Assert.Contains("\"speaker_name\"", json, StringComparison.Ordinal);
        Assert.Contains("\"similarity\"", json, StringComparison.Ordinal);
        Assert.Contains("\"start_ms\"", json, StringComparison.Ordinal);
        Assert.Contains("\"end_ms\"", json, StringComparison.Ordinal);

        // The artifact round-trips and the diagnostics survive deserialization.
        var restored = SpeakerAttributionJson.FromJson(json);
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Entries.Count);
        Assert.All(restored.Entries, e => Assert.NotNull(e.Diagnostics));
        Assert.Equal("matched", restored.Entries.Single(e => e.SpeakerLabel == "speaker_0").Diagnostics!.ResolutionReason);
    }

    // --- backward compatibility: an older artifact without diagnostics still deserializes ---

    [Fact]
    public void OlderArtifact_WithoutDiagnosticsBlock_StillDeserializes()
    {
        // The pre-issue-#50 attribution.json shape: top-level fields only, no diagnostics.
        // `source` serializes as the enum integer (3 = unknown) because no string converter
        // is configured, and that is unchanged by this additive extension.
        const string legacyJson = """
        {
          "session_id": "ses_legacy",
          "entries": [
            {
              "speaker_label": "speaker_0",
              "speaker_id": null,
              "speaker_name": null,
              "confidence": null,
              "source": 3,
              "locked": false
            }
          ]
        }
        """;

        var restored = SpeakerAttributionJson.FromJson(legacyJson);
        Assert.NotNull(restored);
        Assert.Equal("ses_legacy", restored!.SessionId);

        var entry = Assert.Single(restored.Entries);
        Assert.Equal("speaker_0", entry.SpeakerLabel);
        Assert.Equal(SpeakerAssignmentSource.Unknown, entry.Source);
        // The additive diagnostics block is absent, so it deserializes as null: existing
        // consumers that pre-date issue #50 are unaffected.
        Assert.Null(entry.Diagnostics);
    }

    private sealed class ThrowingProvider : ISpeakerIdentityProvider
    {
        public string Name => "throwing";
        public string ModelName => "none";
        public string ModelVersion => "0";
        public int EmbeddingDimension => 0;

        public Task<ExtractedEmbedding> ExtractAsync(SpeakerAudioSample sample, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("model unavailable");

        public Task<IReadOnlyList<SpeakerCandidate>> IdentifyAsync(
            ExtractedEmbedding probe,
            IReadOnlyList<EnrolledSpeaker> enrolled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpeakerCandidate>>([]);
    }
}
