using System.Text.Json;
using MeetCap.Asr;
using MeetCap.Asr.Importing;
using MeetCap.Asr.Transcripts;
using MeetCap.Asr.Volcengine;
using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;
using MeetCap.Core.Transcripts;
using MeetCap.Persistence.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeetCap.IntegrationTests;

/// <summary>
/// End-to-end M3 proof: a recording is imported, inspected, queued, transcribed, and
/// materialized as durable artifacts, using real SQLite, the real artifact layout, and
/// the real Volcengine response parser.
/// </summary>
/// <remarks>
/// The media pipeline and the provider HTTP boundary are stubbed. Real Volcengine
/// transcription is NOT verified in this environment: there are no
/// <c>VOLCENGINE_APP_ID</c> credentials and no access token available, and
/// docs/DEVELOPMENT.md section 7 forbids claiming otherwise.
/// </remarks>
public class ImportEndToEndTests : IDisposable
{
    private const string ProviderJson = """
    {"result":{"text":"hello world","utterances":[
      {"text":"The quotation needs another review.","start_time":0,"end_time":3200,"speaker":"1"},
      {"text":"Agreed, I will send the revision.","start_time":3200,"end_time":6400,"additions":{"speaker":"2"}}
    ]}}
    """;

    private readonly string _root;
    private readonly string _dataRoot;
    private readonly FakeMediaPipeline _media = new();
    private readonly string _sourcePath;

