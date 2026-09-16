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
