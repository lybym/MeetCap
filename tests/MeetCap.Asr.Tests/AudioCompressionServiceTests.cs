using MeetCap.Core.Configuration;
using MeetCap.Core.Media;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.Asr.Tests;

/// <summary>
/// Contract tests for the post-finalize archive derivative (issue #53 Part A): the
/// threshold, duration-equality verification, no-loss fallback, original lifecycle, and
/// session events. The FFmpeg invocation itself is covered by MeetCap.AudioPipeline.Tests;
/// these tests assert the policy and lifecycle around it.
/// </summary>
public class AudioCompressionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "meetcap-compress-" + Guid.NewGuid().ToString("N"));

    public AudioCompressionServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static MediaInfo MakeInfo(string path, long durationMs, string format, string codec) => new()
    {
        Path = path,
        FormatName = format,
        DurationMs = durationMs,
        ByteLength = new FileInfo(path).Length,
        AudioCodec = codec,
        SampleRateHz = 16000,
        Channels = 1,
        BitDepth = 16,
        BitRate = 128_000,
    };

    private (AudioCompressionService Service, FakeMedia Media, CapturingWriter Writer, string LosslessPath, SessionArtifactPaths Paths)
        Setup(long sourceDurationMs = 600_000, bool keepOriginal = true)
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        var paths = new SessionArtifactPaths(dataRoot, "ses_test");
        Directory.CreateDirectory(paths.SessionDirectory);

        var losslessPath = Path.Combine(paths.SessionDirectory, "normalized.wav");
        File.WriteAllBytes(losslessPath, new byte[1024]);

        var media = new FakeMedia { SourceDurationMs = sourceDurationMs };
        var writer = new CapturingWriter();
        var service = new AudioCompressionService(media, writer);
        return (service, media, writer, losslessPath, paths);
    }

    private static RetentionSection Retention(string codec = "mp3", bool keepOriginal = true, bool asrTransport = false) => new()
    {
        Compress = codec,
        CompressBitrateKbps = 128,
        CompressMinSeconds = 300,
        CompressKeepOriginal = keepOriginal,
        CompressAsrTransport = asrTransport,
    };

    [Fact]
    public async Task BelowThreshold_ProducesNoDerivative()
    {
        // 300 s exactly is at the threshold and is never compressed.
        var (service, media, writer, lossless, paths) = Setup(sourceDurationMs: 300_000);
        var losslessMedia = MakeInfo(lossless, 300_000, "wav", "pcm_s16le");

        var result = await service.CompressAsync(
            lossless, losslessMedia, Retention(), paths, paths.SessionDirectory, "compressed",
            canDeleteLossless: true, atMs: 0);

        Assert.False(result.Compressed);
        Assert.Null(result.Artifact);
        Assert.Equal(0, media.CompressCalls);
        Assert.Empty(writer.Events);
        // The lossless WAV is untouched.
        Assert.True(File.Exists(lossless));
    }

    [Fact]
    public async Task AboveThreshold_ProducesDerivativeWithVerifiedDurationAndArtifact()
    {
        var (service, media, writer, lossless, paths) = Setup(sourceDurationMs: 600_000);
        var losslessMedia = MakeInfo(lossless, 600_000, "wav", "pcm_s16le");

        var result = await service.CompressAsync(
            lossless, losslessMedia, Retention(), paths, paths.SessionDirectory, "compressed",
            canDeleteLossless: true, atMs: 0);

        Assert.True(result.Compressed);
        Assert.NotNull(result.Artifact);
        Assert.Equal(SourceArtifact.Roles.Compressed, result.Artifact!.Role);
        Assert.Equal(".mp3", Path.GetExtension(result.DerivativePath));
        Assert.False(string.IsNullOrEmpty(result.Artifact.Sha256));
        // Duration was verified equal within tolerance (the fake returns an exact match).
        Assert.True(MediaCompressionPlanner.DurationMatches(
            losslessMedia.DurationMs, result.DerivativeMedia!.DurationMs));
        Assert.Contains(writer.Events, e => e.Name == SessionEvents.AudioCompressed);
        // keep_original = true: the lossless WAV is kept.
        Assert.True(File.Exists(lossless));
        Assert.False(result.OriginalDeleted);
    }

    [Fact]
    public async Task DurationMismatch_FallsBackKeepsOriginalAndSurfacesEvent()
    {
        var (service, media, writer, lossless, paths) = Setup(sourceDurationMs: 600_000);
        media.DerivativeDurationOffsetMs = 2_000; // 2 s off, far outside the 500 ms tolerance
        var losslessMedia = MakeInfo(lossless, 600_000, "wav", "pcm_s16le");

        var result = await service.CompressAsync(
            lossless, losslessMedia, Retention(), paths, paths.SessionDirectory, "compressed",
            canDeleteLossless: true, atMs: 0);

        Assert.False(result.Compressed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains(writer.Events, e => e.Name == SessionEvents.AudioCompressFailed);
        // No-loss fallback: the lossless WAV stays the upload payload and is kept.
        Assert.True(File.Exists(lossless));
        // The partial derivative is cleaned up.
        Assert.False(File.Exists(Path.Combine(paths.SessionDirectory, "compressed.mp3")));
    }

    [Fact]
    public async Task EncodeFailure_FallsBackKeepsOriginalAndSurfacesEvent()
    {
        var (service, media, writer, lossless, paths) = Setup(sourceDurationMs: 600_000);
        media.FailCompress = true;
        var losslessMedia = MakeInfo(lossless, 600_000, "wav", "pcm_s16le");

        var result = await service.CompressAsync(
            lossless, losslessMedia, Retention(), paths, paths.SessionDirectory, "compressed",
            canDeleteLossless: true, atMs: 0);

        Assert.False(result.Compressed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains(writer.Events, e => e.Name == SessionEvents.AudioCompressFailed);
        Assert.True(File.Exists(lossless));
    }

    [Fact]
    public async Task KeepOriginalFalse_DeletesLosslessOnlyAfterVerifiedEncode()
    {
        var (service, media, writer, lossless, paths) = Setup(sourceDurationMs: 600_000);
        var losslessMedia = MakeInfo(lossless, 600_000, "wav", "pcm_s16le");

        var result = await service.CompressAsync(
            lossless, losslessMedia, Retention(keepOriginal: false), paths, paths.SessionDirectory,
            "compressed", canDeleteLossless: true, atMs: 0);

        Assert.True(result.Compressed);
        Assert.True(result.OriginalDeleted);
        // The normalized lossless derivative is deleted; the compressed derivative stays.
        Assert.False(File.Exists(lossless));
        Assert.True(File.Exists(result.DerivativePath));
    }

    [Fact]
    public async Task KeepOriginalFalse_DoesNotDeleteWhenLosslessIsTheOriginalSourceCopy()
    {
        // The user's original source copy is never deleted by compression, even when
        // keep_original = false: only a normalized derivative is deletable.
        var (service, media, writer, lossless, paths) = Setup(sourceDurationMs: 600_000);
        var losslessMedia = MakeInfo(lossless, 600_000, "wav", "pcm_s16le");

        var result = await service.CompressAsync(
            lossless, losslessMedia, Retention(keepOriginal: false), paths, paths.SessionDirectory,
            "compressed", canDeleteLossless: false, atMs: 0);

        Assert.True(result.Compressed);
        Assert.False(result.OriginalDeleted);
        Assert.True(File.Exists(lossless));
        Assert.True(File.Exists(result.DerivativePath));
    }

    [Fact]
    public async Task KeepOriginalFalse_DoesNotDeleteAfterAFailedEncode()
    {
        var (service, media, writer, lossless, paths) = Setup(sourceDurationMs: 600_000);
        media.FailCompress = true;
        var losslessMedia = MakeInfo(lossless, 600_000, "wav", "pcm_s16le");

        var result = await service.CompressAsync(
            lossless, losslessMedia, Retention(keepOriginal: false), paths, paths.SessionDirectory,
            "compressed", canDeleteLossless: true, atMs: 0);

        Assert.False(result.Compressed);
        Assert.False(result.OriginalDeleted);
        Assert.True(File.Exists(lossless));
    }

    private sealed class FakeMedia : IMediaPipeline
    {
        public long SourceDurationMs = 600_000;
        public long DerivativeDurationOffsetMs = 0;
        public bool FailCompress;
        public int CompressCalls;
        public string? LastDerivativePath;

        public MediaInfo Inspect(string path)
        {
            if (LastDerivativePath is not null && path == LastDerivativePath)
            {
                return MakeInfo(path, SourceDurationMs + DerivativeDurationOffsetMs, "mp3", "mp3");
            }

            return MakeInfo(path, SourceDurationMs, "wav", "pcm_s16le");
        }

        public Task NormalizeAsync(string inputPath, string outputPath, MediaNormalizationPlan plan,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompressAsync(string inputPath, string outputPath, MediaCompressionPlan plan,
            CancellationToken cancellationToken = default)
        {
            CompressCalls++;
            if (FailCompress)
            {
                throw new MediaProbeException("simulated encode failure");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, new byte[128]);
            LastDerivativePath = outputPath;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingWriter : ISessionArtifactWriter
    {
        public List<(string Name, IReadOnlyDictionary<string, object?>? Details)> Events { get; } = new();

        public void EnsureLayout(SessionArtifactPaths paths) => Directory.CreateDirectory(paths.SessionDirectory);

        public string WriteSessionDocument(SessionArtifactPaths paths, SessionDocument document)
        {
            EnsureLayout(paths);
            File.WriteAllText(paths.SessionJson, "{}");
            return paths.SessionJson;
        }

        public SessionDocument? ReadSessionDocument(SessionArtifactPaths paths) => null;

        public void AppendEvent(
            SessionArtifactPaths paths,
            string name,
            long atMs,
            IReadOnlyDictionary<string, object?>? details = null) =>
            Events.Add((name, details));
    }
}
