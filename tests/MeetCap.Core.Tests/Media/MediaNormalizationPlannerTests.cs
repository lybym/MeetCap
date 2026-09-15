using MeetCap.Core.Media;
using Xunit;

namespace MeetCap.Core.Tests.Media;

/// <summary>
/// "Normalization runs only when required" (docs/ARCHITECTURE.md section 13) is only
/// meaningful if the decision itself is a pure, testable function.
/// </summary>
public class MediaNormalizationPlannerTests
{
    private static MediaInfo Info(
        string format = "wav",
        string? codec = "pcm_s16le",
        int sampleRate = 16000,
        int channels = 1,
        int? bitDepth = 16) => new()
    {
        Path = @"C:\recordings\meeting.wav",
        FormatName = format,
        DurationMs = 1000,
        ByteLength = 32000,
        AudioCodec = codec,
        SampleRateHz = sampleRate,
        Channels = channels,
        BitDepth = bitDepth,
        BitRate = 256000,
    };

    [Fact]
    public void AlreadyNormalizedSource_IsNotNormalizedAgain()
    {
        var plan = MediaNormalizationPlanner.Plan(Info());

        Assert.False(plan.Required);
        Assert.Contains("already", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "aac", 44100, 2)]
    [InlineData("wav", "pcm_s24le", 16000, 1)]
    [InlineData("wav", "pcm_s16le", 48000, 1)]
    [InlineData("wav", "pcm_s16le", 16000, 2)]
    [InlineData("mp3", "mp3", 16000, 1)]
    public void NonTargetShape_RequiresNormalization(
        string format,
        string codec,
        int sampleRate,
        int channels)
    {
        var plan = MediaNormalizationPlanner.Plan(Info(format, codec, sampleRate, channels));

        Assert.True(plan.Required);
        Assert.Equal("wav", plan.TargetFormat);
        Assert.Equal("pcm_s16le", plan.TargetAudioCodec);
        Assert.Equal(16000, plan.TargetSampleRateHz);
        Assert.Equal(1, plan.TargetChannels);
        Assert.Equal("wav", plan.TargetProviderFormat);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void UnknownBitDepth_DoesNotForceNormalization()
    {
        // Some containers do not report a bit depth; that alone is not a reason to
        // re-encode the user's audio.
        Assert.False(MediaNormalizationPlanner.Plan(Info(bitDepth: null)).Required);
    }

    [Fact]
    public void SourceWithoutAudio_IsRejectedWithAnActionableMessage()
    {
        var ex = Assert.Throws<MediaProbeException>(
            () => MediaNormalizationPlanner.Plan(Info(codec: null, sampleRate: 0, channels: 0)));

        Assert.Contains("no decodable audio stream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompoundContainerNames_MustAllBeAsrSafe()
    {
        // FFprobe reports "mov,mp4,m4a,..." for m4a files: it is not an ASR-safe container.
        Assert.True(MediaNormalizationPlanner.Plan(Info(format: "mov,mp4")).Required);
        Assert.False(MediaNormalizationPlanner.Plan(Info(format: "wav")).Required);
    }
}
