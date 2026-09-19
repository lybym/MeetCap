using MeetCap.Core.Capture;
using Xunit;

namespace MeetCap.WindowsAudio.Tests;

/// <summary>
/// Coverage for the loopback timing decision — which capture clock a loopback request's
/// stream can be placed by (issue #33, docs/ARCHITECTURE.md section 8.1).
/// </summary>
/// <remarks>
/// The activation itself is COM-bound and needs real audio hardware, but the decision that
/// the process path is placed by QPC rather than by a device position it does not have is a
/// pure function of the request, so it is worth pinning here: getting it wrong is exactly
/// what produced one false discontinuity per buffer on real hardware.
/// </remarks>
public class NAudioLoopbackClockTests
{
    private static readonly CaptureDeviceInfo RenderDevice = new("render-1", "Speakers", true);

    [Fact]
    public void LoopbackClock_SystemRequest_IsPlacedByTheEndpointsDevicePosition()
    {
        var request = new LoopbackCaptureRequest(RenderDevice, LoopbackMode.System, "ffplay");

        // A system request ignores the process name, and system loopback reports the render
        // endpoint's own stream position.
        Assert.False(request.IsProcessLoopback);
        Assert.Equal(
            CaptureClock.DevicePosition,
            NAudioCaptureSourceFactory.LoopbackClock(request));
    }

    [Fact]
    public void LoopbackClock_ProcessRequest_IsPlacedByQpc()
    {
        var request = new LoopbackCaptureRequest(RenderDevice, LoopbackMode.Process, "ffplay");

        // Process loopback reports device_position_frames = 0 for every buffer, so device
        // position can never place it; QPC is what the stream actually supplies.
        Assert.True(request.IsProcessLoopback);
        Assert.Equal(CaptureClock.Qpc, NAudioCaptureSourceFactory.LoopbackClock(request));
    }

    [Fact]
    public void LoopbackClock_RejectsANullRequest()
        => Assert.Throws<ArgumentNullException>(() => NAudioCaptureSourceFactory.LoopbackClock(null!));
}