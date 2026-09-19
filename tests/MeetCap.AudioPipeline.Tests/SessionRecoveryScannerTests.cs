using System.Security.Cryptography;
using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
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
    public async Task Scan_LeavesAPreparedSessionAloneFromTheInstantItIsPublished()
    {
        // The cross-process race the liveness marker has to close. `meetcap start` publishes
        // the session — directory, manifest and CREATED row — and only then begins recording,
        // while `meetcap status` in another process runs this scan. Before the marker was
        // claimed at publication, a scan landing in that window saw an unowned CREATED session
        // and rewrote a perfectly healthy recording to INTERRUPTED, stamped false
        // `session.recovered` events on its clean event log, and made `meetcap stop` — which
        // only finds CREATED/RECORDING sessions — unable to stop it.
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Raced");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        // A real recording writes its active chunk as a `.part` before it is ever closed, so
        // the scan in the window has one more reason to consider the session abandoned.
        var spool = new ChunkSpool(
            paths,
            AudioSource.Mic,
            Format,
            chunkSeconds: 60,
            harness.Database,
            new InMemorySessionEventSink(),
            harness.Clock);
        spool.Append(Packet(0, SecondBytes), new PacketTiming(0, 1_000, 0, false, false));

        try
        {
            Assert.True(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));

            // Run the scan the way `meetcap status` does: a separate process means a separate
            // database connection over the same file, not a shared in-memory object.
            var otherProcess = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
            var report = new SessionRecoveryScanner(otherProcess, harness.Clock).Scan(harness.DataRoot);

            Assert.DoesNotContain(session.SessionId, report.Sessions.Select(s => s.SessionId));
            Assert.DoesNotContain(
                report.Problems,
                problem => problem.Contains(session.SessionId, StringComparison.Ordinal));

            // Nothing belonging to the live session was touched.
            var stored = harness.Database.Sessions.Find(session.SessionId)!;
            Assert.Equal(SessionStatus.Created, stored.Status);
            Assert.Null(stored.StoppedAt);

            SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
            Assert.Equal(SessionStatus.Created, manifest!.Status);
            Assert.Null(manifest.RecoveredAt);
            Assert.False(manifest.Degraded);

            Assert.True(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));
            Assert.False(File.Exists(paths.ChunkFinalPath(AudioSource.Mic, 1)));
            Assert.DoesNotContain(
                ReadEvents(paths),
                e => Name(e) == SessionEventNames.SessionRecovered);

            // `meetcap stop` still finds the session, so a healthy recording stays stoppable.
            Assert.Equal(1, harness.Database.Sessions.CountActive());

            // Drop the simulated in-flight chunk, so what runs next is the ordinary recording
            // a caller would get: the scan is what was under test, and it left this artifact
            // exactly where it found it.
            spool.Dispose();
            File.Delete(paths.ChunkPartPath(AudioSource.Mic, 1));
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                       "Data Source=" + Path.Combine(harness.DataRoot, "meetcap.db")))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText =
                    "DELETE FROM audio_chunks WHERE session_id = $session AND source = 'mic' AND sequence = 1";
                command.Parameters.AddWithValue("$session", session.SessionId);
                command.ExecuteNonQuery();
            }

            // And the recording that follows the scan still completes cleanly.
            using var cancellation = new CancellationTokenSource();
            var run = session.RunAsync(cancellation.Token);
            Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
            TestAudio.EmitSeconds(source, Format, 0, milliseconds: 5_000);
            cancellation.Cancel();

            var outcome = await Wait.ForAsync(run, timeoutMs: 60_000, "the raced recording session");

            Assert.True(outcome.IsClean);
            Assert.Equal(0, CountEvents(ReadEvents(paths), SessionEventNames.SessionRecovered));
        }
        finally
        {
            session.Dispose();
        }
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
    public void Scan_WithAnUnrepairableChunk_ReportsTheGapAndNeverClaimsSuccess()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // A durable closed chunk, then a chunk whose bytes are not a WAV at all, then a
        // durable chunk after it. Recovery can do nothing for the middle chunk, so 2 s of
        // the session timeline has no durable audio and must be reported as such
        // (docs/RELIABILITY.md section 6).
        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 2_000, ChunkStates.Closed);

        File.WriteAllBytes(
            paths.ChunkPartPath(AudioSource.Mic, 2),
            Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());

        WriteChunk(paths, sequence: 3, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 3, startMs: 4_000, endMs: 6_000, ChunkStates.Closed);

        WriteManifest(paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(1, session.CorruptChunks);

        // The gap is the corrupt chunk's own stint, classified as unrecoverable bytes
        // rather than "the device skipped it".
        var gap = Assert.Single(session.Audit.Gaps);
        Assert.Equal(AudioGapReasons.ChunkUnreadable, gap.Reason);
        Assert.Equal(2, gap.Sequence);
        Assert.Equal(2_000, gap.GapMs);

        // Recovery must not claim success while that gap remains.
        Assert.True(session.RecoveryIncomplete);
        Assert.True(report.RecoveryIncomplete);
        Assert.Equal(2_000, report.RemainingGapMs);
        Assert.Contains("still have a known gap", report.Describe(), StringComparison.Ordinal);

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionRepairIncomplete));
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureGap));

        var repairEvent = Assert.Single(events, e => Name(e) == SessionEventNames.SessionRepairIncomplete);
        Assert.Equal(2_000, repairEvent.GetProperty("gap_ms").GetInt64());

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.True(manifest!.GapsRemain);
        Assert.Contains(manifest.GapDetails, line => line.Contains("chunk_unreadable", StringComparison.Ordinal));

        // The audit's finding lives in `gaps_remain` / `gap_details`, not in the live
        // measurement: `gap_total_ms` means "what the capture timeline observed while it was
        // alive" (docs/DATA_MODEL.md section 3) and this session never observed the loss.
        Assert.Equal(0, manifest.GapTotalMs);
    }

    [Fact]
    public void Scan_WithAContiguousTrack_FindsNoGap()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 2_000, ChunkStates.Closed);
        WritePartWithData(paths, sequence: 2, dataBytes: 2 * SecondBytes);
        IndexChunk(paths, workspace.Database, sequence: 2, startMs: 2_000, endMs: 4_000, ChunkStates.Open);

        WriteManifest(paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.Equal(1, session.RepairedChunks);
        Assert.False(session.Audit.HasGap);
        Assert.False(session.RecoveryIncomplete);
        Assert.False(report.RecoveryIncomplete);
        Assert.Equal(0, CountEvents(ReadEvents(paths), SessionEventNames.CaptureGap));
        Assert.Equal(0, CountEvents(ReadEvents(paths), SessionEventNames.SessionRepairIncomplete));

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.False(manifest!.GapsRemain);
    }

    [Fact]
    public void Scan_TargetingOneSession_LeavesUnrelatedSessionsAlone()
    {
        const string otherId = "ses_20260915T160000Z_0000000d";
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);

        WriteChunk(workspace.Paths, sequence: 1, dataBytes: 2 * SecondBytes, close: false);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var otherPaths = new SessionPaths(workspace.DataRoot, otherId);
        otherPaths.CreateDirectories();
        WriteChunk(otherPaths, sequence: 1, dataBytes: 2 * SecondBytes, close: false);
        workspace.Database.Sessions.Insert(new SessionRecord
        {
            Id = otherId,
            Title = "Other",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Recording,
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic },
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        var scanner = new SessionRecoveryScanner(workspace.Database, new FakeClock());
        var report = scanner.Scan(workspace.DataRoot, workspace.SessionId);

        Assert.Equal(1, report.ScannedSessions);
        Assert.Equal(workspace.SessionId, Assert.Single(report.Sessions).SessionId);

        // The unrelated session kept its recording surface untouched.
        Assert.True(File.Exists(otherPaths.ChunkPartPath(AudioSource.Mic, 1)));
        Assert.Equal(SessionStatus.Recording, workspace.Database.Sessions.Find(otherId)!.Status);
    }

    [Fact]
    public void Scan_DoesNotRepeatGapEventsOnALaterPass()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // One durable chunk, then a permanent hole, then another durable chunk: the gap is
        // a fact about the artifacts, so a second repair pass must report the same gap
        // without appending the same event again.
        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 2_000, ChunkStates.Closed);
        File.WriteAllBytes(
            paths.ChunkPartPath(AudioSource.Mic, 2),
            Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());
        WriteChunk(paths, sequence: 3, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 3, startMs: 4_000, endMs: 6_000, ChunkStates.Closed);
        WriteManifest(paths, SessionStatus.Recording);

        var scanner = new SessionRecoveryScanner(workspace.Database, new FakeClock());
        var first = scanner.Scan(workspace.DataRoot);
        var second = scanner.Scan(workspace.DataRoot);

        Assert.True(first.RecoveryIncomplete);
        Assert.True(second.RecoveryIncomplete);
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.CaptureGap));

        // The verdict is a fact about the artifacts, so a later pass must not restate it
        // either. An ungated append would add one identical copy per `meetcap status`,
        // `meetcap start` and `meetcap session repair`, and misstate how often the session
        // was found broken.
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.SessionRepairIncomplete));
    }

    [Fact]
    public void Scan_DoesNotRestateGapAndRepairVerdictsAcrossManyPasses()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 2_000, ChunkStates.Closed);
        File.WriteAllBytes(
            paths.ChunkPartPath(AudioSource.Mic, 2),
            Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());
        WriteChunk(paths, sequence: 3, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 3, startMs: 4_000, endMs: 6_000, ChunkStates.Closed);
        WriteManifest(paths, SessionStatus.Recording);

        var scanner = new SessionRecoveryScanner(workspace.Database, new FakeClock());
        for (var pass = 0; pass < 5; pass++)
        {
            scanner.Scan(workspace.DataRoot);
        }

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureGap));
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionRepairIncomplete));
    }

    [Fact]
    public void Scan_DoesNotRepeatAGapTheLiveRecordingAlreadyReported()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // The shape a real recording leaves behind when the device skips audio: durable audio
        // up to 10 s, a hole, then durable audio from 15 s. The live recorder reported that
        // hole while it was running, in the gap's own coordinates.
        WriteChunk(paths, sequence: 1, dataBytes: 10 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 10_000, ChunkStates.Closed);
        WriteChunk(paths, sequence: 2, dataBytes: 20 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 2, startMs: 15_000, endMs: 35_000, ChunkStates.Closed);

        // Plus a later, separate hole from 35 s to 40 s that the live recording never saw,
        // because the process died before it could report it.
        WriteChunk(paths, sequence: 3, dataBytes: 10 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 3, startMs: 40_000, endMs: 50_000, ChunkStates.Closed);

        File.AppendAllText(
            paths.EventsPath,
            // at_ms is the resume position, as the live recorder writes it; the interval is the
            // hole itself. The audit derives the same hole from the chunk index, so recovery
            // must recognise it and stay silent about that one.
            "{\"event\":\"capture.gap\",\"at_ms\":15000,\"source\":\"mic\"," +
            "\"gap_start_ms\":10000,\"gap_end_ms\":15000,\"gap_ms\":5000," +
            "\"detail\":\"the device skipped audio between buffers.\"}\n");

        WriteManifest(paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);
        var session = Assert.Single(report.Sessions);

        // The audit still finds both holes: recognising what was already recorded must not
        // hide a hole from the audit itself.
        Assert.Equal(2, session.Audit.Gaps.Count);
        Assert.True(session.RecoveryIncomplete);

        var gaps = ReadEvents(paths).Where(e => Name(e) == SessionEventNames.CaptureGap).ToList();

        // Two holes, two events: the live one and the newly discovered one. Reporting the
        // already-recorded hole a second time would describe one hole as two.
        Assert.Equal(2, gaps.Count);

        // Select the audit's own event by the interval it reports, not by the presence of a
        // field the live writer happens not to set: identity here is the hole, and a test that
        // keyed on the current wire shape would break for a reason unrelated to de-duplication.
        var newlyReported = Assert.Single(gaps, e => e.GetProperty("gap_start_ms").GetInt64() == 35_000);
        Assert.Equal(40_000, newlyReported.GetProperty("gap_end_ms").GetInt64());
    }

    [Fact]
    public void Scan_ReportsThePartOfAHoleAConcurrentLiveGapDoesNotCover()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // Durable audio 0..10 s, a stretch with no readable audio, durable audio from 15 s.
        WriteChunk(paths, sequence: 1, dataBytes: 10 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 10_000, ChunkStates.Closed);
        WriteChunk(paths, sequence: 2, dataBytes: 20 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 2, startMs: 15_000, endMs: 35_000, ChunkStates.Closed);

        // The live recorder reported a hole that starts where this one does but reaches past its
        // end. The audit's span is what the chunk index can prove, so the recording's longer
        // wall-clock measurement does not explain it away: the hole the audit found is still
        // real, and the recording's event stands beside it.
        File.AppendAllText(
            paths.EventsPath,
            "{\"event\":\"capture.gap\",\"at_ms\":17000,\"source\":\"mic\"," +
            "\"gap_start_ms\":10000,\"gap_end_ms\":17000,\"gap_ms\":7000," +
            "\"detail\":\"the device skipped audio between buffers.\"}\n");

        WriteManifest(paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.Single(report.Sessions);
        Assert.Equal(2, CountEvents(ReadEvents(paths), SessionEventNames.CaptureGap));
    }

    [Fact]
    public void Scan_ReportsTheUncoveredPartOfAHoleANarrowerLiveGapOverlaps()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // A 2 s stretch at 2..4 s holds an unreadable chunk, while the live recording only
        // observed a 500 ms device skip at its start. Suppressing the audit's gap because the two
        // intervals intersect would drop the only durable record of the other 1.5 s of lost audio
        // and leave the event log contradicting the session's own gaps_remain.
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 2_000, ChunkStates.Closed);
        IndexChunk(paths, workspace.Database, sequence: 2, startMs: 2_000, endMs: 4_000, ChunkStates.Corrupt);
        IndexChunk(paths, workspace.Database, sequence: 3, startMs: 4_000, endMs: 8_000, ChunkStates.Closed);

        File.AppendAllText(
            paths.EventsPath,
            "{\"event\":\"capture.gap\",\"at_ms\":2500,\"source\":\"mic\"," +
            "\"gap_start_ms\":2000,\"gap_end_ms\":2500,\"gap_ms\":500," +
            "\"detail\":\"the device skipped audio between buffers.\"}\n");

        WriteManifest(paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);
        var session = Assert.Single(report.Sessions);

        Assert.Equal(2_000, Assert.Single(session.Audit.Gaps).GapMs);

        var gaps = ReadEvents(paths).Where(e => Name(e) == SessionEventNames.CaptureGap).ToList();
        Assert.Equal(2, gaps.Count);

        // The audit's reason survives in the log. Losing it is how the loss disappears from the
        // only event a downstream reader sees.
        var auditGap = Assert.Single(gaps, e => e.GetProperty("gap_start_ms").GetInt64() == 2_000
                                                && e.GetProperty("gap_end_ms").GetInt64() == 4_000);
        Assert.Equal(AudioGapReasons.ChunkUnreadable, auditGap.GetProperty("reason").GetString());
        Assert.Equal(2_000, auditGap.GetProperty("gap_ms").GetInt64());

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.True(manifest!.GapsRemain);
    }

    [Fact]
    public void Scan_StillSuppressesAHoleALiveGapCoversMoreWidely()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        // The other direction: the recording's outage covers the span the audit can prove, within
        // the quantisation tolerance, so the hole is already reported and must not be reported
        // again. (A live interval that exceeds the audit's by far more than the tolerance is a
        // different measurement, not the same hole — see
        // Scan_ReportsThePartOfAHoleAConcurrentLiveGapDoesNotCover.)
        WriteChunk(paths, sequence: 1, dataBytes: 10 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 10_000, ChunkStates.Closed);
        WriteChunk(paths, sequence: 2, dataBytes: 20 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 2, startMs: 15_000, endMs: 35_000, ChunkStates.Closed);

        File.AppendAllText(
            paths.EventsPath,
            "{\"event\":\"capture.gap\",\"at_ms\":15010,\"source\":\"mic\"," +
            "\"gap_start_ms\":9990,\"gap_end_ms\":15010,\"gap_ms\":5020," +
            "\"detail\":\"the device skipped audio between buffers.\"}\n");

        WriteManifest(paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.Single(report.Sessions);
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.CaptureGap));
    }

    [Fact]
    public void Scan_StillSuppressesAHoleWhoseBoundariesDifferOnlyByQuantisation()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        WriteChunk(paths, sequence: 1, dataBytes: 10 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 10_000, ChunkStates.Closed);
        WriteChunk(paths, sequence: 2, dataBytes: 20 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 2, startMs: 15_000, endMs: 35_000, ChunkStates.Closed);

        // Both boundaries off by a few milliseconds: a wall-clock outage measurement against a
        // chunk-boundary derivation of the same hole. The tolerance exists for exactly this.
        File.AppendAllText(
            paths.EventsPath,
            "{\"event\":\"capture.gap\",\"at_ms\":15004,\"source\":\"mic\"," +
            "\"gap_start_ms\":9996,\"gap_end_ms\":15004,\"gap_ms\":5008," +
            "\"detail\":\"the device skipped audio between buffers.\"}\n");

        WriteManifest(paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.Single(report.Sessions);
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.CaptureGap));
    }

    [Fact]
    public void Scan_ReportsAnIncompleteRepairForACleanlyStoppedSessionThatStillHasAGap()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Completed);
        var paths = workspace.Paths;

        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 2_000, ChunkStates.Closed);

        // A permanently unreadable chunk, and nothing else to repair, on a session that had
        // already stopped cleanly. docs/DATA_MODEL.md section 4 says the event is written when
        // the timeline still holds a provable gap, which is exactly this case.
        File.WriteAllBytes(
            paths.ChunkPartPath(AudioSource.Mic, 2),
            Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());
        WriteManifest(paths, SessionStatus.Completed);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        var session = Assert.Single(report.Sessions);
        Assert.True(session.RecoveryIncomplete);

        // The unreadable bytes were classified, not repaired, so no audio was made durable.
        Assert.Equal(0, session.RepairedChunks);

        var events = ReadEvents(paths);

        // The pass did classify an artifact, so the session-level recovery event records that
        // (the pre-existing rule for a stray artifact on an already clean session), and the
        // incomplete event records that the session is still not whole. The audit's own
        // verdict, not the artifact count, is what decides whether recovery succeeded.
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionRecovered));
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionRepairIncomplete));

        // A terminal session still keeps its own metadata while it says it is not whole.
        var stored = workspace.Database.Sessions.Find(workspace.SessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);
        Assert.True(manifest!.GapsRemain);
        Assert.NotEmpty(manifest.GapDetails);
    }

    [Fact]
    public async Task Scan_DoesNotDoubleCountOrSuppressATerminalGapARealRecordingReported()
    {
        // Recovery runs against the sessions issue #34 added: a track that lost its endpoint and
        // never got it back now ends with a *placed* outage, so its timeline has an origin, closes
        // its chunks and reaches the recovery audit as an ordinary stopped session — where before
        // the fix the doomed track placed nothing and `session repair` had no timeline to audit.
        // Recovery must neither restate that terminal hole as a second gap (double counting the
        // missing audio) nor write the session off as whole while its own evidence says a stretch
        // of the timeline has no audio (docs/RELIABILITY.md sections 7 and 8.2).
        const int OutageSeconds = 12;

        // The seam dates a whole outage without parking: the clock jumps the outage's length at the
        // first reopen attempt, so the measurement that follows always observes it and the test
        // needs no shared gate (docs/DEVELOPMENT.md section 6).
        var dated = 0;
        SessionHarness? harness = null;
        harness = new SessionHarness(
            chunkSeconds: 1,
            bufferSeconds: 30,
            deviceRecoverySeconds: OutageSeconds + 1,
            beforeReopenAttempt: _ => _ =>
            {
                if (Interlocked.Exchange(ref dated, 1) == 0)
                {
                    harness!.Clock.Advance(TimeSpan.FromSeconds(OutageSeconds));
                }

                return Task.CompletedTask;
            });

        using var ownedHarness = harness;

        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.PrepareSessionDirect("Terminal gap cross-check");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1), "capture did not start");

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 10_000);

        // The endpoint never comes back, so the track measures its outage, spends its window and
        // ends fatally with the measured stretch named as a gap.
        harness.Devices.Replace();
        harness.Sources.EnqueueFailure(new DeviceUnavailableException("bluetooth endpoint disconnected"));
        harness.Sources.EnqueueFailure(new DeviceUnavailableException("bluetooth endpoint disconnected"));
        source.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        await Wait.ForAsync(run, timeoutMs: 60_000, "the terminal-gap recording");

        var eventLog = ReadEvents(paths);
        Assert.Equal(1, CountEvents(eventLog, SessionEventNames.CaptureDeviceLostFatal));

        var liveGap = Assert.Single(eventLog, e => Name(e) == SessionEventNames.CaptureGap);
        var liveStart = liveGap.GetProperty("gap_start_ms").GetInt64();
        var liveEnd = liveGap.GetProperty("gap_end_ms").GetInt64();
        Assert.Equal(10_000, liveStart);
        Assert.Equal(10_000 + (OutageSeconds * 1000L), liveEnd);

        // Another process scans the way `meetcap status` does.
        var otherProcess = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        var report = new SessionRecoveryScanner(otherProcess, harness.Clock)
            .Scan(harness.DataRoot, session.SessionId);

        // The terminal gap is the last thing on the track's timeline, so there is no later durable
        // chunk or chunk-number hole for the audit to find a second opinion in. Recovery therefore
        // has nothing to report and nothing to repair — which is the correct outcome, not a silent
        // success: had the audit decided the trailing stretch was a hole, it would have appended a
        // second `capture.gap` for audio the live recording had already accounted for.
        Assert.Empty(report.Sessions);
        Assert.False(report.RecoveryIncomplete);

        var afterScan = ReadEvents(paths);
        Assert.Equal(1, CountEvents(afterScan, SessionEventNames.CaptureGap));
        Assert.DoesNotContain(afterScan, e => Name(e) == SessionEventNames.SessionRepairIncomplete);

        // The one gap the session reported is still the ledger: recovery neither added to it nor
        // wrote the session off as still holding a hole.
        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error);
        Assert.True(manifest is not null, error);
        Assert.False(manifest!.GapsRemain);
        Assert.Empty(manifest.GapDetails);
        Assert.Equal(1, manifest.GapCount);
        Assert.Equal(OutageSeconds * 1000L, manifest.GapTotalMs);
    }

    [Fact]
    public async Task Scan_CrossChecksTheLiveGapAgainstTheAuditOfARealRecording()
    {
        // The invariant the de-duplication rests on, checked against a real recording rather than
        // a hand-written index: the live event and the audit must name the same hole with the same
        // interval, or recovery cannot recognise the hole it already reported.
        using var harness = new SessionHarness(chunkSeconds: 1, bufferSeconds: 30);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Cross check");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        // 1 s of audio, then a device-position skip that resumes at 5 s, then 20 s more. The chunk
        // that is open when the skip happens is closed at the boundary, so the hole spans two
        // chunks and the audit can see it.
        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 1_000);
        long frame = 5 * Format.SampleRate;
        for (var elapsed = 0; elapsed < 20_000; elapsed += 1_000)
        {
            source.Emit(TestAudio.Packet(Format, frame, TestAudio.Frames(Format, 1_000)));
            frame += TestAudio.Frames(Format, 1_000);
        }

        cancellation.Cancel();
        await Wait.ForAsync(run, timeoutMs: 60_000, "the cross-check recording");

        var liveGap = Assert.Single(ReadEvents(paths), e => Name(e) == SessionEventNames.CaptureGap);
        var liveStart = liveGap.GetProperty("gap_start_ms").GetInt64();
        var liveEnd = liveGap.GetProperty("gap_end_ms").GetInt64();

        // A stray .part, so the session is scanned and audited the way `meetcap status` audits a
        // session a killed process left behind.
        var stray = new WaveChunkWriter(
            paths.ChunkPartPath(AudioSource.Mic, 22),
            paths.ChunkFinalPath(AudioSource.Mic, 22),
            Format,
            capacityBytes: 60L * SecondBytes,
            22);
        stray.Append(new byte[SecondBytes]);
        stray.Dispose();

        // Another process scans, the way `meetcap status` does.
        var otherProcess = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        var report = new SessionRecoveryScanner(otherProcess, harness.Clock)
            .Scan(harness.DataRoot, session.SessionId);
        var recovered = Assert.Single(report.Sessions);

        var auditGap = Assert.Single(recovered.Audit.Gaps);
        Assert.Equal(liveStart, auditGap.StartMs);
        Assert.Equal(liveEnd, auditGap.EndMs);
        Assert.Equal(liveEnd, liveGap.GetProperty("at_ms").GetInt64());

        // Because both producers agree, the audit has nothing new to say: the live event already
        // reports the hole, so recovery must not append it a second time.
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.CaptureGap));
    }

    [Fact]
    public void Scan_LeavesTheLiveGapMeasurementAloneAndRecordsItsOwnFindingsSeparately()    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Completed);
        var paths = workspace.Paths;

        // The session recorded and measured its own timeline: no discontinuity, so its live
        // gap accounting is legitimately zero.
        WriteChunk(paths, sequence: 1, dataBytes: 2 * SecondBytes, close: true);
        IndexChunk(paths, workspace.Database, sequence: 1, startMs: 0, endMs: 2_000, ChunkStates.Closed);
        File.WriteAllBytes(
            paths.ChunkPartPath(AudioSource.Mic, 2),
            Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());
        WriteManifest(paths, SessionStatus.Completed);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.True(Assert.Single(report.Sessions).RecoveryIncomplete);

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out _);

        // `gap_count` / `gap_total_ms` mean "what the capture timeline observed while it was
        // alive" (docs/DATA_MODEL.md section 3). Recovery records what it found in
        // `gaps_remain` / `gap_details` instead of overwriting that measurement, so the field
        // never changes meaning and can never claim a loss the live recording did not measure.
        Assert.Equal(0, manifest!.GapCount);
        Assert.Equal(0, manifest.GapTotalMs);
        Assert.True(manifest.GapsRemain);
        Assert.NotEmpty(manifest.GapDetails);
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

    [Fact]
    public void RecoveryRepairsBothTracksOfAnInterruptedOnlineSession()
    {
        // The M2 guarantee this PR relies on per track: a process killed mid-recording leaves
        // an active .part per source, and startup recovery makes each one durable under its
        // own source tree. The scanner is source-agnostic by construction, so this pins that
        // an online session's loopback tree is repaired too, not only audio/mic/.
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        WriteChunk(workspace.Paths, AudioSource.Mic, sequence: 1, dataBytes: 2 * SecondBytes, close: false);
        WriteChunk(workspace.Paths, AudioSource.Loopback, sequence: 1, dataBytes: 3 * SecondBytes, close: false);

        // A finalized WAV whose index row is still `open` — the crash window between the
        // atomic rename and the closed-index upsert (SessionGapAuditor/CloseCurrentChunk).
        WriteFinalWavWithOpenRow(workspace.Paths, workspace.Database, AudioSource.Loopback, sequence: 2, dataBytes: SecondBytes);
        WriteManifest(workspace.Paths, SessionStatus.Recording);

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.Equal(1, report.RecoveredSessions);
        Assert.False(report.RecoveryIncomplete);

        // Both .part files became durable WAVs in their own source tree, and neither is left
        // behind for a later scan to re-repair.
        Assert.False(File.Exists(workspace.Paths.ChunkPartPath(AudioSource.Mic, 1)));
        Assert.False(File.Exists(workspace.Paths.ChunkPartPath(AudioSource.Loopback, 1)));
        Assert.True(File.Exists(workspace.Paths.ChunkFinalPath(AudioSource.Mic, 1)));
        Assert.True(File.Exists(workspace.Paths.ChunkFinalPath(AudioSource.Loopback, 1)));
        Assert.True(File.Exists(workspace.Paths.ChunkFinalPath(AudioSource.Loopback, 2)));

        // The index keeps the two sources distinct: each repaired chunk carries its own
        // source, and the renamed loopback chunk is no longer reported as open.
        var chunks = workspace.Database.Chunks.ListForSession(workspace.SessionId);
        var mic = chunks.Single(c => c.Source == AudioSource.Mic);
        Assert.Equal(ChunkStates.Recovered, mic.Status);

        var loopback = chunks.Where(c => c.Source == AudioSource.Loopback).OrderBy(c => c.Sequence).ToList();
        Assert.Equal(2, loopback.Count);
        Assert.All(loopback, c => Assert.NotEqual(ChunkStates.Open, c.Status));

        // The session stopped pretending to be recording.
        Assert.Equal(SessionStatus.Interrupted, workspace.Database.Sessions.Find(workspace.SessionId)!.Status);
    }

    /// <summary>
    /// Writes a chunk the way the pipeline does: <c>close: true</c> finalizes it,
    /// <c>close: false</c> leaves the <c>.part</c> behind exactly as a process kill would.
    /// </summary>
    private static void WriteChunk(SessionPaths paths, int sequence, int dataBytes, bool close)
        => WriteChunk(paths, AudioSource.Mic, sequence, dataBytes, close);

    private static void WriteChunk(SessionPaths paths, AudioSource source, int sequence, int dataBytes, bool close)
    {
        var writer = new WaveChunkWriter(
            paths.ChunkPartPath(source, sequence),
            paths.ChunkFinalPath(source, sequence),
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
        => WriteFinalWavWithOpenRow(paths, database, AudioSource.Mic, sequence, dataBytes);

    private static void WriteFinalWavWithOpenRow(
        SessionPaths paths, MeetCapDatabase database, AudioSource source, int sequence, int dataBytes)
    {
        var writer = new WaveChunkWriter(
            paths.ChunkPartPath(source, sequence),
            paths.ChunkFinalPath(source, sequence),
            Format,
            capacityBytes: 60L * SecondBytes,
            sequence);

        writer.Append(new byte[dataBytes]);
        writer.Close(DateTimeOffset.UnixEpoch); // patches header, validates, atomic rename

        database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = AudioChunkRecord.BuildId(paths.SessionId, source, sequence),
            SessionId = paths.SessionId,
            Source = source,
            Sequence = sequence,
            RelativePath = paths.RelativeChunkPath(source, sequence),
            StartMs = 0,
            EndMs = 0,
            Format = Format,
            ByteLength = 0,
            Status = ChunkStates.Open,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
    }

    /// <summary>
    /// Writes a chunk index row with an explicit timeline position, so the gap audit can
    /// reason about a track whose sequence numbering has a hole in it.
    /// </summary>
    private static void IndexChunk(
        SessionPaths paths,
        MeetCapDatabase database,
        int sequence,
        long startMs,
        long endMs,
        string status)
    {
        database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = AudioChunkRecord.BuildId(paths.SessionId, AudioSource.Mic, sequence),
            SessionId = paths.SessionId,
            Source = AudioSource.Mic,
            Sequence = sequence,
            RelativePath = paths.RelativeChunkPath(AudioSource.Mic, sequence),
            StartMs = startMs,
            EndMs = endMs,
            Format = Format,
            ByteLength = (endMs - startMs) * 96,
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ClosedAt = ChunkStates.IsDurable(status) ? DateTimeOffset.UnixEpoch : null,
        });
    }

    private static void WriteManifest(SessionPaths paths, string status)
    {        SessionManifestStore.Save(paths.ManifestPath, new SessionManifest
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
