using MeetCap.Asr;
using MeetCap.Asr.Importing;
using MeetCap.Asr.Transcripts;
using MeetCap.Asr.Volcengine;
using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;
using MeetCap.Core.Speakers;
using MeetCap.Core.Transcripts;
using MeetCap.Persistence.Storage;
using MeetCap.Speakers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeetCap.IntegrationTests;

/// <summary>
/// MVP release-gate end-to-end proof: the durable artifacts an ASR run produces
/// (<c>transcript/raw.jsonl</c>) feed the speaker attribution pipeline and become the
/// final attributed transcript artifacts (<c>transcript/final.jsonl</c>,
/// <c>transcript/final.md</c>, <c>speakers/attribution.json</c>) without losing provider
/// anonymous labels, timestamps, or raw text.
/// </summary>
/// <remarks>
/// This stitches the M3 import + file-ASR pipeline to the M6 speaker attribution
/// pipeline — the composition no single existing test exercises. The
/// <c>ImportEndToEndTests</c> stop at <c>raw.jsonl</c>/<c>live.md</c>; the
/// <c>SpeakerAttributionServiceTests</c> drive attribution from hand-built segments and
/// never read an ASR artifact from disk or write the final files. This test uses the real
/// <see cref="VolcengineResponseNormalizer"/>, the real <see cref="AsrJobProcessor"/>, the
/// real on-disk artifact layout, and the real <see cref="SpeakerAttributionService"/>.
///
/// Per <c>docs/DEVELOPMENT.md</c> section 7, the boundaries that need real Windows
/// hardware, real Volcengine credentials, or the real 3D-Speaker model are stubbed: the
/// media pipeline, the provider HTTP boundary, and the identity provider are fakes. This
/// proves the artifact-contract handoff and the attribution composition; it does NOT claim
/// real-hardware validation (the 2-hour soak, real Volcengine transcription, or real
/// voiceprint matching remain a manual Windows checklist).
/// </remarks>
public class AttributionEndToEndTests : IDisposable
{
    /// <summary>
    /// A Volcengine file-ASR response with two anonymous speakers, each utterance long
    /// enough (8 s) to clear the clean-sample minimum so the voiceprint path is exercised
    /// rather than only its short-sample fallback.
    /// </summary>
    private const string ProviderJson = """
    {"result":{"text":"hello world","utterances":[
      {"text":"The quotation needs another review before we send it to the client.","start_time":0,"end_time":8000,"speaker":"1"},
      {"text":"Agreed, I will send the revision by the end of the week.","start_time":8000,"end_time":16000,"additions":{"speaker":"2"}}
    ]}}
    """;

    private readonly string _root;
    private readonly string _dataRoot;
    private readonly FakeMediaPipeline _media = new();
    private readonly string _sourcePath;

