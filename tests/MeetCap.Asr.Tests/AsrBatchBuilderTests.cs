using System.Text.Json;
using MeetCap.Asr.Batching;
using MeetCap.AudioPipeline;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Asr;
using MeetCap.Core.Capture;
using MeetCap.Core.Ids;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.Asr.Tests;

/// <summary>
/// The M4 batch builder is where capture durability meets provider batching: it must only
/// batch durable chunks, must never queue a job whose audio is not on disk, and must
/// survive a restart without queueing the same audio twice
/// (<c>docs/ROADMAP.md</c> M4, <c>docs/RELIABILITY.md</c> section 6).
/// </summary>
public class AsrBatchBuilderTests : IDisposable
{
    private const string SessionId = "ses_live_1";

    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly AudioFormat s_format = new(48_000, 1, 16, AudioSampleFormat.Pcm);

    private readonly string _dataRoot;
    private readonly InMemoryAsrJobStore _jobs = new();
    private readonly InMemorySessionEventSink _events = new();

    public AsrBatchBuilderTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "meetcap-batch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    private SessionArtifactPaths Paths => new(_dataRoot, SessionId);

    private AsrBatchBuilder CreateBuilder(int batchSeconds = 60) =>
        new(
            _jobs,
            new AsrBatchBuilderOptions
            {
                DataRoot = _dataRoot,
                ProviderName = "volcengine",
                BatchSeconds = batchSeconds,
                RequestSpeakerInfo = true,
                TimeProvider = TimeProvider.System,
            });

    /// <summary>
    /// Writes a real durable chunk file for <paramref name="sessionId"/> and describes it the way
    /// the recorder does. A second session id is what makes a cross-session job-id collision
    /// observable, so the session is a parameter rather than always <see cref="SessionId"/>.
    /// </summary>
    private ClosedAudioChunk CreateChunk(
        string sessionId,
        int sequence,
        long startMs,
        long durationMs,
        byte fill = 0x7f,
        AudioFormat? format = null,
        string source = AudioSources.Mic)
    {
        var effectiveFormat = format ?? s_format;
        var paths = new SessionArtifactPaths(_dataRoot, sessionId);
        var relativePath = Path.Combine("audio", source, $"{sequence:D6}.wav").Replace('\\', '/');
        var finalPath = paths.ResolveRelative(relativePath);
        var partPath = finalPath + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var dataBytes = effectiveFormat.FramesToBytes(effectiveFormat.MillisecondsToFrames(durationMs));
        var capacity = dataBytes + effectiveFormat.BlockAlign;
        using (var writer = new WaveChunkWriter(partPath, finalPath, effectiveFormat, capacity, sequence))
        {
            var buffer = new byte[dataBytes];
            Array.Fill(buffer, fill);
            Assert.Equal(buffer.Length, writer.Append(buffer));
            writer.Close(s_now);
        }

        return new ClosedAudioChunk
        {
            SessionId = sessionId,
            Source = source,
            Sequence = sequence,
            FilePath = finalPath,
            RelativePath = relativePath,
            StartMs = startMs,
            EndMs = startMs + durationMs,
            DataBytes = dataBytes,
            Format = effectiveFormat,
        };
    }

    /// <summary>Writes a chunk for this test's default session.</summary>
    private ClosedAudioChunk CreateChunk(
        int sequence,
        long startMs,
        long durationMs,
        byte fill = 0x7f,
        AudioFormat? format = null,
        string source = AudioSources.Mic)
        => CreateChunk(SessionId, sequence, startMs, durationMs, fill, format, source);

    [Fact]
    public void BatchStaysOpenUntilTheConfiguredWindowIsCovered()
    {
        var builder = CreateBuilder(batchSeconds: 60);

        Assert.Null(builder.OnChunkClosed(CreateChunk(1, 0, 30_000)));
        Assert.Equal(1, builder.PendingChunkCount);
        Assert.Equal(0, builder.BatchesQueued);

        // 60 s of captured audio is exactly the configured window, so the second chunk closes
        // the batch: capture chunk duration and ASR batch duration are independent quantities
        // (docs/ASR_STRATEGY.md section 4).
        var batch = builder.OnChunkClosed(CreateChunk(2, 30_000, 30_000));

        Assert.NotNull(batch);
        Assert.Equal(0, batch!.StartMs);
        Assert.Equal(60_000, batch.EndMs);
        Assert.Equal(2, batch.Chunks.Count);
        Assert.Equal(1, builder.BatchesQueued);
        Assert.Equal(0, builder.PendingChunkCount);
        Assert.Single(_jobs.ListBySession(SessionId));
    }

