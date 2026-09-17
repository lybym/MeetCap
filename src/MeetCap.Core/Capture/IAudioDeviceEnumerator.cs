namespace MeetCap.Core.Capture;

/// <summary>
/// Lists the audio endpoints available on this machine — capture endpoints for the
/// microphone track and render endpoints for loopback capture (docs/ARCHITECTURE.md
/// section 7). Implemented by the platform audio boundary
/// (<c>MeetCap.WindowsAudio</c>) and faked in tests, so no command or pipeline type
/// ever sees a vendor device object.
/// </summary>
/// <remarks>
/// <see cref="CaptureDeviceInfo"/> is a generic endpoint record (id, friendly name,
/// whether it is the system default); it describes a render endpoint exactly as well
/// as a capture one, so the loopback track reuses it rather than carrying a parallel
/// type.
/// </remarks>
public interface IAudioDeviceEnumerator
{
    /// <summary>
    /// Currently active capture endpoints. Empty when the machine has no usable
    /// microphone rather than throwing.
    /// </summary>
    IReadOnlyList<CaptureDeviceInfo> EnumerateCaptureDevices();

    /// <summary>
    /// The system default capture endpoint, or <c>null</c> when none is available.
    /// </summary>
    CaptureDeviceInfo? GetDefaultCaptureDevice();

    /// <summary>
    /// Resolves an explicit endpoint id, or <c>null</c> when that endpoint is not
    /// currently an active capture device.
    /// </summary>
    CaptureDeviceInfo? FindCaptureDevice(string deviceId);

    /// <summary>
    /// Currently active render endpoints. These are the speakers/headphones the machine
    /// plays to, and are the source for system loopback capture (docs/ARCHITECTURE.md
    /// section 7). Empty when the machine has no render endpoint rather than throwing.
    /// </summary>
    IReadOnlyList<CaptureDeviceInfo> EnumerateRenderDevices();

    /// <summary>
    /// The system default render endpoint, or <c>null</c> when none is available.
    /// This is the baseline loopback source (docs/ROADMAP.md M5).
    /// </summary>
    CaptureDeviceInfo? GetDefaultRenderDevice();

    /// <summary>
    /// Resolves an explicit render endpoint id, or <c>null</c> when that endpoint is
    /// not currently an active render device.
    /// </summary>
    CaptureDeviceInfo? FindRenderDevice(string deviceId);
}