    public AttributionEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "meetcap-attr-e2e-" + Guid.NewGuid().ToString("N"));
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);

        _sourcePath = Path.Combine(_root, "meeting.wav");
        File.WriteAllBytes(_sourcePath, new byte[512]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string DatabasePath => Path.Combine(_dataRoot, "meetcap.db");

    private void Migrate() => new SqliteMigrator().Migrate(DatabasePath);

    [Fact]
    public async Task VoiceprintMatch_ProducesFinalTranscriptArtifactsResolvingEnrolledSpeakers()
    {
        var (sessionId, paths, database) = await ImportTranscribedAsync();

        // Capture the raw ASR transcript before attribution: it is the contract the
        // attribution pipeline must consume unchanged (read-only source, provenance).
        var rawSegments = new FileTranscriptStore().ReadJsonl(paths.RawTranscriptJsonl);
        Assert.Equal(2, rawSegments.Count);
        Assert.Equal("speaker_1", rawSegments[0].SpeakerLabel);
        Assert.Equal("speaker_2", rawSegments[1].SpeakerLabel);

        // Enroll two speakers whose embeddings the attribution provider will reproduce for
        // the matching anonymous labels.
        await EnrollAsync(database, "Alice", [1, 0, 0, 0]);
        await EnrollAsync(database, "Bob", [0, 1, 0, 0]);

        var provider = LabelEmbeddingProvider(label => label switch
        {
            "speaker_1" => [1, 0, 0, 0],
            "speaker_2" => [0, 1, 0, 0],
            _ => [0, 0, 1, 0],
        });

        var result = await AttributeAsync(database, sessionId, provider, WorkingSampleExtractor());

        // final.jsonl: two attributed segments, ordered by start_ms, names resolved by voiceprint.
        var final = new FileTranscriptStore().ReadJsonl(paths.FinalTranscriptJsonl);
        Assert.Equal(2, final.Count);
        Assert.Equal(rawSegments[0].StartMs, final[0].StartMs);
        Assert.Equal("Alice", final[0].SpeakerName);
        Assert.NotNull(final[0].SpeakerId);
        Assert.False(final[0].ManualSpeakerLock);
        Assert.Equal("Bob", final[1].SpeakerName);
        Assert.NotNull(final[1].SpeakerId);

        // Provider anonymous labels and timestamps are preserved through to the final
        // transcript (release gate: "provider anonymous speaker labels/timestamps are
        // preserved where supported").
        Assert.Equal("speaker_1", final[0].SpeakerLabel);
        Assert.Equal("speaker_2", final[1].SpeakerLabel);
        Assert.Equal(rawSegments[0].EndMs, final[0].EndMs);
        Assert.Equal(rawSegments[1].StartMs, final[1].StartMs);
        Assert.Equal(rawSegments[1].EndMs, final[1].EndMs);

        // raw_text is never modified by attribution (PRD section 2.3, DEVELOPMENT.md section 5).
        Assert.Equal(rawSegments[0].RawText, final[0].RawText);
        Assert.Equal(rawSegments[1].RawText, final[1].RawText);

        // attribution.json is present, parseable, and reproducible.
        var artifact = SpeakerAttributionJson.FromJson(File.ReadAllText(paths.SpeakerAttributionJson));
        Assert.NotNull(artifact);
        Assert.Equal(sessionId, artifact!.SessionId);
        Assert.Equal(2, artifact.Entries.Count);

        var byLabel = artifact.Entries.ToDictionary(e => e.SpeakerLabel);
        Assert.Equal("Alice", byLabel["speaker_1"].SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, byLabel["speaker_1"].Source);
        Assert.Equal("Bob", byLabel["speaker_2"].SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, byLabel["speaker_2"].Source);

        // final.md is the human-readable rendering and carries the resolved names.
        var markdown = File.ReadAllText(paths.FinalTranscriptMarkdown);
        Assert.Contains("Alice", markdown, StringComparison.Ordinal);
        Assert.Contains("Bob", markdown, StringComparison.Ordinal);

        // The raw ASR transcript is never overwritten by attribution: the read-only source
        // survives the whole pipeline byte-for-byte.
        Assert.Equal(rawSegments, new FileTranscriptStore().ReadJsonl(paths.RawTranscriptJsonl));
    }

    [Fact]
    public async Task ManualAssignment_OverridesVoiceprintAndIsLocked()
    {
        var (sessionId, paths, database) = await ImportTranscribedAsync();

        // Enroll Alice (voiceprint would match speaker_1), Bob, and Carol.
        await EnrollAsync(database, "Alice", [1, 0, 0, 0]);
        await EnrollAsync(database, "Bob", [0, 1, 0, 0]);
        await EnrollAsync(database, "Carol", [0, 0, 1, 0]);

        // Manually bind speaker_1 to Carol, even though the voiceprint would match Alice.
        // Manual assignment is authoritative and locked (release gate: "speaker manual
        // assignment cannot be overwritten automatically").
        var carol = database.Speakers.ListSpeakers().Single(s => s.DisplayName == "Carol");
        var registry = new SpeakerRegistry(
            database.Speakers,
            FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]));
        registry.AssignManually(sessionId, "speaker_1", carol);

        // Attribution provider reproduces Alice's embedding for speaker_1 (voiceprint says
        // Alice); speaker_2 reproduces Bob's embedding.
        var provider = LabelEmbeddingProvider(label => label switch
        {
            "speaker_1" => [1, 0, 0, 0],
            "speaker_2" => [0, 1, 0, 0],
            _ => [0, 0, 1, 0],
        });

        var result = await AttributeAsync(database, sessionId, provider, WorkingSampleExtractor());

        var byLabel = result.Artifact.Entries.ToDictionary(e => e.SpeakerLabel);

        // speaker_1 is Carol by manual assignment — NOT Alice, whom the voiceprint would
        // have named. The manual assignment wins and is locked.
        Assert.Equal("Carol", byLabel["speaker_1"].SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Manual, byLabel["speaker_1"].Source);
        Assert.True(byLabel["speaker_1"].Locked);
        Assert.NotEqual("Alice", byLabel["speaker_1"].SpeakerName);

        // speaker_2 has no manual assignment, so voiceprint resolves it to Bob.
        Assert.Equal("Bob", byLabel["speaker_2"].SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Voiceprint, byLabel["speaker_2"].Source);

        // The final transcript carries the locked manual resolution.
        var final = new FileTranscriptStore().ReadJsonl(paths.FinalTranscriptJsonl);
        var speakerOne = final.Single(s => s.SpeakerLabel == "speaker_1");
        Assert.Equal("Carol", speakerOne.SpeakerName);
        Assert.True(speakerOne.ManualSpeakerLock);

        // Attribution did not touch the raw ASR transcript.
        var rawSegments = new FileTranscriptStore().ReadJsonl(paths.RawTranscriptJsonl);
        Assert.Null(rawSegments.Single(s => s.SpeakerLabel == "speaker_1").SpeakerName);
    }

    [Fact]
    public async Task UnknownLabels_RemainUnknownButFinalArtifactsAreWritten()
    {
        var (sessionId, paths, database) = await ImportTranscribedAsync();
        var rawSegments = new FileTranscriptStore().ReadJsonl(paths.RawTranscriptJsonl);

        // No enrolled speakers: every label stays unknown (PRD: low-confidence voices
        // remain unknown; nothing is forcibly named).
        var provider = LabelEmbeddingProvider(label => label switch
        {
            "speaker_1" => [1, 0, 0, 0],
            "speaker_2" => [0, 1, 0, 0],
            _ => [0, 0, 1, 0],
        });

        var result = await AttributeAsync(database, sessionId, provider, WorkingSampleExtractor());

        // The degraded path still writes final artifacts; nothing is forcibly named.
        Assert.All(result.Artifact.Entries, e =>
        {
            Assert.Null(e.SpeakerId);
            Assert.Null(e.SpeakerName);
            Assert.Equal(SpeakerAssignmentSource.Unknown, e.Source);
        });

        var final = new FileTranscriptStore().ReadJsonl(paths.FinalTranscriptJsonl);
        Assert.Equal(2, final.Count);
        Assert.All(final, s =>
        {
            Assert.Null(s.SpeakerId);
            Assert.Null(s.SpeakerName);
        });

        // Anonymous labels, timestamps, and raw_text survive the attribution pass unchanged.
        Assert.Equal("speaker_1", final[0].SpeakerLabel);
        Assert.Equal("speaker_2", final[1].SpeakerLabel);
        Assert.Equal(rawSegments[0].StartMs, final[0].StartMs);
        Assert.Equal(rawSegments[0].EndMs, final[0].EndMs);
        Assert.Equal(rawSegments[0].RawText, final[0].RawText);
        Assert.Equal(rawSegments[1].RawText, final[1].RawText);

        // attribution.json exists and is parseable even in the all-unknown case.
        var artifact = SpeakerAttributionJson.FromJson(File.ReadAllText(paths.SpeakerAttributionJson));
        Assert.NotNull(artifact);
        Assert.Equal(sessionId, artifact!.SessionId);
        Assert.Equal(2, artifact.Entries.Count);
        Assert.All(artifact.Entries, e => Assert.Equal(SpeakerAssignmentSource.Unknown, e.Source));

        // final.md is written so a human can still read the transcript.
        Assert.True(File.Exists(paths.FinalTranscriptMarkdown));
    }

    // --- shared harness ---

    private async Task<(string SessionId, SessionArtifactPaths Paths, MeetCapDatabase Database)> ImportTranscribedAsync()
    {
        Migrate();
        var (service, provider, database) = CreateImportService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        var sessionId = "ses_attr";
        await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = sessionId,
            JobId = "job_attr",
        });

        var paths = new SessionArtifactPaths(_dataRoot, sessionId);
        Assert.True(File.Exists(paths.RawTranscriptJsonl), "import must produce raw.jsonl before attribution");
        return (sessionId, paths, database);
    }

    private (ImportSessionService Service, FakeAsrProvider Provider, MeetCapDatabase Database) CreateImportService()
    {
        var database = new MeetCapDatabase(DatabasePath);
        database.EnsureMigrated();

        var provider = new FakeAsrProvider();
        var transcripts = new FileTranscriptStore();
        var artifacts = new FileSessionArtifactWriter();

        var processor = new AsrJobProcessor(
            database.AsrJobs,
            provider,
            new VolcengineResponseNormalizer(),
            transcripts,
            database.Sessions,
            artifacts,
            new AsrJobProcessorOptions
            {
                DataRoot = _dataRoot,
                PollInterval = TimeSpan.Zero,
                PollTimeout = TimeSpan.FromMinutes(1),
                WriteMarkdown = true,
                CostPerHourCny = 0.8,
                Delay = (_, _) => Task.CompletedTask,
            });

        var service = new ImportSessionService(
            _media,
            database.Sessions,
            database.AsrJobs,
            artifacts,
            transcripts,
            processor,
            new ImportOptions
            {
                DataRoot = _dataRoot,
                ProviderName = provider.Name,
                DefaultTitle = "Untitled Meeting",
                ServiceTier = "standard",
                RequestSpeakerInfo = true,
                CostPerHourCny = 0.8,
                ConfigSnapshotJson = "{\"config_version\":1}",
            });

        return (service, provider, database);
    }

    private async Task EnrollAsync(MeetCapDatabase database, string name, float[] embedding)
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding(embedding);
        var registry = new SpeakerRegistry(database.Speakers, provider);
        await registry.EnrollAsync(
            name,
            new SpeakerAudioSample { Samples = [1], SampleRate = 16000 },
            CancellationToken.None);
    }

    /// <summary>
    /// Runs the attribution pipeline against the session's ASR-produced
    /// <c>raw.jsonl</c> and writes the final artifacts, mirroring the wiring in
    /// <c>meetcap speakers attribute</c>.
    /// </summary>
    private async Task<SpeakerAttributionResult> AttributeAsync(
        MeetCapDatabase database,
        string sessionId,
        ISpeakerIdentityProvider provider,
        SpeakerSampleExtractor extractor)
    {
        var paths = new SessionArtifactPaths(_dataRoot, sessionId);
        var segments = new FileTranscriptStore().ReadJsonl(paths.RawTranscriptJsonl);

        var policy = new SpeakerMatchPolicy(Threshold: 0.80, Margin: 0.05);
        var service = new SpeakerAttributionService(database.Speakers, provider, policy, 5, 15);

        var result = await service.BuildAsync(sessionId, segments, extractor, CancellationToken.None);

        // Write the attribution artifact and the final attributed transcripts.
        Directory.CreateDirectory(paths.SpeakersDirectory);
        File.WriteAllText(paths.SpeakerAttributionJson, SpeakerAttributionJson.ToJson(result.Artifact));

        var transcripts = new FileTranscriptStore();
        var renderOptions = new TranscriptRenderOptions(
            IncludeSource: true,
            IncludeTimestamps: true,
            IncludeSpeakerLabels: true);
        transcripts.WriteJsonl(paths.FinalTranscriptJsonl, result.AttributedSegments);
        transcripts.WriteMarkdown(paths.FinalTranscriptMarkdown, sessionId, result.AttributedSegments, renderOptions);

        return result;
    }

    /// <summary>
    /// A fake identity provider that derives the probe embedding from the sample's
    /// anonymous speaker label, so a test can make a given label match a known enrolled
    /// embedding without the real model.
    /// </summary>
    private static FakeSpeakerIdentityProvider LabelEmbeddingProvider(Func<string, float[]> embeddingByLabel) =>
        new(sample => embeddingByLabel(sample.SpeakerLabel ?? "speaker_unknown"));

    /// <summary>
    /// A sample extractor that always returns a non-null sample carrying the requested
    /// label, so the voiceprint path is actually exercised. Audio extraction from batch
    /// WAVs is owned by the CLI composition root; the boundary is stubbed here per
    /// <c>docs/DEVELOPMENT.md</c> section 7.
    /// </summary>
    private static SpeakerSampleExtractor WorkingSampleExtractor() =>
        (label, startMs, endMs, _) => Task.FromResult<SpeakerAudioSample?>(new SpeakerAudioSample
        {
            Samples = [1, 2, 3, 4],
            SampleRate = 16000,
            SpeakerLabel = label,
            StartMs = startMs,
            EndMs = endMs,
        });
}
