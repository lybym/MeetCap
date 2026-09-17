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
        int maxDeviceRecoveryAttempts = 3,
        TempWorkspace? workspace = null,
        string? mode = null,
        string? renderDeviceId = null,
        string? loopbackMode = null,
        string? loopbackProcessName = null)
    {
        _workspace = workspace ?? new TempWorkspace();
        OwnsWorkspace = workspace is null;

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
            online: online);

        Platform = new CapturePlatform(Devices, Sources, Disk, Clock);
        Database = _workspace.Database;
        Service = new CaptureService(Platform, Database, Settings, maxDeviceRecoveryAttempts);
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

    public void Dispose()
    {
        if (OwnsWorkspace)
        {
            _workspace.Dispose();
        }
    }

    private readonly TempWorkspace _workspace;
}