    public ImportEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "meetcap-integration-" + Guid.NewGuid().ToString("N"));
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

    private (ImportSessionService Service, AsrJobProcessor Processor, FakeAsrProvider Provider, MeetCapDatabase Database) CreateService(
        string tier = "standard",
        bool requestSpeakerInfo = true,
        bool writeMarkdown = true,
        FakeAsrProvider? provider = null)
    {
        var database = new MeetCapDatabase(DatabasePath);
        database.EnsureMigrated();

        provider ??= new FakeAsrProvider();
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
                WriteMarkdown = writeMarkdown,
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
                ServiceTier = tier,
                RequestSpeakerInfo = requestSpeakerInfo,
                CostPerHourCny = 0.8,
                ConfigSnapshotJson = "{\"config_version\":1}",
            });

        return (service, processor, provider, database);
    }

    [Fact]
    public async Task Import_TranscribesAFileIntoTimestampedTranscriptArtifacts()
    {
        Migrate();
        var (service, _, provider, _) = CreateService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        var result = await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            Title = "Weekly Meeting",
            SessionId = "ses_test",
            JobId = "job_test",
            ProviderRequestId = "req-test",
        });

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.SegmentCount);
        Assert.Equal("ses_test", result.SessionId);

        var paths = new SessionArtifactPaths(_dataRoot, "ses_test");
        Assert.True(File.Exists(paths.SessionJson));
        Assert.True(File.Exists(paths.EventsJsonl));
        Assert.True(File.Exists(paths.RawTranscriptJsonl));
        Assert.True(File.Exists(paths.LiveTranscriptMarkdown));
        Assert.True(File.Exists(paths.JobRequestJson("job_test")));
        Assert.True(File.Exists(paths.JobResponseJson("job_test")));
        Assert.True(File.Exists(paths.JobNormalizedJsonl("job_test")));

        // The normalized transcript keeps provider timestamps and anonymous labels.
        var segments = new FileTranscriptStore().ReadJsonl(paths.RawTranscriptJsonl);
        Assert.Equal(2, segments.Count);
        Assert.Equal(0, segments[0].StartMs);
        Assert.Equal(3200, segments[0].EndMs);
        Assert.Equal("speaker_1", segments[0].SpeakerLabel);
        Assert.Equal("speaker_2", segments[1].SpeakerLabel);
        Assert.Equal("The quotation needs another review.", segments[0].RawText);
        Assert.Null(segments[0].SpeakerId);
        Assert.Null(segments[0].SpeakerName);

        var markdown = File.ReadAllText(paths.LiveTranscriptMarkdown);
        Assert.Contains("speaker_1", markdown, StringComparison.Ordinal);
        Assert.Contains("The quotation needs another review.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_CreatesANormalSessionWithSourceTypeImportAndCompletesIt()
    {
        Migrate();
        var (service, _, provider, database) = CreateService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
        });

        var session = database.Sessions.Get("ses_test");

        Assert.NotNull(session);
        Assert.Equal("import", session!.SourceType);
        Assert.Equal("import", session.Mode);
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Equal(754_000, session.DurationMs);
        Assert.Equal(new[] { AudioSource.Import }, session.Tracks);
        Assert.Equal("meeting", session.Title);
    }

    [Fact]
    public async Task Import_RecordsTheSourceArtifactMappingAndLeavesTheOriginalUntouched()
    {
        Migrate();
        var (service, _, provider, _) = CreateService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));
        var originalBytes = File.ReadAllBytes(_sourcePath);

        await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
        });

        var paths = new SessionArtifactPaths(_dataRoot, "ses_test");
        var document = new FileSessionArtifactWriter().ReadSessionDocument(paths);

        Assert.NotNull(document);
        var artifact = Assert.Single(document!.SourceArtifacts);
        Assert.Equal(SourceArtifact.Roles.Original, artifact.Role);
        Assert.Equal(_sourcePath, artifact.OriginalPath);
        Assert.Equal(paths.ImportAudioFile("meeting.wav"), artifact.StoredPath);
        Assert.Equal(512, artifact.ByteLength);
        Assert.Equal(ImportSessionService.Sha256OfFile(_sourcePath), artifact.Sha256);

        // read-only source guarantee: the original is copied, never modified in place.
        Assert.Equal(originalBytes, File.ReadAllBytes(_sourcePath));
        Assert.True(File.Exists(artifact.StoredPath));
    }

    [Fact]
    public async Task Import_NormalizesOnlyWhenTheSourceIsNotAlreadyAsrReady()
    {
        Migrate();

        // An m4a/aac stereo source must be normalized.
        _media.SourceDescriptor = path => FakeMediaPipeline.Source(path, "mov,mp4,m4a,3gp,3g2,mj2", "aac", 44100, 2);
        var (service, _, provider, _) = CreateService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        var result = await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_normalized",
            JobId = "job_normalized",
        });

        Assert.True(result.Normalized);
        Assert.Single(_media.Normalizations);
        Assert.Equal(
            Path.Combine(_dataRoot, "sessions", "ses_normalized", "audio", "import", "normalized.wav"),
            result.NormalizedArtifactPath);

        var paths = new SessionArtifactPaths(_dataRoot, "ses_normalized");
        var document = new FileSessionArtifactWriter().ReadSessionDocument(paths);
        Assert.Equal(2, document!.SourceArtifacts.Count);
        Assert.Contains(document.SourceArtifacts, a => a.Role == SourceArtifact.Roles.Normalized);

        // The submitted artifact is the normalized derivative, not the original.
        var submission = Assert.Single(provider.Submissions);
        Assert.EndsWith("normalized.wav", submission.InputArtifactPath, StringComparison.Ordinal);
        Assert.Equal("wav", submission.AudioFormat);

        Assert.Equal(
            "audio/import/normalized.wav",
            new MeetCapDatabase(DatabasePath).AsrJobs.Get("job_normalized")!.InputArtifact);
    }

    [Fact]
    public async Task Import_DoesNotNormalizeAnAlreadyAsrReadySource()
    {
        Migrate();
        var (service, _, provider, _) = CreateService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        var result = await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
        });

        Assert.False(result.Normalized);
        Assert.Empty(_media.Normalizations);
        Assert.EndsWith("meeting.wav", Assert.Single(provider.Submissions).InputArtifactPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransientFailure_IsResumedAfterARestartAndStillProducesTheTranscript()
    {
        Migrate();
        var (failing, _, provider, _) = CreateService();
        provider.OnSubmit = _ => throw new AsrTransientException("http.503", "provider unavailable");

        var first = await failing.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
            ProviderRequestId = "req-test",
        });

        Assert.False(first.Succeeded);
        Assert.Equal(AsrJobStatus.RetryWait, first.JobStatus);
        Assert.Empty(new FileTranscriptStore().ReadJsonl(first.RawTranscriptPath));

        // Everything about the pending work is durable, so a brand-new process (new store
        // instances over the same database) can pick it up.
        SqliteConnection.ClearAllPools();
        var (resumed, resumedProcessor, resumedProvider, resumedDatabase) = CreateService();
        resumedProvider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        var due = resumedDatabase.AsrJobs.Get("job_test")!;
        resumedDatabase.AsrJobs.Update(due with { NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        var results = await resumedProcessor.RunDueAsync(10);

        var outcome = Assert.Single(results);
        Assert.Equal(AsrJobOutcome.Succeeded, outcome.Outcome);

        // The retry reused the persisted provider task id instead of inventing a new one.
        Assert.Equal("req-test", Assert.Single(resumedProvider.Submissions).ProviderRequestId);
        Assert.Equal(2, outcome.Job.AttemptCount);

        var paths = new SessionArtifactPaths(_dataRoot, "ses_test");
        Assert.Equal(2, new FileTranscriptStore().ReadJsonl(paths.RawTranscriptJsonl).Count);
        Assert.Equal(SessionStatus.Completed, resumedDatabase.Sessions.Get("ses_test")!.Status);
    }

    [Fact]
    public async Task PermanentFailure_FailsVisiblyAndKeepsTheSessionRecoverable()
    {
        Migrate();
        var (service, _, provider, database) = CreateService();
        provider.OnSubmit = _ => throw new AsrPermanentException("http.401", "invalid credential");

        var result = await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
        });

        Assert.False(result.Succeeded);
        Assert.Equal(AsrJobStatus.Failed, result.JobStatus);
        Assert.Equal("http.401", result.ErrorCode);

        // The imported audio and the session directory survive, and the session is not
        // marked complete, so the failure is visible and repairable.
        Assert.True(File.Exists(new SessionArtifactPaths(_dataRoot, "ses_test").ImportAudioFile("meeting.wav")));
        Assert.Equal(SessionStatus.Processing, database.Sessions.Get("ses_test")!.Status);
    }

    [Fact]
    public async Task InvalidMedia_FailsBeforeAnySessionIsCreated()
    {
        Migrate();
        var (service, _, _, database) = CreateService();
        _media.SourceDescriptor = _ => throw new MeetCap.Core.Media.MediaProbeException("no audio stream");

        await Assert.ThrowsAsync<MeetCap.Core.Media.MediaProbeException>(
            () => service.ImportAsync(new ImportRequest
            {
                SourcePath = _sourcePath,
                SessionId = "ses_test",
                JobId = "job_test",
            }));

        Assert.Null(database.Sessions.Get("ses_test"));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "sessions", "ses_test")));
    }

    [Fact]
    public async Task MissingSourceFile_FailsBeforeAnySessionIsCreated()
    {
        Migrate();
        var (service, _, _, database) = CreateService();

        await Assert.ThrowsAsync<MeetCap.Core.Media.MediaProbeException>(
            () => service.ImportAsync(new ImportRequest
            {
                SourcePath = Path.Combine(_root, "missing.wav"),
                SessionId = "ses_test",
            }));

        Assert.Null(database.Sessions.Get("ses_test"));
    }

    [Fact]
    public async Task Import_RecordsJobObservabilityMetadata()
    {
        Migrate();
        var (service, _, provider, database) = CreateService(requestSpeakerInfo: true);
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
        });

        var job = database.AsrJobs.Get("job_test")!;

        Assert.Equal("volcengine", job.Provider);
        Assert.Equal("standard", job.Tier);
        Assert.Equal("import", job.Source);
        Assert.Equal(754_000, job.DurationMs);
        Assert.True(job.SpeakerInfoRequested);
        Assert.True(job.SpeakerInfoReturned);
        Assert.NotNull(job.SubmittedAt);
        Assert.NotNull(job.CompletedAt);
        Assert.Equal(0.1676, job.EstimatedCostCny, precision: 3);
        Assert.NotNull(job.RawResponsePath);
        Assert.NotNull(job.NormalizedResultPath);
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public async Task Import_SanitizedRequestMetadata_ContainsNoCredentialsAndNoInlineAudio()
    {
        Migrate();
        var (service, _, provider, _) = CreateService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
        });

        var requestJson = File.ReadAllText(
            new SessionArtifactPaths(_dataRoot, "ses_test").JobRequestJson("job_test"));

        // Structural assertions rather than a substring grep: the persisted metadata must not
        // carry the audio payload or any credential-bearing material, and must record the
        // speaker-info decision and the size of what was uploaded.
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("access_token", out _));
        Assert.False(root.TryGetProperty("Authorization", out _));
        Assert.False(root.TryGetProperty("headers", out _));
        Assert.True(root.GetProperty("speaker_info_requested").GetBoolean());

        var audio = root.GetProperty("audio");
        Assert.False(audio.TryGetProperty("data", out _));
        Assert.Equal("wav", audio.GetProperty("format").GetString());
        Assert.Equal(512, audio.GetProperty("inline_bytes").GetInt64());

        // The source audio itself must not be duplicated into the job metadata.
        var base64 = Convert.ToBase64String(File.ReadAllBytes(_sourcePath));
        Assert.DoesNotContain(base64, requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_NormalizationFailure_LeavesAVisibleRecoverableSessionWithItsDocument()
    {
        // A normalize/inspect failure happens after the session row exists. The durable
        // session.json must exist by then, so the user sees the half-materialized session
        // instead of an orphaned row.
        Migrate();
        _media.SourceDescriptor = path => FakeMediaPipeline.Source(path, "mov,mp4,m4a,3gp,3g2,mj2", "aac", 44100, 2);
        _media.FailNormalization = true;
        var (service, _, _, database) = CreateService();

        await Assert.ThrowsAsync<MeetCap.Core.Media.MediaProbeException>(
            () => service.ImportAsync(new ImportRequest
            {
                SourcePath = _sourcePath,
                SessionId = "ses_test",
                JobId = "job_test",
            }));

        var paths = new SessionArtifactPaths(_dataRoot, "ses_test");
        var document = new FileSessionArtifactWriter().ReadSessionDocument(paths);

        Assert.NotNull(document);
        Assert.Equal("ses_test", document!.SessionId);
        // The source was already copied, so the mapping records it.
        Assert.Equal(SourceArtifact.Roles.Original, Assert.Single(document.SourceArtifacts).Role);
        Assert.True(File.Exists(paths.ImportAudioFile("meeting.wav")));

        // No job was queued and the session stays recoverable rather than silently complete.
        Assert.Empty(database.AsrJobs.ListBySession("ses_test"));
        Assert.Equal(SessionStatus.Processing, database.Sessions.Get("ses_test")!.Status);

        Assert.Contains(
            SessionEvents.SessionCreated,
            File.ReadAllLines(paths.EventsJsonl).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString()));
    }

    [Fact]
    public async Task Import_TierOverrideIsOneShotAndDoesNotAffectTheDefault()
    {
        Migrate();
        var (service, _, provider, database) = CreateService(tier: "standard");
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
            Tier = "idle",
        });

        Assert.Equal("idle", database.AsrJobs.Get("job_test")!.Tier);
        Assert.Equal("idle", Assert.Single(provider.Submissions).ServiceTier);
    }

    [Fact]
    public async Task SessionDirectory_LayoutMatchesTheDocumentedArtifactContract()
    {
        Migrate();
        var (service, _, provider, _) = CreateService();
        provider.EnqueuePoll(AsrPollResult.Completed(new AsrCompletion(ProviderJson, "20000000")));

        await service.ImportAsync(new ImportRequest
        {
            SourcePath = _sourcePath,
            SessionId = "ses_test",
            JobId = "job_test",
        });

        var directory = Path.Combine(_dataRoot, "sessions", "ses_test");
        foreach (var relative in new[]
                 {
                     "session.json",
                     "events.jsonl",
                     Path.Combine("audio", "import", "meeting.wav"),
                     Path.Combine("asr", "jobs", "job_test", "request.json"),
                     Path.Combine("asr", "jobs", "job_test", "response.json"),
                     Path.Combine("asr", "jobs", "job_test", "normalized.jsonl"),
                     Path.Combine("transcript", "raw.jsonl"),
                     Path.Combine("transcript", "live.md"),
                 })
        {
            Assert.True(File.Exists(Path.Combine(directory, relative)), $"missing artifact: {relative}");
        }

        // events.jsonl is the operational record of the import.
        var events = File.ReadAllLines(Path.Combine(directory, "events.jsonl"))
            .Select(line => JsonDocument.Parse(line))
            .Select(doc => doc.RootElement.GetProperty("event").GetString())
            .ToArray();

        Assert.Contains(SessionEvents.SessionCreated, events);
        Assert.Contains(SessionEvents.SourceImported, events);
        Assert.Contains(SessionEvents.AsrJobQueued, events);
        Assert.Contains(SessionEvents.AsrJobSubmitted, events);
        Assert.Contains(SessionEvents.AsrJobCompleted, events);
        Assert.Contains(SessionEvents.SessionCompleted, events);
    }
}
