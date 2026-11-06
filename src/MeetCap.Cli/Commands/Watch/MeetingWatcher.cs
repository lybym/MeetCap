namespace MeetCap.Cli.Commands.Watch;

using MeetCap.AudioPipeline;
using MeetCap.Core.Time;

/// <summary>
/// The two states of the watch state machine (docs/ARCHITECTURE.md, watch section):
/// <list type="bullet">
/// <item><term>Idle</term><description>no watched session is recording.</description></item>
/// <item><term>Recording</term><description>one cohort session is capturing; new watched
/// launches are noted but do not start a second recording.</description></item>
/// </list>
/// </summary>
internal enum WatchState
{
    Idle,
    Recording,
}

/// <summary>
/// Observer hooks the command uses to report watcher activity to stdout/logs, and that
/// tests use to assert transitions. All hooks are best-effort: a hook throwing must not
/// stop the watcher.
/// </summary>
internal interface IWatchObserver
{
    void OnWatcherStarted(int preExistingProcessCount);

    void OnPollIdle();

    /// <summary>A new cohort session was started.</summary>
    void OnRecordingStarted(string sessionId, WatchTrigger trigger);

    /// <summary>A start was refused because a recording is already active elsewhere.</summary>
    void OnStartRefused(string reason);

    /// <summary>The cohort session was finalized (cleanly or after a recording crash).</summary>
    void OnRecordingFinalized(string sessionId, string stopReason, RecordingSessionOutcome? outcome);
}

/// <summary>
/// The meeting-aware watch state machine: one active watched-process cohort produces one
/// recording session spanning the union of overlapping watched apps (issue #52 review
/// update: cohort identity, not per-app-run).
/// </summary>
/// <remarks>
/// <para>
/// Transitions:
/// <c>idle ──(any new watched main process launch)──&gt; recording ──(ALL watched
/// processes gone, after exit debounce)──&gt; finalize</c>. App exit and app crash are the
/// same signal: a watched process simply disappears from the probe, and the cohort is
/// "all gone" once none remain.
/// </para>
/// <para>
/// <strong>Start</strong> fires on the first validated main-process observation that is a
/// genuine new launch (bounded by polling delay). A process that was already running when
/// the watcher started is seeded into the known set and never triggers (on watcher
/// restart, already-running processes do NOT trigger capture — only new launches do). A
/// process is fingerprinted by PID + start time, so PID reuse by a different process
/// instance is detected as a new launch.
/// </para>
/// <para>
/// <strong>Exit debounce</strong> absorbs brief helper/relaunch churn: when every watched
/// process is gone the watcher waits <see cref="WatchOptions.ExitDebounce"/> before
/// finalizing, so an app that relaunches within the window keeps one cohort/session alive.
/// </para>
/// <para>
/// The engine is deterministic and testable: <see cref="StepAsync"/> performs one poll
/// iteration with no delay and uses the injected <see cref="IClock"/> for all timing, so a
/// test scripts the probe and advances the clock between steps. <see cref="RunAsync"/>
/// drives the real timed loop until cancelled and then performs a fail-safe stop.
/// </para>
/// </remarks>
internal sealed class MeetingWatcher
{
    private readonly IProcessProbe _probe;
    private readonly IWatchRecorder _recorder;
    private readonly IClock _clock;
    private readonly WatchOptions _options;
    private readonly IWatchObserver _observer;

    private WatchState _state = WatchState.Idle;
    private readonly HashSet<WatchedProcess> _known = new();
    private DateTimeOffset? _allGoneSince;
    private WatchSessionHandle? _handle;

    public MeetingWatcher(
        IProcessProbe probe,
        IWatchRecorder recorder,
        IClock clock,
        WatchOptions options,
        IWatchObserver observer)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    /// <summary>The current state (for tests and diagnostics).</summary>
    public WatchState State => _handle is null ? WatchState.Idle : _state;

    /// <summary>True when a cohort session is currently recording.</summary>
    public bool IsRecording => _handle is not null;

    /// <summary>
    /// Seeds the known set with processes already running at watcher start, so they do
    /// not trigger capture on restart (only new launches trigger). Called once before the
    /// first <see cref="StepAsync"/> / <see cref="RunAsync"/>.
    /// </summary>
    public void SeedExisting()
    {
        var preExisting = _probe.Snapshot();
        foreach (var process in preExisting)
        {
            _known.Add(process);
        }

        _observer.OnWatcherStarted(preExisting.Count);
    }

