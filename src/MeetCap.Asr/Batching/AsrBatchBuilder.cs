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

    /// <summary>
    /// Provider the batch was built for, read back from its manifest. Null when the manifest
    /// predates the field or the batch was not reconstructed from one.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Service tier the batch was built for, read back from its manifest. Null when the manifest
    /// predates the field or the batch was not reconstructed from one.
    /// </summary>
    public string? Tier { get; init; }

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
    /// Per-track batch number that a failed materialization must reuse, so a retry overwrites the
    /// same artifact name instead of advancing past it.
    /// </summary>
    private readonly Dictionary<string, int> _retryBatchNumber = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Capture chunks this process had to drop from a batch window because they were unreadable.
    /// </summary>
    /// <remarks>
    /// The chunk stays durable under <c>audio/</c>; what is missing is its transcript, and the
    /// drop is recorded as an explicit <c>asr.batch.failed</c> event rather than passed over in
    /// silence (<c>docs/RELIABILITY.md</c> section 2).
    /// </remarks>
    public int UnreadableChunks { get; private set; }

    /// <summary>Session-relative milliseconds of capture that could not be read into any batch.</summary>
    public long DroppedAudioMs { get; private set; }

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
    /// (<c>docs/ROADMAP.md</c> M4: "flush remaining partial batch at stop").
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

            // A window that cannot be built (an unreadable chunk, or a write failure) produces
            // no batch; the builder has already recorded why, and a stop is not the place to
            // turn a transcript gap into a failed recording.
            var batch = CompleteBatch(pending.Chunks[0].SessionId, source, pending);
            if (batch is not null)
            {
                flushed.Add(batch);
            }
        }

        return flushed;
    }

    /// <summary>
    /// Re-queues batches that an earlier process finalized but never turned into a job, and
    /// discards the <c>.part</c> files of batches it never finalized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the crash-recovery half of "pending batches survive restart". The ordinary
    /// case — a job row that exists and still needs work — is already handled by
    /// <c>meetcap asr resume</c> reading <c>asr_jobs</c>; this method only covers the narrow
    /// window between a batch file becoming durable and its job row being written.
    /// </para>
    /// <para>
    /// Static so the restart entry point can run exactly this pass. It is reachable from both
    /// commands an operator would try: <c>meetcap start</c> (which owns the session it is about
    /// to record) and <c>meetcap asr resume</c>, via
    /// <see cref="EnumerateSessionsWithBatchArtifacts"/>. Requiring a *new* recording to recover
    /// the previous one was not a recovery path an operator could find.
    /// </para>
    /// </remarks>
    /// <returns>The batches that were re-created as jobs.</returns>
    public static IReadOnlyList<AsrBatch> RecoverFinalizedBatches(
        string dataRoot,
        string sessionId,
        IAsrJobStore jobs,
        AsrBatchBuilderOptions options,
        ISessionEventSink? events)
    {
        var paths = new SessionArtifactPaths(dataRoot, sessionId);
        var recovered = new List<AsrBatch>();
        if (!Directory.Exists(paths.AsrBatchesDirectory))
        {
            return recovered;
        }

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
                continue;
            }

            events?.Write(new SessionEvent(SessionEvents.AsrBatchDiscarded, 0)
            {
                Detail =
                    "an unfinished batch file was discarded; its capture chunks are still durable " +
                    "and are batched by the next window.",
            });
        }

        foreach (var file in EnumerateBatchFiles(paths))
        {
            var relativePath = paths.ToRelative(file);
            if (HasJobForArtifact(jobs, sessionId, relativePath))
            {
                continue;
            }

            var batch = TryReadBatchManifest(sessionId, file, relativePath);
            if (batch is not null)
            {
                // The batch's own provider/tier, so a recovered orphan is queued for what the
                // recording was actually configured with rather than for whatever the resuming
                // process happens to be using. Falls back to the caller's options when the
                // manifest predates the fields.
                var provenance = options with
                {
                    ProviderName = batch.Provider ?? options.ProviderName,
                    ServiceTier = batch.Tier ?? options.ServiceTier,
                };

                var queued = QueueJob(paths, batch, jobs, provenance, events, onQueued: null);
                if (queued is not null)
                {
                    recovered.Add(queued);
                }
            }
        }

        return recovered;
    }

    /// <summary>
    /// Runs <see cref="RecoverFinalizedBatches(string, string, IAsrJobStore, AsrBatchBuilderOptions, ISessionEventSink?)"/>
    /// for this builder's own session and job store.
    /// </summary>
    /// <returns>The batches that were re-created as jobs.</returns>
    public IReadOnlyList<AsrBatch> RecoverFinalizedBatches(string sessionId) =>
        RecoverFinalizedBatches(_options.DataRoot, sessionId, _jobs, _options, _events);

    /// <summary>
    /// Sessions under a data root that still hold batch artifacts, newest name last.
    /// </summary>
    /// <remarks>
    /// <c>meetcap asr resume</c> has no session id when it is run without <c>--session</c>, so it
    /// discovers the sessions whose batch surface exists on disk. Reading the artifact tree —
    /// rather than the database — is what makes the pass able to see a batch that was finalized
    /// before its job row existed, which is the whole point of the recovery.
    /// </remarks>
    public static IReadOnlyList<string> EnumerateSessionsWithBatchArtifacts(string dataRoot) =>
        SessionArtifactPaths.EnumerateSessionsWithBatchArtifacts(dataRoot);

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
                // Provenance written with the batch. Null for a manifest from a build that did not
                // record it, in which case the caller falls back to its own configuration.
                Provider = ReadOptionalString(root, "provider"),
                Tier = ReadOptionalString(root, "tier"),
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

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Materializes a batch, writes its timeline manifest, then queues its job.</summary>
    /// <remarks>
    /// <para>
    /// No exception may leave this method. A window that cannot be built is a transcript problem:
    /// letting it escape would travel out of the recording's chunk-close path into
    /// <c>RegisterStorageFailure</c>, which marks a healthy recording degraded and ends it
    /// <c>INTERRUPTED</c> and makes <c>meetcap start</c> exit non-zero — for a batch or manifest
    /// write (<c>docs/ARCHITECTURE.md</c> section 10.2, <c>docs/RELIABILITY.md</c> section 2).
    /// </para>
    /// <para>
    /// A chunk-attributable failure drops only the chunk that caused it and retries the rest of
    /// the window. A write failure drops nothing: the whole window stays pending and is retried,
    /// because discarding audio over a full disk or a permissions problem would be worse than
    /// retrying it.
    /// </para>
    /// </remarks>
    /// <returns>The queued batch, or <c>null</c> when the window could not be built.</returns>
    private AsrBatch? CompleteBatch(string sessionId, string source, PendingBatch pending)
    {
        var paths = new SessionArtifactPaths(_options.DataRoot, sessionId);

        while (true)
        {
            var number = NextBatchNumber(paths, source);
            var batchPath = BatchFilePath(paths, source, number);
            var relativePath = paths.ToRelative(batchPath);

            long dataBytes;
            try
            {
                // The audio artifact is durable before anything references it.
                dataBytes = WavBatchConcatenator.Concatenate(batchPath, pending.Chunks, pending.Format);
            }
            catch (AsrBatchMaterializationException ex)
            {
                RecordFailedWindow(sessionId, source, number, relativePath, pending, ex, unreadableChunk: true);

                // Only the chunk that could not be read is dropped, and it is counted here rather
                // than as the whole window: the remaining chunks are retried below, so counting
                // the window would overstate what the transcript is actually missing.
                var dropped = pending.Chunks
                    .Where(chunk => string.Equals(chunk.RelativePath, ex.ChunkPath, StringComparison.Ordinal))
                    .ToArray();
                foreach (var chunk in dropped)
                {
                    DroppedAudioMs += chunk.DurationMs;
                }

                var before = pending.Chunks.Count;
                pending.Drop(ex.ChunkPath);

                // Termination is asserted here rather than assumed from PendingBatch.Drop's exact
                // RelativePath match succeeding: a retry that makes no progress would otherwise
                // spin forever on the recording consumer thread, which is the worst place to hang.
                if (pending.Chunks.Count >= before)
                {
                    _events?.Write(new SessionEvent(SessionEvents.AsrBatchFailed, pending.StartMs)
                    {
                        Source = source,
                        Reason = "chunk_unreadable",
                        Detail =
                            $"the builder could not remove '{ex.ChunkPath}' from the open window, so it " +
                            "stopped retrying that window instead of looping. The window stays pending " +
                            "and is retried by the next closed chunk.",
                    });

                    return null;
                }

                if (pending.Chunks.Count == 0)
                {
                    // Nothing usable is left in this window; the next closed chunk opens a new
                    // one. The caller sees "no batch queued", which is also what it sees for an
                    // open window.
                    return null;
                }

                // The rest of the window is still good audio. Try it again so it is not lost
                // along with the chunk that could not be read.
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The window could not be written (disk, permissions, capacity). No chunk is
                // to blame, so none is dropped: the chunks stay pending and the next window
                // retries them rather than discarding audio over a transient condition.
                RememberRetryNumber(source, number);
                RecordFailedWindow(sessionId, source, number, relativePath, pending, ex, unreadableChunk: false);
                return null;
            }

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

            // The manifest is the window's second write site, and it is inside its own
            // containment for the same reason the first one is: it is a transcript-layer file, so
            // its failure must not reach the recording. The batch WAV is removed with it, because
            // a WAV with no manifest cannot state its own timeline: TryReadBatchManifest refuses
            // it, so RecoverFinalizedBatches could never queue it and it would accumulate on disk
            // as an artifact nothing can use.
            try
            {
                WriteManifest(batch, _options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                var orphan = RemoveUnusableArtifact(batchPath);
                RememberRetryNumber(source, number);
                RecordFailedWindow(
                    sessionId,
                    source,
                    number,
                    relativePath,
                    pending,
                    ex,
                    unreadableChunk: false,
                    orphanPath: orphan);

                return null;
            }

            ClearRetryNumber(source);
            pending.Chunks.Clear();

            return QueueJob(paths, batch);
        }
    }

    /// <summary>
    /// Deletes a batch WAV whose manifest could not be written, returning the path when it could
    /// not be removed.
    /// </summary>
    /// <remarks>
    /// Best effort by design: the window is retried either way, so a failure here only decides
    /// whether an unusable artifact is reported in the event rather than left silently on disk.
    /// </remarks>
    private static string? RemoveUnusableArtifact(string batchPath)
    {
        try
        {
            if (File.Exists(batchPath))
            {
                File.Delete(batchPath);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return batchPath;
        }
    }

    /// <summary>Records a batch window whose audio did not reach the provider as one batch.</summary>
    /// <remarks>
    /// The count of genuinely lost audio is kept separately from this record: a window that
    /// fails because one chunk is unreadable loses only that chunk, and the rest of the window is
    /// retried, so the window's whole duration is not missing from the transcript.
    /// </remarks>
    /// <param name="orphanPath">
    /// A batch WAV that could not be removed after its manifest failed to write, so it is on disk
    /// with nothing that can queue it. Null in every other case.
    /// </param>
    private void RecordFailedWindow(
        string sessionId,
        string source,
        int number,
        string relativePath,
        PendingBatch pending,
        Exception failure,
        bool unreadableChunk,
        string? orphanPath = null)
    {
        var affectedMs = pending.DurationMs;

        if (unreadableChunk)
        {
            UnreadableChunks++;
        }

        var outcome = unreadableChunk
            ? "The unreadable chunk was dropped from the window and the remaining audio is " +
              "retried; the capture chunk itself is still on disk, so this is a transcript gap."
            : "No chunk was dropped; the window stays pending and is retried.";

        if (orphanPath is not null)
        {
            // Reported rather than hidden: the artifact is unusable (no manifest, so no timeline)
            // and this is the only record that it exists.
            outcome +=
                $" The batch WAV '{orphanPath}' could not be removed and is unusable without its " +
                "manifest; delete it manually.";
        }

        _events?.Write(new SessionEvent(SessionEvents.AsrBatchFailed, pending.StartMs)
        {
            Source = source,
            StartMs = pending.StartMs,
            EndMs = pending.EndMs,
            Count = pending.Chunks.Count,
            Reason = unreadableChunk ? "chunk_unreadable" : "batch_write_failed",
            Detail =
                $"batch '{relativePath}' (window {number}, {affectedMs} ms of session audio over " +
                $"{pending.Chunks.Count} chunk(s)) could not be built: {failure.Message} {outcome}",
        });
    }

    /// <summary>
    /// Writes the batch/source timeline mapping: which durable chunks make up this batch,
    /// where each one sits on the session timeline, and which artifact the provider was
    /// given. Without it a batch file could not be traced back to the audio it was built
    /// from, and the original session timeline could not be reconstructed.
    /// </summary>
    private static void WriteManifest(AsrBatch batch, AsrBatchBuilderOptions options)
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
            // What this batch was built for. Recorded so an orphan recovered by a later process
            // is queued for the provider and tier the recording actually used, instead of
            // silently inheriting whatever the resuming process happens to be configured with.
            ["provider"] = options.ProviderName,
            ["tier"] = options.ServiceTier,
            ["chunks"] = chunks,
        };

        File.WriteAllText(
            manifestPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Creates the persistent job for a materialized batch, plus its events.</summary>
    /// <returns>The batch, or <c>null</c> when the job row could not be written.</returns>
    private AsrBatch? QueueJob(SessionArtifactPaths paths, AsrBatch batch)
    {
        if (QueueJob(paths, batch, _jobs, _options, _events, onQueued: null) is not { } queued)
        {
            return null;
        }

        BatchesQueued++;
        return queued;
    }

    /// <summary>
    /// Creates the persistent job for a materialized batch, plus its events.
    /// </summary>
    /// <remarks>
    /// Static so <see cref="RecoverFinalizedBatches(string, string, IAsrJobStore, AsrBatchBuilderOptions, ISessionEventSink?)"/>
    /// can run the identical logic for a command that owns no live builder.
    /// </remarks>
    private static AsrBatch? QueueJob(
        SessionArtifactPaths paths,
        AsrBatch batch,
        IAsrJobStore jobs,
        AsrBatchBuilderOptions options,
        ISessionEventSink? events,
        Action? onQueued)
    {
        if (HasJobForArtifact(jobs, batch.SessionId, batch.RelativePath))
        {
            // Idempotent: a job derived from this batch artifact already exists, so the audio
            // must not be queued (and therefore billed) a second time.
            return batch;
        }

        var now = options.TimeProvider.GetUtcNow();
        var job = new AsrJob
        {
            Id = JobIdFor(batch.RelativePath),
            SessionId = batch.SessionId,
            Source = batch.Source,
            Tier = options.ServiceTier,
            Provider = options.ProviderName,
            StartMs = batch.StartMs,
            EndMs = batch.EndMs,
            InputArtifact = batch.RelativePath,
            Status = AsrJobStatus.Pending,
            ProviderRequestId = Ids.NewProviderRequestId(),
            DurationMs = (int)Math.Min(int.MaxValue, batch.DurationMs),
            SpeakerInfoRequested = options.RequestSpeakerInfo,
            EstimatedCostCny = AsrCostEstimator.Estimate(batch.DurationMs, options.CostPerHourCny),
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            jobs.Create(job);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The audio artifact and its manifest are already durable, so the next
            // `meetcap asr resume` (or the next start) finds the batch and queues it then. The
            // recording is unaffected; only the transcript is delayed.
            events?.Write(new SessionEvent(SessionEvents.AsrBatchFailed, batch.EndMs)
            {
                Source = batch.Source,
                StartMs = batch.StartMs,
                EndMs = batch.EndMs,
                Count = batch.Chunks.Count,
                Reason = "job_queue_failed",
                Detail =
                    $"batch '{batch.RelativePath}' is durable but its job row could not be written: " +
                    $"{ex.Message} The session timeline keeps the window; run 'meetcap asr resume' " +
                    "to queue it, or start the next recording, which recovers it automatically.",
            });

            return null;
        }

        events?.Write(new SessionEvent(SessionEvents.AsrBatchClosed, batch.EndMs)
        {
            Source = batch.Source,
            StartMs = batch.StartMs,
            EndMs = batch.EndMs,
            Count = batch.Chunks.Count,
            Detail =
                $"closed {batch.Chunks.Count} durable chunk(s) into '{batch.RelativePath}' " +
                $"({batch.DurationMs} ms of session audio).",
        });

        events?.Write(new SessionEvent(SessionEvents.AsrJobQueued, batch.StartMs)
        {
            Source = batch.Source,
            StartMs = batch.StartMs,
            EndMs = batch.EndMs,
            Count = batch.Chunks.Count,
            Detail =
                $"job '{job.Id}' queued for '{batch.RelativePath}'; provider '{job.Provider}', " +
                $"tier '{job.Tier}'. Submission happens off the capture path.",
        });

        onQueued?.Invoke();
        return batch;
    }

    private static bool HasJobForArtifact(IAsrJobStore jobs, string sessionId, string relativePath)
    {
        if (jobs.Get(JobIdFor(relativePath)) is not null)
        {
            return true;
        }

        return jobs.ListBySession(sessionId)
            .Any(job => string.Equals(job.InputArtifact, relativePath, StringComparison.Ordinal));
    }

    private bool HasJobForArtifact(string sessionId, string relativePath) =>
        HasJobForArtifact(_jobs, sessionId, relativePath);

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
        // A window that failed to materialize reuses its own number on the next attempt. Advancing
        // on failure would make each retry target a different artifact name, which both
        // accumulates half-written files on disk and makes the batch that finally succeeds carry a
        // number unrelated to how many batches exist — the reason a long write outage left gaps in
        // the numbering for no reason.
        if (_retryBatchNumber.TryGetValue(source, out var retry))
        {
            return retry;
        }

        // The number only has to be unique within the session directory, so a per-track counter
        // is enough; it starts from whatever a previous process already wrote, which is what
        // keeps recovery from reusing a batch file's name.
        var counter = _nextBatchNumber.TryGetValue(source, out var known)
            ? known
            : HighestExistingBatchNumber(paths, source);

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

    /// <summary>Remembers a batch number whose materialization failed, so the retry reuses it.</summary>
    private void RememberRetryNumber(string source, int number) => _retryBatchNumber[source] = number;

    /// <summary>Clears the retry number once the batch it names has been materialized.</summary>
    private void ClearRetryNumber(string source) => _retryBatchNumber.Remove(source);

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

        /// <summary>
        /// Drops the chunk with the given session-relative path from the open window.
        /// </summary>
        /// <remarks>
        /// Order is preserved for the chunks that remain, so the window's own span still
        /// describes where its audio sits on the session timeline.
        /// </remarks>
        public void Drop(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return;
            }

            Chunks.RemoveAll(chunk => string.Equals(chunk.RelativePath, relativePath, StringComparison.Ordinal));
        }
    }
}
