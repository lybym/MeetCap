namespace MeetCap.Core.Sessions;

/// <summary>
/// The operational event vocabulary appended to <c>events.jsonl</c>
/// (docs/DATA_MODEL.md section 4).
/// </summary>
public static class SessionEventNames
{
    public const string SessionStarted = "session.started";
    public const string SessionStopRequested = "session.stop_requested";
    public const string SessionStopping = "session.stopping";
    public const string SessionStopped = "session.stopped";
    public const string SessionRecovered = "session.recovered";

    public const string ChunkOpened = "audio.chunk.opened";
    public const string ChunkClosed = "audio.chunk.closed";
    public const string ChunkRecovered = "audio.chunk.recovered";
    public const string ChunkCorrupt = "audio.chunk.corrupt";

    public const string CaptureGap = "capture.gap";
    public const string CaptureDiscontinuity = "capture.discontinuity";
    public const string CaptureDeviceLost = "capture.device_lost";
    public const string CaptureDeviceRestored = "capture.device_restored";
    public const string CaptureDeviceLostFatal = "capture.device_lost_fatal";
    public const string CaptureFormatChanged = "capture.format_changed";
    public const string CaptureBufferOverflow = "capture.buffer_overflow";
    public const string CaptureConsumerStalled = "capture.consumer_stalled";

    public const string StorageLowDiskSpace = "storage.low_disk_space";
    public const string StorageDiskExhausted = "storage.disk_exhausted";
    public const string StorageProbeFailed = "storage.probe_failed";

    /// <summary>
    /// Written by startup recovery or <c>meetcap session repair</c> when the session's
    /// timeline still has a provable hole. docs/RELIABILITY.md section 6 forbids
    /// claiming success while a known gap remains, so this event is the durable
    /// statement of that failure.
    /// </summary>
    public const string SessionRepairIncomplete = "session.repair.incomplete";
}

/// <summary>
/// One append-only operational event. Null members are omitted from the JSON line,
/// so each event carries only the fields its name defines.
/// </summary>
public sealed record SessionEvent(string Name, long AtMs)
{
    public string? Source { get; init; }

    public string? Chunk { get; init; }

    public long? StartMs { get; init; }

    public long? EndMs { get; init; }

    public long? GapMs { get; init; }

    public long? DevicePositionFrames { get; init; }

    public long? QpcPositionTicks { get; init; }

    public long? FreeBytes { get; init; }

    /// <summary>
    /// Machine-readable classification of the event, used by <c>capture.gap</c> to state
    /// <em>why</em> audio is missing (docs/DATA_MODEL.md section 4). The event name alone
    /// would make "the device skipped it" indistinguishable from "a chunk could not be
    /// repaired".
    /// </summary>
    public string? Reason { get; init; }

    public int? Count { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// Where session events go. The production implementation appends JSONL; tests
/// capture the events in memory to assert on degraded-state reporting
/// (docs/RELIABILITY.md section 4 step 3).
/// </summary>
public interface ISessionEventSink
{
    void Write(SessionEvent sessionEvent);
}
