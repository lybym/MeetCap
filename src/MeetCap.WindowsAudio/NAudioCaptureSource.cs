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
            // The microphone track is the only capture source this method builds; the
            // loopback track is built by CreateLoopback so the two tracks cannot be
            // confused for one another (docs/ARCHITECTURE.md section 6).
            throw new DeviceUnavailableException(
                $"Create builds the '{AudioSources.Mic}' track only; '{source.ToWireName()}' " +
                "is built by CreateLoopback.");
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

            return Wrap(AudioSource.Mic, mmDevice, device, recorder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mmDevice.Dispose();
            throw new DeviceUnavailableException(
                $"Capture could not be initialized for '{device.DisplayName}': {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Creates the loopback capture source — "what the machine plays" — using NAudio's
    /// supported loopback capture rather than raw WASAPI/COM plumbing
    /// (docs/ARCHITECTURE.md section 2, docs/ROADMAP.md M5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// System loopback (<c>online.loopback.mode = system</c>, the baseline) uses
    /// <c>WasapiRecorderBuilder.WithLoopbackCapture</c> on a render endpoint, which
    /// captures the render device's mix in shared mode. Process loopback
    /// (<c>online.loopback.mode = process</c>, additive) uses
    /// <c>WasapiRecorderBuilder.WithProcessLoopback</c>, NAudio's wrapper for the
    /// Windows process-loopback activation. The two are mutually exclusive activation
    /// paths — system loopback is <c>AUDCLNT_STREAMFLAGS_LOOPBACK</c> on a render
    /// endpoint, process loopback is <c>ActivateAudioInterfaceAsync</c> with a process
    /// filter — so a process request never also sets the system flag.
    /// </para>
    /// <para>
    /// Both paths produce a <c>WasapiRecorder</c> with the same span-based
    /// <c>DataAvailable</c> callback the microphone path already uses, so the loopback
    /// track reuses the same adapter and the same MeetCap-owned packet type. No NAudio
    /// type crosses the assembly boundary (docs/DEVELOPMENT.md section 4).
    /// </para>
    /// </remarks>
    public IAudioCaptureSource CreateLoopback(LoopbackCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var enumerator = new MMDeviceEnumerator();

        MMDevice? mmDevice = null;
        try
        {
            mmDevice = enumerator.GetDevice(request.RenderDevice.Id);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            throw new DeviceUnavailableException(
                $"Render device '{request.RenderDevice.DisplayName}' [{request.RenderDevice.Id}] " +
                $"could not be opened for loopback: {ex.Message}",
                ex);
        }

        try
        {
            var builder = new WasapiRecorderBuilder()
                .WithDevice(mmDevice)
                .WithSharedMode()
                .WithEventSync();

            WasapiRecorder recorder;
            if (request.IsProcessLoopback)
            {
                var processId = ResolveProcessId(request.ProcessName);
                // Process loopback is activated through the Windows process-loopback
                // activation path, which is async, so the async builder is used.
                recorder = builder
                    .WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                    .BuildAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                recorder = builder.WithLoopbackCapture().Build();
            }

            return Wrap(AudioSource.Loopback, mmDevice, request.RenderDevice, recorder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mmDevice.Dispose();
            throw new DeviceUnavailableException(
                $"Loopback capture could not be initialized for '{request.RenderDevice.DisplayName}'" +
                (request.IsProcessLoopback ? $" (process='{request.ProcessName}')" : string.Empty) +
                $": {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Resolves a target process name to an OS process id for process loopback. Returns
    /// the first match; a process that cannot be found fails loudly rather than
    /// silently degrading to system loopback (docs/RELIABILITY.md section 8).
    /// </summary>
    private static uint ResolveProcessId(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new DeviceUnavailableException(
                "Process loopback was requested but capture.online.process_name is empty. " +
                "Set the target process name, or use the baseline 'system' loopback.");
        }

        // GetProcessesByName matches without the .exe extension on Windows.
        var matches = System.Diagnostics.Process.GetProcessesByName(processName);
        foreach (var match in matches)
        {
            using (match)
            {
                try
                {
                    return (uint)match.Id;
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and id read; try the next.
                }
            }
        }

        throw new DeviceUnavailableException(
            $"Process loopback target '{processName}' is not currently running. " +
            "Start the meeting application before beginning capture, or use the baseline 'system' loopback.");
    }

    private static NAudioCaptureSource Wrap(
        AudioSource source,
        MMDevice mmDevice,
        CaptureDeviceInfo deviceInfo,
        WasapiRecorder recorder)
    {
        var capture = new NAudioCaptureSource(source, mmDevice, deviceInfo, recorder);
        capture.Attach();
        return capture;
    }
}
