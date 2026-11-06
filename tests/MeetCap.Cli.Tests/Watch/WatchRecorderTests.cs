using MeetCap.AudioPipeline;
using MeetCap.Cli.Commands.Watch;
using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeetCap.Cli.Tests.Watch;

/// <summary>
/// Exercises the production <see cref="WatchRecorder"/> against the real capture stack
/// (real SQLite, real session directories, fake audio platform): the cross-process
/// ownership guard and the watch.meta.json trigger/stop provenance.
/// </summary>
public class WatchRecorderTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataRoot;
    private readonly FakeCapturePlatformFactory _platform;
    private readonly MeetCapDatabase _database;

    public WatchRecorderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "meetcap-watch-recorder-" + Guid.NewGuid().ToString("N"));
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);

        _platform = new FakeCapturePlatformFactory();
        _platform.Devices.Replace(new CaptureDeviceInfo("mic-default", "Test Microphone", true));

        _database = new MeetCapDatabase(Path.Combine(_dataRoot, "meetcap.db"));
        _database.EnsureMigrated();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private CaptureService NewService()
    {
        var config = new MeetCapConfiguration
        {
            Capture =
            {
                DefaultMode = "offline",
                ChunkSeconds = 1,
                BufferSeconds = 1,
                FlushIntervalMs = 200,
            },
            Storage = { DataRoot = _dataRoot, MinimumFreeSpaceGb = 1 },
            Asr = { Enabled = false },
        };

        var settings = CaptureSettings.FromConfiguration(config, _dataRoot, "offline");
        return new CaptureService(_platform.Create(), _database, settings);
    }

    private static WatchTrigger Trigger(string name = "CloudVideo", int pid = 4242) =>
        new(name, pid, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task TryStart_PublishesSessionAndWritesTriggerMetadata()
    {
        var recorder = new WatchRecorder(NewService(), asrHost: null, new MeetCapConfiguration(), _dataRoot, NullLogger.Instance);

        var handle = recorder.TryStart("CloudVideo 2026-11-07 09-00-00", Trigger());

        Assert.NotNull(handle);
        var sessionId = handle!.SessionId;
        var sessionDirectory = Path.Combine(_dataRoot, SessionPaths.SessionsFolderName, sessionId);
        Assert.True(File.Exists(WatchMetadataStore.PathFor(sessionDirectory)));

        var metadata = WatchMetadataStore.Read(sessionDirectory);
        Assert.NotNull(metadata!.Trigger);
        Assert.Equal("CloudVideo", metadata.Trigger!.ProcessName);
        Assert.Equal(4242, metadata.Trigger.ProcessId);
        Assert.Null(metadata.Stop);

        // Stop and finalize so the background recording task does not leak.
        handle.RequestStop();
        var outcome = await AwaitOutcome(handle);
        Assert.Equal(SessionStatus.Completed, outcome.Status);

        handle.PersistStop(WatchStopReasons.AllWatchedProcessesExited, DateTimeOffset.UtcNow);
        handle.Dispose();

        var stopped = WatchMetadataStore.Read(sessionDirectory);
        Assert.NotNull(stopped!.Stop);
        Assert.Equal(WatchStopReasons.AllWatchedProcessesExited, stopped.Stop!.Reason);
    }

    [Fact]
    public async Task TryStart_RefusesWhileRecordingIsAlreadyActive()
    {
        // Cross-process ownership: while one session holds the recording surface, a second
        // TryStart returns null instead of publishing a concurrent recording.
        var service = NewService();
        var recorder = new WatchRecorder(service, asrHost: null, new MeetCapConfiguration(), _dataRoot, NullLogger.Instance);

        var first = recorder.TryStart("first", Trigger());
        Assert.NotNull(first);

        // The first session is active (CREATED/RECORDING), so FindActiveSession is non-null.
        var second = recorder.TryStart("second", Trigger("WeMeetApp", 9001));
        Assert.Null(second);

        // Clean up the active session.
        first!.RequestStop();
        await AwaitOutcome(first);
        first.Dispose();
    }

    [Fact]
    public async Task TryStart_SucceedsAgainAfterPreviousSessionFinalizes()
    {
        var service = NewService();
        var recorder = new WatchRecorder(service, asrHost: null, new MeetCapConfiguration(), _dataRoot, NullLogger.Instance);

        var first = recorder.TryStart("first", Trigger());
        Assert.NotNull(first);
        first!.RequestStop();
        await AwaitOutcome(first);
        first.PersistStop(WatchStopReasons.AllWatchedProcessesExited, DateTimeOffset.UtcNow);
        first.Dispose();

        // The first session is now terminal, so the ownership guard no longer blocks a new one.
        var second = recorder.TryStart("second", Trigger("WeMeetApp", 9002));
        Assert.NotNull(second);

        second!.RequestStop();
        await AwaitOutcome(second);
        second.Dispose();
    }

    [Fact]
    public async Task AutoAsrFalse_BuildsNoAsrHost_AndRecorderRecordsWithoutProvider()
    {
        // With auto_asr = false the recorder is constructed with a null ASR host, so there
        // is no provider adapter and no network request can be made. This asserts the
        // recording still starts and finalizes through the capture stack alone.
        var recorder = new WatchRecorder(NewService(), asrHost: null, new MeetCapConfiguration(), _dataRoot, NullLogger.Instance);

        var handle = recorder.TryStart("recording-only", Trigger());
        Assert.NotNull(handle);

        handle!.RequestStop();
        var outcome = await AwaitOutcome(handle);
        handle.Dispose();

        Assert.Equal(SessionStatus.Completed, outcome.Status);
    }

    private static async Task<RecordingSessionOutcome> AwaitOutcome(WatchSessionHandle handle)
    {
        // The fake capture source produces audio on a background loop; give it a bounded
        // window to finalize after the stop request rather than waiting indefinitely.
        var completed = await Task.WhenAny(handle.Completion, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != handle.Completion)
        {
            handle.Dispose();
            throw new TimeoutException("The recording did not finalize within 10s after a stop request.");
        }

        return await handle.Completion;
    }
}
