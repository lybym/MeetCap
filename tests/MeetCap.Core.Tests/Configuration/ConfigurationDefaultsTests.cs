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
    public void Default_DeviceRecoveryWindowCoversABluetoothReconnect()
    {
        // Issue #34: the window has to be long enough that a headset which is switched off and
        // on again reappears inside it. The pre-fix behaviour was three one-second retries, so a
        // default that does not clearly exceed that would leave the reported defect in place.
        var c = ConfigurationDefaults.Default();
        Assert.Equal(20, c.Capture.DeviceRecoverySeconds);
        Assert.True(
            c.Capture.DeviceRecoverySeconds > 3,
            "the default recovery window must exceed the three retries that produced issue #34");
    }

    [Fact]
    public void Default_StorageDataRootPlaceholder()
        => Assert.Equal("%LOCALAPPDATA%\\MeetCap", ConfigurationDefaults.Default().Storage.DataRoot);

    [Fact]
    public void Default_MinimumFreeSpaceGb()
        => Assert.Equal(5.0, ConfigurationDefaults.Default().Storage.MinimumFreeSpaceGb);

    [Fact]
    public void Default_VolcengineApiKeyEnvReference()
        => Assert.Equal("env:MEETCAP_VOLCENGINE_API_KEY",
            ConfigurationDefaults.Default().Asr.Volcengine.ApiKey);

    [Fact]
    public void Default_HasNoServiceTierSelector()
    {
        // Issue #26 removed the tier as a product setting: there is exactly one file-ASR
        // service profile, so a default tier would be a routing surface with one value.
        var asrType = typeof(AsrSection);
        Assert.Null(asrType.GetProperty("ServiceTier"));
        Assert.Null(typeof(VolcengineSection).GetProperty("AppId"));
        Assert.Null(typeof(VolcengineSection).GetProperty("Credential"));
        Assert.Null(typeof(VolcengineSection).GetProperty("ResourceId"));
    }

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
        // Issue #49: the shipped 0.82 / 0.08 defaults are deliberately retained as an
        // uncalibrated placeholder, NOT lowered to the measured 0.50 / 0.06 candidate.
        // The 2026-11-05 field measurement (docs/ASR_STRATEGY.md section 10) showed
        // same-speaker same-recording cosine is only ~0.656, so 0.82 over-filters and
        // voiceprint attribution resolves nothing out of the box. The issue #49 review
        // update (2026-11-06) gates the change behind issue #50 (candidate-score
        // diagnostics) and held-out different-day calibration across multiple known
        // speakers/impostors. Until that lands, 0.50/0.06 is an opt-in experiment profile
        // only (docs/CONFIGURATION.md sections 9.2.1-9.2.2). These assertions lock the
        // retained default so an accidental change is caught; the measured separation is
        // documented separately in SpeakerThresholdCalibrationRegressionTests.
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
