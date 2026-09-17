using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
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

    [Fact]
    public void FromConfiguration_OfflineMode_CarriesNoOnlineSettings()
    {
        var settings = CaptureSettings.FromConfiguration(Configuration(), @"C:\data", "offline");

        Assert.Equal(SessionModes.Offline, settings.Mode);
        Assert.False(settings.IsOnline);
        Assert.Null(settings.Online);
    }

    [Fact]
    public void FromConfiguration_OnlineMode_CarriesTheLoopbackConfiguration()
    {
        var configuration = Configuration();
        configuration.Capture.Online.MicrophoneDeviceId = "mic-online";
        configuration.Capture.Online.LoopbackMode = "process";
        configuration.Capture.Online.RenderDeviceId = "render-hdmi";
        configuration.Capture.Online.ProcessName = "WeMeet";

        var settings = CaptureSettings.FromConfiguration(configuration, @"C:\data", "online");

        Assert.Equal(SessionModes.Online, settings.Mode);
        Assert.True(settings.IsOnline);
        Assert.NotNull(settings.Online);
        // The online microphone setting is the one an online session uses.
        Assert.Equal("mic-online", settings.MicrophoneDeviceId);
        Assert.Equal("mic-online", settings.Online!.MicrophoneDeviceId);
        Assert.Equal("process", settings.Online.LoopbackMode);
        Assert.Equal("render-hdmi", settings.Online.RenderDeviceId);
        Assert.Equal("WeMeet", settings.Online.ProcessName);
    }

    [Fact]
    public void FromConfiguration_WithoutAnExplicitMode_UsesTheConfiguredDefault()
    {
        var configuration = Configuration();
        configuration.Capture.DefaultMode = "online";

        var settings = CaptureSettings.FromConfiguration(configuration, @"C:\data");

        Assert.Equal(SessionModes.Online, settings.Mode);
        Assert.True(settings.IsOnline);
    }

    [Fact]
    public void Constructor_RejectsOnlineSettingsForAnOfflineMode()
    {
        var online = new OnlineCaptureSettings("default", "system", "default", string.Empty);

        Assert.Throws<ArgumentException>(
            () => new CaptureSettings(@"C:\data", 60, 5, 1_000, 5, "default", 1, SessionModes.Offline, online));
    }

    [Fact]
    public void Constructor_RejectsAnUnknownMode()
    {
        Assert.Throws<ArgumentException>(
            () => new CaptureSettings(@"C:\data", 60, 5, 1_000, 5, "default", 1, "hybrid", null));
    }
}
