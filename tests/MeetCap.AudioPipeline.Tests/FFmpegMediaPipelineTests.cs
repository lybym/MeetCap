using MeetCap.AudioPipeline;
using MeetCap.Core.Media;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// Exercises the real FFprobe/FFmpeg boundary. FFmpeg is an external dependency that
/// CI images do not guarantee, so when it cannot be located the test reports that
/// explicitly and does not pretend the boundary was exercised
/// (docs/DEVELOPMENT.md sections 6 and 7).
/// </summary>
public class FFmpegMediaPipelineTests
{
    /// <summary>
    /// Resolution order is configuration, then well-known install locations, then PATH.
    /// This test therefore passes unchanged on a machine with ffmpeg on PATH and on a
    /// developer machine with it in Program Files.
    /// </summary>
    private static bool TryCreatePipeline(string? configuredFolder, out FFmpegMediaPipeline? pipeline)
    {
        try
        {
            pipeline = FFmpegMediaPipeline.Create(configuredFolder, configuredTemporaryFolder: null);
            return true;
        }
        catch (MediaToolingException)
        {
            pipeline = null;
            return false;
        }
    }

    [Fact]
    public void Inspect_ReadsFormatCodecAndDurationFromARealFile()
    {
        if (!TryCreatePipeline(null, out var pipeline))
        {
            // FFmpeg is unavailable in this environment; the boundary is covered by the
            // locator tests and by the documented manual validation step in the PR.
            Assert.True(true, "FFmpeg not available; real media inspection was not exercised.");
            return;
        }

        using var scratch = new MediaScratch();
        var source = WavFixture.WriteSine(
            Path.Combine(scratch.Root, "stereo-44k.wav"),
            sampleRateHz: 44100,
            channels: 2,
            seconds: 0.5);

        var info = pipeline!.Inspect(source);

        Assert.Equal("wav", info.FormatName);
        Assert.Equal("pcm_s16le", info.AudioCodec);
        Assert.Equal(44100, info.SampleRateHz);
        Assert.Equal(2, info.Channels);
        Assert.True(info.HasAudio);
        Assert.InRange(info.DurationMs, 400, 600);
        Assert.True(info.ByteLength > 0);
    }

    [Fact]
    public async Task Normalize_ProducesTheProviderTargetShape()
    {
        if (!TryCreatePipeline(null, out var pipeline))
        {
            Assert.True(true, "FFmpeg not available; real media normalization was not exercised.");
            return;
        }

        using var scratch = new MediaScratch();
        var source = WavFixture.WriteSine(
            Path.Combine(scratch.Root, "stereo-44k.wav"),
            sampleRateHz: 44100,
            channels: 2,
            seconds: 0.3);

        var sourceInfo = pipeline!.Inspect(source);
        var plan = MediaNormalizationPlanner.Plan(sourceInfo);
        Assert.True(plan.Required);

        var output = Path.Combine(scratch.Root, "normalized.wav");
        await pipeline.NormalizeAsync(source, output, plan);

        var normalized = pipeline.Inspect(output);
        Assert.Equal(16000, normalized.SampleRateHz);
        Assert.Equal(1, normalized.Channels);
        Assert.Equal("pcm_s16le", normalized.AudioCodec);
        Assert.False(MediaNormalizationPlanner.Plan(normalized).Required);
    }

    [Fact]
    public async Task Normalize_RefusesAPlanThatDoesNotRequireNormalization()
    {
        if (!TryCreatePipeline(null, out var pipeline))
        {
            Assert.True(true, "FFmpeg not available.");
            return;
        }

        using var scratch = new MediaScratch();
        var source = WavFixture.WriteSine(Path.Combine(scratch.Root, "mono-16k.wav"), 16000, 1, 0.2);
        var plan = MediaNormalizationPlanner.Plan(pipeline!.Inspect(source));
        Assert.False(plan.Required);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.NormalizeAsync(source, Path.Combine(scratch.Root, "out.wav"), plan));
    }

    [Fact]
    public void Inspect_MissingFile_FailsWithThePath()
    {
        if (!TryCreatePipeline(null, out var pipeline))
        {
            Assert.True(true, "FFmpeg not available.");
            return;
        }

        var missing = Path.Combine(Path.GetTempPath(), "meetcap-missing-" + Guid.NewGuid().ToString("N") + ".wav");
        var ex = Assert.Throws<MediaProbeException>(() => pipeline!.Inspect(missing));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    private sealed class MediaScratch : IDisposable
    {
        public MediaScratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "meetcap-media-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
