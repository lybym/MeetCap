namespace MeetCap.Cli.Commands.Watch;

/// <summary>
/// One currently-running watched main process, as observed by an
/// <see cref="IProcessProbe"/>. The <see cref="Id"/> + <see cref="StartTime"/> pair is
/// the fingerprint that distinguishes a genuine new launch from a pre-existing process
/// or from PID reuse (issue #52 review update: fingerprint by process ID + start time).
/// </summary>
/// <remarks>
/// Only configured main process names ever appear here; helper/child processes are
/// filtered out by the probe and never trigger or extend a recording.
/// </remarks>
public sealed record WatchedProcess(string Name, int Id, DateTimeOffset StartTime);

/// <summary>
/// Metadata for the process observation that started one watched session: persisted into
/// the session directory so a recording started by <c>meetcap watch</c> carries its
/// trigger provenance (process name/ID/start time) and stop reason.
/// </summary>
public sealed record WatchTrigger(
    string ProcessName,
    int ProcessId,
    DateTimeOffset ProcessStartTime,
    DateTimeOffset ObservedAt);
