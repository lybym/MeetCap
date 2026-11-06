using MeetCap.Cli.Commands.Watch;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.Cli.Tests.Watch;

/// <summary>
/// Watcher state-machine tests with an injected process probe and fake recorder (issue
/// #52 acceptance criteria + review-update test coverage). These cover the cohort state
/// machine without any audio hardware or real processes.
/// </summary>
public class MeetingWatcherTests
{
    private static (MeetingWatcher watcher, FakeProcessProbe probe, FakeWatchRecorder recorder, MutableClock clock, RecordingWatchObserver observer)
        NewWatcher(TimeSpan? debounce = null)
    {
        var probe = new FakeProcessProbe();
        var recorder = new FakeWatchRecorder();
        var clock = new MutableClock();
        var observer = new RecordingWatchObserver();
        var options = WatchTestSupport.DefaultOptions(exitDebounce: debounce ?? TimeSpan.FromSeconds(10));
        var watcher = WatchTestSupport.NewWatcher(probe, recorder, clock, options, observer);
        return (watcher, probe, recorder, clock, observer);
    }

    [Fact]
    public async Task IdleToRecordingToFinalize_StartsOnLaunchStopsOnExit()
    {
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        var cloudVideo = WatchTestSupport.Process("CloudVideo", pid: 4242);

        // No watched process: idle.
        await watcher.StepAsync();
        Assert.False(watcher.IsRecording);
        Assert.Equal(0, recorder.StartCount);

        // CloudVideo launches: start within one poll.
        probe.Set(cloudVideo);
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal(1, recorder.StartCount);
        Assert.Equal("CloudVideo", observer.Started[0].Trigger.ProcessName);
        Assert.Equal(4242, observer.Started[0].Trigger.ProcessId);

        // CloudVideo exits: debounce, then finalize.
        probe.Set();
        await watcher.StepAsync(); // all gone -> debounce starts
        Assert.True(watcher.IsRecording);

        clock.Advance(TimeSpan.FromSeconds(10));
        await watcher.StepAsync(); // debounce elapsed -> finalize
        Assert.False(watcher.IsRecording);
        Assert.Equal(1, recorder.StopCount);
        Assert.Equal(WatchStopReasons.AllWatchedProcessesExited, observer.Finalized[0].Reason);
        Assert.Equal(SessionStatus.Completed, observer.Finalized[0].Outcome!.Status);
    }

    [Fact]
    public async Task TitleTemplate_IsAppliedToTriggeringProcess()
    {
        var (watcher, probe, recorder, _, _) = NewWatcher();
        var app = WatchTestSupport.Process("WeMeetApp", pid: 7);

        probe.Set(app);
        await watcher.StepAsync();

        // The title embeds the triggering app name; the timestamp suffix is local-time
        // formatted, so only the prefix is asserted to stay timezone-independent.
        Assert.Equal(1, recorder.StartCount);
        Assert.StartsWith("WeMeetApp ", recorder.StartCalls[0].Title);
    }

    [Fact]
    public async Task WhileRecording_NewLaunchesAreIgnored_NoDoubleRecording()
    {
        var (watcher, probe, recorder, _, _) = NewWatcher();
        var weMeet = WatchTestSupport.Process("WeMeetApp");

        probe.Set(weMeet);
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);

