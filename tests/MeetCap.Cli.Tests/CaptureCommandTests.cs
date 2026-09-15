using MeetCap.AudioPipeline;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
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
    public void Start_WithOnlineMode_IsRejectedBeforeAnySessionIsCreated()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        var result = harness.Run("start", "Remote Review", "--mode", "online");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("offline", result.Error);
        Assert.Contains("M5", result.Error);

        // No session directory may be created for a rejected mode.
        var sessionsRoot = Path.Combine(harness.DataRoot, "sessions");
        Assert.True(!Directory.Exists(sessionsRoot) || Directory.GetDirectories(sessionsRoot).Length == 0);
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
}
