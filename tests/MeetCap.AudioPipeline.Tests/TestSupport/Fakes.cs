using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using MeetCap.Core.Storage;
using MeetCap.Core.Time;
using MeetCap.Persistence.Storage;

namespace MeetCap.AudioPipeline.Tests.TestSupport;

/// <summary>Deterministic clock so flush and disk-check schedules are controllable.</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 15, 14, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Free-space probe with a settable level and an optional failure.</summary>
internal sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
{
    public long FreeBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    public Exception? Failure { get; set; }

    public int CallCount { get; private set; }

    public long GetAvailableFreeBytes(string path)
    {
        CallCount++;
        if (Failure is not null)
        {
            throw Failure;
        }

        return FreeBytes;
    }
}

internal sealed class FakeDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly List<CaptureDeviceInfo> _devices = new();

    public FakeDeviceEnumerator(params CaptureDeviceInfo[] devices) => _devices.AddRange(devices);

    /// <summary>Replaces the enumerated endpoints, for tests that reshape the machine.</summary>
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
/// Capture source driven by the test rather than by hardware: it records whether it was
/// started, lets the test push packets, and lets the test simulate device loss.
/// </summary>
internal sealed class FakeCaptureSource : IAudioCaptureSource
{
    private bool _disposed;

    public FakeCaptureSource(AudioFormat format, CaptureDeviceInfo device, AudioSource source = AudioSource.Mic)
    {
        Format = format;
        Device = device;
        Source = source;
    }

    public AudioSource Source { get; }

    public AudioFormat Format { get; }

    public CaptureDeviceInfo Device { get; set; }

    public event Action<AudioPacket>? PacketAvailable;

    public event EventHandler<CaptureStoppedEventArgs>? Stopped;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="Start"/> throws it instead of starting.</summary>
    public Exception? StartError { get; set; }

    /// <summary>Runs inside <see cref="Start"/>, which is how a burst is produced deterministically.</summary>
    public Action<FakeCaptureSource>? OnStart { get; set; }

    public void Start()
    {
        if (StartError is not null)
        {
            throw StartError;
        }

        StartCount++;
        OnStart?.Invoke(this);
    }

    public void Stop() => StopCount++;

    /// <summary>Pushes one packet as if the device had delivered it.</summary>
    public void Emit(AudioPacket packet) => PacketAvailable?.Invoke(packet);

    /// <summary>Simulates the device disappearing.</summary>
    public void Fail(Exception error) => Stopped?.Invoke(this, new CaptureStoppedEventArgs(error));

    /// <summary>Simulates capture ending without an error.</summary>
    public void EndUnexpectedly() => Stopped?.Invoke(this, new CaptureStoppedEventArgs(null));

    public void Dispose()
    {
        DisposeCount++;
        _disposed = true;
    }

    public bool IsDisposed => _disposed;
}

/// <summary>
/// Creates fake sources. Each call returns the next scripted source, so device-loss
/// recovery can be exercised by supplying a second one.
/// </summary>
internal sealed class FakeCaptureSourceFactory : IAudioCaptureSourceFactory
{
    private readonly Queue<Func<AudioSource, CaptureDeviceInfo, IAudioCaptureSource>> _scripted = new();
    private readonly List<FakeCaptureSource> _created = new();

    public Func<AudioSource, CaptureDeviceInfo, IAudioCaptureSource>? Fallback { get; set; }

    /// <summary>Sources handed out so far, in order.</summary>
    public IReadOnlyList<FakeCaptureSource> Created => _created;

    public int CreateCount { get; private set; }

    public void Enqueue(FakeCaptureSource source)
        => _scripted.Enqueue((_, _) => source);

    public void EnqueueFailure(Exception error)
        => _scripted.Enqueue((_, _) => throw error);

    public IAudioCaptureSource Create(AudioSource source, CaptureDeviceInfo device)
    {
        CreateCount++;

        var factory = _scripted.Count > 0 ? _scripted.Dequeue() : Fallback;
        if (factory is null)
        {
            var implicitSource = new FakeCaptureSource(TestAudio.Formats.Mono48kPcm, device, source);
            _created.Add(implicitSource);
            return implicitSource;
        }

        var created = factory(source, device);
        if (created is FakeCaptureSource fake)
        {
            _created.Add(fake);
        }

        return created;
    }
}

/// <summary>Shared audio formats and packet builders for the pipeline tests.</summary>
internal static class TestAudio
{
    public static class Formats
    {
        /// <summary>48 kHz mono 16-bit PCM: 2 bytes per frame, 96,000 bytes per second.</summary>
        public static readonly AudioFormat Mono48kPcm = new(48_000, 1, 16, AudioSampleFormat.Pcm);
    }

    public const int TenMsFrames = 480;

    /// <summary>Frames in <paramref name="milliseconds"/> at the format's rate.</summary>
    public static int Frames(AudioFormat format, int milliseconds)
        => (int)format.MillisecondsToFrames(milliseconds);

    public static AudioPacket Packet(
        AudioFormat format,
        long startFrame,
        int frames,
        AudioBufferFlags flags = AudioBufferFlags.None)
        => new(
            AudioSource.Mic,
            format,
            new byte[frames * format.BlockAlign],
            startFrame,
            startFrame * 10_000_000L / format.SampleRate,
            DateTimeOffset.UnixEpoch,
            flags);

    /// <summary>
    /// Emits <paramref name="milliseconds"/> of contiguous audio in 100 ms buffers,
    /// returning the next free device position.
    /// </summary>
    public static long EmitSeconds(
        FakeCaptureSource source,
        AudioFormat format,
        long startFrame,
        int milliseconds,
        int bufferMs = 100)
    {
        var remaining = milliseconds;
        var frame = startFrame;

        while (remaining > 0)
        {
            var chunkMs = Math.Min(bufferMs, remaining);
            var frames = Frames(format, chunkMs);
            source.Emit(Packet(format, frame, frames));
            frame += frames;
            remaining -= chunkMs;
        }

        return frame;
    }
}

/// <summary>Temp data root plus a migrated database with one session row.</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string sessionId = "ses_20260915T140000Z_00000001", string sessionStatus = SessionStatus.Created)
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "meetcap-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataRoot);

        Paths = new SessionPaths(DataRoot, sessionId);
        Paths.CreateDirectories();

        Database = new MeetCapDatabase(Path.Combine(DataRoot, "meetcap.db"));
        Database.EnsureMigrated();
        Database.Sessions.Insert(new SessionRecord
        {
            Id = sessionId,
            Title = "Test Session",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = sessionStatus,
            ConfigVersion = 1,
            ConfigSnapshot = "{}",
            Tracks = new[] { AudioSources.Mic },
            CreatedAt = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero),
        });

        SessionId = sessionId;
    }

    public string DataRoot { get; }

    public string SessionId { get; }

    public SessionPaths Paths { get; }

    public MeetCapDatabase Database { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(DataRoot))
                {
                    Directory.Delete(DataRoot, true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until it holds or the timeout expires.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000, int pollMs = 10)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(pollMs).ConfigureAwait(false);
        }

        return condition();
    }

    public static void Until(Func<bool> condition, int timeoutMs = 10_000, int pollMs = 10)
        => UntilAsync(condition, timeoutMs, pollMs).GetAwaiter().GetResult();
}