        // A different watched app launches mid-session: noted, but no second recording.
        var cloudVideo = WatchTestSupport.Process("CloudVideo");
        probe.Set(weMeet, cloudVideo);
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);
        Assert.True(watcher.IsRecording);
    }

    [Fact]
    public async Task OverlappingWatchedApps_ProduceOneSessionSpanningTheUnion()
    {
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        var weMeet = WatchTestSupport.Process("WeMeetApp");
        var cloudVideo = WatchTestSupport.Process("CloudVideo");

        // WeMeet launches -> one session.
        probe.Set(weMeet);
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);

        // CloudVideo joins mid-session -> still one session.
        probe.Set(weMeet, cloudVideo);
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);

        // WeMeet exits but CloudVideo still running -> cohort alive, no finalize.
        probe.Set(cloudVideo);
        await watcher.StepAsync();
        Assert.Equal(0, observer.Finalized.Count);
        Assert.True(watcher.IsRecording);

        // CloudVideo exits -> debounce -> finalize. Exactly one session spanned the union.
        probe.Set();
        await watcher.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await watcher.StepAsync();

        Assert.Equal(1, recorder.StartCount);
        Assert.Equal(1, recorder.StopCount);
    }

    [Fact]
    public async Task HelperProcessNames_NeverTriggerOrExtend()
    {
        // The production probe only queries configured main names, so helper/child
        // processes (CloudVideoNet, crashpad_handler, ...) never appear in a snapshot.
        // This test asserts the watcher boundary: with no main process surfaced (helpers
        // filtered by the probe), nothing triggers.
        var (watcher, probe, recorder, _, _) = NewWatcher();

        // Probe returns nothing (helpers were filtered out by the probe boundary).
        probe.Set();
        await watcher.StepAsync();
        await watcher.StepAsync();
        Assert.False(watcher.IsRecording);
        Assert.Equal(0, recorder.StartCount);

        // A main process appears -> triggers.
        probe.Set(WatchTestSupport.Process("CloudVideo"));
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);
    }

    [Fact]
    public async Task ExitDebounce_AbsorbsRelaunchChurn_OnlyFinalizesAfterWindow()
    {
        var (watcher, probe, recorder, clock, observer) = NewWatcher(debounce: TimeSpan.FromSeconds(10));
        var app = WatchTestSupport.Process("WeMeetApp", pid: 1);

        probe.Set(app);
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);

        // App exits: not finalized immediately.
        probe.Set();
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal(0, observer.Finalized.Count);

        // Within the debounce window, the app relaunches (new pid+start): cohort stays
        // alive, one session continues, no gap, no second session.
        clock.Advance(TimeSpan.FromSeconds(4));
        probe.Set(WatchTestSupport.Process("WeMeetApp", pid: 2));
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal(1, recorder.StartCount);

        // Now it stays gone past the window -> finalize.
        probe.Set();
        await watcher.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await watcher.StepAsync();
        Assert.False(watcher.IsRecording);
        Assert.Equal(1, recorder.StopCount);
    }

    [Fact]
    public async Task WatchedAppCrashMidSession_FinalizesCleanlyAfterDebounce()
    {
        // An app crash is the same signal as an exit: the process disappears from the
        // probe. The cohort finalizes cleanly after the debounce (issue #52 acceptance).
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        var app = WatchTestSupport.Process("CloudVideo", pid: 9);

        probe.Set(app);
        await watcher.StepAsync();

        // Crash: process gone.
        probe.Set();
        await watcher.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await watcher.StepAsync();

        Assert.False(watcher.IsRecording);
        Assert.Equal(WatchStopReasons.AllWatchedProcessesExited, observer.Finalized[0].Reason);
        Assert.False(observer.Finalized[0].Outcome!.Degraded);
    }

    [Fact]
    public async Task RecordingTaskCrash_FailSafeFinalizeAndReturnsToIdle()
    {
        // If the recording task itself faults (a recorder crash, not an app crash), the
        // watcher catches it, persists a crash stop reason, and returns to idle so a new
        // cohort can trigger a fresh session (review update: watcher crash recovery).
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        recorder.CompletionFault = new InvalidOperationException("recorder exploded");
        var app = WatchTestSupport.Process("CloudVideo");

        probe.Set(app);
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);

        // Trigger finalize: the faulted completion is awaited in StopActiveAsync.
        probe.Set();
        await watcher.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await watcher.StepAsync();

        Assert.False(watcher.IsRecording);
        Assert.Equal(WatchStopReasons.RecordingCrashed, observer.Finalized[0].Reason);
        Assert.Null(observer.Finalized[0].Outcome);
        Assert.Contains(recorder.DisposedSessions, s => s == observer.Started[0].SessionId);

        // Recovery: a new launch can start a fresh session after a crash.
        recorder.CompletionFault = null;
        probe.Set(WatchTestSupport.Process("WeMeetApp"));
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal(2, recorder.StartCount);
    }

    [Fact]
    public async Task PidReuse_AfterExit_TriggersNewSession()
    {
        // Fingerprint is PID + start time. A new process instance reusing a PID has a
        // different start time, so it is a new launch that triggers a fresh session.
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        var startTime = clock.UtcNow;

        var first = WatchTestSupport.Process("CloudVideo", pid: 500, startTime: startTime);
        probe.Set(first);
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);

        // First instance exits and finalizes.
        probe.Set();
        await watcher.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StopCount);

        // A new instance reuses PID 500 but starts later -> new session.
        var reused = WatchTestSupport.Process("CloudVideo", pid: 500, startTime: startTime.AddHours(1));
        probe.Set(reused);
        await watcher.StepAsync();
        Assert.Equal(2, recorder.StartCount);
        Assert.NotEqual(observer.Started[0].SessionId, observer.Started[1].SessionId);
    }

    [Fact]
    public async Task PidReuseDuringSession_KeepsCohortAlive_NoSecondSession()
    {
        // A relaunch within the debounce window keeps one cohort/session alive even when
        // the new instance reuses a PID (different start time).
        var (watcher, probe, recorder, clock, _) = NewWatcher();
        var t0 = clock.UtcNow;

        probe.Set(WatchTestSupport.Process("WeMeetApp", pid: 600, startTime: t0));
        await watcher.StepAsync();
        Assert.Equal(1, recorder.StartCount);

        // Instance exits and a new one (same PID, new start) appears within the window.
        clock.Advance(TimeSpan.FromSeconds(2));
        probe.Set(WatchTestSupport.Process("WeMeetApp", pid: 600, startTime: t0.AddSeconds(2)));
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal(1, recorder.StartCount); // no second session
    }

    [Fact]
    public async Task PreExistingProcess_DoesNotTrigger_OnRestart()
    {
        // On watcher restart, already-running processes do NOT trigger capture — only new
        // launches do (issue #52 review update + docs).
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        var running = WatchTestSupport.Process("CloudVideo", pid: 11);

        probe.Set(running);
        watcher.SeedExisting();
        Assert.Equal(1, observer.PreExistingCount);

        // The pre-existing process is present but does not trigger.
        await watcher.StepAsync();
        await watcher.StepAsync();
        Assert.False(watcher.IsRecording);
        Assert.Equal(0, recorder.StartCount);

        // It relaunches (new pid+start) -> new launch -> triggers.
        probe.Set(WatchTestSupport.Process("CloudVideo", pid: 12, startTime: clock.UtcNow.AddMinutes(1)));
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal(1, recorder.StartCount);
    }

    [Fact]
    public async Task PreExistingProcess_AllowsNewAppToTrigger()
    {
        // A pre-existing watched app does not block a different watched app from triggering
        // a cohort session (the cohort spans the union).
        var (watcher, probe, recorder, _, observer) = NewWatcher();
        var weMeetRunning = WatchTestSupport.Process("WeMeetApp", pid: 20);

        probe.Set(weMeetRunning);
        watcher.SeedExisting(); // WeMeet seeded as pre-existing
        Assert.Equal(1, observer.PreExistingCount);

        await watcher.StepAsync();
        Assert.False(watcher.IsRecording); // pre-existing does not trigger

        // CloudVideo launches (new) -> triggers; WeMeet is part of the cohort.
        probe.Set(weMeetRunning, WatchTestSupport.Process("CloudVideo", pid: 21));
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal("CloudVideo", observer.Started[0].Trigger.ProcessName);
    }

    [Fact]
    public async Task LongLivedIdleApp_NeverTriggers()
    {
        // A watched app that stays open outside a meeting (the review-update caveat) never
        // triggers as long as it does not relaunch.
        var (watcher, probe, recorder, _, _) = NewWatcher();
        var idle = WatchTestSupport.Process("WeMeetApp", pid: 30);

        probe.Set(idle);
        watcher.SeedExisting();

        for (var i = 0; i < 5; i++)
        {
            await watcher.StepAsync();
        }

        Assert.False(watcher.IsRecording);
        Assert.Equal(0, recorder.StartCount);
    }

    [Fact]
    public async Task CrossProcessOwnership_RefusesThenStartsWhenConflictClears()
    {
        // A manual `meetcap start` (or another watcher) holds the recording surface, so
        // TryStart returns null. The watcher defers and starts once the conflict clears.
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        var app = WatchTestSupport.Process("CloudVideo", pid: 40);

        recorder.RefuseStart = true;
        probe.Set(app);
        await watcher.StepAsync();
        Assert.False(watcher.IsRecording);
        Assert.Equal(1, recorder.StartCount); // tried, refused
        Assert.Single(observer.Refusals);

        // Conflict clears on the next poll.
        recorder.RefuseStart = false;
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);
        Assert.Equal(2, recorder.StartCount);
        Assert.Equal("CloudVideo", observer.Started[0].Trigger.ProcessName);
    }

    [Fact]
    public async Task FailSafeStop_OnWatcherShutdown_StopsActiveRecording()
    {
        // When the watcher shuts down (Ctrl+C / cancellation) while recording, it performs
        // a fail-safe stop: the active session is stopped and persisted, not abandoned.
        var (watcher, probe, recorder, clock, observer) = NewWatcher();
        var app = WatchTestSupport.Process("CloudVideo", pid: 50);

        probe.Set(app);
        await watcher.StepAsync();
        Assert.True(watcher.IsRecording);

        await watcher.StopActiveAsync(WatchStopReasons.WatcherStopped);

        Assert.False(watcher.IsRecording);
        Assert.Equal(1, recorder.StopCount);
        Assert.Equal(WatchStopReasons.WatcherStopped, recorder.StopCalls[0].Reason);
        Assert.Equal(WatchStopReasons.WatcherStopped, observer.Finalized[0].Reason);
        Assert.Contains(recorder.DisposedSessions, s => s == observer.Started[0].SessionId);
    }

    [Fact]
    public async Task StopActiveAsync_WhenIdle_IsNoOp()
    {
        var (watcher, _, recorder, _, _) = NewWatcher();

        await watcher.StopActiveAsync(WatchStopReasons.WatcherStopped);

        Assert.False(watcher.IsRecording);
        Assert.Equal(0, recorder.StopCount);
    }
}
