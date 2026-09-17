using MeetCap.AudioPipeline;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Integration coverage for the M1 commands: <c>devices</c>, <c>start</c> and
/// <c>stop</c>, driven through the real command tree.
/// </summary>
public class CaptureCommandTests
{
    [Fact]
    public void Devices_ListsEndpointsAndMarksTheSystemDefault()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();
        harness.Platform.Devices.Replace(
            new CaptureDeviceInfo("mic-1", "USB Microphone", true),
            new CaptureDeviceInfo("mic-2", "Headset Microphone", false));

        var result = harness.Run("devices");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("USB Microphone", result.Output);
        Assert.Contains("Headset Microphone", result.Output);
        Assert.Contains("system default", result.Output);
        Assert.Contains("mic-1", result.Output);
        Assert.Contains("default (whatever Windows currently uses)", result.Output);

        // `devices` is read-only and must not create a database.
        Assert.False(File.Exists(Path.Combine(harness.DataRoot, "meetcap.db")));
    }

    [Fact]
    public void Devices_MarksTheConfiguredEndpoint()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig(microphoneDeviceId: "mic-2");
        harness.Platform.Devices.Replace(
            new CaptureDeviceInfo("mic-1", "USB Microphone", true),
            new CaptureDeviceInfo("mic-2", "Headset Microphone", false));

        var result = harness.Run("devices");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("configured", result.Output);
        Assert.Contains("mic-2", result.Output);
    }

    [Fact]
    public void Devices_WithNoHardware_SaysSoWithoutFailing()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();
        harness.Platform.Devices.Replace();

        var result = harness.Run("devices");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no active capture devices", result.Output);
    }

    [Fact]
    public async Task Start_WithOnlineMode_RecordsBothMicAndLoopbackTracks()
    {
        using var harness = CliHarness.Create();
        harness.WriteOnlineCaptureConfig(chunkSeconds: 1);
        harness.Platform.Devices.SetRenderDevices(
            new CaptureDeviceInfo("render-default", "Test Speakers", true));

        // `meetcap start --mode online` blocks while recording, so drive it from a
        // background thread and stop it through the real `meetcap stop` command.
        var startTask = Task.Run(() => harness.Run("start", "Remote Review", "--mode", "online"));

        var sessionDirectory = WaitForSessionDirectory(harness.DataRoot);
        Assert.NotNull(sessionDirectory);
        WaitForActiveSession(harness.DataRoot);

        var stop = harness.Run("stop");
        var start = await AwaitBounded(
            startTask,
            TimeSpan.FromSeconds(60),
            "meetcap start --mode online did not finish after meetcap stop");

        Assert.Equal(0, stop.ExitCode);
        Assert.Equal(0, start.ExitCode);
        Assert.Contains("mode: online", start.Output);

        // An online session creates independent mic and loopback chunk trees.
        var micChunks = Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "mic"), "*.wav");
        var loopbackChunks = Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "loopback"), "*.wav");
        Assert.NotEmpty(micChunks);
        Assert.NotEmpty(loopbackChunks);
        Assert.Empty(Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "mic"), "*.part"));
        Assert.Empty(Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "loopback"), "*.part"));

        var manifest = File.ReadAllText(Path.Combine(sessionDirectory!, "session.json"));
        Assert.Contains("\"mode\": \"online\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"mic\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"loopback\"", manifest, StringComparison.Ordinal);

        var events = File.ReadAllText(Path.Combine(sessionDirectory!, "events.jsonl"));
        // The event log is compact JSONL (no space after the colon), so the source labels
        // appear as "source":"mic" / "source":"loopback".
        Assert.Contains("\"source\":\"mic\"", events, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"loopback\"", events, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_WithOnlineMode_AndNoRenderDevice_FailsWithAnActionableMessage()
    {
        using var harness = CliHarness.Create();
        harness.WriteOnlineCaptureConfig();
        // No render device is offered, so loopback cannot start.
        harness.Platform.Devices.SetRenderDevices();

        var result = harness.Run("start", "No Loopback", "--mode", "online");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("render device", result.Error, StringComparison.OrdinalIgnoreCase);

        // No session directory may be created when the render device cannot be resolved.
        var sessionsRoot = Path.Combine(harness.DataRoot, "sessions");
        Assert.True(!Directory.Exists(sessionsRoot) || Directory.GetDirectories(sessionsRoot).Length == 0);
    }

    [Fact]
    public void Start_WithAnInvalidLoopbackMode_FailsBeforeAnySessionIsCreated()
    {
        using var harness = CliHarness.Create();
        harness.WriteOnlineCaptureConfig(loopbackMode: "bogus");
        harness.Platform.Devices.SetRenderDevices(
            new CaptureDeviceInfo("render-default", "Test Speakers", true));

        var result = harness.Run("start", "Bad Loopback", "--mode", "online");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("loopback_mode", result.Error, StringComparison.Ordinal);
        Assert.Contains("bogus", result.Error, StringComparison.Ordinal);
        Assert.Contains("system", result.Error, StringComparison.Ordinal);

        // No session directory, manifest or audio/loopback/ may exist: the mode is part of
        // "can this session record?", so it has to be answered before publication
        // (docs/M1_WINDOWS_VALIDATION.md section 13).
        var sessionsRoot = Path.Combine(harness.DataRoot, "sessions");
        Assert.True(!Directory.Exists(sessionsRoot) || Directory.GetDirectories(sessionsRoot).Length == 0);
    }

    [Fact]
    public void Start_WhenLoopbackCaptureCannotBeBuilt_PrintsTheActionableReasonOnStderr()
    {
        using var harness = CliHarness.Create();
        harness.WriteOnlineCaptureConfig(loopbackMode: "process", processName: "WeMeet");
        harness.Platform.Devices.SetRenderDevices(
            new CaptureDeviceInfo("render-default", "Test Speakers", true));

        // The process-loopback target is resolved by the platform boundary when it builds the
        // capture source, which is after the session has been published. The operator still
        // has to see why the start failed, not only a generic message: the session cannot
        // report that reason anywhere except events.jsonl unless the CLI relays it.
        var reason =
            "Process loopback target 'WeMeet' is not currently running. " +
            "Start the meeting application before beginning capture, or use the baseline 'system' loopback.";
        harness.Platform.Sources.LoopbackFallback = _ => throw new DeviceUnavailableException(reason);

        var result = harness.Run("start", "Process Loopback", "--mode", "online");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(reason, result.Error, StringComparison.Ordinal);

        // This failure happens after publication, so the session is left INTERRUPTED with an
        // empty loopback tree rather than not existing at all.
        var sessionsRoot = Path.Combine(harness.DataRoot, "sessions");
        var sessionDirectory = Directory.GetDirectories(sessionsRoot).Single();
        var manifest = File.ReadAllText(Path.Combine(sessionDirectory, "session.json"));
        Assert.Contains("\"status\": \"INTERRUPTED\"", manifest, StringComparison.Ordinal);
        Assert.Contains("capture_start_failed", manifest, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(sessionDirectory, "audio", "loopback")));
    }

    [Fact]
    public void Devices_ListsRenderEndpointsForTheLoopbackSource()
    {
        using var harness = CliHarness.Create();
        harness.WriteOnlineCaptureConfig(loopbackMode: "process", processName: "WeMeet", renderDeviceId: "render-2");
        harness.Platform.Devices.Replace(new CaptureDeviceInfo("mic-1", "USB Microphone", true));
        harness.Platform.Devices.SetRenderDevices(
            new CaptureDeviceInfo("render-1", "Studio Speakers", true),
            new CaptureDeviceInfo("render-2", "Meeting Headset", false));

        var result = harness.Run("devices");

        Assert.Equal(0, result.ExitCode);

        // The render endpoints are what system/process loopback captures from, so they are
        // listed separately from the capture endpoints.
        Assert.Contains("active render devices (loopback source):", result.Output, StringComparison.Ordinal);
        Assert.Contains("Studio Speakers", result.Output, StringComparison.Ordinal);
        Assert.Contains("Meeting Headset", result.Output, StringComparison.Ordinal);
        Assert.Contains("render-1", result.Output, StringComparison.Ordinal);
        Assert.Contains("render-2", result.Output, StringComparison.Ordinal);

        // The configured loopback target is named, and the configured render endpoint is
        // marked so an operator can see which one a recording would use.
        Assert.Contains("configured loopback: mode=process", result.Output, StringComparison.Ordinal);
        Assert.Contains("render device=render-2", result.Output, StringComparison.Ordinal);
        Assert.Contains("process='WeMeet'", result.Output, StringComparison.Ordinal);
        Assert.Contains("Meeting Headset  (configured)", result.Output, StringComparison.Ordinal);
        Assert.Contains("Studio Speakers  (system default)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Devices_WithNoRenderEndpoint_SaysSoWithoutFailing()
    {
        using var harness = CliHarness.Create();
        harness.WriteOnlineCaptureConfig();
        harness.Platform.Devices.SetRenderDevices();

        var result = harness.Run("devices");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no active render devices were found.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_WithAnUnknownMode_IsRejected()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        var result = harness.Run("start", "Hybrid", "--mode", "hybrid");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("hybrid", result.Error);
    }

    [Fact]
    public void Start_WithNoCaptureDevice_FailsWithAnActionableMessage()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();
        harness.Platform.Devices.Replace();

        var result = harness.Run("start", "No Mic");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No default capture device", result.Error);
        Assert.Contains("meetcap devices", result.Error);
    }

    [Fact]
    public void Start_WithInsufficientFreeSpace_FailsWithAnActionableMessage()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig(minimumFreeSpaceGb: 5);
        harness.Platform.Disk.FreeBytes = 512L * 1024 * 1024;

        var result = harness.Run("start", "No Space");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("storage.minimum_free_space_gb", result.Error);
    }

    [Fact]
    public void Stop_WithoutAnActiveSession_Fails()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        var result = harness.Run("stop");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no active recording session", result.Error);
    }

    [Fact]
    public async Task Start_ThenStop_ProducesACompletedSessionWithDurableChunks()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig(chunkSeconds: 1);

        // `meetcap start` blocks while recording, so drive it from a background thread
        // and stop it through the real `meetcap stop` command.
        var startTask = Task.Run(() => harness.Run("start", "Weekly Review"));

        var sessionDirectory = WaitForSessionDirectory(harness.DataRoot);
        Assert.NotNull(sessionDirectory);

        // `meetcap stop` finds the active session through the database, and PrepareSession
        // writes the session manifest before it inserts that row. Waiting on the manifest
        // alone (WaitForSessionDirectory) leaves a window where stop sees no active
        // session, writes no stop marker, and the recording runs unbounded. Wait until the
        // session is active in the database before requesting the stop.
        WaitForActiveSession(harness.DataRoot);

        var stop = harness.Run("stop");

        // Bounded: if the recorder ever fails to stop, the test must fail with a clear
        // message rather than hanging the suite and the CI job.
        var start = await AwaitBounded(
            startTask,
            TimeSpan.FromSeconds(60),
            "meetcap start did not finish after meetcap stop");

        Assert.Equal(0, stop.ExitCode);
        Assert.Contains("stopped session", stop.Output);

        Assert.Equal(0, start.ExitCode);
        Assert.Contains("recording. press Ctrl+C", start.Output);
        Assert.Contains("chunks closed:", start.Output);

        var sessionId = Path.GetFileName(sessionDirectory!);
        var chunks = Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "mic"), "*.wav");
        Assert.NotEmpty(chunks);
        Assert.Empty(Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "mic"), "*.part"));

        // Every closed chunk is an independently readable WAV file.
        foreach (var chunk in chunks)
        {
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(chunk), 0, 4));
        }

        var manifest = File.ReadAllText(Path.Combine(sessionDirectory!, "session.json"));
        Assert.Contains("\"status\": \"COMPLETED\"", manifest, StringComparison.Ordinal);

        var events = File.ReadAllText(Path.Combine(sessionDirectory!, "events.jsonl"));
        Assert.Contains("\"session.started\"", events, StringComparison.Ordinal);
        Assert.Contains("\"audio.chunk.closed\"", events, StringComparison.Ordinal);
        Assert.Contains("\"session.stop_requested\"", events, StringComparison.Ordinal);
        Assert.Contains("\"session.stopped\"", events, StringComparison.Ordinal);

        var database = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        var stored = database.Sessions.Find(sessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);
        Assert.Equal(chunks.Length, database.Chunks.CountForSession(sessionId));
    }

    [Fact]
    public async Task Status_WhileARecordingIsInProgress_ReportsItActiveAndNeverRecoversIt()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig(chunkSeconds: 1);

        var startTask = Task.Run(() => harness.Run("start", "Live Status"));
        var sessionDirectory = WaitForSessionDirectory(harness.DataRoot);
        Assert.NotNull(sessionDirectory);
        var sessionId = Path.GetFileName(sessionDirectory!);

        // The session row exists before capture starts, so wait for the state that
        // actually means "a recording is in progress". The recording claims its liveness
        // marker at the very start of the run, before this transition, so observing
        // RECORDING guarantees the marker is held.
        WaitForSessionStatus(harness.DataRoot, sessionId, SessionStatus.Recording);

        // `meetcap status` runs the startup recovery scan from a different process. The
        // live recording holds its liveness marker, so the scan must leave it alone:
        // rewriting it to INTERRUPTED would also make `meetcap stop` unable to find it,
        // leaving a recording that cannot be stopped.
        var status = harness.Run("status");

        Assert.Equal(0, status.ExitCode);
        Assert.Contains("recovery: no incomplete sessions found", status.Output);
        Assert.Contains("sessions: 1 active", status.Output);

        // The scan must not have touched the recording: it is still recording, and its
        // event log gained no false recovery event.
        var database = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        Assert.NotNull(database.Sessions.FindActiveSession());
        Assert.Equal(SessionStatus.Recording, database.Sessions.Find(sessionId)!.Status);

        using (var stream = new FileStream(
            Path.Combine(sessionDirectory!, "events.jsonl"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            Assert.DoesNotContain("session.recovered", reader.ReadToEnd(), StringComparison.Ordinal);
        }

        // End the recording so the harness can clean up. This deliberately does not assert
        // `meetcap stop`'s exit code: `stop` waits only 15 s for the recorder to confirm,
        // which a heavily loaded CI runner can exceed even though the stop was signalled
        // and honoured. The property under test — the scan left the live session untouched
        // — has already been asserted above; that the recording still reaches a terminal
        // state confirms it was never made unstoppable.
        harness.Run("stop");
        await AwaitBounded(
            startTask,
            TimeSpan.FromSeconds(60),
            "meetcap start did not finish after meetcap stop");

        Assert.False(SessionStatus.IsActive(database.Sessions.Find(sessionId)!.Status));
    }

    [Fact]
    public void Status_ReportsAndRecoversASessionThatWasNotCleanlyStopped()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig(chunkSeconds: 1);

        // First pass creates the database.
        Assert.Equal(0, harness.Run("status").ExitCode);

        var database = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        const string sessionId = "ses_20260915T150000Z_0000000c";
        database.Sessions.Insert(new SessionRecord
        {
            Id = sessionId,
            Title = "Killed Session",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Recording,
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic },
            CreatedAt = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
        });

        // A chunk that was being written when the process died.
        var paths = new SessionPaths(harness.DataRoot, sessionId);
        paths.CreateDirectories();
        var writer = new WaveChunkWriter(
            paths.ChunkPartPath(AudioSource.Mic, 1),
            paths.ChunkFinalPath(AudioSource.Mic, 1),
            new AudioFormat(48_000, 1, 16, AudioSampleFormat.Pcm),
            capacityBytes: 96_000,
            sequence: 1);
        writer.Append(new byte[48_000]);
        writer.Dispose();

        var result = harness.Run("status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1 incomplete session(s)", result.Output);
        Assert.Contains(sessionId, result.Output);
        Assert.Contains("1 chunk(s) recovered", result.Output);

        // Startup recovery made the audio durable and stopped pretending the session is
        // still recording.
        Assert.False(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));
        Assert.True(File.Exists(paths.ChunkFinalPath(AudioSource.Mic, 1)));
        Assert.Equal(SessionStatus.Interrupted, database.Sessions.Find(sessionId)!.Status);
    }

    [Fact]
    public void Status_OnACleanDataRoot_ReportsNoIncompleteSessions()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        var result = harness.Run("status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("recovery: no incomplete sessions found", result.Output);
    }

    [Fact]
    public void Root_HelpListsTheCaptureCommands()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run();

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("devices", result.Error);
        Assert.Contains("start", result.Error);
        Assert.Contains("stop", result.Error);
        Assert.Contains("session", result.Error);
    }

    [Fact]
    public void SessionRepair_OnACleanDataRoot_SucceedsWithNothingToDo()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        // First pass creates the database.
        Assert.Equal(0, harness.Run("status").ExitCode);

        var result = harness.Run("session", "repair");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no incomplete sessions found", result.Output);
    }

    [Fact]
    public void SessionRepair_RepairsASessionThatWasNotCleanlyStoppedAndExitsZeroWhenNothingIsMissing()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        Assert.Equal(0, harness.Run("status").ExitCode);

        var sessionId = SeedKilledSession(harness, dataBytes: 48_000);

        var result = harness.Run("session", "repair", "--session", sessionId);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(sessionId, result.Output);
        Assert.Contains("1 chunk(s) recovered", result.Output);
        Assert.Contains("timeline: no gaps", result.Output);

        // The active chunk became durable and the session stopped claiming to be recording.
        var paths = new SessionPaths(harness.DataRoot, sessionId);
        Assert.False(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));
        Assert.True(File.Exists(paths.ChunkFinalPath(AudioSource.Mic, 1)));

        var database = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        Assert.Equal(SessionStatus.Interrupted, database.Sessions.Find(sessionId)!.Status);
    }

    [Fact]
    public void SessionRepair_WithAnUnrecoverableChunk_ExitsNonZeroAndStatesWhereTheAudioIsMissing()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        Assert.Equal(0, harness.Run("status").ExitCode);

        var sessionId = SeedKilledSession(harness, dataBytes: 48_000);

        // Replace the recoverable active chunk with bytes that are not a WAV at all, so
        // recovery can classify it but cannot repair it. docs/RELIABILITY.md section 6
        // forbids reporting success while a known gap remains, so this must exit non-zero.
        var paths = new SessionPaths(harness.DataRoot, sessionId);
        File.WriteAllBytes(
            paths.ChunkPartPath(AudioSource.Mic, 1),
            Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());

        var result = harness.Run("session", "repair", "--session", sessionId);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("1 chunk(s) unreadable", result.Output);
        Assert.Contains("still have a known gap", result.Output);
        Assert.Contains("gap:", result.Output);
        Assert.Contains("recovery is incomplete", result.Error);

        // The unreadable bytes are retained for inspection, never deleted.
        Assert.True(File.Exists(paths.ChunkPartPath(AudioSource.Mic, 1)));

        var events = File.ReadAllText(Path.Combine(paths.SessionDirectory, "events.jsonl"));
        Assert.Contains("\"session.repair.incomplete\"", events, StringComparison.Ordinal);
        Assert.Contains("\"capture.gap\"", events, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionRepair_ForAnUnknownSession_ExitsNonZeroWithAnActionableMessage()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        Assert.Equal(0, harness.Run("status").ExitCode);

        var result = harness.Run("session", "repair", "--session", "ses_20990101T000000Z_ffffffff");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("was not found", result.Output);
        Assert.Contains("meetcap status", result.Error);
    }

    /// <summary>
    /// Creates the artifacts a killed recording leaves behind: a RECORDING session row and
    /// an active <c>.part</c> chunk with real audio in it.
    /// </summary>
    private static string SeedKilledSession(CliHarness harness, int dataBytes)
    {
        const string sessionId = "ses_20260915T150000Z_0000000e";
        var database = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        database.Sessions.Insert(new SessionRecord
        {
            Id = sessionId,
            Title = "Killed Session",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Recording,
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic },
            CreatedAt = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
        });

        var paths = new SessionPaths(harness.DataRoot, sessionId);
        paths.CreateDirectories();
        var writer = new WaveChunkWriter(
            paths.ChunkPartPath(AudioSource.Mic, 1),
            paths.ChunkFinalPath(AudioSource.Mic, 1),
            new AudioFormat(48_000, 1, 16, AudioSampleFormat.Pcm),
            capacityBytes: 96_000,
            sequence: 1);
        writer.Append(new byte[dataBytes]);
        writer.Dispose();

        return sessionId;
    }

    private static async Task<CliResult> AwaitBounded(Task<CliResult> task, TimeSpan timeout, string description)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"{description} (waited {timeout}).");
        }

        return await task.ConfigureAwait(false);
    }

    private static string? WaitForSessionDirectory(string dataRoot)
    {
        var sessionsRoot = Path.Combine(dataRoot, "sessions");
        var deadline = Environment.TickCount64 + 30_000;

        while (Environment.TickCount64 < deadline)
        {
            if (Directory.Exists(sessionsRoot))
            {
                var directory = Directory.GetDirectories(sessionsRoot).FirstOrDefault();
                if (directory is not null && File.Exists(Path.Combine(directory, "session.json")))
                {
                    return directory;
                }
            }

            Thread.Sleep(50);
        }

        return null;
    }

    /// <summary>
    /// Waits until <c>meetcap start</c> has an active session row in the database — the
    /// same place <c>meetcap stop</c> looks for the session to stop. Closes the window
    /// between the session manifest being written and its database row being inserted.
    /// </summary>
    private static void WaitForActiveSession(string dataRoot)
    {
        var database = new MeetCapDatabase(Path.Combine(dataRoot, "meetcap.db"));
        var deadline = Environment.TickCount64 + 30_000;

        while (Environment.TickCount64 < deadline)
        {
            if (database.IsInitialized() && database.Sessions.FindActiveSession() is not null)
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("meetcap start did not create an active session row in time.");
    }

    /// <summary>
    /// Waits until the session reaches <paramref name="status"/> in the database. The row
    /// exists before capture starts, so a test that needs a recording to actually be live
    /// has to wait for the status transition rather than for the row.
    /// </summary>
    private static void WaitForSessionStatus(string dataRoot, string sessionId, string status)
    {
        var database = new MeetCapDatabase(Path.Combine(dataRoot, "meetcap.db"));
        var deadline = Environment.TickCount64 + 30_000;

        while (Environment.TickCount64 < deadline)
        {
            if (database.IsInitialized() &&
                string.Equals(database.Sessions.Find(sessionId)?.Status, status, StringComparison.Ordinal))
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"meetcap start did not reach status {status} in time.");
    }
}
