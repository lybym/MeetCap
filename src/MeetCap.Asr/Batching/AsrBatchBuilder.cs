namespace MeetCap.Asr.Batching;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetCap.Core.Asr;
using MeetCap.Core.Capture;
using MeetCap.Core.Ids;
using MeetCap.Core.Sessions;

/// <summary>Knobs for <see cref="AsrBatchBuilder"/>, populated from configuration.</summary>
public sealed record AsrBatchBuilderOptions
{
    /// <summary>MeetCap data root; batch artifacts are resolved under it.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Provider name recorded on every queued job, e.g. <c>volcengine</c>.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Configured <c>asr.file_batch_seconds</c> (default 300).</summary>
    public int BatchSeconds { get; init; } = 300;

    /// <summary>Configured <c>asr.service_tier</c>.</summary>
    public string ServiceTier { get; init; } = "standard";

    /// <summary>Configured <c>asr.volcengine.request_speaker_info</c>.</summary>
    public bool RequestSpeakerInfo { get; init; } = true;

    public double CostPerHourCny { get; init; } = 0.8;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>One materialized ASR batch of closed capture chunks.</summary>
public sealed record AsrBatch
{
    public required string SessionId { get; init; }

    public required string Source { get; init; }

    /// <summary>1-based batch number within the track, used in the durable file name.</summary>
    public required int Number { get; init; }

    /// <summary>
    /// The chunks this batch was built from. Empty when the batch was reconstructed from
    /// its manifest during recovery, where only the batch's own mapping is needed.
    /// </summary>
    public IReadOnlyList<ClosedAudioChunk> Chunks { get; init; } = Array.Empty<ClosedAudioChunk>();

    /// <summary>Absolute path of the durable batch WAV.</summary>
    public required string FilePath { get; init; }

    /// <summary>Session-relative artifact path stored on the job's <c>input_artifact</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Session-relative position of the batch's first frame.</summary>
    public required long StartMs { get; init; }

    /// <summary>Session-relative exclusive end of the batch's audio.</summary>
    public required long EndMs { get; init; }

    /// <summary>Audio data bytes in the batch, excluding the WAV header.</summary>
    public required long DataBytes { get; init; }

    public long DurationMs => EndMs - StartMs;
}

/// <summary>
/// Builds file-ASR batches from durably closed capture chunks and queues each batch as a
/// persistent ASR job (<c>docs/ROADMAP.md</c> M4, <c>docs/ASR_STRATEGY.md</c> section 3).
/// </summary>
/// <remarks>
/// <para>
/// Capture chunk duration and ASR batch duration are different quantities: chunks are the
/// durability unit (default 60 s) and batches are the provider context unit (default
/// 300 s). This type is the only place the two are related, and it groups by source, so the
/// M5 dual-track path keeps mic and loopback independent without changing the model.
/// </para>
/// <para>
/// Ordering is deliberate and crash-safe: the batch WAV is fully written, validated and
/// renamed into its durable name <em>before</em> the job row is created. A crash anywhere
/// leaves either chunks whose batch was never completed (recovered on the next start) or a
/// durable batch file with no job (also recovered on the next start) — never a queued job
/// whose audio does not exist. The job id is derived from the batch artifact path, so
/// recovery is idempotent and cannot queue the same audio twice.
/// </para>
/// <para>
/// Nothing here performs HTTP. Submission, polling, retry state, and Polly live in
/// <see cref="AsrJobProcessor"/> and the provider adapter, so a network outage only leaves
/// jobs in <c>pending</c>/<c>retry_wait</c> while capture continues untouched
/// (<c>docs/RELIABILITY.md</c> section 9).
/// </para>
/// </remarks>
public sealed class AsrBatchBuilder
{
    private static readonly JsonSerializerOptions s_manifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly IAsrJobStore _jobs;
    private readonly AsrBatchBuilderOptions _options;
    private readonly Dictionary<string, PendingBatch> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextBatchNumber = new(StringComparer.Ordinal);

    /// <summary>
    /// The session's own event sink, so batch events share the recorder's append lock and
    /// the log cannot interleave. Supplied by the composition root once the session exists.
    /// </summary>
    private ISessionEventSink? _events;

