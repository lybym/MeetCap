using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Core.Tests.Configuration;

/// <summary>
/// The M3 settings (media toolchain location and file-ASR provider behaviour) are part
/// of the configuration contract: each key must be declared, defaulted, and validated
/// exactly as docs/DEVELOPMENT.md section 9 requires.
/// </summary>
public class M3ConfigurationValidationTests
{
    [Fact]
    public void DefaultConfiguration_IsValidAndCarriesTheDocumentedM3Defaults()
    {
        var config = ConfigurationDefaults.Default();

        var result = ConfigurationValidator.Validate(config);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));

        Assert.Equal(string.Empty, config.Media.FfmpegBinaryFolder);
        Assert.Equal(0.8, config.Asr.Volcengine.CostPerHourCny);
        Assert.Equal(5, config.Asr.Volcengine.PollIntervalSeconds);
        Assert.Equal(900, config.Asr.Volcengine.PollTimeoutSeconds);
        Assert.Equal(30, config.Asr.Volcengine.HttpTimeoutSeconds);
    }

    [Theory]
    [InlineData("media.ffmpeg_binary_folder")]
    [InlineData("media.ffmpeg_temporary_folder")]
    [InlineData("asr.volcengine.cost_per_hour_cny")]
    [InlineData("asr.volcengine.poll_interval_seconds")]
    [InlineData("asr.volcengine.poll_timeout_seconds")]
    [InlineData("asr.volcengine.http_timeout_seconds")]
    public void NewKeys_ArePartOfTheSchema(string key) =>
        Assert.True(ConfigSchema.IsValidLeafKey(key), $"'{key}' must be a schema key.");

    [Theory]
    [InlineData("C:\\ffmpeg\\bin")]
    [InlineData("env:MEETCAP_FFMPEG_BIN")]
    [InlineData("")]
    public void MediaFolder_AcceptsAbsolutePathsEnvReferencesOrEmpty(string value)
    {
        var config = ConfigurationDefaults.Default();
        config.Media.FfmpegBinaryFolder = value;

        Assert.True(ConfigurationValidator.Validate(config).IsValid);
    }

    [Fact]
    public void MediaFolder_RejectsRelativePaths()
    {
        var config = ConfigurationDefaults.Default();
        config.Media.FfmpegBinaryFolder = "ffmpeg\\bin";

        var result = ConfigurationValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("media.ffmpeg_binary_folder", StringComparison.Ordinal)
                && error.Contains("absolute", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ZeroPollInterval_IsRejected(int seconds)
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.PollIntervalSeconds = seconds;

        Assert.False(ConfigurationValidator.Validate(config).IsValid);
    }

    [Fact]
    public void NegativeCostRate_IsRejected()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.CostPerHourCny = -1;

        var result = ConfigurationValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("cost_per_hour_cny", StringComparison.Ordinal));
    }
}
