using MeetCap.AudioPipeline;
using MeetCap.Cli.Commands.Watch;
using MeetCap.Core.Sessions;
using MeetCap.Core.Time;

namespace MeetCap.Cli.Tests.Watch;

/// <summary>
/// Scripted process probe for watcher state-machine tests. The production
/// <see cref="ProcessProbe"/> filters helper/child processes by only querying configured
/// main names; this fake stands in for that boundary, so a test controls exactly which
/// watched main processes are "running" on each poll.
/// </summary>
internal sealed class FakeProcessProbe : IProcessProbe
{
    private IReadOnlyList<WatchedProcess> _current = Array.Empty<WatchedProcess>();

    public void Set(params WatchedProcess[] processes) => _current = processes;

    public IReadOnlyList<WatchedProcess> Snapshot() => _current;
}

/// <summary>
/// Mutable wall clock for deterministic debounce timing. The watcher reads
/// <see cref="IClock.UtcNow"/> for every timing decision, so advancing this clock between
/// steps drives the exit debounce without any real delay.
/// </summary>
internal sealed class MutableClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
}

/// <summary>
/// Captures watcher activity for assertions. Mirrors <see cref="IWatchObserver"/> so a
/// test can assert transitions, refused starts, and finalize reasons.
/// </summary>
internal sealed class RecordingWatchObserver : IWatchObserver
{
    public int StartedCount { get; private set; }
    public int PreExistingCount { get; private set; }
    public List<(string SessionId, WatchTrigger Trigger)> Started { get; } = new();
    public List<string> Refusals { get; } = new();
    public List<(string SessionId, string Reason, RecordingSessionOutcome? Outcome)> Finalized { get; } = new();
    public List<WatchState> IdlePolls { get; } = new();

    public void OnWatcherStarted(int preExistingProcessCount) => PreExistingCount = preExistingProcessCount;

    public void OnPollIdle() => IdlePolls.Add(WatchState.Idle);

    public void OnRecordingStarted(string sessionId, WatchTrigger trigger)
    {
        StartedCount++;
        Started.Add((sessionId, trigger));
    }

    public void OnStartRefused(string reason) => Refusals.Add(reason);

    public void OnRecordingFinalized(string sessionId, string stopReason, RecordingSessionOutcome? outcome)
        => Finalized.Add((sessionId, stopReason, outcome));
}

/// <summary>
/// Fake recorder that records start/stop/dispose calls and controls the recording's
/// completion task. <see cref="RefuseStart"/> simulates a cross-process ownership
/// conflict (a manual <c>meetcap start</c> already active); <see cref="CompletionFault"/>
/// simulates a recorder crash mid-session.
/// </summary>
internal sealed class FakeWatchRecorder : IWatchRecorder
{
    public List<(string Title, WatchTrigger Trigger)> StartCalls { get; } = new();
    public List<(string SessionId, string Reason, DateTimeOffset StoppedAt)> StopCalls { get; } = new();
    public List<string> DisposedSessions { get; } = new();

    public bool RefuseStart { get; set; }
    public Exception? CompletionFault { get; set; }
    public Func<string, RecordingSessionOutcome>? OutcomeFactory { get; set; }

    public int StartCount => StartCalls.Count;
    public int StopCount => StopCalls.Count;

    private int _counter;

    public WatchSessionHandle? TryStart(string title, WatchTrigger trigger)
    {
        StartCalls.Add((title, trigger));

        if (RefuseStart)
        {
            return null;
        }

        var sessionId = $"watch-session-{++_counter}";
        var tcs = new TaskCompletionSource<RecordingSessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        return new WatchSessionHandle(
            sessionId,
            trigger,
            tcs.Task,
            requestStop: () =>
            {
                if (CompletionFault is { } fault)
                {
                    tcs.SetException(fault);
                }
                else
                {
                    tcs.SetResult(OutcomeFactory?.Invoke(sessionId) ?? WatchTestOutcomes.Clean(sessionId));
                }
            },
            persistStop: (reason, stoppedAt) => StopCalls.Add((sessionId, reason, stoppedAt)),
            dispose: () => DisposedSessions.Add(sessionId));
    }
}

/// <summary>Helpers for building <see cref="WatchedProcess"/> and outcomes in tests.</summary>
internal static class WatchTestSupport
{
    private static int s_nextPid = 1000;
    private static readonly DateTimeOffset s_baseStart = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);

    public static WatchedProcess Process(string name, int? pid = null, DateTimeOffset? startTime = null)
        => new(name, pid ?? Interlocked.Increment(ref s_nextPid), startTime ?? s_baseStart);

    public static WatchOptions DefaultOptions(
        TimeSpan? pollInterval = null,
        TimeSpan? exitDebounce = null,
        string titleTemplate = "{app} {start:yyyy-MM-dd HH-mm-ss}",
        IReadOnlyList<string>? processes = null)
        => new(
            Enabled: true,
            Processes: processes ?? new[] { "WeMeetApp", "CloudVideo" },
            PollInterval: pollInterval ?? TimeSpan.FromMilliseconds(10),
            ExitDebounce: exitDebounce ?? TimeSpan.FromSeconds(10),
            CaptureMode: "online",
            AutoAsr: false,
            TitleTemplate: titleTemplate);

    public static MeetingWatcher NewWatcher(
        FakeProcessProbe probe,
        FakeWatchRecorder recorder,
        MutableClock clock,
        WatchOptions options,
        RecordingWatchObserver observer)
        => new(probe, recorder, clock, options, observer);
}

internal static class WatchTestOutcomes
{
    public static RecordingSessionOutcome Clean(string sessionId) => new(
        sessionId,
        $"dir/{sessionId}",
        SessionStatus.Completed,
        DurationMs: 60_000,
        ChunksClosed: 1,
        ClosedDataBytes: 1024,
        Degraded: false,
        EndReason: null);

    public static RecordingSessionOutcome Interrupted(string sessionId) => new(
        sessionId,
        $"dir/{sessionId}",
        SessionStatus.Interrupted,
        DurationMs: 30_000,
        ChunksClosed: 1,
        ClosedDataBytes: 512,
        Degraded: true,
        EndReason: "capture_failed");
}
