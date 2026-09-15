using MeetCap.Core.Capture;
using MeetCap.Persistence.Storage;

namespace MeetCap.AudioPipeline.Tests.TestSupport;

/// <summary>
/// Wires a fake platform, a real database and a real filesystem into a
/// <see cref="CaptureService"/>, so a whole recording session can be exercised without
/// audio hardware.
/// </summary>
internal sealed class SessionHarness : IDisposable
{
    private readonly TempWorkspace _workspace;

    public SessionHarness(
        int chunkSeconds = 60,
        int bufferSeconds = 60,
        int flushIntervalMs = 1_000,
        double minimumFreeSpaceGb = 5,
        string microphoneDeviceId = "mic-default",
        int maxDeviceRecoveryAttempts = 3,
        TempWorkspace? workspace = null)
    {
        _workspace = workspace ?? new TempWorkspace();
        OwnsWorkspace = workspace is null;

        Clock = new FakeClock();
        Disk = new FakeDiskSpaceProbe();
        Device = new CaptureDeviceInfo("mic-default", "Test Microphone", true);
        Devices = new FakeDeviceEnumerator(Device);
        Sources = new FakeCaptureSourceFactory();

        Settings = new CaptureSettings(
            _workspace.DataRoot,
            chunkSeconds,
            bufferSeconds,
            flushIntervalMs,
            minimumFreeSpaceGb,
            microphoneDeviceId,
            configVersion: 1);

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

    public FakeDeviceEnumerator Devices { get; }

    public FakeCaptureSourceFactory Sources { get; }

    public CaptureSettings Settings { get; }

    public CapturePlatform Platform { get; }

    public CaptureService Service { get; }

    public void Dispose()
    {
        if (OwnsWorkspace)
        {
            _workspace.Dispose();
        }
    }
}
