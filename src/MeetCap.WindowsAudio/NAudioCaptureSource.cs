namespace MeetCap.WindowsAudio;

using System.Runtime.InteropServices;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// One WASAPI capture of a Windows endpoint, exposed as MeetCap-owned packets.
/// </summary>
/// <remarks>
/// <para>
/// The NAudio callback is used exactly as docs/ARCHITECTURE.md section 7 requires: it
/// copies the buffer into a private array, attaches the device position and QPC
/// timestamp NAudio exposes, and hands the packet on. It performs no file, database or
/// network work, so a slow consumer can only fill the caller's bounded queue.
/// </para>
/// <para>
/// The device's own mix format is captured rather than converted. Converting or
/// resampling here would be exactly the kind of DSP work the capture path must not do,
/// and docs/DEVELOPMENT.md section 5 requires the raw source to be preserved.
/// </para>
/// </remarks>
internal sealed class NAudioCaptureSource : IAudioCaptureSource
{
    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private bool _started;
    private bool _disposed;

    public NAudioCaptureSource(
        AudioSource source,
        MMDevice device,
        CaptureDeviceInfo deviceInfo,
        WasapiRecorder recorder)
    {
        _device = device;
        _recorder = recorder;

        var waveFormat = recorder.WaveFormat
            ?? throw new DeviceUnavailableException(
                $"The capture device '{deviceInfo.DisplayName}' did not report a usable wave format.");

        Source = source;
        Format = NAudioWaveFormatAdapter.ToAudioFormat(waveFormat, deviceInfo);
        Device = deviceInfo with
        {
            Id = string.IsNullOrWhiteSpace(recorder.DeviceId) ? deviceInfo.Id : recorder.DeviceId,
            FriendlyName = string.IsNullOrWhiteSpace(recorder.DeviceFriendlyName)
                ? deviceInfo.DisplayName
                : recorder.DeviceFriendlyName,
        };
    }

    public AudioSource Source { get; }

    public AudioFormat Format { get; }

    public CaptureDeviceInfo Device { get; }

    public event Action<AudioPacket>? PacketAvailable;

    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            _recorder.StartRecording();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _started = false;
            throw new DeviceUnavailableException(
                $"Capture could not start on '{Device.DisplayName}': {ex.Message}",
                ex);
        }
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The device may already be gone; the session records that separately.
            Stopped?.Invoke(this, new CaptureStoppedEventArgs(ex));
        }
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> data,
        AudioClientBufferFlags flags,
        long devicePositionFrames,
        long qpcPosition)
    {
        var handler = PacketAvailable;
        if (handler is null || data.IsEmpty)
        {
            return;
        }

        // The span is only valid for the duration of this callback, so the bytes have
        // to be copied before the packet crosses to the consumer thread.
        var blockAlign = Format.BlockAlign;
        var alignedLength = data.Length - (data.Length % blockAlign);
        if (alignedLength <= 0)
        {
            return;
        }

        var payload = data[..alignedLength].ToArray();

        var packet = new AudioPacket(
            Source,
            Format,
            payload,
            devicePositionFrames,
            qpcPosition > 0 ? qpcPosition : null,
            DateTimeOffset.UtcNow,
            NAudioWaveFormatAdapter.ToBufferFlags(flags));

        handler(packet);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _started = false;
        Stopped?.Invoke(this, new CaptureStoppedEventArgs(e.Exception));
    }

    internal void Attach()
    {
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;

        try
        {
            _recorder.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Releasing a device that has already disappeared must not mask the
            // session outcome.
        }

        _device.Dispose();
    }
}

/// <summary>Creates WASAPI capture sources for resolved endpoints.</summary>
public sealed class NAudioCaptureSourceFactory : IAudioCaptureSourceFactory
{
    public IAudioCaptureSource Create(AudioSource source, CaptureDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (source != AudioSource.Mic)
        {
            // M1 captures the microphone only; loopback arrives with M5 and must not
            // silently record something else in the meantime.
            throw new DeviceUnavailableException(
                $"M1 captures the '{AudioSources.Mic}' track only; '{source.ToWireName()}' is not implemented yet.");
        }

        using var enumerator = new MMDeviceEnumerator();

        MMDevice? mmDevice = null;
        try
        {
            mmDevice = enumerator.GetDevice(device.Id);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            throw new DeviceUnavailableException(
                $"Capture device '{device.DisplayName}' [{device.Id}] could not be opened: {ex.Message}",
                ex);
        }

        try
        {
            var recorder = new WasapiRecorderBuilder()
                .WithDevice(mmDevice)
                // Shared mode keeps the device usable by other applications and needs no
                // exclusive-format negotiation, which is what a recorder must not do.
                .WithSharedMode()
                .WithEventSync()
                .Build();

            var capture = new NAudioCaptureSource(source, mmDevice, device, recorder);
            capture.Attach();
            return capture;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mmDevice.Dispose();
            throw new DeviceUnavailableException(
                $"Capture could not be initialized for '{device.DisplayName}': {ex.Message}",
                ex);
        }
    }
}
