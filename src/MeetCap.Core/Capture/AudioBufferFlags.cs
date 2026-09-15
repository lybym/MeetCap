namespace MeetCap.Core.Capture;

/// <summary>
/// MeetCap-owned mirror of the WASAPI capture-buffer flags. The capture boundary
/// translates the vendor flags into these values so no vendor type reaches
/// <c>MeetCap.Core</c> (docs/ARCHITECTURE.md section 3).
/// </summary>
/// <remarks>
/// docs/RELIABILITY.md sections 7 and 8: a discontinuity or a bad timestamp must be
/// recorded as an explicit event, never silently smoothed over.
/// </remarks>
[Flags]
public enum AudioBufferFlags
{
    None = 0,

    /// <summary>The device reported a discontinuity before this buffer.</summary>
    DataDiscontinuity = 1,

    /// <summary>The buffer is digital silence (the device had no signal to deliver).</summary>
    Silent = 2,

    /// <summary>The device/driver reported an unreliable timestamp for this buffer.</summary>
    TimestampError = 4,
}
