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
    private readonly List<CaptureDeviceInfo> _renderDevices = new();

    public FakeDeviceEnumerator(params CaptureDeviceInfo[] devices) => _devices.AddRange(devices);

    /// <summary>Replaces the enumerated capture endpoints, for tests that reshape the machine.</summary>
    public void Replace(params CaptureDeviceInfo[] devices)
    {
        _devices.Clear();
        _devices.AddRange(devices);
    }

    /// <summary>Sets the render endpoints available for loopback (docs/ROADMAP.md M5).</summary>
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

    /// <summary>
    /// Which device timing this fake's packets are placed by. Defaults to
    /// <see cref="CaptureClock.DevicePosition"/>; a test that reproduces a stream with no
    /// position of its own sets <see cref="CaptureClock.Qpc"/>
    /// (docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    public CaptureClock Clock { get; set; } = CaptureClock.DevicePosition;

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

    /// <summary>
    /// Scripted sources handed out for loopback, in dequeue order. When empty the
    /// <see cref="LoopbackFallback"/> (or an implicit source) is used, mirroring the
    /// microphone path so a dual-track test can script both tracks independently
    /// (docs/ARCHITECTURE.md section 6: the two tracks never wait for one another).
    /// </summary>
    private readonly Queue<Func<LoopbackCaptureRequest, IAudioCaptureSource>> _scriptedLoopback = new();

    /// <summary>Loopback sources handed out so far, in order.</summary>
    public IReadOnlyList<FakeCaptureSource> LoopbackCreated => _loopbackCreated;

    private readonly List<FakeCaptureSource> _loopbackCreated = new();

    public Func<LoopbackCaptureRequest, IAudioCaptureSource>? LoopbackFallback { get; set; }

    public void EnqueueLoopback(FakeCaptureSource source)
        => _scriptedLoopback.Enqueue(_ => source);

    public void EnqueueLoopbackFailure(Exception error)
        => _scriptedLoopback.Enqueue(_ => throw error);

    public IAudioCaptureSource CreateLoopback(LoopbackCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var factory = _scriptedLoopback.Count > 0 ? _scriptedLoopback.Dequeue() : LoopbackFallback;
        if (factory is null)
        {
            var implicitSource = new FakeCaptureSource(
                TestAudio.Formats.Mono48kPcm,
                request.RenderDevice,
                AudioSource.Loopback);
            _loopbackCreated.Add(implicitSource);
            return implicitSource;
        }

        var created = factory(request);
        if (created is FakeCaptureSource fake)
        {
            _loopbackCreated.Add(fake);
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
        => Packet(format, startFrame, frames, AudioSource.Mic, flags);

    public static AudioPacket Packet(
        AudioFormat format,
        long startFrame,
        int frames,
        AudioSource source,
        AudioBufferFlags flags = AudioBufferFlags.None)
        => new(
            source,
            format,
            new byte[frames * format.BlockAlign],
            startFrame,
            startFrame * 10_000_000L / format.SampleRate,
            DateTimeOffset.UnixEpoch,
            flags);

    /// <summary>
    /// Emits <paramref name="milliseconds"/> of contiguous audio in 100 ms buffers,
    /// returning the next free device position. Packets are labelled with the source's
    /// own track so mic and loopback stay distinguishable (docs/ARCHITECTURE.md section 6).
    /// </summary>
    public static long EmitSeconds(
        FakeCaptureSource source,
        AudioFormat format,
        long startFrame,
        int milliseconds,
        int bufferMs = 100)
        => EmitSeconds(source, format, startFrame, milliseconds, source.Source, bufferMs);

    /// <summary>
    /// Emits <paramref name="milliseconds"/> of contiguous audio on a specific track,
    /// returning the next free device position.
    /// </summary>
    public static long EmitSeconds(
        FakeCaptureSource source,
        AudioFormat format,
        long startFrame,
        int milliseconds,
        AudioSource track,
        int bufferMs = 100)
    {
        var remaining = milliseconds;
        var frame = startFrame;

        while (remaining > 0)
        {
            var chunkMs = Math.Min(bufferMs, remaining);
            var frames = Frames(format, chunkMs);
            source.Emit(Packet(format, frame, frames, track));
            frame += frames;
            remaining -= chunkMs;
        }

        return frame;
    }

    /// <summary>
    /// A packet from a capture source that reports no device position of its own: the
    /// Windows process-loopback shape, where every buffer carries device position 0 and
    /// only the QPC timestamp advances (issue #33, docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    public static AudioPacket PacketWithoutDevicePosition(
        AudioFormat format,
        long startFrame,
        int frames,
        AudioSource source = AudioSource.Loopback,
        AudioBufferFlags flags = AudioBufferFlags.None)
        => new(
            source,
            format,
            new byte[frames * format.BlockAlign],
            0,
            startFrame * 10_000_000L / format.SampleRate,
            DateTimeOffset.UnixEpoch,
            flags);

    /// <summary>
    /// Emits <paramref name="milliseconds"/> of contiguous audio whose only timing is the
    /// QPC timestamp, returning the next free position in frames. The counterpart of
    /// <see cref="EmitSeconds"/> for a stream with no device position of its own
    /// (docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    public static long EmitSecondsWithoutDevicePosition(
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
            source.Emit(PacketWithoutDevicePosition(format, frame, frames, source.Source));
            frame += frames;
            remaining -= chunkMs;
        }

        return frame;
    }

    /// <summary>
    /// A packet that carries neither a device position nor a QPC timestamp, so no clock can
    /// place it (docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    public static AudioPacket PacketWithoutAnyTiming(
        AudioFormat format,
        int frames,
        AudioSource source)
        => new(
            source,
            format,
            new byte[frames * format.BlockAlign],
            0,
            null,
            DateTimeOffset.UnixEpoch);
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

    /// <summary>
    /// Awaits a task with a bound. A test that waits on background recording work must
    /// never be able to hang the whole suite (and the CI job) forever, so a stall
    /// becomes a clear failure instead.
    /// </summary>
    public static async Task<T> ForAsync<T>(Task<T> task, int timeoutMs, string description)
    {
        ArgumentNullException.ThrowIfNull(task);

        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"{description} did not finish within {timeoutMs} ms.");
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>Bounded await for a task that returns no value.</summary>
    public static async Task ForAsync(Task task, int timeoutMs, string description)
    {
        ArgumentNullException.ThrowIfNull(task);

        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"{description} did not finish within {timeoutMs} ms.");
        }

        await task.ConfigureAwait(false);
    }
}
