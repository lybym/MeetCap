namespace MeetCap.Core.Media;

/// <summary>
/// Media inspection and normalization boundary (<c>docs/ARCHITECTURE.md</c>
/// section 13). Implemented by <c>MeetCap.AudioPipeline</c> on top of
/// FFprobe/FFmpeg; domain code depends only on this abstraction.
/// </summary>
/// <remarks>
/// No custom media probing or transcoding logic is allowed anywhere else in the
/// codebase: this interface is the single place a file is inspected or converted.
/// </remarks>
public interface IMediaPipeline
{
    /// <summary>Inspects a media file. Throws <see cref="MediaProbeException"/> on unreadable input.</summary>
    MediaInfo Inspect(string path);

    /// <summary>
    /// Produces the normalized artifact described by <paramref name="plan"/>.
    /// Callers only invoke this when <see cref="MediaNormalizationPlan.Required"/>
    /// is true.
    /// </summary>
    Task NormalizeAsync(
        string inputPath,
        string outputPath,
        MediaNormalizationPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a compressed lossy derivative of a finalized lossless audio artifact
    /// (issue #53 Part A). Callers only invoke this when
    /// <see cref="MediaCompressionPlan.Required"/> is true, and only from a complete
    /// finalized artifact — never from a capture callback and never while capture is
    /// running. The input is the lossless WAV; per-chunk re-encoding is explicitly
    /// avoided so there is no generation loss.
    /// </summary>
    /// <remarks>
    /// The default implementation throws: compression is opt-in and only the FFmpeg-backed
    /// pipeline (and test doubles that exercise the policy) override it. A pipeline that
    /// never compresses inherits the throwing default, so an accidental compress request
    /// surfaces as an explicit error instead of a silent no-op.
    /// </remarks>
    Task CompressAsync(
        string inputPath,
        string outputPath,
        MediaCompressionPlan plan,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This media pipeline does not implement audio compression. " +
            "Compression requires the FFmpeg-backed pipeline (media.ffmpeg_binary_folder).");
}

/// <summary>The external FFmpeg/FFprobe toolchain could not be located or executed.</summary>
public sealed class MediaToolingException : InvalidOperationException
{
    public MediaToolingException(string message)
        : base(message)
    {
    }

    public MediaToolingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The input file could not be inspected or is unusable as an ASR source.</summary>
public sealed class MediaProbeException : InvalidOperationException
{
    public MediaProbeException(string message)
        : base(message)
    {
    }

    public MediaProbeException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