    /// <summary>
    /// One deterministic poll iteration (no delay). Detects new launches, starts a cohort
    /// session, debounces exit, and finalizes when the cohort is gone. Returns the
    /// resulting state. Awaiting the recording's completion on finalize makes this async.
    /// </summary>
    public async Task<WatchState> StepAsync()
    {
        var now = _clock.UtcNow;
        var current = _probe.Snapshot();

        if (_handle is null)
        {
            HandleIdle(current, now);
            return WatchState.Idle;
        }

        if (current.Count > 0)
        {
            // The cohort is still alive. Note any watched process that launched
            // mid-session (a different watched app) so it is tracked, and reset the exit
            // debounce — a relaunch within the window keeps one session alive.
            _allGoneSince = null;
            foreach (var process in current)
            {
                _known.Add(process);
            }

            return WatchState.Recording;
        }

        // Every watched process is gone. Start (or continue) the exit debounce; only
        // finalize once the debounce window has elapsed, so brief helper churn or an app
        // relaunch does not split one cohort into two sessions.
        _allGoneSince ??= now;
        if (now < _allGoneSince + _options.ExitDebounce)
        {
            return WatchState.Recording;
        }

        await StopActiveAsync(WatchStopReasons.AllWatchedProcessesExited).ConfigureAwait(false);
        return WatchState.Idle;
    }

    /// <summary>
    /// Runs the timed poll loop until <paramref name="cancellationToken"/> is cancelled,
    /// then performs a fail-safe stop of any active recording (the watcher never leaves a
    /// recording running when it shuts down).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        SeedExisting();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single poll must not kill the watcher. The recorder's own failures are
                // confined to StopActiveAsync; anything unexpected here is logged via the
                // observer and the loop continues.
                _observer.OnStartRefused($"poll error: {ex.Message}");
            }

            try
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await StopActiveAsync(WatchStopReasons.WatcherStopped).ConfigureAwait(false);
    }

    /// <summary>
    /// Finalizes the active cohort session: requests stop, awaits completion, persists the
    /// stop metadata, and returns to idle. Safe to call when no session is active (no-op).
    /// A recording task that faults (recorder crash) is caught: the stop metadata is still
    /// written and the watcher returns to idle, leaving the session for crash recovery.
    /// </summary>
    public async Task StopActiveAsync(string reason)
    {
        var handle = _handle;
        if (handle is null)
        {
            _allGoneSince = null;
            _state = WatchState.Idle;
            return;
        }

        _handle = null;
        _allGoneSince = null;

        RecordingSessionOutcome? outcome = null;
        var stopReason = reason;
        try
        {
            handle.RequestStop();
            outcome = await handle.Completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The recording task itself faulted (a recorder crash, not an app crash). The
            // session is left in whatever state its own teardown reached; startup recovery
            // handles it on the next command. The watcher still records why it stopped and
            // returns to idle so a future cohort can trigger a fresh session.
            stopReason = WatchStopReasons.RecordingCrashed;
            _observer.OnRecordingFinalized(handle.SessionId, stopReason, null);
            PersistStopSafe(handle, stopReason);
            handle.Dispose();
            _state = WatchState.Idle;
            return;
        }

        PersistStopSafe(handle, stopReason);
        handle.Dispose();
        _observer.OnRecordingFinalized(handle.SessionId, stopReason, outcome);
        _state = WatchState.Idle;
    }

    private void HandleIdle(IReadOnlyList<WatchedProcess> current, DateTimeOffset now)
    {
        // A "new launch" is a watched process whose PID + start time has not been seen
        // since the watcher started. Pre-existing processes (seeded at startup) and
        // processes already noted mid-session are not new, so they do not trigger.
        var launches = new List<WatchedProcess>();
        foreach (var process in current)
        {
            if (_known.Add(process))
            {
                launches.Add(process);
            }
        }

        if (launches.Count == 0)
        {
            _observer.OnPollIdle();
            return;
        }

        var triggering = launches[0];
        var title = WatchTitleTemplate.Render(_options.TitleTemplate, triggering.Name, now);
        var trigger = new WatchTrigger(triggering.Name, triggering.Id, triggering.StartTime, now);

        var handle = _recorder.TryStart(title, trigger);
        if (handle is null)
        {
            // Cross-process ownership conflict: another recording (a manual `meetcap start`
            // or another watcher) is active. Do NOT record the new launches as known, so the
            // next poll re-evaluates them and starts capture once the conflict clears.
            foreach (var launched in launches)
            {
                _known.Remove(launched);
            }

            _observer.OnStartRefused("a recording is already active; deferring until it finishes");
            return;
        }

        _handle = handle;
        _state = WatchState.Recording;
        _allGoneSince = null;
        _observer.OnRecordingStarted(handle.SessionId, trigger);
    }

    private static void PersistStopSafe(WatchSessionHandle handle, string reason)
    {
        try
        {
            handle.PersistStop(reason, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            // Metadata persistence is best-effort; the session artifacts are already safe.
        }
    }
}

/// <summary>Stable stop-reason strings persisted into watch.meta.json.</summary>
internal static class WatchStopReasons
{
    public const string AllWatchedProcessesExited = "all_watched_processes_exited";
    public const string WatcherStopped = "watcher_stopped";
    public const string RecordingCrashed = "recording_crashed";
}
