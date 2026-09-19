using MeetCap.Core.Capture;
using MeetCap.Core.Storage;
using MeetCap.Core.Time;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Capture platform for CLI tests: real command code, real filesystem, real SQLite, but
/// no audio hardware (docs/DEVELOPMENT.md section 7).
/// </summary>
internal sealed class FakeCapturePlatformFactory : ICapturePlatformFactory
{
    public FakeDeviceEnumerator Devices { get; } = new();

    public FakeCaptureSourceFactory Sources { get; } = new();

    public FakeDiskSpaceProbe Disk { get; } = new();

    public FakeClock Clock { get; } = new();

    public CapturePlatform Create() => new(Devices, Sources, Disk, Clock);
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 15, 14, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
{
    public long FreeBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    public long GetAvailableFreeBytes(string path) => FreeBytes;
}

internal sealed class FakeDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly List<CaptureDeviceInfo> _devices = new();
    private readonly List<CaptureDeviceInfo> _renderDevices = new();

    public void Replace(params CaptureDeviceInfo[] devices)
    {
        _devices.Clear();
        _devices.AddRange(devices);
    }

    public void SetRenderDevices(params CaptureDeviceInfo[] devices)
    {
        _renderDevices.Clear();
        _renderDevices.AddRange(devices);
    }

    public IReadOnlyList<CaptureDeviceInfo> EnumerateCaptureDevices() => _devices;

    public CaptureDeviceInfo? GetDefaultCaptureDevice() => _devices.FirstOrDefault(d => d.IsDefault);

    public CaptureDeviceInfo? FindCaptureDevice(string deviceId)
        => _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CaptureDeviceInfo> EnumerateRenderDevices() => _renderDevices;

    public CaptureDeviceInfo? GetDefaultRenderDevice() => _renderDevices.FirstOrDefault(d => d.IsDefault);

    public CaptureDeviceInfo? FindRenderDevice(string deviceId)
        => _renderDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A capture source that behaves like a live endpoint: once started it keeps
/// producing audio until it is stopped, so command-level tests can record a real
/// session in the background. Used for both the mic and loopback tracks
/// (docs/ARCHITECTURE.md section 5).
/// </summary>
internal sealed class FakeCaptureSource : IAudioCaptureSource
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _producer;

    public FakeCaptureSource(AudioFormat format, CaptureDeviceInfo device, AudioSource source)
    {
        Format = format;
        Device = device;
        Source = source;
    }

    public AudioSource Source { get; }

    public AudioFormat Format { get; }

    /// <summary>
    /// This fake produces advancing device positions, so it declares the default clock
    /// (docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    public CaptureClock Clock => CaptureClock.DevicePosition;

    public CaptureDeviceInfo Device { get; }

    public event Action<AudioPacket>? PacketAvailable;

    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    public int StartCount { get; private set; }

    public Exception? StartError { get; set; }

    public void Start()
    {
        if (StartError is not null)
        {
            throw StartError;
        }

        StartCount++;
        _producer = Task.Run(ProduceAsync);
    }

    private async Task ProduceAsync()
    {
        long frame = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frames = (int)Format.MillisecondsToFrames(100);
                PacketAvailable?.Invoke(new AudioPacket(
                    Source,
                    Format,
                    new byte[frames * Format.BlockAlign],
                    frame,
                    frame * 10_000_000L / Format.SampleRate,
                    DateTimeOffset.UtcNow));

                frame += frames;
                await Task.Delay(20, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    public void Stop()
    {
        _stop.Cancel();
        try
        {
            _producer?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The producer is already ending.
        }

        Stopped?.Invoke(this, new CaptureStoppedEventArgs(null));
    }

    public void Dispose() => _stop.Dispose();
}

internal sealed class FakeCaptureSourceFactory : IAudioCaptureSourceFactory
{
    private readonly List<FakeCaptureSource> _created = new();
    private readonly List<FakeCaptureSource> _loopbackCreated = new();

    public Func<AudioSource, CaptureDeviceInfo, IAudioCaptureSource>? Fallback { get; set; }

    public Func<LoopbackCaptureRequest, IAudioCaptureSource>? LoopbackFallback { get; set; }

    public IReadOnlyList<FakeCaptureSource> Created => _created;

    public IReadOnlyList<FakeCaptureSource> LoopbackCreated => _loopbackCreated;

    public AudioFormat Format { get; set; } = new(48_000, 1, 16, AudioSampleFormat.Pcm);

    public IAudioCaptureSource Create(AudioSource source, CaptureDeviceInfo device)
    {
        if (Fallback is not null)
        {
            return Fallback(source, device);
        }

        var created = new FakeCaptureSource(Format, device, source);
        _created.Add(created);
        return created;
    }

    public IAudioCaptureSource CreateLoopback(LoopbackCaptureRequest request)
    {
        if (LoopbackFallback is not null)
        {
            return LoopbackFallback(request);
        }

        var created = new FakeCaptureSource(Format, request.RenderDevice, AudioSource.Loopback);
        _loopbackCreated.Add(created);
        return created;
    }
}
