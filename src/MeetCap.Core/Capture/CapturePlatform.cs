namespace MeetCap.Core.Capture;

using MeetCap.Core.Storage;
using MeetCap.Core.Time;

/// <summary>
/// Everything the recording pipeline needs from the outside world. The CLI
/// composition root builds the production instance; tests build one from fakes, so
/// no pipeline code reaches for a static or a real device.
/// </summary>
public sealed class CapturePlatform
{
    public CapturePlatform(
        IAudioDeviceEnumerator devices,
        IAudioCaptureSourceFactory captureSources,
        IDiskSpaceProbe diskSpace,
        IClock clock)
    {
        Devices = devices ?? throw new ArgumentNullException(nameof(devices));
        CaptureSources = captureSources ?? throw new ArgumentNullException(nameof(captureSources));
        DiskSpace = diskSpace ?? throw new ArgumentNullException(nameof(diskSpace));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IAudioDeviceEnumerator Devices { get; }

    public IAudioCaptureSourceFactory CaptureSources { get; }

    public IDiskSpaceProbe DiskSpace { get; }

    public IClock Clock { get; }
}
