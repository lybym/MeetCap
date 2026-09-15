namespace MeetCap.AudioPipeline;

using System.Globalization;
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

    /// <summary>A one-line summary for the CLI.</summary>
    public string Describe()
        => RecoveredSessions == 0
            ? "no incomplete sessions found"
            : $"{RecoveredSessions} incomplete session(s): {RepairedChunks} chunk(s) recovered, " +
              $"{CorruptChunks} chunk(s) unreadable";
}

/// <summary>
/// Implements the startup scan of docs/RELIABILITY.md section 6: find sessions that
/// were not cleanly stopped, repair the <c>.part</c> chunks that can be repaired, mark
/// the ones that cannot, and never present a repaired session as clean.
/// </summary>
/// <remarks>
/// The scan has to run before a new recording starts, because it is the only thing
/// that can turn a kill-9 leftover into durable audio. It is idempotent: a session
/// whose status is already terminal and that has no <c>.part</c> files left is
/// skipped, so running it on every command is safe.
/// </remarks>
public sealed class SessionRecoveryScanner
{
    private readonly MeetCapDatabase _database;
    private readonly IClock _clock;

    public SessionRecoveryScanner(MeetCapDatabase database, IClock clock)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Scans every session directory under <paramref name="dataRoot"/>.</summary>
    public RecoveryReport Scan(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var directories = SessionPaths.EnumerateSessionDirectories(dataRoot);
        var recovered = new List<RecoveredSession>();
        var problems = new List<string>();

        foreach (var directory in directories)
        {
            var sessionId = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(sessionId))
            {
                continue;
            }

            try
            {
                var session = ScanSession(new SessionPaths(dataRoot, sessionId), problems);
                if (session is not null)
                {
                    recovered.Add(session);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // One unreadable session must not abort the scan.
                problems.Add($"session '{sessionId}' could not be recovered: {ex.Message}");
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

        using var events = new JsonlSessionEventSink(paths.EventsPath);
        var atMs = SessionEndMs(paths.SessionId);
        var recoveredChunks = new List<RecoveredChunk>();

        foreach (var partFile in partFiles)
        {
            var chunk = RecoverPartFile(paths, partFile, events, atMs);
            if (chunk is not null)
            {
                recoveredChunks.Add(chunk);
            }
        }

        var degraded = true;
        var detail = BuildDetail(recoveredChunks);

        events.Write(new SessionEvent(SessionEventNames.SessionRecovered, atMs)
        {
            Count = recoveredChunks.Count,
            Detail = "this session was not cleanly stopped; " + detail,
        });

        var interruptedAt = _clock.UtcNow;

        if (manifest is not null)
        {
            manifest.Status = SessionStatus.Interrupted;
            manifest.Degraded = true;
            manifest.EndReason = manifest.EndReason ?? "interrupted";
            manifest.RecoveredAt = interruptedAt;
            SessionManifestStore.Save(paths.ManifestPath, manifest);
        }

        _database.Sessions.UpdateLifecycle(
            paths.SessionId,
            SessionStatus.Interrupted,
            interruptedAt,
            stoppedAt: null,
            durationMs: atMs);

        return new RecoveredSession(
            paths.SessionId,
            paths.SessionDirectory,
            SessionStatus.Interrupted,
            recoveredChunks,
            degraded,
            detail);
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
        File.Move(partPath, finalPath, overwrite: true);

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

    private static string BuildDetail(IReadOnlyList<RecoveredChunk> chunks)
    {
        var repaired = chunks.Count(c => c.Status == ChunkStates.Recovered);
        var corrupt = chunks.Count(c => c.Status == ChunkStates.Corrupt);
        return $"{repaired} chunk(s) repaired, {corrupt} chunk(s) unreadable";
    }
}
