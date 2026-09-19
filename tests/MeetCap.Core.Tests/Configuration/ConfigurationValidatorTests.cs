using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Core.Tests.Configuration;

public class ConfigurationValidatorTests
{
    [Fact]
    public void Validate_DefaultConfig_IsValid()
    {
        var result = ConfigurationValidator.Validate(ConfigurationDefaults.Default());
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidDefaultMode_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Capture.DefaultMode = "hybrid";
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("capture.default_mode"));
    }

    [Theory]
    [InlineData("asr.service_tier")]
    [InlineData("asr.volcengine.app_id")]
    [InlineData("asr.volcengine.credential")]
    [InlineData("asr.volcengine.resource_id")]
    public void Validate_LegacyProviderKeys_AreBlockedWithAMigrationMessage(string legacyKey)
    {
        // Issue #26 removed the tier selector, the legacy AppID/Access Token pair, and the
        // configurable resource id. A v1 configuration that still contains one must not pass
        // validation and quietly fall back to the new defaults.
        var result = ConfigurationValidator.Validate(ConfigurationDefaults.Default(), new[] { legacyKey });

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.StartsWith(
                $"Legacy configuration key '{legacyKey}' is not supported",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_LegacyCredentialKey_TellsTheUserToRenameItToApiKey()
    {
        var result = ConfigurationValidator.Validate(
            ConfigurationDefaults.Default(),
            new[] { "asr.volcengine.credential" });

        var error = Assert.Single(result.Errors);
        Assert.Contains("api_key", error, StringComparison.Ordinal);
        Assert.Contains("MEETCAP_VOLCENGINE_API_KEY", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_LegacyAppIdKey_ExplainsThatTheNewConsoleUsesAnApiKey()
    {
        var result = ConfigurationValidator.Validate(
            ConfigurationDefaults.Default(),
            new[] { "asr.volcengine.app_id" });

        var error = Assert.Single(result.Errors);
        Assert.Contains("api_key", error, StringComparison.Ordinal);
        Assert.Contains("X-Api-App-Key", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_LegacyResourceIdKey_NamesTheFixedSeedAsrResourceId()
    {
        var result = ConfigurationValidator.Validate(
            ConfigurationDefaults.Default(),
            new[] { "asr.volcengine.resource_id" });

        var error = Assert.Single(result.Errors);
        Assert.Contains("volc.seedasr.auc", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_LegacyServiceTierKey_ExplainsThatThereIsNoTierToSelect()
    {
        var result = ConfigurationValidator.Validate(
            ConfigurationDefaults.Default(),
            new[] { "asr.service_tier" });

        var error = Assert.Single(result.Errors);
        Assert.Contains("Seed-ASR 2.0 Standard HTTP", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_InvalidLoopbackMode_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Capture.Online.LoopbackMode = "magic";
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("capture.online.loopback_mode"));
    }

    [Fact]
    public void Validate_NegativeDeviceRecoveryWindow_Errors()
    {
        // A negative window has no meaning: it would give a track a retry budget smaller than no
        // retries at all (issue #34, docs/CONFIGURATION.md section 6).
        var c = ConfigurationDefaults.Default();
        c.Capture.DeviceRecoverySeconds = -1;

        var result = ConfigurationValidator.Validate(c);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("capture.device_recovery_seconds"));
    }

    [Fact]
    public void Validate_ZeroDeviceRecoveryWindow_IsAcceptedAsNoRecovery()
    {
        // Zero is a deliberate choice ("do not attempt device recovery at all"), not a typo, so
        // it must pass validation (docs/CONFIGURATION.md section 6).
        var c = ConfigurationDefaults.Default();
        c.Capture.DeviceRecoverySeconds = 0;

        var result = ConfigurationValidator.Validate(c);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_InvalidLogLevel_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Logging.Level = "Verbose";
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("logging.level"));
    }

    [Fact]
    public void Validate_NonPositiveChunkSeconds_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Capture.ChunkSeconds = 0;
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("capture.chunk_seconds"));
    }

    [Fact]
    public void Validate_NegativeBufferSeconds_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Capture.BufferSeconds = -1;
        Assert.False(ConfigurationValidator.Validate(c).IsValid);
    }

    [Fact]
    public void Validate_ThresholdOutOfRange_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Speakers.Identity.MatchThreshold = 1.5;
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("speakers.identity.match_threshold"));
    }

    [Fact]
    public void Validate_MarginOutOfRange_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Speakers.Identity.MatchMargin = -0.1;
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("speakers.identity.match_margin"));
    }

    [Fact]
    public void Validate_UnknownIdentityProvider_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Speakers.Identity.Provider = "local";
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("speakers.identity.provider"));
    }

    [Fact]
    public void Validate_InvertedSampleWindow_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Speakers.Identity.SampleMinSeconds = 20;
        c.Speakers.Identity.SampleMaxSeconds = 15;
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("speakers.identity.sample_max_seconds"));
    }

    [Fact]
    public void Validate_NonPositiveSampleMinSeconds_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Speakers.Identity.SampleMinSeconds = 0;
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("speakers.identity.sample_min_seconds"));
    }

    [Fact]
    public void Validate_ProcessLoopbackWithoutProcessName_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Capture.Online.LoopbackMode = "process";
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("capture.online.process_name"));
    }

    [Fact]
    public void Validate_ProcessLoopbackWithProcessName_IsValid()
    {
        var c = ConfigurationDefaults.Default();
        c.Capture.Online.LoopbackMode = "process";
        c.Capture.Online.ProcessName = "Teams";
        Assert.True(ConfigurationValidator.Validate(c).IsValid);
    }

    [Fact]
    public void Validate_SystemLoopbackNeedsNoProcessName()
    {
        var c = ConfigurationDefaults.Default();
        Assert.Equal("system", c.Capture.Online.LoopbackMode);
        Assert.Equal(string.Empty, c.Capture.Online.ProcessName);
        Assert.True(ConfigurationValidator.Validate(c).IsValid);
    }

    [Fact]
    public void Validate_SpeakersEnabledWithoutProviderSpeakerInfo_Warns()
    {
        var c = ConfigurationDefaults.Default();
        c.Asr.Volcengine.RequestSpeakerInfo = false;
        var result = ConfigurationValidator.Validate(c);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("request_speaker_info"));
    }

    [Fact]
    public void Validate_SpeakersDisabledWithoutProviderSpeakerInfo_DoesNotWarn()
    {
        var c = ConfigurationDefaults.Default();
        c.Speakers.Enabled = false;
        c.Asr.Volcengine.RequestSpeakerInfo = false;
        var result = ConfigurationValidator.Validate(c);
        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("request_speaker_info"));
    }

    [Fact]
    public void Validate_WrongSchemaVersion_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.ConfigVersion = 2;
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("config_version"));
    }

    [Fact]
    public void Validate_UnknownKeys_ProduceWarningsButStayValid()
    {
        var result = ConfigurationValidator.Validate(
            ConfigurationDefaults.Default(),
            new[] { "capture.offline.bogus_key", "unknownsection.whoops" });
        Assert.True(result.IsValid);
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Warnings, w => w.Contains("capture.offline.bogus_key"));
    }

    [Fact]
    public void Validate_StreamingEnabledWithFileStrategy_Warns()
    {
        var c = ConfigurationDefaults.Default();
        c.Asr.StreamingEnabled = true;
        c.Asr.Strategy = "file";
        var result = ConfigurationValidator.Validate(c);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("streaming"));
    }

    [Fact]
    public void Validate_EmptyDataRoot_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Storage.DataRoot = "   ";
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("storage.data_root"));
    }

    [Fact]
    public void Validate_NegativeRetryMaxAttempts_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Asr.RetryMaxAttempts = -1;
        Assert.False(ConfigurationValidator.Validate(c).IsValid);
    }

    [Fact]
    public void Validate_ZeroFileBatchSeconds_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Asr.FileBatchSeconds = 0;
        Assert.False(ConfigurationValidator.Validate(c).IsValid);
    }
}
