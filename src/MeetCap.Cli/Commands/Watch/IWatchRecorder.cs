namespace MeetCap.Cli.Commands.Watch;

using MeetCap.AudioPipeline;

/// <summary>
/// Starts and stops one watched recording session on behalf of the watcher. Abstracted
/// so the state machine is testable without audio: production uses
/// <see cref="WatchRecorder"/> (the existing capture stack), tests supply a fake that
/// records start/stop calls and controls the completion task.
/// </summary>
/// <remarks>
/// <see cref="TryStart"/> returns <c>null</c> when a recording is already active — the
/// cross-process ownership guard (<see cref="CaptureService.FindActiveSession"/>) keeps
/// the watcher from double-recording alongside a manual <c>meetcap start</c> or another
/// watcher. The watcher treats a <c>null</c> result as "do not record now" and retries
/// on the next poll, so capture begins once the conflicting recording finishes.
/// </remarks>
public interface IWatchRecorder
{
    /// <summary>
    /// Starts a recording session for one trigger, or returns <c>null</c> when a
    /// recording is already active (cross-process ownership conflict).
    /// </summary>
    WatchSessionHandle? TryStart(string title, WatchTrigger trigger);
}

/// <summary>
/// A recording session started by the watcher. The watcher owns it in-process: if the
/// watcher dies, the recorder dies with it and the existing crash-recovery scan handles
/// the session (docs/ARCHITECTURE.md, watch section) — the same guarantee as manual mode.
/// </summary>
/// <remarks>
/// Constructed with the closures the watcher needs so production (which wraps a live
/// <see cref="RecordingSession"/>) and tests (which wrap a task completion source) share
/// one type with no inheritance ceremony.
/// </remarks>
public sealed class WatchSessionHandle : IDisposable
{
    private readonly Action _requestStop;
    private readonly Action<string, DateTimeOffset> _persistStop;
    private readonly Action _dispose;
    private int _disposed;

    public WatchSessionHandle(
        string sessionId,
        WatchTrigger trigger,
        Task<RecordingSessionOutcome> completion,
        Action requestStop,
        Action<string, DateTimeOffset> persistStop,
        Action dispose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(requestStop);
        ArgumentNullException.ThrowIfNull(persistStop);
        ArgumentNullException.ThrowIfNull(dispose);

        SessionId = sessionId;
        Trigger = trigger;
        Completion = completion;
        _requestStop = requestStop;
        _persistStop = persistStop;
        _dispose = dispose;
    }

    /// <summary>The session id of the recording this handle owns.</summary>
    public string SessionId { get; }

    /// <summary>The process observation that triggered this session.</summary>
    public WatchTrigger Trigger { get; }

    /// <summary>
    /// The recording's <see cref="RecordingSession.RunAsync"/> task. Completes with the
    /// session outcome once the recording reaches a terminal state, or faults if the
    /// recorder itself crashed (which the watcher treats as a fail-safe finalize).
    /// </summary>
    public Task<RecordingSessionOutcome> Completion { get; }

    /// <summary>Signals the recording to stop (cancels its run token).</summary>
    public void RequestStop() => _requestStop();

    /// <summary>
    /// Persists the stop reason and time into the session's watch metadata, after the
    /// recording has completed (or after a crash, so the trigger provenance is still
    /// recorded).
    /// </summary>
    public void PersistStop(string reason, DateTimeOffset stoppedAt) => _persistStop(reason, stoppedAt);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}
