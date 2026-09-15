using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using Xunit;

namespace MeetCap.Core.Tests.Capture;

public class CaptureSettingsTests
{
    private static MeetCapConfiguration Configuration()
    {
        var configuration = new MeetCapConfiguration();
        configuration.Capture.ChunkSeconds = 60;
        configuration.Capture.BufferSeconds = 5;
        configuration.Capture.FlushIntervalMs = 1_000;
        configuration.Capture.Offline.MicrophoneDeviceId = "mic-1";
        configuration.Storage.MinimumFreeSpaceGb = 5;
        return configuration;
    }

    [Fact]
    public void FromConfiguration_MapsTheCaptureRelevantSettings()
    {
        var settings = CaptureSettings.FromConfiguration(Configuration(), @"C:\data");

        Assert.Equal(@"C:\data", settings.DataRoot);
        Assert.Equal(60, settings.ChunkSeconds);
        Assert.Equal(5, settings.BufferSeconds);
        Assert.Equal(1_000, settings.FlushIntervalMs);
        Assert.Equal("mic-1", settings.MicrophoneDeviceId);
        Assert.Equal(5L * 1024 * 1024 * 1024, settings.MinimumFreeSpaceBytes);
        Assert.Equal(SchemaVersion.Current, settings.ConfigVersion);
    }

    [Fact]
    public void FromConfiguration_WithAnUnusableChunkLength_FailsWithAConfigurationMessage()
    {
        var configuration = Configuration();
        configuration.Capture.ChunkSeconds = 0;

        var error = Assert.Throws<MeetCapException>(
            () => CaptureSettings.FromConfiguration(configuration, @"C:\data"));

        Assert.Contains("meetcap config validate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyDataRoot()
    {
        Assert.Throws<ArgumentException>(() => new CaptureSettings(" ", 60, 5, 1_000, 5, "default", 1));
    }

    [Fact]
    public void CriticalFreeSpace_HasAHardFloorBelowTheWarningThreshold()
    {
        var settings = new CaptureSettings(@"C:\data", 60, 5, 1_000, 5, "default", 1);

        Assert.True(CaptureSettings.CriticalFreeSpaceBytes < settings.MinimumFreeSpaceBytes);
    }
}
