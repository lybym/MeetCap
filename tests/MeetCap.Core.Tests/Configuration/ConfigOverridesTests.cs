using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Core.Tests.Configuration;

public class ConfigOverridesTests
{
    [Fact]
    public void Apply_OverridesChunkSecondsInMemory()
    {
        var c = ConfigurationDefaults.Default(); // 60
        ConfigOverrides.Apply(c, new Dictionary<string, string> { ["capture.chunk_seconds"] = "30" });
        Assert.Equal(30, c.Capture.ChunkSeconds);
    }

    [Fact]
    public void Apply_OverridesServiceTier()
    {
        var c = ConfigurationDefaults.Default();
        ConfigOverrides.Apply(c, new Dictionary<string, string> { ["asr.service_tier"] = "idle" });
        Assert.Equal("idle", c.Asr.ServiceTier);
    }

    [Fact]
    public void Apply_OverridesDefaultMode()
    {
        var c = ConfigurationDefaults.Default();
        ConfigOverrides.Apply(c, new Dictionary<string, string> { ["capture.default_mode"] = "online" });
        Assert.Equal("online", c.Capture.DefaultMode);
    }

    [Fact]
    public void Apply_OverridesBoolAndLogLevel()
    {
        var c = ConfigurationDefaults.Default();
        ConfigOverrides.Apply(c, new Dictionary<string, string>
        {
            ["asr.enabled"] = "false",
            ["logging.level"] = "Debug",
        });
        Assert.False(c.Asr.Enabled);
        Assert.Equal("Debug", c.Logging.Level);
    }

    [Fact]
    public void Apply_BadInteger_ThrowsFormatException()
    {
        var c = ConfigurationDefaults.Default();
        Assert.Throws<FormatException>(() =>
            ConfigOverrides.Apply(c, new Dictionary<string, string> { ["capture.chunk_seconds"] = "abc" }));
    }

    [Fact]
    public void Apply_UnknownKey_IsIgnoredNoThrow()
    {
        var c = ConfigurationDefaults.Default();
        ConfigOverrides.Apply(c, new Dictionary<string, string> { ["nope.nope"] = "x" });
        Assert.Equal(60, c.Capture.ChunkSeconds);
    }
}
