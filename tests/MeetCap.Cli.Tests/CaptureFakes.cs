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

    public void Replace(params CaptureDeviceInfo[] devices)
    {
        _devices.Clear();
        _devices.AddRange(devices);
    }

    public IReadOnlyList<CaptureDeviceInfo> EnumerateCaptureDevices() => _devices;

    public CaptureDeviceInfo? GetDefaultCaptureDevice() => _devices.FirstOrDefault(d => d.IsDefault);

    public CaptureDeviceInfo? FindCaptureDevice(string deviceId)
        => _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A capture source that behaves like a live microphone: once started it keeps
/// producing 48 kHz mono audio until it is stopped, so command-level tests can record a
/// real session in the background.
/// </summary>
internal sealed class FakeCaptureSource : IAudioCaptureSource
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _producer;

    public FakeCaptureSource(AudioFormat format, CaptureDeviceInfo device)
    {
        Format = format;
        Device = device;
    }

    public AudioSource Source => AudioSource.Mic;

    public AudioFormat Format { get; }

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
                    AudioSource.Mic,
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
            Console.Error.WriteLine("[DIAG] FAKE: producer exited (OCE)");
            // Stopped.
        }
    }

    public void Stop()
    {
        Console.Error.WriteLine("[DIAG] FAKE: stop enter");
        _stop.Cancel();
        try
        {
            var ok = _producer?.Wait(TimeSpan.FromSeconds(5));
            Console.Error.WriteLine($"[DIAG] FAKE: producer wait returned ok={ok}");
        }
        catch (AggregateException)
        {
            Console.Error.WriteLine("[DIAG] FAKE: producer wait threw AggregateException");
            // The producer is already ending.
        }

        Stopped?.Invoke(this, new CaptureStoppedEventArgs(null));
        Console.Error.WriteLine("[DIAG] FAKE: stop exit");
    }

    public void Dispose() => _stop.Dispose();
}

internal sealed class FakeCaptureSourceFactory : IAudioCaptureSourceFactory
{
    private readonly List<FakeCaptureSource> _created = new();

    public Func<AudioSource, CaptureDeviceInfo, IAudioCaptureSource>? Fallback { get; set; }

    public IReadOnlyList<FakeCaptureSource> Created => _created;

    public AudioFormat Format { get; set; } = new(48_000, 1, 16, AudioSampleFormat.Pcm);

    public IAudioCaptureSource Create(AudioSource source, CaptureDeviceInfo device)
    {
        if (Fallback is not null)
        {
            return Fallback(source, device);
        }

        var created = new FakeCaptureSource(Format, device);
        _created.Add(created);
        return created;
    }
}
