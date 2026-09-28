namespace MeetCap.Core.Capture;

/// <summary>
/// Why a track is reported degraded without having ended early. These are durable wire
/// values recorded in <c>session.json</c>'s <c>track_health[].degraded_reason</c>, kept
/// separate from a track's <c>end_reason</c> because the track completed and it is the
/// content of what it recorded that is in question (docs/RELIABILITY.md section 17).
/// </summary>
public static class CaptureDegradedReasons
{
    /// <summary>
    /// A process-loopback track carried samples, but not one of them held decodable content:
    /// every sample was digital zero (or a non-finite value) (issue #38). The track may simply
    /// have had a silent target, or the process-loopback capture may have failed to deliver the
    /// target tree's audio; the session states that the track is silent rather than reporting the
    /// run as healthy.
    /// </summary>
    public const string SilentProcessLoopback = "silent_process_loopback";

    /// <summary>
    /// A process-loopback track started capturing and then delivered no samples at all for the
    /// whole session. The stream produced nothing — not even the zero-filled buffers issue #38
    /// recorded — so the track would otherwise be a completed, healthy, empty file.
    /// </summary>
    public const string EmptyProcessLoopback = "empty_process_loopback";
}