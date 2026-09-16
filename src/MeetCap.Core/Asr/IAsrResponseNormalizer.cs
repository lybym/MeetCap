namespace MeetCap.Core.Asr;

/// <summary>
/// Turns a raw provider response into normalized <see cref="Transcripts.TranscriptSegment"/>
/// objects. Implemented inside the provider adapter, because only the adapter knows
/// the provider's JSON shape.
/// </summary>
/// <remarks>
/// Splitting normalization from the HTTP poll is what makes raw-response retention
/// meaningful: the raw response is written to disk before this method runs, so a
/// parser fix can be applied to an already-billed result
/// (<c>docs/ARCHITECTURE.md</c> section 14).
/// </remarks>
public interface IAsrResponseNormalizer
{
    AsrNormalizationResult Normalize(string rawResponseJson, AsrNormalizationContext context);
}

/// <summary>Everything the normalizer needs that is not in the provider response.</summary>
public sealed record AsrNormalizationContext
{
    public required string SessionId { get; init; }

    public required string JobId { get; init; }

    public required string Source { get; init; }

    /// <summary>
    /// Session-relative position of the submitted audio's first frame, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Provider timestamps are relative to the submitted file. A live ASR batch is a window
    /// into a longer session, so its segments are only in session coordinates once this
    /// offset is applied. Zero for a whole-session artifact such as an import
    /// (<c>docs/DATA_MODEL.md</c> section 6).
    /// </remarks>
    public long StartOffsetMs { get; init; }
}

/// <summary>Normalized segments plus what the provider actually returned.</summary>
public sealed record AsrNormalizationResult
{
    public required IReadOnlyList<Transcripts.TranscriptSegment> Segments { get; init; }

    /// <summary>True when the provider returned anonymous speaker labels.</summary>
    public required bool SpeakerInfoReturned { get; init; }

    /// <summary>Provider-level error code when the response carried a failure instead of a result.</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>Raised when a provider response cannot be parsed at all.</summary>
public sealed class AsrNormalizationException : InvalidOperationException
{
    public AsrNormalizationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
