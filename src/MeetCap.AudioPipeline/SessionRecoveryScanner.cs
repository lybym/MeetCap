namespace MeetCap.AudioPipeline;

using System.Globalization;
using System.Text.Json;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using MeetCap.Core.Time;
using MeetCap.Persistence.Storage;

/// <summary>One chunk that startup recovery had to deal with.</summary>
public sealed record RecoveredChunk(
    int Sequence,
    string Source,
    string Path,
    string Status,
    long DataBytes,
    string? Detail);

/// <summary>One session that was found not cleanly stopped.</summary>
public sealed record RecoveredSession(
    string SessionId,
    string SessionDirectory,
    string Status,
    IReadOnlyList<RecoveredChunk> Chunks,
    bool Degraded,
    string? Detail)
{
    public int RepairedChunks => Chunks.Count(c => c.Status == ChunkStates.Recovered);

    public int CorruptChunks => Chunks.Count(c => c.Status == ChunkStates.Corrupt);

    /// <summary>
    /// The session's post-repair gap audit. A session whose timeline still has a provable
    /// hole must never be reported as fully recovered (docs/RELIABILITY.md section 6).
    /// </summary>
    public SessionAudit Audit { get; init; } = SessionAudit.Clean(string.Empty);

    /// <summary>True when a known stretch of the session timeline holds no durable audio.</summary>
    public bool RecoveryIncomplete => Audit.RecoveryIncomplete;
}

/// <summary>Outcome of a startup recovery scan.</summary>
public sealed class RecoveryReport
{
    public RecoveryReport(
        int scannedSessions,
        IReadOnlyList<RecoveredSession> sessions,
        IReadOnlyList<string> problems)
    {
        ScannedSessions = scannedSessions;
        Sessions = sessions;
        Problems = problems;
    }

    /// <summary>How many session directories were inspected.</summary>
    public int ScannedSessions { get; }

    /// <summary>Sessions that needed recovery, oldest first.</summary>
    public IReadOnlyList<RecoveredSession> Sessions { get; }

    /// <summary>
    /// Non-fatal problems encountered while scanning, such as a session directory
    /// whose <c>session.json</c> could not be read.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    public bool HasFindings => Sessions.Count > 0;

    public int RecoveredSessions => Sessions.Count;

    public int RepairedChunks => Sessions.Sum(s => s.RepairedChunks);

    public int CorruptChunks => Sessions.Sum(s => s.CorruptChunks);

    /// <summary>Total audio bytes made durable by this scan.</summary>
    public long RecoveredDataBytes
        => Sessions.SelectMany(s => s.Chunks)
            .Where(c => ChunkStates.IsDurable(c.Status))
            .Sum(c => c.DataBytes);

    /// <summary>Sessions that still have a known gap after every repair that could be made.</summary>
    public IReadOnlyList<RecoveredSession> SessionsWithRemainingGaps
        => Sessions.Where(s => s.RecoveryIncomplete).ToList();

    /// <summary>
    /// True when recovery must not claim success, because a known gap remains.
    /// </summary>
    public bool RecoveryIncomplete => SessionsWithRemainingGaps.Count > 0;

    /// <summary>Missing audio across every session this scan touched, in milliseconds.</summary>
    public long RemainingGapMs => Sessions.Sum(s => s.Audit.TotalGapMs);

    /// <summary>A one-line summary for the CLI.</summary>
    public string Describe()
    {
        if (RecoveredSessions == 0)
        {
            return "no incomplete sessions found";
        }

        var summary =
            $"{RecoveredSessions} incomplete session(s): {RepairedChunks} chunk(s) recovered, " +
            $"{CorruptChunks} chunk(s) unreadable";

        return RecoveryIncomplete
            ? summary + $"; {SessionsWithRemainingGaps.Count} session(s) still have a known gap " +
                        $"({RemainingGapMs} ms missing)"
            : summary;
    }
}

