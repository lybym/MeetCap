using MeetCap.Core.Asr;
using MeetCap.Core.Speakers;
using MeetCap.Core.Transcripts;
using MeetCap.Persistence.Storage;
using MeetCap.Speakers.Tests.Fakes;
using Xunit;

namespace MeetCap.Speakers.Tests;

/// <summary>
/// Tests the per-session speaker attribution pipeline (Issue #8 acceptance criteria
/// 3-6, 8). Verifies manual assignment priority, voiceprint matching, unknown
/// preservation, raw_text immutability, and provider-failure isolation.
/// </summary>
public class SpeakerAttributionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MeetCapDatabase _database;
    private readonly SpeakerMatchPolicy _policy = new(Threshold: 0.80, Margin: 0.05);

    public SpeakerAttributionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "meetcap-attr-test-" + Guid.NewGuid().ToString("N"), "meetcap.db");
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

    private static TranscriptSegment Seg(string label, string text, long start = 0) => new()
    {
        SegmentId = $"seg_{label}_{start}", SessionId = "ses_1", Source = "mic",
        StartMs = start, EndMs = start + 5000, RawText = text, SpeakerLabel = label,
    };

    private async Task EnrollSpeaker(string name, float[] embedding)
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding(embedding);
        var registry = new SpeakerRegistry(_database.Speakers, provider);
        await registry.EnrollAsync(name, new SpeakerAudioSample { Samples = [1], SampleRate = 16000 }, CancellationToken.None);
    }

    private FakeSpeakerIdentityProvider ProviderByLabel() => new(sample =>
    {
        // Map the speaker label to a known embedding so the attribution pipeline can match.
        return sample.SpeakerLabel switch
        {
            "speaker_0" => new float[] { 1, 0, 0, 0 },
            "speaker_1" => new float[] { 0, 1, 0, 0 },
            _ => new float[] { 0, 0, 1, 0 },
        };
    });

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

    [Fact]
    public async Task ManualAssignment_WinsOverVoiceprint()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);
        await EnrollSpeaker("Bob", [0, 1, 0, 0]);

        // Manually assign speaker_0 to Bob (not Alice, even though the voiceprint would match Alice).
        var bob = _database.Speakers.ListSpeakers().Single(s => s.DisplayName == "Bob");
        var registry = new SpeakerRegistry(_database.Speakers, ProviderByLabel());
        registry.AssignManually("ses_1", "speaker_0", bob);

        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderByLabel(), _policy, 5, 15);

        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", "hello"),
        };

        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        var entry = result.Artifact.Entries.Single(e => e.SpeakerLabel == "speaker_0");
        Assert.Equal("Bob", entry.SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Manual, entry.Source);
        Assert.True(entry.Locked);
    }

    [Fact]
    public async Task VoiceprintMatch_AboveThreshold_ResolvesToEnrolledSpeaker()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderByLabel(), _policy, 5, 15);

        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", "hello world"),
        };

        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        var entry = result.Artifact.Entries.Single();
        Assert.Equal("Alice", entry.SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, entry.Source);
    }

    [Fact]
    public async Task UnknownLabel_StaysUnknown()
    {
        // No enrolled speakers — the label should stay unknown.
        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderByLabel(), _policy, 5, 15);

        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", "hello"),
        };

        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        var entry = result.Artifact.Entries.Single();
        Assert.Null(entry.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, entry.Source);
    }

    [Fact]
    public async Task AttributedSegments_NeverModifyRawText()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderByLabel(), _policy, 5, 15);

        var originalText = "the original transcript text";
        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", originalText),
        };

        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        Assert.Equal(originalText, result.AttributedSegments[0].RawText);
    }

    [Fact]
    public async Task AttributedSegments_FillSpeakerIdAndName()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);
        var alice = _database.Speakers.ListSpeakers().Single();

        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderByLabel(), _policy, 5, 15);

        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", "hello"),
            Seg("speaker_0", "world", start: 5000),
        };

        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        Assert.All(result.AttributedSegments, s =>
        {
            Assert.Equal(alice.Id, s.SpeakerId);
            Assert.Equal("Alice", s.SpeakerName);
            Assert.NotNull(s.SpeakerConfidence);
        });
    }

    [Fact]
    public async Task ProviderFailure_LabelStaysUnknown_WithoutCrashing()
    {
        // Enroll a speaker so the identification path is actually exercised.
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        // A provider that always throws during extraction.
        var failingProvider = new ThrowingProvider();

        var service = new SpeakerAttributionService(
            _database.Speakers, failingProvider, _policy, 5, 15);

        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", "hello"),
        };

        // The pipeline should not crash; the label stays unknown.
        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        var entry = result.Artifact.Entries.Single();
        Assert.Null(entry.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, entry.Source);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task SampleExtractorReturningNull_LabelStaysUnknown()
    {
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);

        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderByLabel(), _policy, 5, 15);

        // The extractor returns null (no audio available), so the label stays unknown.
        SpeakerSampleExtractor nullExtractor = (_, _, _, _) => Task.FromResult<SpeakerAudioSample?>(null);

        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", "hello"),
        };

        var result = await service.BuildAsync("ses_1", segments, nullExtractor, CancellationToken.None);

        var entry = result.Artifact.Entries.Single();
        Assert.Null(entry.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Unknown, entry.Source);
    }

    [Fact]
    public async Task AttributionArtifact_IsSerializableJson()
    {
        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderByLabel(), _policy, 5, 15);

        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", "hello"),
            Seg("speaker_1", "world", start: 5000),
        };

        var result = await service.BuildAsync("ses_1", segments, ExtractorReturningSamples, CancellationToken.None);

        var json = SpeakerAttributionJson.ToJson(result.Artifact);
        var restored = SpeakerAttributionJson.FromJson(json);

        Assert.NotNull(restored);
        Assert.Equal("ses_1", restored!.SessionId);
        Assert.Equal(2, restored.Entries.Count);
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
