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

    private FFOptions CreateOptions() => new()
    {
        BinaryFolder = _binaries.BinaryFolder,
        TemporaryFilesFolder = _temporaryFolder,
    };
}
