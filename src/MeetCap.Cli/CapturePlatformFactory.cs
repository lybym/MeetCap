namespace MeetCap.Cli;

using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Time;
using MeetCap.WindowsAudio;

/// <summary>
/// Builds the capture platform a command runs against. Abstracted so command tests can
/// drive <c>devices</c>, <c>start</c> and <c>stop</c> without audio hardware
/// (docs/DEVELOPMENT.md section 7).
/// </summary>
internal interface ICapturePlatformFactory
{
    CapturePlatform Create();
}

/// <summary>
/// The production platform: NAudio 3 for enumeration and WASAPI capture, the real
/// filesystem for free-space checks, and the system clock.
/// </summary>
internal sealed class NAudioCapturePlatformFactory : ICapturePlatformFactory
{
    public static readonly NAudioCapturePlatformFactory Instance = new();

    public CapturePlatform Create() => new(
        new NAudioDeviceEnumerator(),
        new NAudioCaptureSourceFactory(),
        new DriveInfoDiskSpaceProbe(),
        SystemClock.Instance);
}
