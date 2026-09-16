namespace MeetCap.Core.Capture;

/// <summary>Why a capture source stopped.</summary>
public sealed class CaptureStoppedEventArgs : EventArgs
{
    public CaptureStoppedEventArgs(Exception? error)
    {
        Error = error;
    }

    /// <summary>Non-null when capture ended because of a failure (for example device loss).</summary>
    public Exception? Error { get; }

    /// <summary>True when capture ended because of an error rather than a requested stop.</summary>
    public bool IsFault => Error is not null;
}

/// <summary>
/// A running capture of one track. Implementations live in
/// <c>MeetCap.WindowsAudio</c>; the recording pipeline only sees this contract.
/// </summary>
/// <remarks>
/// docs/ARCHITECTURE.md section 7: the implementation's callback may only copy the
/// buffer, attach timing metadata and return. Everything expensive happens on the
/// consumer side of <see cref="PacketAvailable"/>.
/// </remarks>
public interface IAudioCaptureSource : IDisposable
{
    AudioSource Source { get; }

    /// <summary>The device's native format for this session.</summary>
    AudioFormat Format { get; }

    CaptureDeviceInfo Device { get; }

    /// <summary>
    /// Raised on the capture thread for every buffer. Handlers MUST only hand the
    /// packet to a bounded queue and return.
    /// </summary>
    event Action<AudioPacket>? PacketAvailable;

    /// <summary>Raised when capture ends, whether requested or by failure.</summary>
    event EventHandler<CaptureStoppedEventArgs>? Stopped;

    /// <summary>Begins capture. Throws when the device cannot be opened.</summary>
    void Start();

    /// <summary>
    /// Requests a stop and returns once the device is released. Safe to call when
    /// capture already stopped.
    /// </summary>
    void Stop();
}

/// <summary>
/// Creates capture sources. Abstracted so the recording pipeline can be exercised
/// without audio hardware (docs/DEVELOPMENT.md section 7).
/// </summary>
public interface IAudioCaptureSourceFactory
{
    IAudioCaptureSource Create(AudioSource source, CaptureDeviceInfo device);
}