/// <summary>
/// Implements the startup scan of docs/RELIABILITY.md section 6: find sessions that
/// were not cleanly stopped, repair the <c>.part</c> chunks that can be repaired, mark
/// the ones that cannot, and never present a repaired session as clean.
/// </summary>
/// <remarks>
/// The scan has to run before a new recording starts, because it is the only thing
/// that can turn a kill-9 leftover into durable audio. Two rules make it safe to run on
/// every command:
/// <list type="bullet">
/// <item>a session that has not cleanly stopped is only reconciled when its liveness
/// marker is free, so a recording that is still in progress is never touched
/// (<see cref="SessionRecordingLock"/>);</item>
/// <item>a session that is already terminal keeps its terminal status and its clean-stop
/// timestamp, even when a stray artifact is repaired.</item>
/// </list>
/// </remarks>
public sealed class SessionRecoveryScanner
{
    /// <summary>
    /// Suffix appended to a <c>.part</c> that collided with an existing final
    /// <c>.wav</c> during recovery. The artifact is retained on disk for inspection
    /// but is not re-enumerated as a <c>.part</c> (it does not end in
    /// <c>.part</c>) nor as a final <c>.wav</c> by a future scan, so recovery stays
    /// idempotent.
    /// </summary>
    public const string CollidedSuffix = ".collided";

    private readonly MeetCapDatabase _database;
    private readonly IClock _clock;

    public SessionRecoveryScanner(MeetCapDatabase database, IClock clock)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Scans every session directory under <paramref name="dataRoot"/>.</summary>
    public RecoveryReport Scan(string dataRoot) => Scan(dataRoot, sessionId: null);

    /// <summary>
    /// Scans the session directories under <paramref name="dataRoot"/>, or only
    /// <paramref name="sessionId"/> when one is given. The filtered form is what
    /// <c>meetcap session repair --session</c> uses, so an operator can repair one
    /// session without walking an unrelated data root.
    /// </summary>
    public RecoveryReport Scan(string dataRoot, string? sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var directories = SessionPaths.EnumerateSessionDirectories(dataRoot);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            directories = directories
                .Where(d => string.Equals(Path.GetFileName(d), sessionId, StringComparison.Ordinal))
                .ToList();
        }

        var recovered = new List<RecoveredSession>();
        var problems = new List<string>();

        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            try
            {
                var session = ScanSession(new SessionPaths(dataRoot, id), problems);
                if (session is not null)
                {
                    recovered.Add(session);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // One unreadable session must not abort the scan.
                problems.Add($"session '{id}' could not be recovered: {ex.Message}");
            }
        }

