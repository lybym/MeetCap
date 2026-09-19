using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;

namespace MeetCap.AudioPipeline.Tests.TestSupport;

/// <summary>
/// Wires a fake platform, a real database and a real filesystem into a
/// <see cref="CaptureService"/>, so a whole recording session can be exercised without
/// audio hardware.
/// </summary>
internal sealed class SessionHarness : IDisposable
{
    public SessionHarness(
        int chunkSeconds = 60,
        int bufferSeconds = 60,
        int flushIntervalMs = 1_000,
        double minimumFreeSpaceGb = 5,
        string microphoneDeviceId = "mic-default",
        int deviceRecoverySeconds = 3,
        TempWorkspace? workspace = null,
        string? mode = null,
        string? renderDeviceId = null,
        string? loopbackMode = null,
        string? loopbackProcessName = null,
        Func<CancellationToken, Task>? beforeReopenAttempt = null)
    {
        _workspace = workspace ?? new TempWorkspace();
        OwnsWorkspace = workspace is null;
        _beforeReopenAttempt = beforeReopenAttempt;

        Clock = new FakeClock();
        Disk = new FakeDiskSpaceProbe();
        Device = new CaptureDeviceInfo("mic-default", "Test Microphone", true);
        RenderDevice = new CaptureDeviceInfo("render-default", "Test Speakers", true);
        Devices = new FakeDeviceEnumerator(Device);
        Devices.SetRenderDevices(RenderDevice);
        Sources = new FakeCaptureSourceFactory();

        var isOnline = string.Equals(mode, SessionModes.Online, StringComparison.Ordinal);
        OnlineCaptureSettings? online = null;
        if (isOnline)
        {
            online = new OnlineCaptureSettings(
                microphoneDeviceId,
                loopbackMode ?? "system",
                renderDeviceId ?? "default",
                loopbackProcessName ?? string.Empty);
        }

        Settings = new CaptureSettings(
            _workspace.DataRoot,
            chunkSeconds,
            bufferSeconds,
            flushIntervalMs,
            minimumFreeSpaceGb,
            microphoneDeviceId,
            configVersion: 1,
            mode: isOnline ? SessionModes.Online : SessionModes.Offline,
            online: online,
            deviceRecoverySeconds: deviceRecoverySeconds);

        Platform = new CapturePlatform(Devices, Sources, Disk, Clock);
        Database = _workspace.Database;

        // The service derives its retry budget from the settings, exactly as production does,
        // so a test that changes the recovery window exercises the shipped derivation instead
        // of a parallel one (docs/RELIABILITY.md section 8).
        Service = new CaptureService(Platform, Database, Settings);
    }

    public bool OwnsWorkspace { get; }

    public TempWorkspace Workspace => _workspace;

    public string DataRoot => _workspace.DataRoot;

    public MeetCapDatabase Database { get; }

    public FakeClock Clock { get; }

    public FakeDiskSpaceProbe Disk { get; }

    public CaptureDeviceInfo Device { get; }

    /// <summary>The render endpoint an online session loopbacks from (docs/ROADMAP.md M5).</summary>
    public CaptureDeviceInfo RenderDevice { get; }

    public FakeDeviceEnumerator Devices { get; }

    public FakeCaptureSourceFactory Sources { get; }

    public CaptureSettings Settings { get; }

    public CapturePlatform Platform { get; }

    public CaptureService Service { get; }

    /// <summary>
    /// A single-microphone track spec built from this harness's platform, for tests that
    /// construct a <see cref="RecordingSession"/> directly rather than through
    /// <see cref="CaptureService.PrepareSession"/>.
    /// </summary>
    public IReadOnlyList<CaptureTrackSpec> MicTrackSpecs()
        => new[]
        {
            new CaptureTrackSpec(
                AudioSource.Mic,
                Device,
                device => Platform.CaptureSources.Create(AudioSource.Mic, device),
                () => AudioDeviceResolver.TryResolve(Devices, Settings.MicrophoneDeviceId)),
        };

    /// <summary>
    /// The retry budget production derives for this harness's recovery window
    /// (<see cref="CaptureService.RecoveryAttemptsFor"/>). Exposed so a test that constructs a
    /// <see cref="RecordingSession"/> directly spends the same budget production would.
    /// </summary>
    public int DeviceRecoveryAttempts => CaptureService.RecoveryAttemptsFor(Settings.DeviceRecoverySeconds);

    /// <summary>
    /// Creates an unstarted <see cref="RecordingSession"/> for this harness's own session
    /// artifacts, taking its liveness marker as the service does. Tests that need to observe a
    /// recovery attempt directly use this instead of <see cref="CaptureService.PrepareSession"/>
    /// so the outage-gating seam can be installed.
    /// </summary>
    /// <param name="online">
    /// When true, builds both the microphone and the loopback track spec, so a dual-track test
    /// can install the seam on both tracks. The harness must have been constructed with
    /// <c>mode: "online"</c>.
    /// </param>
    public RecordingSession PrepareSessionDirect(string title = "Direct Session", bool online = false)
    {
        var paths = Workspace.Paths;
        var tracks = online ? new[] { AudioSources.Mic, AudioSources.Loopback } : new[] { AudioSources.Mic };
        var manifest = new SessionManifest
        {
            SessionId = paths.SessionId,
            Title = title,
            Mode = Settings.Mode,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Created,
            ConfigVersion = Settings.ConfigVersion,
            Tracks = tracks,
            ChunkSeconds = Settings.ChunkSeconds,
        };
        SessionManifestStore.Save(paths.ManifestPath, manifest);

        var recordingLock = SessionRecordingLock.TryAcquire(paths.RecordingLockPath);
        if (recordingLock is null)
        {
            throw new InvalidOperationException(
                "the direct session could not claim its liveness marker; another session is running");
        }

        return new RecordingSession(
            paths,
            Settings,
            Platform,
            Database,
            new JsonlSessionEventSink(paths.EventsPath),
            online ? MicAndLoopbackTrackSpecs() : MicTrackSpecs(),
            Clock,
            manifest,
            DeviceRecoveryAttempts,
            recordingLock,
            afterPacketWritten: null,
            beforeReopenAttempt: _beforeReopenAttempt);
    }

    /// <summary>
    /// The microphone and loopback track specs, in the order the composition root produces them
    /// (microphone first, loopback second; docs/ROADMAP.md M5).
    /// </summary>
    public IReadOnlyList<CaptureTrackSpec> MicAndLoopbackTrackSpecs()
    {
        var renderDeviceId = Settings.Online?.RenderDeviceId ?? "default";
        var loopbackMode = LoopbackModes.Parse(Settings.Online?.LoopbackMode ?? "system");
        var processName = Settings.Online?.ProcessName ?? string.Empty;

        return new[]
        {
            new CaptureTrackSpec(
                AudioSource.Mic,
                Device,
                device => Platform.CaptureSources.Create(AudioSource.Mic, device),
                () => AudioDeviceResolver.TryResolve(Devices, Settings.MicrophoneDeviceId)),
            new CaptureTrackSpec(
                AudioSource.Loopback,
                RenderDevice,
                device => Platform.CaptureSources.CreateLoopback(
                    new LoopbackCaptureRequest(device, loopbackMode, processName)),
                () => AudioDeviceResolver.TryResolveRender(Devices, renderDeviceId)),
        };
    }

    public void Dispose()
    {
        if (OwnsWorkspace)
        {
            _workspace.Dispose();
        }
    }

    private readonly Func<CancellationToken, Task>? _beforeReopenAttempt;
    private readonly TempWorkspace _workspace;
}
