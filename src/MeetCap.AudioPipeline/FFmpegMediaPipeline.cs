namespace MeetCap.AudioPipeline;

using FFMpegCore;
using MeetCap.Core.Media;

/// <summary>
/// <see cref="IMediaPipeline"/> implemented with FFprobe/FFmpeg through FFMpegCore
/// (<c>docs/ARCHITECTURE.md</c> section 13).
/// </summary>
/// <remarks>
/// <para>
/// FFMpegCore types never leave this assembly: the domain consumes
/// <see cref="MediaInfo"/> and <see cref="MediaNormalizationPlan"/> only. No other
/// component may probe or transcode media, so "normalization only when required"
/// stays a single, testable decision.
/// </para>
/// <para>
/// Nothing here is called from a capture callback; normalization happens on the
/// import/processing path (<c>docs/RELIABILITY.md</c> section 3).
/// </para>
/// </remarks>
public sealed class FFmpegMediaPipeline : IMediaPipeline
{
    private readonly FfmpegBinaries _binaries;
    private readonly string _temporaryFolder;

    public FFmpegMediaPipeline(FfmpegBinaries binaries, string temporaryFolder)
    {
        _binaries = binaries ?? throw new ArgumentNullException(nameof(binaries));
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryFolder);
        _temporaryFolder = temporaryFolder;
    }

    /// <summary>Builds a pipeline from configured paths, using process environment values.</summary>
    public static FFmpegMediaPipeline Create(
        string? configuredBinaryFolder,
        string? configuredTemporaryFolder,
        Func<string, string?>? environment = null)
    {
        var lookup = environment ?? Environment.GetEnvironmentVariable;
        var binaries = FfmpegBinaryLocator.Resolve(configuredBinaryFolder, lookup);
        var temporaryFolder = FfmpegBinaryLocator.ResolveTemporaryFolder(configuredTemporaryFolder, lookup);
        return new FFmpegMediaPipeline(binaries, temporaryFolder);
    }

    public MediaInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new MediaProbeException($"Media file not found: {path}");
        }

        IMediaAnalysis analysis;
        try
        {
            analysis = FFProbe.Analyse(path, CreateOptions(), string.Empty);
        }
        catch (Exception ex) when (ex is not MediaProbeException and not MediaToolingException)
        {
            throw new MediaProbeException(
                $"FFprobe could not inspect '{path}': {ex.Message}. " +
                "Confirm the file is a readable audio or video recording.", ex);
        }

        var audio = analysis.PrimaryAudioStream;
        var duration = analysis.Duration > TimeSpan.Zero ? analysis.Duration : analysis.Format.Duration;

        return new MediaInfo
        {
            Path = path,
            FormatName = analysis.Format.FormatName ?? string.Empty,
            DurationMs = (long)Math.Round(duration.TotalMilliseconds),
            ByteLength = new FileInfo(path).Length,
            AudioCodec = audio?.CodecName,
            SampleRateHz = audio?.SampleRateHz ?? 0,
            Channels = audio?.Channels ?? 0,
            BitDepth = audio?.BitDepth,
            BitRate = (long)Math.Round(audio?.BitRate ?? analysis.Format.BitRate),
            HasVideoStream = analysis.PrimaryVideoStream is not null,
        };
    }

    public async Task NormalizeAsync(
        string inputPath,
        string outputPath,
        MediaNormalizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.Required)
        {
            throw new InvalidOperationException(
                "NormalizeAsync was called with a plan that does not require normalization. " +
                "Normalization must run only when required (docs/ARCHITECTURE.md section 13).");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        Directory.CreateDirectory(_temporaryFolder);

        try
        {
            var processed = await FFMpegArguments
                .FromFileInput(new FileInfo(inputPath), _ => { })
                .OutputToFile(outputPath, overwrite: true, arguments => arguments
                    // Drop any video track and select the first audio stream, then
                    // resample to the normalized target the provider accepts.
                    .WithCustomArgument("-vn")
                    .WithAudioCodec(plan.TargetAudioCodec)
                    .WithAudioSamplingRate(plan.TargetSampleRateHz)
                    .WithCustomArgument($"-ac {plan.TargetChannels}")
                    .ForceFormat(plan.TargetFormat))
                .ProcessAsynchronously(throwOnError: true, CreateOptions())
                .ConfigureAwait(false);

            if (!processed || !File.Exists(outputPath))
            {
                throw new MediaProbeException(
                    $"FFmpeg did not produce a normalized artifact at '{outputPath}'.");
            }
        }
        catch (Exception ex) when (ex is not MediaProbeException and not MediaToolingException)
        {
            throw new MediaProbeException(
                $"FFmpeg could not normalize '{inputPath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Encodes a compressed lossy derivative from the lossless WAV (issue #53 Part A).
    /// Encodes once from the complete finalized artifact; per-chunk re-encoding is
    /// avoided. The caller re-inspects the output and asserts duration equality before
    /// publishing it, so this method only owns the FFmpeg invocation.
    /// </summary>
    /// <remarks>
    /// The derivative keeps the source's sample rate and channel count: the lossless input
    /// is already the 16 kHz mono PCM the ASR path normalizes to, and changing those here
    /// would be a second normalization. Only the codec and bitrate change. Constant bitrate
    /// keeps the derivative's size predictable (the inline-vs-TOS selection and the sizing
    /// estimates rely on it). The documented duration tolerance (500 ms) absorbs the codec's
    /// inherent frame delay/padding (a LAME/AAC frame is ~23–26 ms); the bitexact flags below
    /// suppress muxer-level optional metadata, not that encoder delay
    /// (docs/ASR_STRATEGY.md section 12.2).
    /// </remarks>
    public async Task CompressAsync(
        string inputPath,
        string outputPath,
        MediaCompressionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.Required)
        {
            throw new InvalidOperationException(
                "CompressAsync was called with a plan that does not require compression. " +
                "Compression must run only when the threshold is exceeded and a codec is selected.");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        Directory.CreateDirectory(_temporaryFolder);

        // Atomic publication (issue #53 review update): encode to a .part sidecar, then move
        // it onto the final name only after FFmpeg reports success. A crash or abort mid-encode
        // leaves a partial .part (cleaned up on the failure path) rather than a half-written
        // derivative at the final location. This mirrors the NormalizingAsrProvider pattern.
        var partPath = outputPath + ".part";

        try
        {
            try
            {
                var processed = await FFMpegArguments
                    .FromFileInput(new FileInfo(inputPath), _ => { })
                    .OutputToFile(partPath, overwrite: true, arguments => arguments
                        // Drop any video track and select the first audio stream.
                        .WithCustomArgument("-vn")
                        .WithAudioCodec(plan.TargetAudioCodec)
                        // Constant bitrate: the derivative's size must be predictable for the
                        // inline-vs-TOS selection and the documented sizing estimates.
                        .WithCustomArgument($"-b:a {plan.TargetBitrateKbps}k")
                        // Suppress muxer-level optional metadata (most relevant to MOV/MP4);
                        // the documented tolerance absorbs the codec's inherent frame padding.
                        .WithCustomArgument("-fflags +bitexact")
                        .WithCustomArgument("-flags +bitexact")
                        .ForceFormat(plan.TargetFormat))
                    .ProcessAsynchronously(throwOnError: true, CreateOptions())
                    .ConfigureAwait(false);

                if (!processed || !File.Exists(partPath))
                {
                    throw new MediaProbeException(
                        $"FFmpeg did not produce a compressed artifact at '{outputPath}'.");
                }

                File.Move(partPath, outputPath, overwrite: true);
            }
            catch (Exception ex) when (ex is not MediaProbeException and not MediaToolingException)
            {
                throw new MediaProbeException(
                    $"FFmpeg could not compress '{inputPath}': {ex.Message}", ex);
            }
        }
        finally
        {
            // A leftover .part means the encode or the move did not complete; it must never
            // survive as a stale partial derivative a later run could mistake for complete.
            TryCleanupPart(partPath);
        }
    }

    private static void TryCleanupPart(string partPath)
    {
        try
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a partial sidecar must not mask the original failure.
        }
    }

    private FFOptions CreateOptions() => new()
    {
        BinaryFolder = _binaries.BinaryFolder,
        TemporaryFilesFolder = _temporaryFolder,
    };
}
