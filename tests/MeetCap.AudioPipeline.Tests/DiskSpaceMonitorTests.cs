using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public class DiskSpaceMonitorTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void Inspect_ClassifiesFreeSpaceAgainstTheConfiguredThresholds()
    {
        var probe = new FakeDiskSpaceProbe();
        var monitor = new DiskSpaceMonitor(probe, minimumFreeBytes: 5 * Gigabyte);

        probe.FreeBytes = 50 * Gigabyte;
        Assert.Equal(DiskSpaceLevel.Ok, monitor.Inspect("root").Level);

        probe.FreeBytes = 2 * Gigabyte;
        var low = monitor.Inspect("root");
        Assert.Equal(DiskSpaceLevel.Low, low.Level);
        Assert.Equal(2 * Gigabyte, low.FreeBytes);

        probe.FreeBytes = 1_000;
        Assert.Equal(DiskSpaceLevel.Critical, monitor.Inspect("root").Level);
    }

    [Fact]
    public void EnsureSufficientAtStart_ThrowsWithAnActionableMessage()
    {
        var probe = new FakeDiskSpaceProbe { FreeBytes = 1 };
        var monitor = new DiskSpaceMonitor(probe, minimumFreeBytes: 5 * Gigabyte);

        var error = Assert.Throws<InsufficientDiskSpaceException>(() => monitor.EnsureSufficientAtStart(@"C:\data"));

        Assert.Contains("storage.minimum_free_space_gb", error.Message, StringComparison.Ordinal);
        Assert.Contains("storage.data_root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSufficientAtStart_AllowsAWarningLevelToStart()
    {
        var probe = new FakeDiskSpaceProbe { FreeBytes = 6 * Gigabyte };
        var monitor = new DiskSpaceMonitor(probe, minimumFreeBytes: 5 * Gigabyte);

        monitor.EnsureSufficientAtStart(@"C:\data");

        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public void Constants_KeepTheHardFloorBelowTheDefaultWarningThreshold()
    {
        Assert.True(CaptureSettings.CriticalFreeSpaceBytes < 5 * Gigabyte);
    }

    [Fact]
    public void Format_ProducesReadableSizes()
    {
        Assert.Equal("2.00 GB", DiskSpaceMonitor.Format(2 * Gigabyte));
        Assert.Equal("512.0 MB", DiskSpaceMonitor.Format(512L * 1024 * 1024));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveThresholds()
    {
        var probe = new FakeDiskSpaceProbe();

        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskSpaceMonitor(probe, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskSpaceMonitor(probe, 1, 0));
    }

    [Fact]
    public void DriveInfoProbe_ReportsFreeSpaceForAnExistingAncestorOfANewRoot()
    {
        var probe = new DriveInfoDiskSpaceProbe();
        var nested = Path.Combine(Path.GetTempPath(), "meetcap-not-created", Guid.NewGuid().ToString("N"));

        var free = probe.GetAvailableFreeBytes(nested);

        Assert.True(free > 0);
    }

    [Fact]
    public void DriveInfoProbe_RejectsAnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new DriveInfoDiskSpaceProbe().GetAvailableFreeBytes(" "));
    }
}