    public AsrBatchBuilder(IAsrJobStore jobs, AsrBatchBuilderOptions options)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderName);
        if (options.BatchSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.BatchSeconds,
                "asr.file_batch_seconds must be positive.");
        }
    }

    /// <summary>Batches queued as jobs so far in this process.</summary>
    public int BatchesQueued { get; private set; }

    /// <summary>Chunks buffered into the open batch of each track but not yet queued.</summary>
    public int PendingChunkCount
    {
        get
        {
            var total = 0;
            foreach (var pending in _pending.Values)
            {
                total += pending.Chunks.Count;
            }

            return total;
        }
    }

    /// <summary>Attaches the recording session's event sink for this session.</summary>
    public void AttachEventSink(ISessionEventSink? events) => _events = events;

    /// <summary>
    /// Adds one durably closed capture chunk to its track's open batch, closing and queueing
    /// the batch as soon as the configured window is covered.
    /// </summary>
    /// <returns>The batch this chunk completed, or <c>null</c> when the batch is still open.</returns>
    public AsrBatch? OnChunkClosed(ClosedAudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (!_pending.TryGetValue(chunk.Source, out var pending))
        {
            pending = new PendingBatch(chunk.Format);
            _pending[chunk.Source] = pending;
        }

        pending.Chunks.Add(chunk);

        return pending.DurationMs >= _options.BatchSeconds * 1000L
            ? CompleteBatch(chunk.SessionId, chunk.Source, pending)
            : null;
    }

    /// <summary>
    /// Closes and queues the remaining partial batch of every track
    /// (<c>docs/ROADMAP.md</c> M4: "flush remaining batch at stop").
    /// </summary>
    /// <returns>The batches that were queued by this call.</returns>
    public IReadOnlyList<AsrBatch> FlushPendingBatches()
    {
        var flushed = new List<AsrBatch>();
        foreach (var source in _pending.Keys.ToArray())
        {
            var pending = _pending[source];
            if (pending.Chunks.Count == 0)
            {
                continue;
            }

            flushed.Add(CompleteBatch(pending.Chunks[0].SessionId, source, pending));
        }

        return flushed;
    }

    /// <summary>
    /// Re-queues batches that an earlier process finalized but never turned into a job, and
    /// discards the <c>.part</c> files of batches it never finalized.
    /// </summary>
    /// <remarks>
    /// This is the crash-recovery half of "pending batches survive restart". The ordinary
    /// case — a job row that exists and still needs work — is already handled by
    /// <c>meetcap asr resume</c> reading <c>asr_jobs</c>; this method only covers the narrow
    /// window between a batch file becoming durable and its job row being written.
    /// </remarks>
    /// <returns>The batches that were re-created as jobs.</returns>
    public IReadOnlyList<AsrBatch> RecoverFinalizedBatches(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var paths = new SessionArtifactPaths(_options.DataRoot, sessionId);
        if (!Directory.Exists(paths.AsrBatchesDirectory))
        {
            return Array.Empty<AsrBatch>();
        }

        DiscardOrphanedPartFiles(paths);

        var recovered = new List<AsrBatch>();
        foreach (var file in EnumerateBatchFiles(paths))
        {
            var relativePath = paths.ToRelative(file);
            if (HasJobForArtifact(sessionId, relativePath))
            {
                continue;
            }

            var batch = TryReadBatchManifest(sessionId, file, relativePath);
            if (batch is not null)
            {
                recovered.Add(QueueJob(paths, batch));
            }
        }

        return recovered;
    }

    private void DiscardOrphanedPartFiles(SessionArtifactPaths paths)
    {
        foreach (var part in Directory.EnumerateFiles(
                     paths.AsrBatchesDirectory,
                     "*" + WavBatchConcatenator.PartSuffix,
                     SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A batch file that cannot be discarded is inert: no job references it, and
                // its capture chunks are still durable under audio/.
                continue;
            }

            _events?.Write(new SessionEvent(SessionEvents.AsrBatchDiscarded, 0)
            {
                Detail =
                    "an unfinished batch file was discarded; its capture chunks are still durable " +
                    "and are batched by the next window.",
            });
        }
    }

    private static IEnumerable<string> EnumerateBatchFiles(SessionArtifactPaths paths) =>
        Directory
            .EnumerateFiles(paths.AsrBatchesDirectory, "*.wav", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// Rebuilds the durable half of a batch's own record from its manifest.
    /// </summary>
    /// <remarks>
    /// Only the batch's mapping is needed to re-create the job: which artifact it is, and
    /// where it sits on the session timeline. The per-chunk list stays in the manifest
    /// itself, so a reader that wants the source mapping reads the file.
    /// </remarks>
    private static AsrBatch? TryReadBatchManifest(string sessionId, string batchPath, string relativePath)
    {
        var manifestPath = Path.ChangeExtension(batchPath, ".json");
        if (!File.Exists(manifestPath))
        {
            // A durable WAV with no manifest cannot state its own timeline, so it is not
            // evidence strong enough to queue a billable job from. The chunks it was built
            // from remain durable.
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;

            var source = root.TryGetProperty("source", out var sourceElement)
                ? sourceElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            return new AsrBatch
            {
                SessionId = sessionId,
                Source = source,
                Number = root.TryGetProperty("batch", out var number) && number.TryGetInt32(out var parsed)
                    ? parsed
                    : 0,
                FilePath = batchPath,
                RelativePath = relativePath,
                StartMs = root.TryGetProperty("start_ms", out var start) ? start.GetInt64() : 0,
                EndMs = root.TryGetProperty("end_ms", out var end) ? end.GetInt64() : 0,
                DataBytes = root.TryGetProperty("data_bytes", out var bytes) ? bytes.GetInt64() : 0,
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Materializes a batch, writes its timeline manifest, then queues its job.</summary>
    private AsrBatch CompleteBatch(string sessionId, string source, PendingBatch pending)
    {
        var paths = new SessionArtifactPaths(_options.DataRoot, sessionId);
        var number = NextBatchNumber(paths, source);
        var batchPath = BatchFilePath(paths, source, number);
        var relativePath = paths.ToRelative(batchPath);

        // The audio artifact is durable before anything references it.
        var dataBytes = WavBatchConcatenator.Concatenate(batchPath, pending.Chunks, pending.Format);

        var batch = new AsrBatch
        {
            SessionId = sessionId,
            Source = source,
            Number = number,
            Chunks = pending.Chunks.ToArray(),
            FilePath = batchPath,
            RelativePath = relativePath,
            StartMs = pending.StartMs,
            EndMs = pending.EndMs,
            DataBytes = dataBytes,
        };

        WriteManifest(batch);
        pending.Chunks.Clear();

        return QueueJob(paths, batch);
    }

    /// <summary>
    /// Writes the batch/source timeline mapping: which durable chunks make up this batch,
    /// where each one sits on the session timeline, and which artifact the provider was
    /// given. Without it a batch file could not be traced back to the audio it was built
    /// from, and the original session timeline could not be reconstructed.
    /// </summary>
    private static void WriteManifest(AsrBatch batch)
    {
        var manifestPath = Path.ChangeExtension(batch.FilePath, ".json");
        var directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var chunks = new JsonArray();
        foreach (var chunk in batch.Chunks)
        {
            chunks.Add(new JsonObject
            {
                ["sequence"] = chunk.Sequence,
                ["artifact"] = chunk.RelativePath,
                ["start_ms"] = chunk.StartMs,
                ["end_ms"] = chunk.EndMs,
                ["data_bytes"] = chunk.DataBytes,
            });
        }

        var document = new JsonObject
        {
            ["session_id"] = batch.SessionId,
            ["source"] = batch.Source,
            ["batch"] = batch.Number,
            ["artifact"] = batch.RelativePath,
            ["start_ms"] = batch.StartMs,
            ["end_ms"] = batch.EndMs,
            ["duration_ms"] = batch.DurationMs,
            ["data_bytes"] = batch.DataBytes,
            ["chunks"] = chunks,
        };

        File.WriteAllText(
            manifestPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Creates the persistent job for a materialized batch, plus its events.</summary>
    private AsrBatch QueueJob(SessionArtifactPaths paths, AsrBatch batch)
    {
        if (HasJobForArtifact(batch.SessionId, batch.RelativePath))
        {
            // Idempotent: a job derived from this batch artifact already exists, so the audio
            // must not be queued (and therefore billed) a second time.
            return batch;
        }

        var now = Now();
        var job = new AsrJob
        {
            Id = JobIdFor(batch.RelativePath),
            SessionId = batch.SessionId,
            Source = batch.Source,
            Tier = _options.ServiceTier,
            Provider = _options.ProviderName,
            StartMs = batch.StartMs,
            EndMs = batch.EndMs,
            InputArtifact = batch.RelativePath,
            Status = AsrJobStatus.Pending,
            ProviderRequestId = Ids.NewProviderRequestId(),
            DurationMs = (int)Math.Min(int.MaxValue, batch.DurationMs),
            SpeakerInfoRequested = _options.RequestSpeakerInfo,
            EstimatedCostCny = AsrCostEstimator.Estimate(batch.DurationMs, _options.CostPerHourCny),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _jobs.Create(job);

        _events?.Write(new SessionEvent(SessionEvents.AsrBatchClosed, batch.EndMs)
        {
            Source = batch.Source,
            StartMs = batch.StartMs,
            EndMs = batch.EndMs,
            Count = batch.Chunks.Count,
            Detail =
                $"closed {batch.Chunks.Count} durable chunk(s) into '{batch.RelativePath}' " +
                $"({batch.DurationMs} ms of session audio).",
        });

        _events?.Write(new SessionEvent(SessionEvents.AsrJobQueued, batch.StartMs)
        {
            Source = batch.Source,
            StartMs = batch.StartMs,
            EndMs = batch.EndMs,
            Count = batch.Chunks.Count,
            Detail =
                $"job '{job.Id}' queued for '{batch.RelativePath}'; provider '{job.Provider}', " +
                $"tier '{job.Tier}'. Submission happens off the capture path.",
        });

        BatchesQueued++;
        return batch;
    }

    private bool HasJobForArtifact(string sessionId, string relativePath)
    {
        if (_jobs.Get(JobIdFor(relativePath)) is not null)
        {
            return true;
        }

        return _jobs.ListBySession(sessionId)
            .Any(job => string.Equals(job.InputArtifact, relativePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// Derives the job id from the batch artifact path, so a batch file and its job are
    /// matched deterministically and re-running recovery cannot queue the same audio twice.
    /// </summary>
    internal static string JobIdFor(string relativeBatchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeBatchPath);

        var name = Path.GetFileNameWithoutExtension(relativeBatchPath);
        var sanitized = new string(name
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')
            .ToArray());

        return Ids.JobPrefix + sanitized;
    }

    private int NextBatchNumber(SessionArtifactPaths paths, string source)
    {
        // The number only has to be unique within the session directory, so a per-track counter
        // is enough; it starts from whatever a previous process already wrote, which is what
        // keeps recovery from reusing a batch file's name.
        var counter = _nextBatchNumber.TryGetValue(source, out var known)
            ? known
            : HighestExistingBatchNumber(paths, source);

        var directory = BatchDirectory(paths, source);
        int candidate;
        do
        {
            candidate = ++counter;
        }
        while (File.Exists(BatchFilePath(paths, source, candidate))
               || File.Exists(BatchFilePath(paths, source, candidate) + WavBatchConcatenator.PartSuffix));

        _nextBatchNumber[source] = candidate;
        return candidate;
    }

    private static int HighestExistingBatchNumber(SessionArtifactPaths paths, string source)
    {
        var directory = BatchDirectory(paths, source);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "batch-*.wav*"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("batch-", StringComparison.Ordinal)
                && int.TryParse(name["batch-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                highest = Math.Max(highest, number);
            }
        }

        return highest;
    }

    private static string BatchDirectory(SessionArtifactPaths paths, string source)
        => Path.Combine(paths.AsrBatchesDirectory, source);

    private static string BatchFilePath(SessionArtifactPaths paths, string source, int number)
        => Path.Combine(
            BatchDirectory(paths, source),
            "batch-" + number.ToString("D6", CultureInfo.InvariantCulture) + ".wav");

    private DateTimeOffset Now() => _options.TimeProvider.GetUtcNow();

    /// <summary>Chunks buffered for one track while its batch window is still open.</summary>
    private sealed class PendingBatch
    {
        public PendingBatch(AudioFormat format) => Format = format;

        public AudioFormat Format { get; }

        public List<ClosedAudioChunk> Chunks { get; } = new();

        public long StartMs => Chunks.Count == 0 ? 0 : Chunks[0].StartMs;

        public long EndMs => Chunks.Count == 0 ? 0 : Chunks[^1].EndMs;

        public long DurationMs => EndMs - StartMs;
    }
}
