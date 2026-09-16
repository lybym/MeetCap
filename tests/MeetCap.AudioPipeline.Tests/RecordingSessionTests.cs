using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// End-to-end behaviour of one offline recording session, driven by a scripted capture
/// source. These are the tests behind the M1 acceptance criteria that can be proven
/// without audio hardware; the real-hardware run is documented separately in
/// <c>docs/M1_WINDOWS_VALIDATION.md</c>.
/// </summary>
public class RecordingSessionTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    /// <summary>One second of 48 kHz mono 16-bit audio.</summary>
    private const int SecondBytes = 96_000;

    [Fact]
    public async Task RunAsync_CleanStop_ClosesExactChunksAndCompletesTheSession()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Weekly Review");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1), "capture did not start");
        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 150_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(outcome.IsClean);
        Assert.Equal(3, outcome.ChunksClosed);
        Assert.Equal(150_000, outcome.DurationMs);
        Assert.Equal(60, harness.Settings.ChunkSeconds);

        // Exactly three durable chunks: 60 s, 60 s and the 30 s remainder.
        Assert.Equal(new[] { "000001.wav", "000002.wav", "000003.wav" }, ChunkFileNames(paths));
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.part"));

        var expectedBytes = new[] { 60 * SecondBytes, 60 * SecondBytes, 30 * SecondBytes };
        var files = ChunkFileNames(paths);
        for (var i = 0; i < files.Length; i++)
        {
            var validation = MeetCap.AudioPipeline.Wave.WaveChunkValidator.ValidateClosedFile(
                Path.Combine(paths.AudioDirectory(AudioSource.Mic), files[i]),
                Format);
            Assert.True(validation.IsValid, validation.Error);
            Assert.Equal(expectedBytes[i], validation.DataBytes);
        }

        var chunks = harness.Database.Chunks.ListForSession(session.SessionId);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(ChunkStates.Closed, c.Status));
        Assert.Equal(new long[] { 0, 60_000, 120_000 }, chunks.Select(c => c.StartMs));
        Assert.Equal(new long[] { 60_000, 120_000, 150_000 }, chunks.Select(c => c.EndMs));
        Assert.Equal(150 * SecondBytes, harness.Database.Chunks.TotalByteLengthForSession(session.SessionId));

        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);
        Assert.Equal(150_000, stored.DurationMs);
        Assert.NotNull(stored.StoppedAt);

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionStarted));
        Assert.Equal(3, CountEvents(events, SessionEventNames.ChunkOpened));
        Assert.Equal(3, CountEvents(events, SessionEventNames.ChunkClosed));
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionStopped));
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureGap));
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureDiscontinuity));
    }

    [Fact]
    public async Task RunAsync_CleanStop_RecordsTheClosedChunkTimelineInTheEventLog()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Timeline");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 60_000);
        cancellation.Cancel();
        await Finish(run);

        var firstClose = ReadEvents(paths).First(e => Name(e) == SessionEventNames.ChunkClosed);

        Assert.Equal("mic", firstClose.GetProperty("source").GetString());
        Assert.Equal("000001.wav", firstClose.GetProperty("chunk").GetString());
        Assert.Equal(0, firstClose.GetProperty("start_ms").GetInt64());
        Assert.Equal(60_000, firstClose.GetProperty("end_ms").GetInt64());
    }

    [Fact]
    public async Task RunAsync_DeviceGap_RecordsAnExplicitGapEvent()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, bufferSeconds: 30);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Gap");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 1_000);

        // The device skipped 2 seconds of audio and carried on.
        var skipFrames = 2 * Format.SampleRate + TestAudio.Frames(Format, 1_000);
        source.Emit(TestAudio.Packet(Format, skipFrames, TestAudio.Frames(Format, 1_000)));
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        var gap = Assert.Single(ReadEvents(paths), e => Name(e) == SessionEventNames.CaptureGap);
        Assert.Equal(2_000, gap.GetProperty("gap_ms").GetInt64());
    }

    [Fact]
    public async Task RunAsync_DeviceFlags_RecordsADiscontinuityEvent()
    {
        using var harness = new SessionHarness();
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Discontinuity");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 100);
        source.Emit(TestAudio.Packet(
            Format,
            TestAudio.Frames(Format, 100),
            TestAudio.Frames(Format, 100),
            AudioBufferFlags.DataDiscontinuity));
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.CaptureDiscontinuity));
    }

    [Fact]
    public async Task RunAsync_StreamStartDiscontinuityFlag_IsRecordedButDoesNotDegradeTheSession()
    {
        using var harness = new SessionHarness();
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Stream Start");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        // Windows flags the first buffer of a WASAPI stream with DataDiscontinuity
        // because there is nothing before it in that stream. That is not lost audio and
        // must not turn a clean recording into a failed command.
        source.Emit(TestAudio.Packet(
            Format,
            0,
            TestAudio.Frames(Format, 100),
            AudioBufferFlags.DataDiscontinuity));
        TestAudio.EmitSeconds(source, Format, TestAudio.Frames(Format, 100), milliseconds: 900);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.False(outcome.Degraded);
        Assert.True(outcome.IsClean);

        // The device flag is still visible in the session log.
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.CaptureDiscontinuity));

        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);
        Assert.False(ReadManifest(paths).Degraded);
    }

    [Fact]
    public async Task RunAsync_DeviceLoss_ClosesTheAudioAlreadyCapturedAndRecovers()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, maxDeviceRecoveryAttempts: 2);
        var first = new FakeCaptureSource(Format, harness.Device);
        var second = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(first);
        harness.Sources.Enqueue(second);

        var session = harness.Service.PrepareSession("Device Loss");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1));

        TestAudio.EmitSeconds(first, Format, 0, milliseconds: 20_000);
        first.Fail(new InvalidOperationException("device unplugged"));

        // Recovery waits ~1 s before reopening the endpoint.
        Assert.True(
            await Wait.UntilAsync(() => second.StartCount == 1, timeoutMs: 10_000),
            "capture was not reopened");

        TestAudio.EmitSeconds(second, Format, 0, milliseconds: 20_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        Assert.False(outcome.IsClean);
        Assert.Equal(2, outcome.ChunksClosed);

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLost));
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceRestored));
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));

        // The outage is carried as an explicit gap rather than shrinking the timeline.
        var restored = events.First(e => Name(e) == SessionEventNames.CaptureDeviceRestored);
        Assert.True(restored.GetProperty("gap_ms").GetInt64() >= 0);

        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task RunAsync_RecoveredDeviceWithADifferentFormat_EndsTheSessionInsteadOfMislabelingAudio()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, maxDeviceRecoveryAttempts: 2);
        var first = new FakeCaptureSource(Format, harness.Device);

        // The endpoint comes back at 44.1 kHz stereo float. The session's chunk headers,
        // chunk index and capture timeline are already written against 48 kHz mono PCM,
        // so continuing would write these bytes under the wrong header: validation would
        // still pass and the chunk would play at the wrong speed.
        var changedFormat = new AudioFormat(44_100, 2, 32, AudioSampleFormat.IeeeFloat);
        var second = new FakeCaptureSource(changedFormat, harness.Device);
        harness.Sources.Enqueue(first);
        harness.Sources.Enqueue(second);

        var session = harness.Service.PrepareSession("Format Change");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1));

        TestAudio.EmitSeconds(first, Format, 0, milliseconds: 10_000);
        first.Fail(new InvalidOperationException("device unplugged"));

        // Recovery opens the endpoint again, finds the new format, and must end the
        // session. The replacement source is therefore never started: the factory is
        // asked for it (CreateCount 2), and the session ends without ever writing its
        // bytes.
        var outcome = await Finish(run);

        Assert.Equal(2, harness.Sources.CreateCount);
        Assert.Equal(0, second.StartCount);

        // The session stopped cleanly — capture ended and every artifact was closed — but
        // it is degraded because the endpoint changed format underneath it, so the CLI
        // must not report success (docs/DEVELOPMENT.md section 8).
        Assert.True(outcome.Degraded);
        Assert.False(outcome.IsClean);
        Assert.Equal("device_format_changed", outcome.EndReason);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.ChunksClosed);

        var events = ReadEvents(paths);

        // The change is stated explicitly, with both formats, instead of being inferred
        // from a session that merely stopped early.
        var changed = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureFormatChanged);
        var detail = changed.GetProperty("detail").GetString()!;

        // Both formats are named, so the artifact can be audited without re-deriving why
        // the session ended. The exact rendering comes from AudioFormat.ToString().
        Assert.Contains(changedFormat.ToString(), detail, StringComparison.Ordinal);
        Assert.Contains(Format.ToString(), detail, StringComparison.Ordinal);
        Assert.Contains("ieee_float", detail, StringComparison.Ordinal);
        Assert.Contains("16-bit pcm", detail, StringComparison.Ordinal);

        // The session ends through the same visible fatal path a lost device uses.
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));

        // What was captured at the session format is durable under that format's header.
        var chunk = Assert.Single(ChunkFileNames(paths));
        var validation = MeetCap.AudioPipeline.Wave.WaveChunkValidator.ValidateClosedFile(
            Path.Combine(paths.AudioDirectory(AudioSource.Mic), chunk),
            Format);
        Assert.True(validation.IsValid, validation.Error);

        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);
        Assert.NotNull(stored.StoppedAt);
    }

    [Fact]
    public async Task RunAsync_HoldsTheSessionRecordingLockWhileRecordingAndReleasesItAfterwards()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Liveness");
        var paths = Paths(harness, session);

        // The marker is claimed by PrepareSession, before the session is published: a
        // session that is visible to `meetcap status` must always already be owned.
        Assert.True(SessionRecordingLock.IsHeld(paths.RecordingLockPath));

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        // While recording, the exclusive marker tells `meetcap status` (running in another
        // process) that this session is live and must not be recovered.
        Assert.True(
            await Wait.UntilAsync(() => SessionRecordingLock.IsHeld(paths.RecordingLockPath)),
            "the recording did not hold its session liveness marker");

        cancellation.Cancel();
        var outcome = await Finish(run);

        Assert.True(outcome.IsClean);

        // The marker is released, and the session is terminal, so a later scan sees a
        // completed session rather than one that still owns the recording surface.
        Assert.False(SessionRecordingLock.IsHeld(paths.RecordingLockPath));

        var reacquired = SessionRecordingLock.TryAcquire(paths.RecordingLockPath);
        Assert.NotNull(reacquired);
        reacquired!.Dispose();
    }

    [Fact]
    public async Task PrepareSession_AStartupScanInThePrepareToRunWindowCannotMutateTheSession()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        // The cross-process race the liveness marker has to close: `meetcap start` publishes
        // the session (manifest plus CREATED row) and only then begins recording, while a
        // concurrent `meetcap status` runs the startup recovery scan. If the marker were not
        // already held at publication, the scan would see an unowned CREATED session, rewrite
        // it to INTERRUPTED and stamp false recovery events on a recording that is perfectly
        // healthy — and `meetcap stop` could no longer find it.
        var session = harness.Service.PrepareSession("Raced");
        var paths = Paths(harness, session);
        try
        {
            // Stand in for the other process's `meetcap status`: same scanner, same data root.
            var report = harness.Service.RunStartupRecovery();

            // The live session's directory really was visited — it is skipped because it is
            // owned, not because the scan never reached it. (The harness seeds one extra
            // session directory of its own, which has no marker and is legitimately adopted,
            // so the report is not empty; the live session simply does not appear in it.)
            var directories = Directory.GetDirectories(Path.Combine(harness.DataRoot, "sessions"));
            Assert.Equal(directories.Length, report.ScannedSessions);
            Assert.Contains(session.SessionDirectory, directories, StringComparer.OrdinalIgnoreCase);
            Assert.True(
                SessionRecordingLock.IsHeld(paths.RecordingLockPath),
                "the prepared session did not claim its liveness marker");
            Assert.DoesNotContain(session.SessionId, report.Sessions.Select(s => s.SessionId));

            // No problem was reported *about the live session*: the scan did not read,
            // repair or rewrite anything belonging to it.
            Assert.DoesNotContain(
                report.Problems,
                problem => problem.Contains(session.SessionId, StringComparison.Ordinal));

            // The live session is untouched: still CREATED, not degraded, not recovered.
            var stored = harness.Database.Sessions.Find(session.SessionId)!;
            Assert.Equal(SessionStatus.Created, stored.Status);
            Assert.Null(stored.StoppedAt);

            var manifest = ReadManifest(paths);
            Assert.Equal(SessionStatus.Created, manifest.Status);
            Assert.Null(manifest.RecoveredAt);
            Assert.False(manifest.Degraded);

            // The clean event log gained no fabricated failure semantics.
            Assert.DoesNotContain(
                ReadEvents(paths),
                e => Name(e) == SessionEventNames.SessionRecovered);

            // `meetcap stop` still finds the session, so a healthy recording stays stoppable.
            var active = harness.Service.FindActiveSession();
            Assert.NotNull(active);
            Assert.Equal(session.SessionId, active!.SessionId);

            // And the recording that follows the scan still completes cleanly.
            using var cancellation = new CancellationTokenSource();
            var run = session.RunAsync(cancellation.Token);
            Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
            TestAudio.EmitSeconds(source, Format, 0, milliseconds: 5_000);
            cancellation.Cancel();

            var outcome = await Finish(run);

            Assert.True(outcome.IsClean);
            Assert.Equal(0, CountEvents(ReadEvents(paths), SessionEventNames.SessionRecovered));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task RecordingWithoutTheClaimedMarker_FailsLoudlyInsteadOfRacingRecovery()
    {
        using var harness = new SessionHarness();

        var prepared = harness.Service.PrepareSession("Releases");
        var paths = Paths(harness, prepared);

        // A caller that abandons the prepared session must not leave the marker held: an
        // unheld marker is what tells a later startup scan the session was not cleanly
        // stopped and may be adopted.
        Assert.True(SessionRecordingLock.IsHeld(paths.RecordingLockPath));
        prepared.Dispose();
        Assert.False(SessionRecordingLock.IsHeld(paths.RecordingLockPath));

        // Recording without the marker would let a startup recovery scan reconcile the
        // chunk surface out from under the live session, so it must fail loudly rather
        // than proceed unowned.
        var unowned = new RecordingSession(
            paths,
            harness.Settings,
            harness.Platform,
            harness.Database,
            new JsonlSessionEventSink(paths.EventsPath),
            harness.Device,
            harness.Clock,
            ReadManifest(paths),
            maxDeviceRecoveryAttempts: 0,
            recordingLock: null);

        var error = await Assert.ThrowsAsync<MeetCapException>(() => unowned.RunAsync());
        Assert.Contains("liveness marker", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DeviceLossThatCannotBeRecovered_EndsDegradedButKeepsTheAudio()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, maxDeviceRecoveryAttempts: 1);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);
        harness.Sources.EnqueueFailure(new DeviceUnavailableException("no device"));

        var session = harness.Service.PrepareSession("Unrecoverable");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 5_000);
        source.Fail(new InvalidOperationException("device unplugged"));

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        Assert.False(outcome.IsClean);
        Assert.Equal("device_lost", outcome.EndReason);
        Assert.Equal(1, outcome.ChunksClosed);

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLost));
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));

        // The audio captured before the device vanished is durable.
        Assert.Single(ChunkFileNames(paths));
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.part"));
    }

    [Fact]
    public async Task RunAsync_CaptureCannotStart_MarksTheSessionInterrupted()
    {
        using var harness = new SessionHarness(maxDeviceRecoveryAttempts: 0);
        var source = new FakeCaptureSource(Format, harness.Device)
        {
            StartError = new DeviceUnavailableException("busy"),
        };
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Never Started");
        var paths = Paths(harness, session);

        var outcome = await Finish(session.RunAsync());

        Assert.Equal(SessionStatus.Interrupted, outcome.Status);
        Assert.False(outcome.IsClean);
        Assert.Equal("capture_start_failed", outcome.EndReason);
        Assert.Equal(0, outcome.ChunksClosed);

        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal(SessionStatus.Interrupted, stored.Status);

        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.SessionStopped));
    }

    [Fact]
    public async Task RunAsync_StopRequestFile_EndsTheSessionCleanly()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("External Stop");
        var paths = Paths(harness, session);

        var run = session.RunAsync();
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 10_000);

        // This is exactly what `meetcap stop` does in another process.
        new SessionStopSignal(paths.StopRequestPath).Request("meetcap stop");

        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(outcome.IsClean);
        Assert.Equal("stop_requested", outcome.EndReason);
        Assert.Equal(1, outcome.ChunksClosed);

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionStopRequested));

        // The marker is cleaned up so the directory does not look like it is still stopping.
        Assert.False(File.Exists(paths.StopRequestPath));
    }

    [Fact]
    public async Task RunAsync_StopRequestedBeforeCaptureStarts_IsNotDiscarded()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Early Stop");
        var paths = Paths(harness, session);

        // `meetcap stop` can land in the window between the session row existing and
        // capture starting. That request must survive, otherwise the recording runs
        // forever and `meetcap stop` reports a timeout.
        new SessionStopSignal(paths.StopRequestPath).Request("meetcap stop");

        var outcome = await Wait.ForAsync(
            session.RunAsync(),
            timeoutMs: 20_000,
            "a session whose stop was requested before capture started");

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal("stop_requested", outcome.EndReason);
        Assert.False(File.Exists(paths.StopRequestPath));
    }

    [Fact]
    public async Task RunAsync_LowDiskSpace_WarnsButKeepsRecording()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Low Disk");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 5_000);

        // Below the configured minimum (5 GB) but far above the hard floor.
        harness.Disk.FreeBytes = 1L * 1024 * 1024 * 1024;
        harness.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(await Wait.UntilAsync(() => CountDiskEvents(paths) > 0, timeoutMs: 5_000));

        cancellation.Cancel();
        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.StorageLowDiskSpace));
    }

    [Fact]
    public async Task RunAsync_CriticalDiskSpace_StopsTheSessionVisibly()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Disk Full");
        var paths = Paths(harness, session);

        var run = session.RunAsync();
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 5_000);
        harness.Disk.FreeBytes = 1_000;
        harness.Clock.Advance(TimeSpan.FromSeconds(11));

        var outcome = await Wait.UntilAsync(() => run.IsCompleted, timeoutMs: 10_000)
            ? await run
            : throw new TimeoutException("the session did not stop when free space ran out");

        Assert.Equal("disk_exhausted", outcome.EndReason);
        Assert.True(outcome.Degraded);
        Assert.Equal(SessionStatus.Completed, outcome.Status);

        // Everything captured before the stop is closed and readable.
        Assert.Equal(1, outcome.ChunksClosed);
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.part"));
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.StorageDiskExhausted));
    }

    [Fact]
    public async Task RunAsync_DiskProbeFailure_IsReportedOnceAndRecordingContinues()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Probe Failure");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 5_000);

        harness.Disk.Failure = new MeetCapException("volume query failed");
        harness.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(await Wait.UntilAsync(() => CountEvents(ReadEvents(paths), SessionEventNames.StorageProbeFailed) > 0, timeoutMs: 5_000));

        harness.Clock.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(400);

        cancellation.Cancel();
        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.StorageProbeFailed));
        Assert.Equal(1, outcome.ChunksClosed);
    }

    [Fact]
    public async Task RunAsync_QueueOverflow_ReportsDroppedAudioInsteadOfBlockingCapture()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, bufferSeconds: 1);
        var source = new FakeCaptureSource(Format, harness.Device);
        source.OnStart = s =>
        {
            // A burst far larger than the bounded queue, produced on the capture thread.
            for (var i = 0; i < 20_000; i++)
            {
                s.Emit(TestAudio.Packet(Format, i * 4_800L, 4_800));
            }
        };
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Overflow");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
        Assert.True(
            await Wait.UntilAsync(() => CountEvents(ReadEvents(paths), SessionEventNames.CaptureBufferOverflow) > 0),
            "overflow was not reported");

        cancellation.Cancel();
        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);

        var overflow = ReadEvents(paths).First(e => Name(e) == SessionEventNames.CaptureBufferOverflow);
        Assert.True(overflow.GetProperty("count").GetInt32() > 0);

        // Capture itself was never blocked: the loop exited through cancellation, not a
        // stuck callback.
        Assert.True(outcome.DurationMs > 0);
    }

    [Fact]
    public async Task RunAsync_StalledConsumer_ReportsBackpressureAndKeepsTheQueueBounded()
    {
        // docs/RELIABILITY.md section 4: when downstream work cannot keep up, recording to
        // disk has priority, optional work is delayed, a degraded event is emitted, and RAM
        // must not grow without bound. A real slow disk cannot be produced on demand, so the
        // consumer itself is slowed through the harness seam; the queue is a real bounded
        // channel either way.
        using var harness = new SessionHarness(chunkSeconds: 60, bufferSeconds: 1);
        var source = new FakeCaptureSource(Format, harness.Device);
        source.OnStart = s =>
        {
            // 200 s of audio in 100 ms buffers, produced in one burst on the capture thread.
            TestAudio.EmitSeconds(s, Format, 0, milliseconds: 200_000);
        };
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession(
            "Stalled",
            afterPacketWritten: () => Thread.Sleep(20));
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        // The stall is measured against capture.buffer_seconds, so a one-second buffer is
        // reported after four housekeeping ticks.
        Assert.True(
            await Wait.UntilAsync(
                () => CountEvents(ReadEvents(paths), SessionEventNames.CaptureConsumerStalled) > 0,
                timeoutMs: 30_000),
            "the stalled consumer was never reported");

        cancellation.Cancel();
        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);

        // Memory stayed bounded: the backlog never exceeded the configured bound, and the
        // accounting says so with the bound it actually used.
        Assert.Equal(QueueCapacityFor(harness.Settings), outcome.CaptureHealth.CapacityPackets);
        Assert.True(outcome.CaptureHealth.PeakQueuedPackets <= outcome.CaptureHealth.CapacityPackets);
        Assert.True(outcome.CaptureHealth.StallEvents > 0);
        Assert.True(outcome.CaptureHealth.LongestStallMs >= 1_000);

        var stalled = ReadEvents(paths).First(e => Name(e) == SessionEventNames.CaptureConsumerStalled);
        Assert.Equal("mic", stalled.GetProperty("source").GetString());
        Assert.True(stalled.GetProperty("count").GetInt32() > 0);

        // A stalled consumer is a bounded-buffer condition, not a capture failure: the
        // session still reaches a described terminal state with its terminal event.
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(1, CountEvents(ReadEvents(paths), SessionEventNames.SessionStopped));
    }

    [Fact]
    public async Task RunAsync_CleanSession_ReportsAnUnusedBufferAndNoGaps()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, bufferSeconds: 30);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Quiet");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 5_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.IsClean);
        Assert.Equal(0, outcome.GapCount);
        Assert.Equal(0, outcome.GapTotalMs);
        Assert.Equal(0, outcome.CaptureHealth.DroppedPackets);
        Assert.Equal(0, outcome.CaptureHealth.StallEvents);
        Assert.Equal(QueueCapacityFor(harness.Settings), outcome.CaptureHealth.CapacityPackets);

        // The bounded-buffer accounting is part of the durable session document, not only
        // of the returned result.
        var manifest = ReadManifest(paths);
        Assert.NotNull(manifest.CaptureHealth);
        Assert.Equal(outcome.CaptureHealth.CapacityPackets, manifest.CaptureHealth!.CapacityPackets);
        Assert.Equal(0, manifest.GapTotalMs);
        Assert.Equal(0, manifest.GapCount);
        Assert.False(manifest.GapsRemain);
    }

    [Fact]
    public async Task RunAsync_DeviceGap_IsPersistedAsMissingAudioInTheSessionDocument()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, bufferSeconds: 30);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Gap Accounting");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 1_000);
        var skipFrames = 2 * Format.SampleRate + TestAudio.Frames(Format, 1_000);
        source.Emit(TestAudio.Packet(Format, skipFrames, TestAudio.Frames(Format, 1_000)));
        cancellation.Cancel();

        var outcome = await Finish(run);

        // The discontinuity the device reported is quantified on the result and written to
        // session.json, so "how much audio is missing" survives as a durable fact rather
        // than being recoverable only from the log.
        Assert.Equal(1, outcome.GapCount);
        Assert.Equal(2_000, outcome.GapTotalMs);

        var manifest = ReadManifest(paths);
        Assert.Equal(1, manifest.GapCount);
        Assert.Equal(2_000, manifest.GapTotalMs);
        Assert.True(manifest.Degraded);
        Assert.NotNull(manifest.CaptureHealth);
        Assert.Equal(2_000, manifest.CaptureHealth!.GapTotalMs);
    }

    [Fact]
    public async Task RunAsync_QueueOverflow_IsCountedInTheSessionBufferAccounting()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, bufferSeconds: 1);
        var source = new FakeCaptureSource(Format, harness.Device);
        source.OnStart = s =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                s.Emit(TestAudio.Packet(Format, i * 4_800L, 4_800));
            }
        };
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Overflow Accounting");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
        Assert.True(
            await Wait.UntilAsync(() => CountEvents(ReadEvents(paths), SessionEventNames.CaptureBufferOverflow) > 0),
            "overflow was not reported");

        cancellation.Cancel();
        var outcome = await Finish(run);

        Assert.True(outcome.CaptureHealth.DroppedPackets > 0);
        Assert.True(outcome.CaptureHealth.OverflowEvents > 0);
        Assert.Equal(QueueCapacityFor(harness.Settings), outcome.CaptureHealth.CapacityPackets);
        Assert.True(outcome.CaptureHealth.PeakQueuedPackets <= outcome.CaptureHealth.CapacityPackets);

        // The overflow event reports the running drop total instead of a per-event delta, so
        // an operator can see how much audio the bound cost without adding up events.
        var overflow = ReadEvents(paths).First(e => Name(e) == SessionEventNames.CaptureBufferOverflow);
        Assert.True(overflow.GetProperty("count").GetInt32() > 0);

        var manifest = ReadManifest(paths);
        Assert.NotNull(manifest.CaptureHealth);
        Assert.True(manifest.CaptureHealth!.DroppedPackets > 0);
    }

    private static int QueueCapacityFor(MeetCap.Core.Capture.CaptureSettings settings)
        => Math.Max(8, settings.BufferSeconds * 100);

    [Fact]
    public void SessionManifest_SerializesTheCaptureHealthAndGapFieldsUnderTheirDocumentedNames()
    {
        // docs/DATA_MODEL.md section 3 documents the exact JSON keys of session.json, and
        // `meetcap session repair` / `meetcap status` are read by humans and scripts. The
        // wire names come from SessionManifestStore's snake_case policy, so they are pinned
        // here rather than only in prose: a rename of the C# property would otherwise change
        // the artifact contract silently.
        var manifest = new SessionManifest
        {
            SessionId = "ses_20260915T140000Z_0000000f",
            Title = "Wire names",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Completed,
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic },
            ChunkSeconds = 60,
            GapCount = 2,
            GapTotalMs = 3_000,
            GapsRemain = true,
            GapDetails = new[] { "mic 0..3000 ms (3000 ms, chunk 000001, not_captured): no durable chunk." },
            CaptureHealth = new AudioBufferHealth
            {
                CapacityPackets = 500,
                PeakQueuedPackets = 37,
                DroppedPackets = 4,
                OverflowEvents = 2,
                LongestStallMs = 1_250,
                StallEvents = 1,
                GapTotalMs = 3_000,
                GapCount = 2,
            },
        };

        var json = SessionManifestStore.Serialize(manifest);

        foreach (var key in new[]
                 {
                     "\"gap_count\"",
                     "\"gap_total_ms\"",
                     "\"gaps_remain\"",
                     "\"gap_details\"",
                     "\"capture_health\"",
                     "\"capacity_packets\"",
                     "\"peak_queued_packets\"",
                     "\"dropped_packets\"",
                     "\"overflow_events\"",
                     "\"longest_stall_ms\"",
                     "\"stall_events\"",
                     "\"is_degraded\"",
                 })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }

        Assert.Contains("\"capacity_packets\": 500", json, StringComparison.Ordinal);
        Assert.Contains("\"is_degraded\": true", json, StringComparison.Ordinal);

        // Round-trips through the same store the recording writes with.
        var path = Path.Combine(Path.GetTempPath(), "meetcap-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SessionManifestStore.Save(path, manifest);
            Assert.True(SessionManifestStore.TryLoad(path, out var loaded, out var error), error);
            Assert.Equal(500, loaded!.CaptureHealth!.CapacityPackets);
            Assert.True(loaded.CaptureHealth.IsDegraded);
            Assert.Equal(2, loaded.GapCount);
            Assert.Equal(3_000, loaded.GapTotalMs);
            Assert.True(loaded.GapsRemain);
            Assert.Single(loaded.GapDetails);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PrepareSession_WritesAManifestAndASessionRowBeforeRecordingStarts()
    {
        using var harness = new SessionHarness(chunkSeconds: 30);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Manifest");
        var paths = Paths(harness, session);

        Assert.True(File.Exists(paths.ManifestPath));
        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal(SessionStatus.Created, stored.Status);
        Assert.Equal("Manifest", stored.Title);
        Assert.Equal(SessionModes.Offline, stored.Mode);
        Assert.Equal(new[] { AudioSources.Mic }, stored.Tracks);

        var json = File.ReadAllText(paths.ManifestPath);
        Assert.Contains("\"session_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"chunk_seconds\": 30", json, StringComparison.Ordinal);

        // The config snapshot must never carry secret material.
        Assert.DoesNotContain("credential", stored.ConfigSnapshot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"chunk_seconds\":30", stored.ConfigSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareSession_RefusesToStartWithoutEnoughFreeSpace()
    {
        using var harness = new SessionHarness();
        harness.Disk.FreeBytes = 1L * 1024 * 1024 * 1024; // 1 GB, below the 5 GB minimum

        var sessionsRoot = Path.Combine(harness.DataRoot, SessionPaths.SessionsFolderName);
        var before = Directory.Exists(sessionsRoot) ? Directory.GetDirectories(sessionsRoot).Length : 0;

        var error = Assert.Throws<InsufficientDiskSpaceException>(() => harness.Service.PrepareSession("No Space"));

        Assert.Contains("storage.minimum_free_space_gb", error.Message, StringComparison.Ordinal);

        // The check runs before any artifact is written.
        var after = Directory.Exists(sessionsRoot) ? Directory.GetDirectories(sessionsRoot).Length : 0;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task RunAsync_RecordsTheCaptureDeviceAndNativeFormatInTheManifest()
    {
        using var harness = new SessionHarness();
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Track Info");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
        cancellation.Cancel();
        await Finish(run);

        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error);
        Assert.True(manifest is not null, error);

        var track = Assert.Single(manifest!.Capture);
        Assert.Equal("mic", track.Source);
        Assert.Equal("mic-default", track.DeviceId);
        Assert.Equal(48_000, track.SampleRate);
        Assert.Equal("pcm", track.SampleFormat);
        Assert.Equal(SessionStatus.Completed, manifest.Status);
        Assert.False(manifest.Degraded);
    }

    [Fact]
    public async Task RunAsync_WritesTheDocumentedFinalizingCheckpointBeforeCompleting()
    {
        using var harness = new SessionHarness(chunkSeconds: 60);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Finalizing");
        var paths = Paths(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1));
        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 10_000);
        cancellation.Cancel();

        await Finish(run);

        // The documented CREATED -> RECORDING -> FINALIZING -> COMPLETED lifecycle
        // writes an explicit 'session.stopping' checkpoint while artifacts are being
        // closed, so a crash during finalization is recoverable instead of looking like
        // a clean RECORDING.
        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.SessionStopping));

        Assert.Equal(SessionStatus.Completed, harness.Database.Sessions.Find(session.SessionId)!.Status);
    }

    private static SessionPaths Paths(SessionHarness harness, RecordingSession session)
        => new(harness.DataRoot, session.SessionId);

    /// <summary>
    /// Bounded wait for a recording session. A stalled session must fail the test with a
    /// clear message instead of hanging the suite.
    /// </summary>
    private static Task<RecordingSessionOutcome> Finish(Task<RecordingSessionOutcome> run)
        => Wait.ForAsync(run, timeoutMs: 60_000, "the recording session");

    private static SessionManifest ReadManifest(SessionPaths paths)
    {
        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error);
        Assert.True(manifest is not null, error);
        return manifest!;
    }

    private static string[] ChunkFileNames(SessionPaths paths)
        => Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.wav")
            .Select(p => Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static string? Name(JsonElement element)
        => element.TryGetProperty("event", out var name) ? name.GetString() : null;

    private static int CountEvents(IReadOnlyList<JsonElement> events, string name)
        => events.Count(e => Name(e) == name);

    private static int CountDiskEvents(SessionPaths paths)
        => CountEvents(ReadEvents(paths), SessionEventNames.StorageLowDiskSpace);

    private static IReadOnlyList<JsonElement> ReadEvents(SessionPaths paths)
    {
        if (!File.Exists(paths.EventsPath))
        {
            return Array.Empty<JsonElement>();
        }

        // The recording session keeps the log open for writing, so read it with
        // read/write sharing rather than taking an exclusive read handle.
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
