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

    /// <summary>
    /// Which device timing this source's packets can be placed by
    /// (docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see cref="CaptureClock.DevicePosition"/>, which is what WASAPI
    /// microphone capture and system loopback report. A source declares
    /// <see cref="CaptureClock.Qpc"/> only when the captured stream has no position of its
    /// own, which is how Windows process loopback behaves: every buffer carries
    /// <c>device_position_frames = 0</c> while the QPC timestamp advances.
    /// </para>
    /// <para>
    /// The recording pipeline reads this from the same source that supplied
    /// <see cref="Format"/>, so the timeline is placed by a clock the stream actually
    /// provides. Nothing is inferred from the packet values afterwards.
    /// </para>
    /// </remarks>
    CaptureClock Clock { get; }

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
    /// <summary>
    /// Creates a microphone capture source for a resolved capture endpoint. The
    /// returned source has <see cref="IAudioCaptureSource.Source"/> equal to
    /// <see cref="AudioSource.Mic"/>.
    /// </summary>
    IAudioCaptureSource Create(AudioSource source, CaptureDeviceInfo device);

    /// <summary>
    /// Creates the loopback capture source — "what the machine plays" — for a resolved
    /// render endpoint and a loopback mode (docs/ARCHITECTURE.md section 7,
    /// docs/ROADMAP.md M5). The returned source has
    /// <see cref="IAudioCaptureSource.Source"/> equal to
    /// <see cref="AudioSource.Loopback"/>.
    /// </summary>
    /// <remarks>
    /// The implementation uses NAudio's supported loopback capture rather than raw
    /// WASAPI/COM plumbing (docs/ARCHITECTURE.md section 2). Process loopback is an
    /// additive capture-source option; system loopback is the baseline, so a request
    /// whose <see cref="LoopbackCaptureRequest.Mode"/> is
    /// <see cref="LoopbackMode.System"/> ignores
    /// <see cref="LoopbackCaptureRequest.ProcessName"/>.
    /// </remarks>
    IAudioCaptureSource CreateLoopback(LoopbackCaptureRequest request);
}
