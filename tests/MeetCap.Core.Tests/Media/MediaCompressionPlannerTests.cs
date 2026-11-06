using MeetCap.Core.Media;
using Xunit;

namespace MeetCap.Core.Tests.Media;

/// <summary>
/// The compression threshold boundary is the acceptance criterion that must survive
/// every refactor, so the planner is a pure function and the boundary is a unit test
/// (issue #53: "300 s exactly = no compression").
/// </summary>
public class MediaCompressionPlannerTests
{
    private const string CodecMp3 = "mp3";
    private const string CodecAac = "aac";
    private const int Bitrate = 128;
    private const int MinSeconds = 300;

    private static MediaInfo Info(long durationMs) => new()
    {
        Path = @"C:\recordings\meeting.wav",
        FormatName = "wav",
        DurationMs = durationMs,
        ByteLength = 32_000 * (durationMs / 1000),
        AudioCodec = "pcm_s16le",
        SampleRateHz = 16000,
        Channels = 1,
        BitDepth = 16,
        BitRate = 256_000,
    };

    [Theory]
    [InlineData(0L)]
    [InlineData(60_000L)]
    [InlineData(299_999L)]
    [InlineData(300_000L)] // exactly at the threshold: never compressed
    public void Plan_AtOrBelowThreshold_IsNeverCompressed(long durationMs)
    {
        // The threshold applies to a complete finalized derivative. At or below 300 s the
        // artifact stays as the lossless WAV, byte-identical to today, even with a codec
        // selected.
        var plan = MediaCompressionPlanner.Plan(Info(durationMs), CodecMp3, Bitrate, MinSeconds);

        Assert.False(plan.Required);
    }

    [Theory]
    [InlineData(300_001L)] // one millisecond over the threshold
    [InlineData(600_000L)]
    [InlineData(9_600_000L)] // 2 h 40 m, the deployment that triggered the issue
    public void Plan_AboveThreshold_RequiresCompression(long durationMs)
    {
        var plan = MediaCompressionPlanner.Plan(Info(durationMs), CodecMp3, Bitrate, MinSeconds);

        Assert.True(plan.Required);
        Assert.Equal(CodecMp3, plan.Codec);
        Assert.Equal(CodecMp3, plan.ProviderFormat);
        Assert.Equal(".mp3", plan.Extension);
        Assert.Equal(Bitrate, plan.TargetBitrateKbps);
    }

    [Fact]
    public void Plan_DisabledCodec_IsNeverRequiredEvenForLongArtifacts()
    {
        // compress = "none" cannot coexist with "over five minutes are always compressed":
        // the threshold is mandatory only when compression is enabled (issue #53 review
        // update). A 2 h 40 m artifact with no codec stays WAV.
        var plan = MediaCompressionPlanner.Plan(Info(9_600_000L), "none", Bitrate, MinSeconds);

        Assert.False(plan.Required);
    }

    [Theory]
    [InlineData(CodecMp3, "mp3", "libmp3lame", "mp3", ".mp3")]
    [InlineData(CodecAac, "adts", "aac", "aac", ".aac")]
    public void Plan_ResolvedCodec_MapsToProviderFormatAndExtension(
        string codec, string targetFormat, string targetAudioCodec, string providerFormat, string extension)
    {
        var plan = MediaCompressionPlanner.Plan(Info(600_000L), codec, Bitrate, MinSeconds);

        Assert.True(plan.Required);
        Assert.Equal(targetFormat, plan.TargetFormat);
        Assert.Equal(targetAudioCodec, plan.TargetAudioCodec);
        Assert.Equal(providerFormat, plan.ProviderFormat);
        Assert.Equal(extension, plan.Extension);
    }

    [Theory]
    [InlineData(300_000L, 300_000L, true)]      // exact match
    [InlineData(300_000L, 300_500L, true)]      // +500 ms, within tolerance
    [InlineData(300_000L, 299_500L, true)]      // -500 ms, within tolerance
    [InlineData(300_000L, 300_501L, false)]     // +501 ms, exceeds tolerance
    [InlineData(300_000L, 299_499L, false)]     // -501 ms, exceeds tolerance
    public void DurationMatches_RespectsTheDocumentedTolerance(
        long sourceMs, long derivativeMs, bool expected)
    {
        // The timeline-correctness guard: the derivative's duration must match the lossless
        // source within the documented tolerance before it can be published as the ASR
        // payload (docs/ASR_STRATEGY.md section 12.2).
        Assert.Equal(expected, MediaCompressionPlanner.DurationMatches(sourceMs, derivativeMs));
    }

    [Fact]
    public void DurationTolerance_IsSmallEnoughToCatchGrossErrors()
    {
        // The tolerance must catch a wrong file or a truncation (a fraction of the duration)
        // without rejecting a legitimate frame of codec delay/padding.
        Assert.True(MediaCompressionPlanner.DurationToleranceMs <= 1000);
        Assert.True(MediaCompressionPlanner.DurationToleranceMs > 0);
    }
}
