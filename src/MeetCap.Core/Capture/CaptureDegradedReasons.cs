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
    /// A process-loopback track carried samples, but every one of them was digital zero
    /// (issue #38). The track may simply have had a silent target, or the process-loopback
    /// capture may have failed to deliver the target tree's audio; the session states that
    /// the track is silent rather than reporting the run as healthy.
    /// </summary>
    public const string SilentProcessLoopback = "silent_process_loopback";
}