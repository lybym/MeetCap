namespace MeetCap.Core.Capture;

/// <summary>
/// Lists the capture endpoints available on this machine. Implemented by the
/// platform audio boundary (<c>MeetCap.WindowsAudio</c>) and faked in tests, so no
/// command or pipeline type ever sees a vendor device object.
/// </summary>
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
}
