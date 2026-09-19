namespace MeetCap.Core.Capture;

/// <summary>
/// Which piece of device timing places a track's buffers on the session timeline
/// (docs/ARCHITECTURE.md section 8.1).
/// </summary>
/// <remarks>
/// <para>
/// The choice is a property of the capture stream, not of the session or the mode: a
/// source either reports a stream position of its own or it does not, and the timeline
/// has to be placed by whatever the stream actually provides
/// (docs/ARCHITECTURE.md section 8: "Do not build the unified timeline only from
/// wall-clock timestamps").
/// </para>
/// <para>
/// A source declares its clock through <see cref="IAudioCaptureSource.Clock"/>, so the
/// recording pipeline learns it from the same object that supplied the session format.
/// Nothing is re-derived from the packet values at run time: a track whose stream has no
/// device position is never judged by one.
/// </para>
/// </remarks>
public enum CaptureClock
{
    /// <summary>
    /// The device's own stream position, in frames. The default, and what WASAPI
    /// microphone capture and system loopback both report: the position counts the
    /// frames the endpoint's stream has produced, so it is an exact sample count.
    /// </summary>
    DevicePosition,

    /// <summary>
    /// The device's QPC timestamp, in 100-nanosecond units, used when the captured
    /// stream has no position of its own.
    /// </summary>
    /// <remarks>
    /// Windows process loopback is the case this exists for. It captures a process
    /// tree through <c>ActivateAudioInterfaceAsync</c> rather than an endpoint's own
    /// stream, and on the real Windows/NAudio combination documented in
    /// docs/ARCHITECTURE.md section 8.1 the audio engine then reports
    /// <c>device_position_frames = 0</c> for <em>every</em> buffer while the QPC
    /// timestamp advances normally. Placing such a track by device position is what
    /// produced one false discontinuity per buffer; placing it by QPC reproduces the
    /// same monotonic session timeline the baseline sources get.
    /// </remarks>
    Qpc,
}

/// <summary>
/// Wire names for <see cref="CaptureClock"/>, used in session diagnostics so the record
/// says which timing a track's timeline was built from (docs/DATA_MODEL.md section 4).
/// </summary>
/// <remarks>
/// The clock is chosen by the capture source, never by the user: it is not a
/// configuration key, so this vocabulary is written to the event log and read back by a
/// human rather than parsed as input.
/// </remarks>
public static class CaptureClocks
{
    public const string DevicePosition = "device_position";
    public const string Qpc = "qpc";

    public static string ToWireName(this CaptureClock clock) => clock switch
    {
        CaptureClock.DevicePosition => DevicePosition,
        CaptureClock.Qpc => Qpc,
        _ => throw new ArgumentOutOfRangeException(nameof(clock), clock, "Unknown capture clock."),
    };
}