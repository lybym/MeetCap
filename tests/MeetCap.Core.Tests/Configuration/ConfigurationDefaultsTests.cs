using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Core.Tests.Configuration;

public class ConfigurationDefaultsTests
{
    [Fact]
    public void Default_HasCurrentSchemaVersion()
        => Assert.Equal(1, ConfigurationDefaults.Default().ConfigVersion);

    [Fact]
    public void Default_OfflineMode()
        => Assert.Equal("offline", ConfigurationDefaults.Default().Capture.DefaultMode);

    [Fact]
    public void Default_DurabilityValues()
    {
        var c = ConfigurationDefaults.Default();
        Assert.Equal(60, c.Capture.ChunkSeconds);
        Assert.Equal(5, c.Capture.BufferSeconds);
        Assert.Equal(1000, c.Capture.FlushIntervalMs);
    }

    [Fact]
    public void Default_StorageDataRootPlaceholder()
        => Assert.Equal("%LOCALAPPDATA%\\MeetCap", ConfigurationDefaults.Default().Storage.DataRoot);

    [Fact]
    public void Default_MinimumFreeSpaceGb()
        => Assert.Equal(5.0, ConfigurationDefaults.Default().Storage.MinimumFreeSpaceGb);

    [Fact]
    public void Default_AsrServiceTierStandard()
        => Assert.Equal("standard", ConfigurationDefaults.Default().Asr.ServiceTier);

    [Fact]
    public void Default_VolcengineCredentialEnvReference()
        => Assert.Equal("env:MEETCAP_VOLCENGINE_ACCESS_TOKEN",
            ConfigurationDefaults.Default().Asr.Volcengine.Credential);

    [Fact]
    public void Default_AllSectionsInitialized()
    {
        var c = ConfigurationDefaults.Default();
        Assert.NotNull(c.App);
        Assert.NotNull(c.Capture);
        Assert.NotNull(c.Capture.Offline);
        Assert.NotNull(c.Capture.Online);
        Assert.NotNull(c.Storage);
        Assert.NotNull(c.Asr);
        Assert.NotNull(c.Asr.Volcengine);
        Assert.NotNull(c.Speakers);
        Assert.NotNull(c.Speakers.Identity);
        Assert.NotNull(c.Speakers.SherpaOnnx);
        Assert.NotNull(c.Transcript);
        Assert.NotNull(c.Logging);
        Assert.NotNull(c.Retention);
    }

    [Fact]
    public void Default_SystemLoopbackIsTheBaseline()
    {
        var online = ConfigurationDefaults.Default().Capture.Online;
        Assert.Equal("system", online.LoopbackMode);
        Assert.Equal(string.Empty, online.ProcessName);
    }

    [Fact]
    public void Default_SpeakerIdentityContract()
    {
        var c = ConfigurationDefaults.Default();

        Assert.True(c.Asr.Volcengine.RequestSpeakerInfo);
        Assert.True(c.Transcript.IncludeSpeakerLabels);
        Assert.Equal("sherpa_onnx_3dspeaker", c.Speakers.Identity.Provider);
        Assert.Equal(0.82, c.Speakers.Identity.MatchThreshold);
        Assert.Equal(0.08, c.Speakers.Identity.MatchMargin);
        Assert.Equal(5, c.Speakers.Identity.SampleMinSeconds);
        Assert.Equal(15, c.Speakers.Identity.SampleMaxSeconds);
        Assert.Equal(
            "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            c.Speakers.SherpaOnnx.Model);
        Assert.Equal(string.Empty, c.Speakers.SherpaOnnx.ModelPath);
    }
}
