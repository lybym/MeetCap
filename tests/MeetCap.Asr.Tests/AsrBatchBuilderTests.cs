using System.Text.Json;
using MeetCap.Asr.Batching;
using MeetCap.AudioPipeline;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Asr;
using MeetCap.Core.Capture;
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
                ServiceTier = "standard",
                RequestSpeakerInfo = true,
                TimeProvider = TimeProvider.System,
            });

    /// <summary>Writes a real durable chunk file and describes it the way the recorder does.</summary>
    private ClosedAudioChunk CreateChunk(
        int sequence,
        long startMs,
        long durationMs,
        byte fill = 0x7f,
        AudioFormat? format = null)
    {
        var effectiveFormat = format ?? s_format;
        var relativePath = Path.Combine("audio", "mic", $"{sequence:D6}.wav").Replace('\\', '/');
        var finalPath = Paths.ResolveRelative(relativePath);
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
            SessionId = SessionId,
            Source = "mic",
            Sequence = sequence,
            FilePath = finalPath,
            RelativePath = relativePath,
            StartMs = startMs,
            EndMs = startMs + durationMs,
            DataBytes = dataBytes,
            Format = effectiveFormat,
        };
    }

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
        Assert.Equal("standard", job.Tier);
        Assert.Equal(300_000, job.StartMs);
        Assert.Equal(320_000, job.EndMs);
        Assert.Equal(20_000, job.DurationMs);
        Assert.True(job.SpeakerInfoRequested);
        Assert.False(string.IsNullOrWhiteSpace(job.ProviderRequestId));
        Assert.StartsWith("asr/batches/mic/", job.InputArtifact, StringComparison.Ordinal);
        Assert.True(File.Exists(Paths.ResolveRelative(job.InputArtifact)));
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
