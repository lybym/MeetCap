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

    [Fact]
    public void Validate_InvalidServiceTier_Errors()
    {
        var c = ConfigurationDefaults.Default();
        c.Asr.ServiceTier = "premium";
        var result = ConfigurationValidator.Validate(c);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("asr.service_tier"));
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
        c.Speakers.MatchThreshold = 1.5;
        Assert.False(ConfigurationValidator.Validate(c).IsValid);
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