    [Fact]
    public void BatchArtifactConcatenatesTheDurableChunkAudioInTimelineOrder()
    {
        var builder = CreateBuilder(batchSeconds: 20);
        var first = CreateChunk(1, 0, 10_000, fill: 0x11);
        var second = CreateChunk(2, 10_000, 10_000, fill: 0x22);

        Assert.Null(builder.OnChunkClosed(first));
        var batch = builder.OnChunkClosed(second);

        Assert.NotNull(batch);
        Assert.True(File.Exists(batch!.FilePath), $"expected the batch artifact at {batch.FilePath}");

        // The batch is a real, independently readable WAV: the same validation the capture
        // spool applies to a closed chunk applies to it.
        var validation = WaveChunkValidator.ValidateClosedFile(batch.FilePath, s_format);
        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal(first.DataBytes + second.DataBytes, validation.DataBytes);

        var data = File.ReadAllBytes(batch.FilePath)[WavHeader.Size..];
        Assert.Equal(WavHeader.Size + first.DataBytes + second.DataBytes, new FileInfo(batch.FilePath).Length);
        Assert.All(data[..(int)first.DataBytes], b => Assert.Equal(0x11, b));
        Assert.All(data[(int)first.DataBytes..], b => Assert.Equal(0x22, b));

        // No half-written batch is left behind.
        Assert.Empty(Directory.GetFiles(Paths.AsrBatchesDirectory, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void BatchManifestPreservesTheBatchAndSourceTimelineMapping()
    {
        var builder = CreateBuilder(batchSeconds: 20);
        var first = CreateChunk(1, 0, 10_000);
        var second = CreateChunk(2, 10_000, 10_000);

        builder.OnChunkClosed(first);
        var batch = builder.OnChunkClosed(second)!;

        var manifestPath = Path.ChangeExtension(batch.FilePath, ".json");
        Assert.True(File.Exists(manifestPath), $"expected the batch manifest at {manifestPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.Equal(SessionId, root.GetProperty("session_id").GetString());
        Assert.Equal("mic", root.GetProperty("source").GetString());
        Assert.Equal(batch.RelativePath, root.GetProperty("artifact").GetString());
        Assert.Equal(0, root.GetProperty("start_ms").GetInt64());
        Assert.Equal(20_000, root.GetProperty("end_ms").GetInt64());

        var chunks = root.GetProperty("chunks").EnumerateArray().ToArray();
        Assert.Equal(2, chunks.Length);
        Assert.Equal(first.RelativePath, chunks[0].GetProperty("artifact").GetString());
        Assert.Equal(0, chunks[0].GetProperty("start_ms").GetInt64());
        Assert.Equal(10_000, chunks[0].GetProperty("end_ms").GetInt64());
        Assert.Equal(second.RelativePath, chunks[1].GetProperty("artifact").GetString());
        Assert.Equal(10_000, chunks[1].GetProperty("start_ms").GetInt64());
    }

    [Fact]
    public void EachBatchIsQueuedAsAPendingPersistentJobCarryingItsSessionTimelineOffset()
    {
        var builder = CreateBuilder(batchSeconds: 20);
        builder.OnChunkClosed(CreateChunk(1, 300_000, 10_000));
        builder.OnChunkClosed(CreateChunk(2, 310_000, 10_000));

        var job = Assert.Single(_jobs.ListBySession(SessionId));

        Assert.Equal(AsrJobStatus.Pending, job.Status);
        Assert.Equal("volcengine", job.Provider);
        Assert.Equal("mic", job.Source);
        // `tier` is a schema-compatibility constant, not a configured value (docs/DATA_MODEL.md
        // section 6, issue #26).
        Assert.Equal(AsrJob.StandardTier, job.Tier);
        Assert.Equal(300_000, job.StartMs);
        Assert.Equal(320_000, job.EndMs);
        Assert.Equal(20_000, job.DurationMs);
        Assert.True(job.SpeakerInfoRequested);
        Assert.False(string.IsNullOrWhiteSpace(job.ProviderRequestId));
        Assert.StartsWith("asr/batches/mic/", job.InputArtifact, StringComparison.Ordinal);
        Assert.True(File.Exists(Paths.ResolveRelative(job.InputArtifact)));
    }

    [Fact]
    public void TwoConcurrentSourcesAreBatchedIndependentlyPerSource()
    {
        // An online session closes mic and loopback chunks concurrently, so the builder has to
        // keep one open window per source and complete them independently: a loopback chunk
        // must neither advance nor truncate the microphone's window, and each source's batch
        // artifact and job must name its own source (docs/ARCHITECTURE.md section 7.2,
        // docs/ROADMAP.md M5).
        var builder = CreateBuilder(batchSeconds: 20);

        // Interleaved arrival, both tracks starting at session-relative 0.
        Assert.Null(builder.OnChunkClosed(CreateChunk(1, 0, 10_000, source: AudioSources.Mic)));
        Assert.Null(builder.OnChunkClosed(CreateChunk(1, 0, 10_000, source: AudioSources.Loopback)));

        // The microphone window reaches 20 s of its own audio, so it closes on the mic chunk
        // alone — the interleaved loopback chunks contributed nothing to it.
        var micBatch = builder.OnChunkClosed(CreateChunk(2, 10_000, 10_000, source: AudioSources.Mic));

        Assert.NotNull(micBatch);
        Assert.Equal(AudioSources.Mic, micBatch!.Source);
        Assert.StartsWith("asr/batches/mic/", micBatch.RelativePath, StringComparison.Ordinal);
        Assert.Equal(0, micBatch.StartMs);
        Assert.Equal(20_000, micBatch.EndMs);
        Assert.Equal(new[] { "mic", "mic" }, micBatch.Chunks.Select(c => c.Source));
        Assert.True(File.Exists(micBatch.FilePath), $"expected the batch artifact at {micBatch.FilePath}");

        // One more mic chunk starts a fresh window. Closing the loopback window must not
        // touch it.
        Assert.Null(builder.OnChunkClosed(CreateChunk(3, 20_000, 10_000, source: AudioSources.Mic)));

        // The loopback window is separate: it closes on the loopback chunk, to the same
        // session-relative span but its own artifact and its own numbering.
        var loopbackBatch = builder.OnChunkClosed(CreateChunk(2, 10_000, 10_000, source: AudioSources.Loopback));

        Assert.NotNull(loopbackBatch);
        Assert.Equal(AudioSources.Loopback, loopbackBatch!.Source);
        Assert.StartsWith("asr/batches/loopback/", loopbackBatch.RelativePath, StringComparison.Ordinal);
        Assert.Equal(0, loopbackBatch.StartMs);
        Assert.Equal(20_000, loopbackBatch.EndMs);
        Assert.Equal(new[] { "loopback", "loopback" }, loopbackBatch.Chunks.Select(c => c.Source));
        Assert.NotEqual(micBatch.RelativePath, loopbackBatch.RelativePath);
        Assert.True(File.Exists(loopbackBatch.FilePath), $"expected the batch artifact at {loopbackBatch.FilePath}");

        // Two batches are queued — one per source — and the microphone's fresh window did not
        // absorb the loopback chunks: it still holds exactly its own single chunk.
        Assert.Equal(2, builder.BatchesQueued);
        Assert.Equal(1, builder.PendingChunkCount);

        // The remaining partial window is flushed as that source's own batch, never merged
        // across tracks.
        var flushed = builder.FlushPendingBatches();

        Assert.Equal(3, builder.BatchesQueued);
        var flushedMic = Assert.Single(flushed);
        Assert.Equal(AudioSources.Mic, flushedMic.Source);
        Assert.Equal(20_000, flushedMic.StartMs);
        Assert.Equal(30_000, flushedMic.EndMs);
        Assert.All(flushedMic.Chunks, c => Assert.Equal(AudioSources.Mic, c.Source));

        // One queued job per (source, window), each pointing at that source's artifact, and
        // each with its own job id. Batch numbering restarts at 1 per track, so a job id
        // derived from the batch file name alone would collide here and the second track's job
        // would silently replace the first (asr_jobs.id is the primary key), leaving one track
        // never transcribed at all.
        var jobs = _jobs.ListBySession(SessionId);
        Assert.Equal(3, jobs.Count);
        Assert.Equal(2, jobs.Count(j => j.Source == AudioSources.Mic));
        Assert.Equal(1, jobs.Count(j => j.Source == AudioSources.Loopback));
        Assert.Equal(3, jobs.Select(j => j.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(jobs.Where(j => j.Source == AudioSources.Mic), j => Assert.StartsWith("asr/batches/mic/", j.InputArtifact, StringComparison.Ordinal));
        Assert.All(jobs.Where(j => j.Source == AudioSources.Loopback), j => Assert.StartsWith("asr/batches/loopback/", j.InputArtifact, StringComparison.Ordinal));

        // The two tracks' first windows are both numbered 1 but must not share an id.
        var micFirst = jobs.Single(j => j.Source == AudioSources.Mic && j.InputArtifact.EndsWith("batch-000001.wav", StringComparison.Ordinal));
        var loopbackFirst = jobs.Single(j => j.Source == AudioSources.Loopback && j.InputArtifact.EndsWith("batch-000001.wav", StringComparison.Ordinal));
        Assert.NotEqual(micFirst.Id, loopbackFirst.Id);

        // Each job still points at a batch artifact that exists, so nothing was overwritten.
        Assert.True(File.Exists(Paths.ResolveRelative(micFirst.InputArtifact)));
        Assert.True(File.Exists(Paths.ResolveRelative(loopbackFirst.InputArtifact)));
    }

    [Fact]
    public void FlushingQueuesThePartialBatchSoAudioAfterTheLastFullWindowStillReachesTheProvider()
    {
        var builder = CreateBuilder(batchSeconds: 300);
        builder.OnChunkClosed(CreateChunk(1, 0, 60_000));
        builder.OnChunkClosed(CreateChunk(2, 60_000, 60_000));

        Assert.Empty(_jobs.ListBySession(SessionId));

        var flushed = builder.FlushPendingBatches();

        var batch = Assert.Single(flushed);
        Assert.Equal(0, batch.StartMs);
        Assert.Equal(120_000, batch.EndMs);
        Assert.Equal(2, batch.Chunks.Count);

        var job = Assert.Single(_jobs.ListBySession(SessionId));
        Assert.Equal(120_000, job.EndMs);

        // A second flush has nothing left to queue, so a stop cannot double-bill the window.
        Assert.Empty(builder.FlushPendingBatches());
        Assert.Single(_jobs.ListBySession(SessionId));
    }

    [Fact]
    public void JobIdFor_IsUniquePerSessionAndPerTrackWhenBothUseTheSameBatchNumber()
    {
        // Batch numbers restart at 1 for every track *and* for every session, while asr_jobs.id
        // is the primary key of one table shared by all sessions in a data root. The id
        // therefore has to carry both scopes, or two sessions collide on the same row.
        var mic = AsrBatchBuilder.JobIdFor(SessionId, AudioSources.Mic, "asr/batches/mic/batch-000001.wav");
        var loopback = AsrBatchBuilder.JobIdFor(SessionId, AudioSources.Loopback, "asr/batches/loopback/batch-000001.wav");
        var otherSession = AsrBatchBuilder.JobIdFor("ses_other_1", AudioSources.Mic, "asr/batches/mic/batch-000001.wav");

        Assert.NotEqual(mic, loopback);
        Assert.NotEqual(mic, otherSession);
        Assert.Contains(AudioSources.Mic, mic, StringComparison.Ordinal);
        Assert.Contains(AudioSources.Loopback, loopback, StringComparison.Ordinal);
        Assert.Contains(SessionId, mic, StringComparison.Ordinal);
        Assert.Contains("ses_other_1", otherSession, StringComparison.Ordinal);

        // Deterministic, so the same artifact always maps to the same job.
        Assert.Equal(mic, AsrBatchBuilder.JobIdFor(SessionId, AudioSources.Mic, "asr/batches/mic/batch-000001.wav"));
    }

    [Fact]
    public void ASecondSessionInTheSameDataRootStillQueuesItsOwnBatchOne()
    {
        // The regression test for the cross-session collision. Every other batching test uses one
        // session id, so an id that is unique per (source, batch number) but not per session looks
        // correct. Here two sessions share one job store — exactly what the single meetcap.db
        // does — and each must end up with its own job for its own batch-000001.
        //
        // Each session gets its own builder, as production does: a builder is constructed per
        // recording/batch region, and its batch counter is per (builder, source).
        const string alpha = "ses_alpha_1";
        const string beta = "ses_beta_1";

        var alphaBuilder = CreateBuilder(batchSeconds: 20);
        var alphaBatch = QueueOneWindow(alphaBuilder, alpha, AudioSources.Mic, fill: 0x11);

        var betaBuilder = CreateBuilder(batchSeconds: 20);
        var betaBatch = QueueOneWindow(betaBuilder, beta, AudioSources.Mic, fill: 0x22);

        // Both sessions produced their own batch-000001 — the artifact name is only unique within
        // a session — and both batches were reported as queued.
        Assert.NotNull(alphaBatch);
        Assert.NotNull(betaBatch);
        Assert.Equal("asr/batches/mic/batch-000001.wav", alphaBatch!.RelativePath);
        Assert.Equal(alphaBatch.RelativePath, betaBatch!.RelativePath);
        Assert.NotEqual(alphaBatch.FilePath, betaBatch.FilePath);

        // ...and each session really has its own job, pointing at its own artifact. Before the id
        // carried the session, beta's lookup found alpha's row, took the idempotency branch, and
        // left beta with no job at all while still counting the batch as queued.
        var alphaJob = Assert.Single(_jobs.ListBySession(alpha));
        var betaJob = Assert.Single(_jobs.ListBySession(beta));
        Assert.NotEqual(alphaJob.Id, betaJob.Id);
        Assert.Equal(alpha, alphaJob.SessionId);
        Assert.Equal(beta, betaJob.SessionId);
        Assert.Equal(alphaBatch.RelativePath, alphaJob.InputArtifact);
        Assert.Equal(betaBatch.RelativePath, betaJob.InputArtifact);
        Assert.Equal(AsrJobStatus.Pending, alphaJob.Status);
        Assert.Equal(AsrJobStatus.Pending, betaJob.Status);

        // Each job's durable batch artifact is the one its own session built.
        Assert.True(File.Exists(alphaBatch.FilePath));
        Assert.True(File.Exists(betaBatch.FilePath));
        Assert.NotEqual(alphaBatch.FilePath, betaBatch.FilePath);

        // Recovery stays idempotent for both: a re-run queues nothing and removes no row.
        Assert.Empty(alphaBuilder.RecoverFinalizedBatches(alpha));
        Assert.Empty(betaBuilder.RecoverFinalizedBatches(beta));
        Assert.Single(_jobs.ListBySession(alpha));
        Assert.Single(_jobs.ListBySession(beta));
    }

    /// <summary>
    /// Closes one 20 s batch window for <paramref name="sessionId"/> and returns the batch the
    /// builder reported as queued.
    /// </summary>
    private AsrBatch? QueueOneWindow(
        AsrBatchBuilder builder,
        string sessionId,
        string source,
        byte fill)
    {
        builder.OnChunkClosed(CreateChunk(sessionId, 1, 0, 10_000, fill: fill, source: source));
        return builder.OnChunkClosed(CreateChunk(sessionId, 2, 10_000, 10_000, fill: fill, source: source));
    }

    [Fact]
    public void RecoveryStillRecognisesAJobQueuedBeforeTheIdCarriedItsSource()
    {
        // An M4-era row was derived from the batch file name alone (job_batch-000001). Recovery
        // must still see that batch as already queued — otherwise it would queue (and bill) the
        // same audio a second time. This is a back-compatibility test: the dedup works through
        // the session-scoped input_artifact match, not through the id.
        var builder = CreateBuilder(batchSeconds: 20);
        builder.OnChunkClosed(CreateChunk(1, 0, 10_000));
        var batch = builder.OnChunkClosed(CreateChunk(2, 10_000, 10_000))!;

        // Replace the modern job with one carrying the legacy id, exactly as an existing data
        // root from M4 would hold it.
        foreach (var job in _jobs.ListBySession(SessionId))
        {
            _jobs.Remove(job.Id);
        }

        _jobs.Create(new AsrJob
        {
            Id = Ids.JobPrefix + "batch-000001",
            SessionId = SessionId,
            Source = AudioSources.Mic,
            Provider = "volcengine",
            StartMs = batch.StartMs,
            EndMs = batch.EndMs,
            InputArtifact = batch.RelativePath,
            Status = AsrJobStatus.Pending,
            ProviderRequestId = "legacy-request-id",
            DurationMs = 20_000,
            CreatedAt = s_now,
            UpdatedAt = s_now,
        });

        Assert.Empty(builder.RecoverFinalizedBatches(SessionId));
        Assert.Single(_jobs.ListBySession(SessionId));
    }

    [Fact]
    public void RecoveryQueuesABatchThatWasFinalizedButNeverTurnedIntoAJob()
    {
        var builder = CreateBuilder(batchSeconds: 20);
        builder.OnChunkClosed(CreateChunk(1, 0, 10_000));
        var batch = builder.OnChunkClosed(CreateChunk(2, 10_000, 10_000))!;

        // Model the crash window the durable-before-queue ordering creates: the batch audio is
        // on disk, but the process died before the job row was written, so a fresh process
        // sees a durable batch with no job.
        foreach (var job in _jobs.ListBySession(SessionId))
        {
            _jobs.Remove(job.Id);
        }

        Assert.Empty(_jobs.ListBySession(SessionId));

        var recovered = builder.RecoverFinalizedBatches(SessionId);

        var recreated = Assert.Single(recovered);
        Assert.Equal(batch.RelativePath, recreated.RelativePath);
        Assert.Equal(batch.StartMs, recreated.StartMs);
        Assert.Equal(batch.EndMs, recreated.EndMs);

        var recreatedJob = Assert.Single(_jobs.ListBySession(SessionId));
        Assert.Equal(batch.RelativePath, recreatedJob.InputArtifact);
        Assert.Equal(AsrJobStatus.Pending, recreatedJob.Status);

        // Recovery is idempotent: running it again must not queue the same audio twice.
        Assert.Empty(builder.RecoverFinalizedBatches(SessionId));
        Assert.Single(_jobs.ListBySession(SessionId));
    }

    [Fact]
    public void RecoveryDiscardsAnUnfinishedBatchPartAndRecordsWhy()
    {
        var builder = CreateBuilder(batchSeconds: 300);
        builder.AttachEventSink(_events);
        builder.OnChunkClosed(CreateChunk(1, 0, 60_000));

        // A batch WAV that a killed process left half-written is inert: no job references it
        // and its capture chunks are still durable under audio/.
        var directory = Path.Combine(Paths.AsrBatchesDirectory, "mic");
        Directory.CreateDirectory(directory);
        var partPath = Path.Combine(directory, "batch-000001.wav.part");
        File.WriteAllBytes(partPath, new byte[WavHeader.Size + 16]);

        Assert.Empty(builder.RecoverFinalizedBatches(SessionId));
        Assert.False(File.Exists(partPath));
        Assert.Contains(
            _events.Events,
            e => string.Equals(e.Name, SessionEvents.AsrBatchDiscarded, StringComparison.Ordinal));
    }

    [Fact]
    public void AChunkInADifferentFormatIsRejectedInsteadOfChangingHowTheAudioDecodes()
    {
        // The guard in WavBatchConcatenator: concatenating a chunk whose header describes a
        // different sample rate / channel count / sample format would produce a file that is
        // internally consistent and plays at the wrong speed, with nothing in the artifact saying
        // so. It has to fail loudly, and the builder has to contain that failure like any other
        // unreadable chunk.
        var builder = CreateBuilder(batchSeconds: 20);
        builder.AttachEventSink(_events);

        var good = CreateChunk(1, 0, 10_000);

        var otherFormat = new AudioFormat(16_000, 1, 16, AudioSampleFormat.Pcm);
        var mismatched = CreateChunk(2, 10_000, 10_000, format: otherFormat);

        Assert.Null(builder.OnChunkClosed(good));
        var salvaged = builder.OnChunkClosed(mismatched);

        // The mismatched chunk is dropped, its window is recorded, and the good chunk is still
        // submitted: the failure is contained to the chunk that caused it.
        Assert.NotNull(salvaged);
        Assert.Equal(good.RelativePath, Assert.Single(salvaged!.Chunks).RelativePath);
        Assert.Equal(1, builder.UnreadableChunks);
        Assert.Equal(10_000, builder.DroppedAudioMs);

        var failure = Assert.Single(
            _events.Events,
            e => string.Equals(e.Name, SessionEvents.AsrBatchFailed, StringComparison.Ordinal));
        Assert.Equal("chunk_unreadable", failure.Reason);
        Assert.Contains("Mixing formats", failure.Detail!, StringComparison.Ordinal);

        // And no batch artifact was left claiming to hold audio it decoded differently.
        var batch = Assert.Single(Directory.GetFiles(Paths.AsrBatchesDirectory, "*.wav", SearchOption.AllDirectories));
        var validation = WaveChunkValidator.ValidateClosedFile(batch, s_format);
        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal(good.DataBytes, validation.DataBytes);
    }

    [Fact]
    public void ABatchWindowThatCannotBeMaterializedDoesNotBlockLaterWindows()
    {
        // A chunk that is missing or not readable audio is exactly what an aborted capture
        // write leaves behind. The window cannot reach the provider, but batching must advance
        // past it: one poisoned chunk must not stop the track's transcription for the rest of
        // the meeting (issue #6 hard constraint: a stalled ASR queue must not produce unbounded
        // growth). The rest of the window is retried, so only the unreadable chunk is missing.
        var builder = CreateBuilder(batchSeconds: 20);
        builder.AttachEventSink(_events);

        var first = CreateChunk(1, 0, 10_000);
        var second = CreateChunk(2, 10_000, 10_000);
        Assert.Null(builder.OnChunkClosed(first));

        // The chunk goes away between being announced and the window closing.
        File.Delete(second.FilePath);

        // The remaining chunk is retried on its own and still reaches the provider: the window
        // is not discarded along with the chunk that could not be read.
        var salvaged = builder.OnChunkClosed(second);

        Assert.NotNull(salvaged);
        Assert.Equal(first.RelativePath, Assert.Single(salvaged!.Chunks).RelativePath);
        Assert.Equal(0, builder.PendingChunkCount);
        Assert.Equal(1, builder.UnreadableChunks);
        Assert.Equal(10_000, builder.DroppedAudioMs);

        var failure = Assert.Single(
            _events.Events,
            e => string.Equals(e.Name, SessionEvents.AsrBatchFailed, StringComparison.Ordinal));
        Assert.Equal("chunk_unreadable", failure.Reason);
        Assert.Contains("still on disk", failure.Detail!, StringComparison.Ordinal);

        // The next window batches normally, which is the whole point.
        var third = CreateChunk(3, 20_000, 10_000);
        var fourth = CreateChunk(4, 30_000, 10_000);
        Assert.Null(builder.OnChunkClosed(third));
        var next = builder.OnChunkClosed(fourth);

        Assert.NotNull(next);
        Assert.Equal(20_000, next!.StartMs);
        Assert.Equal(40_000, next.EndMs);
        Assert.Equal(2, _jobs.ListBySession(SessionId).Count);
        Assert.Equal(2, builder.BatchesQueued);

        // The pending window is bounded by the batch window, not by the number of failures.
        Assert.Equal(0, builder.PendingChunkCount);
    }

    [Fact]
    public void RepeatedMaterializationFailureLeavesThePendingWindowBounded()
    {
        // The unbounded-growth half of the same defect, driven through the CLI-shaped path
        // (OnChunkClosed) rather than only through FlushPendingBatches.
        var builder = CreateBuilder(batchSeconds: 20);
        builder.AttachEventSink(_events);

        var sequence = 1;
        var queued = 0;
        for (var window = 0; window < 20; window++)
        {
            var first = CreateChunk(sequence++, window * 20_000, 10_000);
            var second = CreateChunk(sequence++, window * 20_000 + 10_000, 10_000);
            Assert.Null(builder.OnChunkClosed(first));
            File.Delete(second.FilePath);
            if (builder.OnChunkClosed(second) is not null)
            {
                queued++;
            }

            Assert.True(
                builder.PendingChunkCount <= 2,
                $"the open batch window grew to {builder.PendingChunkCount} chunks after {window + 1} failures");
        }

        // Every window advanced: the unreadable chunk was dropped and the good chunk was queued,
        // so no window is stuck and nothing accumulates.
        Assert.Equal(20, queued);
        Assert.Equal(0, builder.PendingChunkCount);
        Assert.Equal(20, builder.UnreadableChunks);
        Assert.Equal(200_000, builder.DroppedAudioMs);
        Assert.Equal(20, _jobs.ListBySession(SessionId).Count);
        Assert.Equal(
            20,
            _events.Events.Count(e => string.Equals(e.Name, SessionEvents.AsrBatchFailed, StringComparison.Ordinal)));
    }

    [Fact]
    public void AManifestThatCannotBeWrittenIsContainedLikeAnyOtherWriteFailure()
    {
        // The manifest is the second write site of a window (`File.WriteAllText`), and it used to
        // sit outside the containment that covers the batch WAV. On a full disk, a read-only
        // artifact directory or a permissions problem it threw out of `OnChunkClosed` and travelled
        // to the recording's chunk-close path, where RegisterStorageFailure turns it into
        // `degraded` + `end_reason=storage_error` + SessionStatus.Interrupted and makes
        // `meetcap start` exit 1 — for a transcript-layer write, which is exactly what
        // docs/ARCHITECTURE.md section 10.2 promises cannot happen.
        //
        // A directory in place of `batch-NNNNNN.json` makes File.WriteAllText fail deterministically
        // on Windows and Linux, so no permission trickery is needed.
        var builder = CreateBuilder(batchSeconds: 20);
        builder.AttachEventSink(_events);

        var first = CreateChunk(1, 0, 10_000);
        var second = CreateChunk(2, 10_000, 10_000);
        Assert.Null(builder.OnChunkClosed(first));

        var manifestDirectory = Path.Combine(Paths.AsrBatchesDirectory, "mic", "batch-000001.json");
        Directory.CreateDirectory(manifestDirectory);

        // No exception may leave the builder: it contains its own failures.
        var failed = builder.OnChunkClosed(second);

        Assert.Null(failed);

        // The failure is recorded with the documented reason, not passed silently.
        var failure = Assert.Single(
            _events.Events,
            e => string.Equals(e.Name, SessionEvents.AsrBatchFailed, StringComparison.Ordinal));
        Assert.Equal("batch_write_failed", failure.Reason);
        Assert.Contains("batch-000001.json", failure.Detail!, StringComparison.Ordinal);

        // Nothing is dropped: the whole window is still pending so its audio is retried.
        Assert.Equal(2, builder.PendingChunkCount);
        Assert.Equal(0, builder.UnreadableChunks);
        Assert.Equal(0, builder.DroppedAudioMs);
        Assert.Empty(_jobs.ListBySession(SessionId));

        // And no manifest-less batch WAV is left behind for recovery to trip over: a WAV without
        // its manifest cannot state its own timeline, so TryReadBatchManifest refuses it and
        // RecoverFinalizedBatches could never queue it.
        Assert.Empty(Directory.GetFiles(Paths.AsrBatchesDirectory, "*.wav", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Paths.AsrBatchesDirectory, "*.part", SearchOption.AllDirectories));

        // Once the obstruction is gone the same window is materialized and queued normally, which
        // is what "the window stays pending and is retried" has to mean.
        Directory.Delete(manifestDirectory);

        var recovered = builder.FlushPendingBatches();

        var batch = Assert.Single(recovered);
        Assert.Equal(0, batch.StartMs);
        Assert.Equal(20_000, batch.EndMs);
        Assert.Equal(2, batch.Chunks.Count);
        Assert.True(File.Exists(Path.ChangeExtension(batch.FilePath, ".json")));
        Assert.Single(_jobs.ListBySession(SessionId));
        Assert.Equal(0, builder.PendingChunkCount);
    }

    [Fact]
    public void APersistentWriteFailureKeepsEveryChunkAndSpansTheWholeFailedStretch()
    {
        // The write-failure branch, which the unreadable-chunk test above does not reach: every
        // iteration there deletes a chunk, i.e. exercises `reason=chunk_unreadable`. Here the
        // failure is a write site (the manifest path), which is the branch that deliberately
        // retains the window instead of dropping anything.
        //
        // This test pins what the branch actually does, and therefore what
        // docs/ARCHITECTURE.md section 10.2 has to say: nothing is lost or discarded, no
        // exception leaves the builder, and the retained window spans the whole failed stretch,
        // so recovering after a long failure submits that stretch as one request.
        var builder = CreateBuilder(batchSeconds: 20);
        builder.AttachEventSink(_events);

        var batchDirectory = Path.Combine(Paths.AsrBatchesDirectory, "mic");
        Directory.CreateDirectory(batchDirectory);

        // Obstruct the manifest path for the whole streak. A directory in place of a file makes
        // File.WriteAllText fail deterministically on Windows and Linux, and because the directory
        // survives the attempt it blocks whatever batch number the builder chooses next, which
        // keeps this test independent of the numbering it is not about.
        var obstruction = Path.Combine(batchDirectory, "batch-000001.json");
        Directory.CreateDirectory(obstruction);

        var sequence = 1;
        var windows = 10;
        for (var window = 1; window <= windows; window++)
        {
            var first = CreateChunk(sequence++, (window - 1) * 20_000, 10_000);
            var second = CreateChunk(sequence++, (window - 1) * 20_000 + 10_000, 10_000);

            // Neither call throws: no exception may leave the builder, at either write site.
            Assert.Null(builder.OnChunkClosed(first));
            Assert.Null(builder.OnChunkClosed(second));

            // Every attempt is reported with the write reason, and the retained window is retried
            // at each later threshold crossing, so at least one record exists per window.
            var failures = _events.Events
                .Where(e => string.Equals(e.Name, SessionEvents.AsrBatchFailed, StringComparison.Ordinal))
                .ToArray();
            Assert.True(
                failures.Length >= window,
                $"window {window}: expected at least {window} failure record(s), found {failures.Length}");
            Assert.All(failures, e => Assert.Equal("batch_write_failed", e.Reason));

            // Nothing usable is left on disk: the batch WAV is removed with the manifest it
            // cannot be paired with, because RecoverFinalizedBatches refuses a manifest-less WAV.
            Assert.Empty(Directory.GetFiles(batchDirectory, "*.wav"));
        }

        Assert.All(
            _events.Events.Where(e => string.Equals(e.Name, SessionEvents.AsrBatchFailed, StringComparison.Ordinal)),
            e => Assert.Equal("batch_write_failed", e.Reason));
        Assert.Equal(0, builder.UnreadableChunks);
        Assert.Equal(0, builder.DroppedAudioMs);
        Assert.Empty(_jobs.ListBySession(SessionId));

        // Every chunk is still retained: a write failure never drops audio. The window is retried
        // at each later threshold crossing and absorbs the chunks that closed in the meantime —
        // this is the consequence the docs name, and it is why recovering after a long outage
        // submits the whole stretch as one request.
        Assert.Equal(2 * windows, builder.PendingChunkCount);

        // Remove the obstruction and let the accumulated window through: it is submitted as one
        // request covering the entire failed stretch, and nothing is missing from it.
        Directory.Delete(obstruction);

        var recovered = builder.FlushPendingBatches();

        var batch = Assert.Single(recovered);
        Assert.Equal(2 * windows, batch.Chunks.Count);
        Assert.Equal(0, batch.StartMs);
        Assert.Equal(windows * 20_000, batch.EndMs);
        Assert.Single(_jobs.ListBySession(SessionId));
        Assert.Equal(0, builder.PendingChunkCount);
        Assert.True(File.Exists(Path.ChangeExtension(batch.FilePath, ".json")));

        // The retry kept the failed window's own number instead of advancing past it: the session
        // has one batch artifact, numbered 1, with no gap in the sequence.
        Assert.Equal("asr/batches/mic/batch-000001.wav", batch.RelativePath);
    }

    [Fact]
    public void AChunkWithNoBatchFileYetQueuesNothingOnRecovery()
    {
        var builder = CreateBuilder(batchSeconds: 300);
        builder.OnChunkClosed(CreateChunk(1, 0, 60_000));

        // Only a complete window (or an explicit flush at stop) is allowed to reach the
        // provider, so an open window must survive recovery as an open window.
        Assert.Empty(builder.RecoverFinalizedBatches(SessionId));
        Assert.Empty(_jobs.ListBySession(SessionId));
    }

    [Fact]
    public void ABatchClosesOnCapturedAudioTimeAndCarriesAHoleInTheSessionTimeline()
    {
        var builder = CreateBuilder(batchSeconds: 60);

        // A device outage at 30 s: the next chunk resumes 120 s later on the session timeline.
        // The window closes on captured audio time, not on wall-clock span, so the batch still
        // holds exactly 60 s of speech. The missing stretch cannot be represented inside one
        // file, so the batch's own span is longer than its audio and the batch manifest is what
        // states where each chunk really sits (docs/RELIABILITY.md section 7: gaps are never
        // hidden by shifting timestamps).
        builder.OnChunkClosed(CreateChunk(1, 0, 30_000));
        var batch = builder.OnChunkClosed(CreateChunk(2, 150_000, 30_000));

        Assert.NotNull(batch);
        Assert.Equal(0, batch!.StartMs);
        Assert.Equal(180_000, batch.EndMs);
        Assert.Equal(60_000, batch.Chunks.Sum(c => c.DurationMs));

        var manifestPath = Path.ChangeExtension(batch.FilePath, ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var chunks = document.RootElement.GetProperty("chunks").EnumerateArray().ToArray();
        Assert.Equal(150_000, chunks[1].GetProperty("start_ms").GetInt64());
    }
}