        return new RecoveryReport(directories.Count, recovered, problems);
    }

    private RecoveredSession? ScanSession(SessionPaths paths, List<string> problems)
    {
        var existing = _database.Sessions.Find(paths.SessionId);
        var partFiles = EnumeratePartFiles(paths).ToList();

        if (partFiles.Count == 0 && !SessionStatus.NeedsRecovery(existing?.Status))
        {
            return null;
        }

        // A live recording owns its own chunk surface. Recovery must never run against
        // it: it would rewrite a healthy session to INTERRUPTED, stamp false
        // 'session.recovered' / 'audio.chunk.corrupt' events on a clean recording, and
        // (because `meetcap stop` only finds CREATED/RECORDING sessions) leave a
        // recording that can no longer be stopped. The session row cannot express this
        // on its own: RECORDING is exactly what a killed process leaves behind too, so
        // the exclusive liveness marker is the discriminator.
        if (SessionRecordingLock.IsHeld(paths.RecordingLockPath))
        {
            return null;
        }

        // Only a session that never cleanly stopped is rewritten to INTERRUPTED. A
        // terminal session may still carry a stray artifact (for example a `.part` left
        // by a duplicate or a late close), and that artifact is repaired below — but the
        // session stays COMPLETED and keeps its `stopped_at`, because it *was* cleanly
        // stopped (docs/DATA_MODEL.md section 1, docs/ARCHITECTURE.md section 20).
        var targetStatus = SessionStatus.NeedsRecovery(existing?.Status) || existing is null
            ? SessionStatus.Interrupted
            : existing.Status;

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var manifestError);
        if (manifest is null && manifestError is not null)
        {
            problems.Add($"session '{paths.SessionId}': {manifestError}");
        }

        var sessionRecord = existing ?? BuildSessionRecord(paths, manifest);

        // The session row has to exist before any chunk row is written, because
        // audio_chunks references sessions and the foreign key is enforced.
        if (existing is null)
        {
            _database.Sessions.Insert(sessionRecord);
        }

        var repaired = RepairArtifacts(paths, partFiles);
        try
        {
            return FinishRecovery(paths, existing, manifest, targetStatus, repaired);
        }
        finally
        {
            // The runner owns the event log: it must be closed and flushed before the scan
            // reports its findings.
            repaired.Events.Dispose();
        }
    }

    /// <summary>
    /// Audits the repaired session, writes its gap and recovery events, and updates the
    /// session row and manifest.
    /// </summary>
    private RecoveredSession FinishRecovery(
        SessionPaths paths,
        SessionRecord? existing,
        SessionManifest? manifest,
        string targetStatus,
        RepairPass repaired)
    {
        var audit = SessionGapAuditor.Audit(paths.SessionId, _database.Chunks.ListForSession(paths.SessionId));
        WriteGapEvents(repaired.Events, audit, repaired.RecoveryStartMs);

        var interrupted = string.Equals(targetStatus, SessionStatus.Interrupted, StringComparison.Ordinal);
        var captureHealth = manifest?.CaptureHealth;
        var degraded = interrupted
            || (manifest?.Degraded ?? false)
            || audit.RecoveryIncomplete
            || (captureHealth?.IsDegraded ?? false);
        var detail = BuildDetail(repaired.Chunks, audit);
        // Reconciliation can create or extend a chunk row (notably when a process died
        // after the atomic rename but before the index upsert). Session metadata must
        // reflect that durable audio rather than the stale pre-recovery maximum.
        var sessionEndMs = SessionEndMs(paths.SessionId);

        // Only an actual recovery writes the recovery event. A targeted
        // `meetcap session repair` re-scans the same session on every invocation, so an
        // unconditional append here would grow the event log without recording anything
        // new. A session that was not cleanly stopped always writes it: that transition
        // is the recovery.
        if (repaired.Chunks.Count > 0 || interrupted)
        {
            repaired.Events.Write(new SessionEvent(SessionEventNames.SessionRecovered, sessionEndMs)
            {
                Count = repaired.Chunks.Count,
                GapMs = audit.TotalGapMs > 0 ? audit.TotalGapMs : null,
                Reason = audit.RecoveryIncomplete ? "incomplete" : null,
                Detail = (interrupted
                        ? "this session was not cleanly stopped; "
                        : "this session stopped cleanly but held a stray artifact; ") + detail,
            });

            if (audit.RecoveryIncomplete)
            {
                // docs/RELIABILITY.md section 6: a repair that could not make the session
                // whole records that fact instead of reporting success.
                repaired.Events.Write(new SessionEvent(
                    SessionEventNames.SessionRepairIncomplete,
                    sessionEndMs)
                {
                    GapMs = audit.TotalGapMs,
                    Count = audit.Gaps.Count,
                    Reason = audit.HasProblems ? "audit_failed" : "gap_detected",
                    Detail =
                        "recovery could not make this session whole; " + audit.Describe() +
                        (audit.HasProblems ? " (the audit itself failed: " + string.Join("; ", audit.Problems) + ")" : string.Empty) +
                        ". The audio that is missing has no durable chunk and was not invented.",
                });
            }
        }

        // A terminal session keeps its own metadata: its `stopped_at`, its `duration_ms`
        // and its `recovered_at` are already the truth, and overwriting them would
        // present a cleanly stopped recording as an interrupted one.
        var updatedAt = interrupted ? _clock.UtcNow : existing?.UpdatedAt ?? _clock.UtcNow;

        if (manifest is not null)
        {
            manifest.Status = targetStatus;
            manifest.Degraded = degraded;
            manifest.EndReason = manifest.EndReason ?? (interrupted ? "interrupted" : null);
            manifest.GapCount = Math.Max(manifest.GapCount, audit.Gaps.Count);
            manifest.GapTotalMs = Math.Max(manifest.GapTotalMs, audit.TotalGapMs);
            manifest.GapsRemain = audit.RecoveryIncomplete;
            manifest.GapDetails = audit.DescribeGaps().ToList();

            if (interrupted)
            {
                manifest.RecoveredAt = _clock.UtcNow;
            }

            SessionManifestStore.Save(paths.ManifestPath, manifest);
        }

        _database.Sessions.UpdateLifecycle(
            paths.SessionId,
            targetStatus,
            updatedAt,
            stoppedAt: interrupted ? null : existing?.StoppedAt,
            durationMs: interrupted ? sessionEndMs : existing?.DurationMs ?? sessionEndMs);

        return new RecoveredSession(
            paths.SessionId,
            paths.SessionDirectory,
            targetStatus,
            repaired.Chunks,
            degraded,
            detail)
        {
            Audit = audit,
        };
    }

    /// <summary>
    /// Artifacts the repair pass had to deal with, plus the log it wrote to and the
    /// timeline position repair events are placed after.
    /// </summary>
    /// <remarks>
    /// The event sink is owned by the caller, not by the repair pass: the gap audit and
    /// the session-level recovery event are written after the artifacts have been
    /// reconciled, and they must land in the same log.
    /// </remarks>
    private sealed record RepairPass(
        IReadOnlyList<RecoveredChunk> Chunks,
        JsonlSessionEventSink Events,
        long RecoveryStartMs);

    /// <summary>
    /// Repairs one session's <c>.part</c> chunks and reconciles final WAVs whose index row
    /// never became durable. Shared by the startup scan and by a targeted
    /// <c>meetcap session repair</c>, so both paths classify artifacts identically.
    /// </summary>
    private RepairPass RepairArtifacts(SessionPaths paths, IReadOnlyList<string> partFiles)
    {
        var events = new JsonlSessionEventSink(paths.EventsPath);

        // Use the pre-recovery end only to place individual repair events after the
        // already-indexed audio. Recovery can promote an open/missing index row to a
        // chunk with a later end, so the session-level end is calculated again by the
        // caller.
        var recoveryStartMs = SessionEndMs(paths.SessionId);
        var recoveredChunks = new List<RecoveredChunk>();

        foreach (var partFile in partFiles)
        {
            var chunk = RecoverPartFile(paths, partFile, events, recoveryStartMs);
            if (chunk is not null)
            {
                recoveredChunks.Add(chunk);
            }
        }

        // A chunk may also have been atomically renamed to its final .wav and then the
        // process died before the closed-index upsert landed (docs/RELIABILITY.md
        // section 5: "atomic rename -> mark CLOSED"). Those final WAVs are durable on
        // disk but their index row is still 'open' (or was never written), so without
        // this pass they would never be promoted to durable and downstream consumers
        // would skip them. Reconcile them the same way a repaired .part is reconciled.
        foreach (var wavPath in EnumerateFinalWavFiles(paths))
        {
            var chunk = ReconcileFinalWav(paths, wavPath, events, recoveryStartMs);
            if (chunk is not null)
            {
                recoveredChunks.Add(chunk);
            }
        }

        return new RepairPass(recoveredChunks, events, recoveryStartMs);
    }

    /// <summary>
    /// Writes an explicit <c>capture.gap</c> event for every gap the audit found,
    /// skipping positions the event log already describes.
    /// </summary>
    /// <remarks>
    /// docs/RELIABILITY.md section 7 forbids hiding missing audio by only shifting later
    /// timestamps, so a hole the live recording never saw (because the process died) has
    /// to become an event too. Recovery is idempotent, so the already-recorded positions
    /// are read back first: otherwise every <c>meetcap status</c> would append the same
    /// gap again and the event log would grow without bound.
    /// </remarks>
    private static void WriteGapEvents(ISessionEventSink events, SessionAudit audit, long atMs)
    {
        if (!audit.HasGap)
        {
            return;
        }

        var alreadyReported = ReadReportedGapPositions(events);
        foreach (var gap in audit.Gaps)
        {
            if (alreadyReported.Contains((gap.StartMs, gap.Source)))
            {
                continue;
            }

            events.Write(new SessionEvent(SessionEventNames.CaptureGap, Math.Max(gap.EndMs, atMs))
            {
                Source = gap.Source,
                StartMs = gap.StartMs,
                EndMs = gap.EndMs,
                GapMs = gap.GapMs,
                Reason = gap.Reason,
                Count = gap.MissingSequences.Count > 0 ? gap.MissingSequences.Count : null,
                Detail = gap.Detail,
            });
        }
    }

    /// <summary>
    /// The <c>(start_ms, source)</c> positions already described by a <c>capture.gap</c>
    /// event in the session's log.
    /// </summary>
    private static HashSet<(long StartMs, string Source)> ReadReportedGapPositions(ISessionEventSink events)
    {
        var positions = new HashSet<(long, string)>();
        if (events is not JsonlSessionEventSink jsonl || !File.Exists(jsonl.Path))
        {
            return positions;
        }

        foreach (var line in ReadLines(jsonl.Path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("event", out var name) ||
                    name.GetString() != SessionEventNames.CaptureGap ||
                    !root.TryGetProperty("start_ms", out var startMs) ||
                    !startMs.TryGetInt64(out var start) ||
                    !root.TryGetProperty("source", out var source))
                {
                    continue;
                }

                positions.Add((start, source.GetString() ?? string.Empty));
            }
            catch (JsonException)
            {
                // A torn final line from a killed process is expected; it simply carries
                // no position to de-duplicate against.
            }
        }

        return positions;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        // The event log is appended by another process while a recording runs, so it is
        // opened with the sharing the sink itself uses.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private RecoveredChunk? RecoverPartFile(
        SessionPaths paths,
        string partPath,
        ISessionEventSink events,
        long atMs)
    {
        var fileName = Path.GetFileName(partPath);
        var sourceName = Path.GetFileName(Path.GetDirectoryName(partPath));
        var sequence = ParseSequence(fileName);

        if (sequence is null || !AudioSources.TryParse(sourceName, out var source))
        {
            events.Write(new SessionEvent(SessionEventNames.ChunkCorrupt, atMs)
            {
                Source = sourceName,
                Chunk = fileName,
                Detail = "the .part file name does not match the chunk naming contract; it was left untouched.",
            });
            return new RecoveredChunk(0, sourceName ?? "unknown", partPath, ChunkStates.Corrupt, 0, "unrecognised file name");
        }

        var info = new FileInfo(partPath);
        var dataBytes = info.Length - WavHeader.Size;

        if (dataBytes <= 0)
        {
            // A placeholder chunk with no audio. Nothing is lost by removing it, and
            // leaving it behind would make every future scan report it again.
            TryDelete(partPath);
            events.Write(new SessionEvent(SessionEventNames.ChunkCorrupt, atMs)
            {
                Source = source.ToWireName(),
                Chunk = fileName,
                Detail = "the chunk held no audio data and was discarded.",
            });
            MarkCorrupt(paths, source, sequence.Value, partPath, dataBytes: 0);
            return new RecoveredChunk(
                sequence.Value,
                source.ToWireName(),
                partPath,
                ChunkStates.Corrupt,
                0,
                "no audio data");
        }

        if (!WaveChunkValidator.TryReadHeader(partPath, out var header, out var readError) || !header.IsValid)
        {
            var reason = header.Error ?? readError ?? "header could not be read";
            events.Write(new SessionEvent(SessionEventNames.ChunkCorrupt, atMs)
            {
                Source = source.ToWireName(),
                Chunk = fileName,
                Detail = "the chunk header could not be repaired: " + reason,
            });
            MarkCorrupt(paths, source, sequence.Value, partPath, dataBytes);
            return new RecoveredChunk(
                sequence.Value,
                source.ToWireName(),
                partPath,
                ChunkStates.Corrupt,
                dataBytes,
                reason);
        }

        var format = header.Format!;

        try
        {
            RepairHeader(partPath, dataBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            events.Write(new SessionEvent(SessionEventNames.ChunkCorrupt, atMs)
            {
                Source = source.ToWireName(),
                Chunk = fileName,
                Detail = "the chunk header could not be rewritten: " + ex.Message,
            });
            MarkCorrupt(paths, source, sequence.Value, partPath, dataBytes);
            return new RecoveredChunk(
                sequence.Value,
                source.ToWireName(),
                partPath,
                ChunkStates.Corrupt,
                dataBytes,
                ex.Message);
        }

        var validation = WaveChunkValidator.ValidateClosedFile(partPath, format);
        if (!validation.IsValid)
        {
            events.Write(new SessionEvent(SessionEventNames.ChunkCorrupt, atMs)
            {
                Source = source.ToWireName(),
                Chunk = fileName,
                Detail = "the repaired chunk did not validate: " + validation.Error,
            });
            MarkCorrupt(paths, source, sequence.Value, partPath, dataBytes);
            return new RecoveredChunk(
                sequence.Value,
                source.ToWireName(),
                partPath,
                ChunkStates.Corrupt,
                dataBytes,
                validation.Error);
        }

        var finalPath = paths.ChunkFinalPath(source, sequence.Value);

        // Recovery must never overwrite a durable final WAV (docs/RELIABILITY.md:42;
        // docs/ARCHITECTURE.md:464). A .part alongside a same-sequence .wav is a stale
        // or duplicate artifact from an abnormal shutdown; overwriting the .wav would
        // irreversibly destroy previously closed audio. Retain both artifacts — the
        // .part is renamed to .collided so it survives for inspection but is not
        // re-enumerated as a .part on the next scan — and report the collision as
        // corrupt so the ambiguity is visible, not silently resolved. The existing
        // .wav and its index row are left untouched here; ReconcileFinalWav promotes
        // the .wav to durable if its row is still open or missing.
        if (File.Exists(finalPath))
        {
            var collidedPath = partPath + CollidedSuffix;
            File.Move(partPath, collidedPath, overwrite: true);

            events.Write(new SessionEvent(SessionEventNames.ChunkCorrupt, atMs)
            {
                Source = source.ToWireName(),
                Chunk = fileName,
                Detail = "a final WAV already exists for this sequence; the .part was retained as .collided to avoid overwriting closed audio.",
            });

            return new RecoveredChunk(
                sequence.Value,
                source.ToWireName(),
                collidedPath,
                ChunkStates.Corrupt,
                dataBytes,
                "a final WAV already exists for this sequence; the .part was retained to avoid overwriting closed audio");
        }

        File.Move(partPath, finalPath, overwrite: false);

        var durationMs = format.FramesToMilliseconds(format.BytesToFrames(dataBytes));
        UpsertChunk(
            paths,
            source,
            sequence.Value,
            finalPath,
            format,
            dataBytes,
            ChunkStates.Recovered,
            durationMs);

        events.Write(new SessionEvent(SessionEventNames.ChunkRecovered, atMs + durationMs)
        {
            Source = source.ToWireName(),
            Chunk = Path.GetFileName(finalPath),
            StartMs = atMs,
            EndMs = atMs + durationMs,
        });

        return new RecoveredChunk(
            sequence.Value,
            source.ToWireName(),
            paths.RelativeChunkPath(source, sequence.Value),
            ChunkStates.Recovered,
            dataBytes,
            null);
    }

    /// <summary>
    /// Reconciles one final <c>.wav</c> whose index row is not durable. This is the
    /// recovery path for a crash that landed between the atomic rename in
    /// <see cref="WaveChunkWriter.Close"/> and the closed-index upsert in
    /// <see cref="ChunkSpool.CloseCurrentChunk"/>: the WAV is already durable on disk,
    /// but its row is still <c>open</c> (or was never written).
    /// </summary>
    /// <returns>A recovered/corrupt chunk, or <c>null</c> when there is nothing to do.</returns>
    private RecoveredChunk? ReconcileFinalWav(
        SessionPaths paths,
        string wavPath,
        ISessionEventSink events,
        long atMs)
    {
        var fileName = Path.GetFileName(wavPath);
        var sourceName = Path.GetFileName(Path.GetDirectoryName(wavPath));
        var sequence = ParseSequence(fileName);

        if (sequence is null || !AudioSources.TryParse(sourceName, out var source))
        {
            return null;
        }

        var existing = _database.Chunks.Find(paths.SessionId, source, sequence.Value);
        if (existing is not null && ChunkStates.IsDurable(existing.Status))
        {
            // Already closed cleanly or already recovered: never touch durable audio.
            return null;
        }

        // The close/index transition was interrupted between the atomic rename and the
        // closed-index upsert. The WAV is durable on disk, but its index row is still
        // 'open' (or was never written). Validate it and make it durable.
        var expectedFormat = existing?.Format;
        if (expectedFormat is null)
        {
            if (!WaveChunkValidator.TryReadHeader(wavPath, out var header, out var readError) || !header.IsValid)
            {
                return MarkFinalWavCorrupt(
                    paths, source, sequence.Value, wavPath, events, atMs,
                    reason: header.Error ?? readError ?? "header could not be read");
            }

            expectedFormat = header.Format!;
        }

        var validation = WaveChunkValidator.ValidateClosedFile(wavPath, expectedFormat);
        if (!validation.IsValid)
        {
            return MarkFinalWavCorrupt(
                paths, source, sequence.Value, wavPath, events, atMs,
                reason: validation.Error ?? "the final WAV did not validate");
        }

        var dataBytes = validation.DataBytes;
        var durationMs = expectedFormat.FramesToMilliseconds(expectedFormat.BytesToFrames(dataBytes));
        var startMs = existing?.StartMs ?? 0;

        UpsertChunk(paths, source, sequence.Value, wavPath, expectedFormat, dataBytes, ChunkStates.Recovered, durationMs);

        events.Write(new SessionEvent(SessionEventNames.ChunkRecovered, startMs + durationMs)
        {
            Source = source.ToWireName(),
            Chunk = fileName,
            StartMs = startMs,
            EndMs = startMs + durationMs,
            Detail = "the final WAV was on disk but its index row was not durable; it was reconciled on recovery.",
        });

        return new RecoveredChunk(
            sequence.Value,
            source.ToWireName(),
            paths.RelativeChunkPath(source, sequence.Value),
            ChunkStates.Recovered,
            dataBytes,
            null);
    }

    private RecoveredChunk MarkFinalWavCorrupt(
        SessionPaths paths,
        AudioSource source,
        int sequence,
        string wavPath,
        ISessionEventSink events,
        long atMs,
        string reason)
    {
        var dataBytes = Math.Max(0L, new FileInfo(wavPath).Length - WavHeader.Size);

        events.Write(new SessionEvent(SessionEventNames.ChunkCorrupt, atMs)
        {
            Source = source.ToWireName(),
            Chunk = Path.GetFileName(wavPath),
            Detail = "the final WAV could not be reconciled: " + reason,
        });

        MarkCorrupt(paths, source, sequence, wavPath, dataBytes);

        return new RecoveredChunk(
            sequence,
            source.ToWireName(),
            wavPath,
            ChunkStates.Corrupt,
            dataBytes,
            reason);
    }

    private static IEnumerable<string> EnumerateFinalWavFiles(SessionPaths paths)
    {
        var audioRoot = Path.Combine(paths.SessionDirectory, SessionPaths.AudioFolderName);
        if (!Directory.Exists(audioRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(audioRoot, "*" + SessionPaths.WaveExtension, SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(SessionPaths.PartSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static void RepairHeader(string partPath, long dataBytes)
    {
        using var stream = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[WavHeader.Size];
        stream.ReadExactly(header);
        WavHeader.PatchDataLength(header, dataBytes);
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(flushToDisk: true);
    }

    private void MarkCorrupt(SessionPaths paths, AudioSource source, int sequence, string path, long dataBytes)
    {
        var existing = _database.Chunks.Find(paths.SessionId, source, sequence);
        var format = existing?.Format ?? new AudioFormat(48_000, 1, 16, AudioSampleFormat.Pcm);

        _database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = AudioChunkRecord.BuildId(paths.SessionId, source, sequence),
            SessionId = paths.SessionId,
            Source = source,
            Sequence = sequence,
            RelativePath = existing?.RelativePath ?? paths.RelativeChunkPath(source, sequence),
            StartMs = existing?.StartMs ?? 0,
            EndMs = existing?.EndMs ?? 0,
            Format = format,
            ByteLength = dataBytes,
            Status = ChunkStates.Corrupt,
            DevicePositionFrames = existing?.DevicePositionFrames,
            QpcPositionTicks = existing?.QpcPositionTicks,
            CreatedAt = existing?.CreatedAt ?? _clock.UtcNow,
            ClosedAt = null,
        });
    }

    private void UpsertChunk(
        SessionPaths paths,
        AudioSource source,
        int sequence,
        string finalPath,
        AudioFormat format,
        long dataBytes,
        string status,
        long durationMs)
    {
        var existing = _database.Chunks.Find(paths.SessionId, source, sequence);
        var startMs = existing?.StartMs ?? 0;

        _database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = AudioChunkRecord.BuildId(paths.SessionId, source, sequence),
            SessionId = paths.SessionId,
            Source = source,
            Sequence = sequence,
            RelativePath = paths.RelativeChunkPath(source, sequence),
            StartMs = startMs,
            EndMs = startMs + durationMs,
            Format = format,
            ByteLength = dataBytes,
            Status = status,
            DevicePositionFrames = existing?.DevicePositionFrames,
            QpcPositionTicks = existing?.QpcPositionTicks,
            CreatedAt = existing?.CreatedAt ?? _clock.UtcNow,
            ClosedAt = _clock.UtcNow,
        });
    }

    private SessionRecord BuildSessionRecord(SessionPaths paths, SessionManifest? manifest) => new()
    {
        Id = paths.SessionId,
        Title = manifest?.Title ?? "(recovered session)",
        Mode = manifest?.Mode ?? SessionModes.Offline,
        SourceType = manifest?.SourceType ?? SessionSourceTypes.Live,
        Status = SessionStatus.Interrupted,
        StartedAt = manifest?.StartedAt,
        ConfigVersion = manifest?.ConfigVersion ?? 0,
        Tracks = manifest?.Tracks ?? new[] { AudioSources.Mic },
        CreatedAt = manifest?.StartedAt ?? _clock.UtcNow,
        UpdatedAt = _clock.UtcNow,
    };

    private long SessionEndMs(string sessionId)
        => _database.Chunks.ListForSession(sessionId).Select(c => c.EndMs).DefaultIfEmpty(0).Max();

    private static IEnumerable<string> EnumeratePartFiles(SessionPaths paths)
    {
        var audioRoot = Path.Combine(paths.SessionDirectory, SessionPaths.AudioFolderName);
        if (!Directory.Exists(audioRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(audioRoot, "*" + SessionPaths.PartSuffix, SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static int? ParseSequence(string fileName)
    {
        // 000003.wav.part
        var stem = fileName.EndsWith(SessionPaths.PartSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^SessionPaths.PartSuffix.Length]
            : fileName;

        var name = Path.GetFileNameWithoutExtension(stem);
        return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence > 0
            ? sequence
            : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving an empty placeholder behind is harmless; the next scan retries.
        }
    }

    private static string BuildDetail(IReadOnlyList<RecoveredChunk> chunks, SessionAudit audit)
    {
        var repaired = chunks.Count(c => c.Status == ChunkStates.Recovered);
        var corrupt = chunks.Count(c => c.Status == ChunkStates.Corrupt);
        return $"{repaired} chunk(s) repaired, {corrupt} chunk(s) unreadable; " +
               $"timeline: {audit.Describe()}";
    }
}
