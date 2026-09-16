using System.Security.Cryptography;
using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// The forced-kill acceptance criteria: previously closed chunks must survive, the
/// active chunk must be detected and repaired on the next startup, and a session that
/// was not cleanly stopped must never be presented as clean.
/// </summary>
public class SessionRecoveryScannerTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    private const int SecondBytes = 96_000;

    [Fact]
    public void Scan_AfterAForcedKill_RepairsTheActiveChunkAndNeverTouchesClosedOnes()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // A session that recorded two full minutes and then died mid-third-chunk. The
        // spool produces exactly the artifacts a real run leaves behind, including the
        // chunk index rows, before the process is abandoned.
        var clock = new FakeClock();
        var spool = new ChunkSpool(
            paths,
            AudioSource.Mic,
            Format,
            chunkSeconds: 60,
            workspace.Database,
            new InMemorySessionEventSink(),
            clock);

        spool.Append(Packet(0, 60 * SecondBytes), new PacketTiming(0, 60_000, 0, false, false));
        spool.Append(Packet(2_880_000, 60 * SecondBytes), new PacketTiming(60_000, 120_000, 0, false, false));
        spool.Append(Packet(5_760_000, 17 * SecondBytes), new PacketTiming(120_000, 137_000, 0, false, false));

        // Kill: the third chunk is never finalized.
        spool.Dispose();
        WriteManifest(paths, SessionStatus.Recording);

        Assert.True(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 3)));

        var closedBefore = HashFile(paths.ChunkFinalPath(AudioSource.Mic, 1));
        var secondBefore = HashFile(paths.ChunkFinalPath(AudioSource.Mic, 2));

        var report = new SessionRecoveryScanner(workspace.Database, clock).Scan(workspace.DataRoot);

        Assert.True(report.HasFindings);
        var session = Assert.Single(report.Sessions);
        Assert.Equal(workspace.SessionId, session.SessionId);
        Assert.Equal(SessionStatus.Interrupted, session.Status);
        Assert.Equal(1, session.RepairedChunks);
        Assert.Equal(0, session.CorruptChunks);

        // Previously closed chunks are byte-identical: the kill cannot reach them.
        Assert.Equal(closedBefore, HashFile(paths.ChunkFinalPath(AudioSource.Mic, 1)));
        Assert.Equal(secondBefore, HashFile(paths.ChunkFinalPath(AudioSource.Mic, 2)));

        // The active chunk became durable and independently readable.
        Assert.False(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 3)));
        var repaired = paths.ChunkFinalPath(AudioSource.Mic, 3);
        Assert.True(File.Exists(repaired));
        var validation = WaveChunkValidator.ValidateClosedFile(repaired, Format);
        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal(17 * SecondBytes, validation.DataBytes);

        var chunks = workspace.Database.Chunks.ListForSession(workspace.SessionId);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(
            new[] { ChunkStates.Closed, ChunkStates.Closed, ChunkStates.Recovered },
            chunks.Select(c => c.Status));
        Assert.Equal(137 * SecondBytes, chunks.Sum(c => c.ByteLength));

        // The session is explicitly interrupted, and no longer claims to be recording.
        var stored = workspace.Database.Sessions.Find(workspace.SessionId)!;
        Assert.Equal(SessionStatus.Interrupted, stored.Status);
        Assert.Null(stored.StoppedAt);
        Assert.Equal(137_000, stored.DurationMs);

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.Equal(SessionStatus.Interrupted, manifest!.Status);
        Assert.True(manifest.Degraded);
        Assert.NotNull(manifest.RecoveredAt);

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionRecovered));
        Assert.Equal(1, CountEvents(events, SessionEventNames.ChunkRecovered));
        var sessionRecovered = Assert.Single(events, e => Name(e) == SessionEventNames.SessionRecovered);
        Assert.Equal(137_000, sessionRecovered.GetProperty("at_ms").GetInt64());
    }

    private static AudioPacket Packet(long devicePositionFrames, int dataBytes)
        => new(
            AudioSource.Mic,
            Format,
            new byte[dataBytes],
            devicePositionFrames,
            devicePositionFrames * 10_000_000L / Format.SampleRate,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void Scan_IsIdempotent_ASecondPassFindsNothingToDo()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        WriteChunk(workspace.Paths, sequence: 1, dataBytes: 4 * SecondBytes, close: false);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var scanner = new SessionRecoveryScanner(workspace.Database, new FakeClock());
        var first = scanner.Scan(workspace.DataRoot);
        var second = scanner.Scan(workspace.DataRoot);

        Assert.Equal(1, first.RecoveredSessions);
        Assert.Equal(0, second.RecoveredSessions);

        // The repaired chunk was not touched again.
        Assert.Equal(
            4 * SecondBytes,
            WaveChunkValidator.ValidateClosedFile(
                workspace.Paths.ChunkFinalPath(AudioSource.Mic, 1),
                Format).DataBytes);
    }

    [Fact]
    public void Scan_DiscardsAChunkThatHoldsNoAudioAndReportsIt()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var partPath = workspace.Paths.ChunkPartPath(AudioSource.Mic, 1);
        WritePartWithData(workspace.Paths, sequence: 1, dataBytes: 0);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(1, session.CorruptChunks);

        // A placeholder with no audio carries no information, so it is removed rather
        // than reported on every future scan.
        Assert.False(File.Exists(partPath));

        var chunk = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Corrupt, chunk.Status);

        var events = ReadEvents(workspace.Paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.ChunkCorrupt));
        Assert.Equal(0, CountEvents(events, SessionEventNames.ChunkRecovered));
    }

    [Fact]
    public void Scan_KeepsAnUnreadableChunkOnDiskAndMarksItCorrupt()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var partPath = workspace.Paths.ChunkPartPath(AudioSource.Mic, 1);

        // Not a WAV file at all, but long enough to look like one.
        File.WriteAllBytes(partPath, Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(0, session.RepairedChunks);
        Assert.Equal(1, session.CorruptChunks);

        // Raw bytes are never deleted: the file stays for inspection.
        Assert.True(File.Exists(partPath));
        Assert.Equal(512, new FileInfo(partPath).Length);

        var chunk = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Corrupt, chunk.Status);
        Assert.Equal(1, CountEvents(ReadEvents(workspace.Paths), SessionEventNames.ChunkCorrupt));
    }

    [Fact]
    public void Scan_ReconcilesAFinalWavAndMarksTheSessionInterruptedWhenItWasNeverStopped()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        WriteChunk(workspace.Paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        // The closed WAV's index row was never written, so recovery reconciles it to
        // durable instead of leaving it unindexed for downstream consumers to skip.
        Assert.Equal(1, session.RepairedChunks);
        Assert.Equal(0, session.CorruptChunks);

        var chunk = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Recovered, chunk.Status);
        Assert.Equal(2 * SecondBytes, chunk.ByteLength);

        var events = ReadEvents(workspace.Paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.ChunkRecovered));

        // The session was never cleanly stopped, so it stays interrupted even though its
        // one chunk was made durable.
        Assert.Equal(SessionStatus.Interrupted, workspace.Database.Sessions.Find(workspace.SessionId)!.Status);
    }

    [Fact]
    public void Scan_ReconcilesAFinalWavWhoseIndexRowIsStillOpenAfterAKillBetweenRenameAndUpsert()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);

        // The spool opened the chunk (open index row + .part), then the close path ran
        // far enough to atomically rename .part -> .wav, but the process was killed
        // before the closed-index upsert landed. The row is still 'open' while the WAV
        // is already durable on disk.
        WriteFinalWavWithOpenRow(workspace.Paths, workspace.Database, sequence: 1, dataBytes: 2 * SecondBytes);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var rowBefore = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Open, rowBefore.Status);
        Assert.True(File.Exists(workspace.Paths.ChunkFinalPath(AudioSource.Mic, 1)));
        Assert.False(File.Exists(workspace.Paths.ChunkPartPath(AudioSource.Mic, 1)));

        var hashBefore = HashFile(workspace.Paths.ChunkFinalPath(AudioSource.Mic, 1));

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(1, session.RepairedChunks);
        Assert.Equal(0, session.CorruptChunks);

        // The dangling 'open' row is promoted to durable; the WAV itself is untouched.
        var rowAfter = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Recovered, rowAfter.Status);
        Assert.Equal(2 * SecondBytes, rowAfter.ByteLength);
        Assert.NotNull(rowAfter.ClosedAt);
        Assert.Equal(hashBefore, HashFile(workspace.Paths.ChunkFinalPath(AudioSource.Mic, 1)));

        var events = ReadEvents(workspace.Paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.ChunkRecovered));
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionRecovered));

        var stored = workspace.Database.Sessions.Find(workspace.SessionId)!;
        Assert.Equal(SessionStatus.Interrupted, stored.Status);
        Assert.Equal(2_000, stored.DurationMs);
        var sessionRecovered = Assert.Single(events, e => Name(e) == SessionEventNames.SessionRecovered);
        Assert.Equal(2_000, sessionRecovered.GetProperty("at_ms").GetInt64());
    }

    [Fact]
    public void Scan_NeverOverwritesAClosedWavWhenASameSequencePartCollides()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // A valid, durably-closed chunk (sequence 1) whose index row is CLOSED.
        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        workspace.Database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = AudioChunkRecord.BuildId(paths.SessionId, AudioSource.Mic, 1),
            SessionId = paths.SessionId,
            Source = AudioSource.Mic,
            Sequence = 1,
            RelativePath = paths.RelativeChunkPath(AudioSource.Mic, 1),
            StartMs = 0,
            EndMs = 2_000,
            Format = Format,
            ByteLength = 2 * SecondBytes,
            Status = ChunkStates.Closed,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ClosedAt = DateTimeOffset.UnixEpoch,
        });
        WriteManifest(paths, SessionStatus.Recording);

        var closedHashBefore = HashFile(paths.ChunkFinalPath(AudioSource.Mic, 1));

        // A stale/duplicate .part for the SAME sequence with DIFFERENT content.
        // Without the collision guard, recovery would File.Move(..., overwrite: true)
        // and irreversibly replace the closed WAV.
        WritePartWithData(paths, sequence: 1, dataBytes: 3 * SecondBytes);

        Assert.True(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));
        Assert.True(File.Exists(paths.ChunkFinalPath(AudioSource.Mic, 1)));

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);

        // The closed WAV is byte-identical: recovery never overwrote it.
        Assert.Equal(closedHashBefore, HashFile(paths.ChunkFinalPath(AudioSource.Mic, 1)));

        // Its index row is still durably CLOSED, not downgraded by the collision.
        var row = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Closed, row.Status);
        Assert.Equal(2 * SecondBytes, row.ByteLength);

        // The collision is visible as a corrupt chunk, not silently resolved.
        Assert.Equal(1, session.CorruptChunks);
        Assert.Equal(0, session.RepairedChunks);

        // The stale .part is retained on disk as .collided, not deleted and not
        // renamed over the WAV.
        Assert.False(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));
        var collided = paths.ChunkPartPath(AudioSource.Mic, 1)
            + SessionRecoveryScanner.CollidedSuffix;
        Assert.True(File.Exists(collided));

        // One corrupt event for the collision; no recovery event for the .part.
        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.ChunkCorrupt));
        Assert.Equal(0, CountEvents(events, SessionEventNames.ChunkRecovered));

        // A second scan is idempotent: the .collided artifact is not re-enumerated as
        // a .part, and the session is already INTERRUPTED, so there is nothing to do.
        var second = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);
        Assert.Equal(0, second.RecoveredSessions);
    }

    [Fact]
    public void Scan_RetainsTheCollidingPartAndReconcilesTheFinalWavWhenItsRowIsOpen()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);

        // The crash window between the atomic rename and the closed-index upsert left
        // a durable .wav whose row is still 'open'. A stale .part for the same
        // sequence also exists, so recovery must not overwrite the .wav.
        WriteFinalWavWithOpenRow(workspace.Paths, workspace.Database, sequence: 1, dataBytes: 2 * SecondBytes);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var wavHashBefore = HashFile(workspace.Paths.ChunkFinalPath(AudioSource.Mic, 1));

        // A stale .part with different content for the same sequence.
        WritePartWithData(workspace.Paths, sequence: 1, dataBytes: 3 * SecondBytes);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);

        // The final WAV is byte-identical: the .part never overwrote it.
        Assert.Equal(wavHashBefore, HashFile(workspace.Paths.ChunkFinalPath(AudioSource.Mic, 1)));

        // The .part collision is reported as corrupt...
        Assert.Equal(1, session.CorruptChunks);

        // ...and the .wav's open row is reconciled to durable by the final-WAV pass.
        var row = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Recovered, row.Status);
        Assert.Equal(2 * SecondBytes, row.ByteLength);
        Assert.Equal(1, session.RepairedChunks);

        // Both the corrupt collision event and the recovered-WAV event are present.
        var events = ReadEvents(workspace.Paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.ChunkCorrupt));
        Assert.Equal(1, CountEvents(events, SessionEventNames.ChunkRecovered));

        // The stale .part was retained as .collided, not renamed over the WAV.
        Assert.False(File.Exists(workspace.Paths.ChunkPartPath(AudioSource.Mic, 1)));
        Assert.True(File.Exists(
            workspace.Paths.ChunkPartPath(AudioSource.Mic, 1) + SessionRecoveryScanner.CollidedSuffix));
    }

    [Fact]
    public void Scan_AdoptsAnOrphanDirectoryThatHasAudioButNoSessionRow()
    {
        const string orphanId = "ses_20260915T150000Z_0000000a";
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Completed);

        var orphan = new SessionPaths(workspace.DataRoot, orphanId);
        orphan.CreateDirectories();
        WriteChunk(orphan, sequence: 1, dataBytes: 3 * SecondBytes, close: false);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(orphanId, session.SessionId);
        Assert.Equal(1, session.RepairedChunks);

        var stored = workspace.Database.Sessions.Find(orphanId);
        Assert.NotNull(stored);
        Assert.Equal(SessionStatus.Interrupted, stored.Status);
        Assert.Equal("(recovered session)", stored.Title);
    }

    [Fact]
    public void Scan_LeavesACleanlyCompletedSessionAlone()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Completed);
        WriteChunk(workspace.Paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        WriteManifest(workspace.Paths, SessionStatus.Completed);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.False(report.HasFindings);
        Assert.Equal(1, report.ScannedSessions);
        Assert.Equal(SessionStatus.Completed, workspace.Database.Sessions.Find(workspace.SessionId)!.Status);
    }

    [Fact]
    public void Scan_RepairsAStrayArtifactOnACompletedSessionWithoutDowngradingIt()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Completed);
        var paths = workspace.Paths;

        // A session that stopped cleanly, whose directory nevertheless holds a later
        // stray `.part` with real audio in it (a duplicate or a late close). The session
        // was cleanly stopped, so docs/DATA_MODEL.md section 1 and
        // docs/ARCHITECTURE.md section 20 say it must stay COMPLETED: recovery may repair
        // the stray artifact, but it must not reclassify the session or clear the
        // clean-stop timestamp it already recorded.
        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        WriteManifest(paths, SessionStatus.Completed);

        var stoppedAt = new DateTimeOffset(2026, 9, 15, 14, 5, 0, TimeSpan.Zero);
        workspace.Database.Sessions.UpdateLifecycle(
            workspace.SessionId,
            SessionStatus.Completed,
            stoppedAt,
            stoppedAt,
            durationMs: 2_000);

        WritePartWithData(paths, sequence: 2, dataBytes: 3 * SecondBytes);
        Assert.True(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 2)));

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Equal(2, session.RepairedChunks);
        Assert.False(session.Degraded);

        // The stray audio is made durable...
        Assert.False(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 2)));
        Assert.True(File.Exists(paths.ChunkFinalPath(AudioSource.Mic, 2)));
        var recovered = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 2)!;
        Assert.Equal(ChunkStates.Recovered, recovered.Status);
        Assert.Equal(3 * SecondBytes, recovered.ByteLength);

        // ...but the session keeps its terminal status, its clean-stop timestamp and its
        // duration, and is never stamped as recovered.
        var stored = workspace.Database.Sessions.Find(workspace.SessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);
        Assert.Equal(stoppedAt, stored.StoppedAt);
        Assert.Equal(2_000, stored.DurationMs);

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.Equal(SessionStatus.Completed, manifest!.Status);
        Assert.False(manifest.Degraded);
        Assert.Null(manifest.RecoveredAt);

        // The report still states plainly that a stray artifact had to be dealt with.
        var recoveredEvent = Assert.Single(ReadEvents(paths), e => Name(e) == SessionEventNames.SessionRecovered);
        Assert.Contains(
            "stopped cleanly but held a stray artifact",
            recoveredEvent.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ReconcilesAChunkLeftBehindWhileTheSessionWasFinalizing()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Finalizing);
        var paths = workspace.Paths;

        // The crash window the FINALIZING checkpoint exists for: capture has ended and the
        // artifacts are being closed, so the process died after the atomic rename but
        // before the closed-index upsert. The session is not terminal, so recovery has to
        // reconcile the WAV and state that this session never finished.
        WriteFinalWavWithOpenRow(paths, workspace.Database, sequence: 1, dataBytes: 2 * SecondBytes);
        WriteManifest(paths, SessionStatus.Finalizing);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(SessionStatus.Interrupted, session.Status);
        Assert.Equal(1, session.RepairedChunks);

        var row = workspace.Database.Chunks.Find(workspace.SessionId, AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Recovered, row.Status);
        Assert.Equal(2 * SecondBytes, row.ByteLength);

        var stored = workspace.Database.Sessions.Find(workspace.SessionId)!;
        Assert.Equal(SessionStatus.Interrupted, stored.Status);
        Assert.Equal(2_000, stored.DurationMs);

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.Equal(SessionStatus.Interrupted, manifest!.Status);
        Assert.NotNull(manifest.RecoveredAt);

        var sessionRecovered = Assert.Single(ReadEvents(paths), e => Name(e) == SessionEventNames.SessionRecovered);
        Assert.Equal(2_000, sessionRecovered.GetProperty("at_ms").GetInt64());
    }

    [Fact]
    public void Scan_LeavesALiveRecordingAloneWhileItHoldsTheLivenessMarker()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // A recording that is still in progress: its session row is legitimately
        // RECORDING, and at this instant its active chunk is a `.part` file. Artifact
        // presence alone cannot distinguish this from a killed session, so the recording
        // claims the exclusive liveness marker and the scan must stand down.
        WriteChunk(paths, sequence: 1, dataBytes: 3 * SecondBytes, close: false);
        WriteManifest(paths, SessionStatus.Recording);

        using var held = SessionRecordingLock.TryAcquire(paths.RecordingLockPath);
        Assert.NotNull(held);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.False(report.HasFindings);
        Assert.Equal(0, report.RecoveredSessions);

        // Nothing about the live session was touched: it is still recording, its active
        // chunk is still being written, and its event log gained no false recovery events.
        var stored = workspace.Database.Sessions.Find(workspace.SessionId)!;
        Assert.Equal(SessionStatus.Recording, stored.Status);
        Assert.Null(stored.StoppedAt);
        Assert.True(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));
        Assert.False(File.Exists(paths.ChunkFinalPath(AudioSource.Mic, 1)));

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.Equal(SessionStatus.Recording, manifest!.Status);
        Assert.Null(manifest.RecoveredAt);
        Assert.False(File.Exists(paths.EventsPath) && File.ReadAllText(paths.EventsPath).Contains("session.recovered", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_RecoversASessionWhoseLivenessMarkerWasReleasedByAForcedKill()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // The same session as above, except the process was killed: the artifact and the
        // RECORDING row are identical, but the marker is free because the operating
        // system released the handle. Recovery must still run, otherwise a forced kill
        // would stop being recoverable.
        WriteChunk(paths, sequence: 1, dataBytes: 3 * SecondBytes, close: false);
        WriteManifest(paths, SessionStatus.Recording);

        var killed = SessionRecordingLock.TryAcquire(paths.RecordingLockPath);
        Assert.NotNull(killed);
        killed!.Dispose();

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(1, session.RepairedChunks);
        Assert.Equal(SessionStatus.Interrupted, workspace.Database.Sessions.Find(workspace.SessionId)!.Status);
    }

    [Fact]
    public void Scan_ReportsUnreadableSessionsWithoutAbortingTheWholeScan()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        WriteChunk(workspace.Paths, sequence: 1, dataBytes: 2 * SecondBytes, close: false);

        // A manifest that cannot be parsed must not stop recovery of the audio.
        File.WriteAllText(workspace.Paths.ManifestPath, "{ this is not json");

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.Single(report.Sessions);
        Assert.NotEmpty(report.Problems);
        Assert.Equal(1, report.RepairedChunks);
    }

    [Fact]
    public void RecoveryReport_DescribesItsFindings()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        WriteChunk(workspace.Paths, sequence: 1, dataBytes: 2 * SecondBytes, close: false);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.Contains("1 incomplete session(s)", report.Describe(), StringComparison.Ordinal);
        Assert.Equal(2 * SecondBytes, report.RecoveredDataBytes);
    }

    /// <summary>
    /// Writes a chunk the way the pipeline does: <c>close: true</c> finalizes it,
    /// <c>close: false</c> leaves the <c>.part</c> behind exactly as a process kill would.
    /// </summary>
    private static void WriteChunk(SessionPaths paths, int sequence, int dataBytes, bool close)
    {
        var writer = new WaveChunkWriter(
            paths.ChunkPartPath(AudioSource.Mic, sequence),
            paths.ChunkFinalPath(AudioSource.Mic, sequence),
            Format,
            capacityBytes: 60L * SecondBytes,
            sequence);

        var payload = new byte[dataBytes];
        new Random(sequence).NextBytes(payload);
        writer.Append(payload);

        if (close)
        {
            writer.Close(DateTimeOffset.UnixEpoch);
        }
        else
        {
            writer.Dispose();
        }
    }

    private static void WritePartWithData(SessionPaths paths, int sequence, int dataBytes)
    {
        var writer = new WaveChunkWriter(
            paths.ChunkPartPath(AudioSource.Mic, sequence),
            paths.ChunkFinalPath(AudioSource.Mic, sequence),
            Format,
            capacityBytes: 60L * SecondBytes,
            sequence);

        if (dataBytes > 0)
        {
            writer.Append(new byte[dataBytes]);
        }

        writer.Dispose();
    }

    /// <summary>
    /// Reproduces the crash window in <c>ChunkSpool.CloseCurrentChunk</c> between the
    /// atomic rename and the closed-index upsert: the WAV is finalized on disk, but the
    /// index row is still <c>open</c> from when the chunk was opened.
    /// </summary>
    private static void WriteFinalWavWithOpenRow(
        SessionPaths paths, MeetCapDatabase database, int sequence, int dataBytes)
    {
        var writer = new WaveChunkWriter(
            paths.ChunkPartPath(AudioSource.Mic, sequence),
            paths.ChunkFinalPath(AudioSource.Mic, sequence),
            Format,
            capacityBytes: 60L * SecondBytes,
            sequence);

        writer.Append(new byte[dataBytes]);
        writer.Close(DateTimeOffset.UnixEpoch); // patches header, validates, atomic rename

        database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = AudioChunkRecord.BuildId(paths.SessionId, AudioSource.Mic, sequence),
            SessionId = paths.SessionId,
            Source = AudioSource.Mic,
            Sequence = sequence,
            RelativePath = paths.RelativeChunkPath(AudioSource.Mic, sequence),
            StartMs = 0,
            EndMs = 0,
            Format = Format,
            ByteLength = 0,
            Status = ChunkStates.Open,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
    }

    private static void WriteManifest(SessionPaths paths, string status)
    {
        SessionManifestStore.Save(paths.ManifestPath, new SessionManifest
        {
            SessionId = paths.SessionId,
            Title = "Test Session",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = status,
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic },
            ChunkSeconds = 60,
            Capture = new[] { CaptureTrackInfo.From(AudioSource.Mic, new CaptureDeviceInfo("mic", "Mic", true), Format) },
        });
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? Name(JsonElement element)
        => element.TryGetProperty("event", out var name) ? name.GetString() : null;

    private static int CountEvents(IReadOnlyList<JsonElement> events, string name)
        => events.Count(e => Name(e) == name);

    private static IReadOnlyList<JsonElement> ReadEvents(SessionPaths paths)
    {
        if (!File.Exists(paths.EventsPath))
        {
            return Array.Empty<JsonElement>();
        }

        string[] lines;
        using (var stream = new FileStream(paths.EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            lines = reader.ReadToEnd().Split('\n');
        }

        var elements = new List<JsonElement>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            elements.Add(document.RootElement.Clone());
        }

        return elements;
    }
}
