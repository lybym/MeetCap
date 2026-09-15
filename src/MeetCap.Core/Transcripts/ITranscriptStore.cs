namespace MeetCap.Core.Transcripts;

/// <summary>
/// Writes normalized transcript artifacts. Implemented in <c>MeetCap.Asr</c>.
/// </summary>
/// <remarks>
/// Raw normalized JSONL is internally mandatory and is never gated by
/// configuration (<c>docs/CONFIGURATION.md</c> section 10); the Markdown rendering is
/// what <c>[transcript]</c> settings control.
/// </remarks>
public interface ITranscriptStore
{
    /// <summary>Appends segments to a JSONL file, creating it when absent. Returns the count written.</summary>
    int AppendJsonl(string path, IReadOnlyList<TranscriptSegment> segments);

    /// <summary>Writes (replacing) a JSONL file.</summary>
    void WriteJsonl(string path, IReadOnlyList<TranscriptSegment> segments);

    /// <summary>Reads a JSONL file. Returns an empty list when the file is absent.</summary>
    IReadOnlyList<TranscriptSegment> ReadJsonl(string path);

    /// <summary>Writes (replacing) the Markdown transcript.</summary>
    void WriteMarkdown(
        string path,
        string sessionId,
        IReadOnlyList<TranscriptSegment> segments,
        TranscriptRenderOptions options);
}

/// <summary>Which optional columns the Markdown transcript includes.</summary>
public sealed record TranscriptRenderOptions(bool IncludeSource, bool IncludeTimestamps, bool IncludeSpeakerLabels)
{
    public static readonly TranscriptRenderOptions Default = new(
        IncludeSource: true,
        IncludeTimestamps: true,
        IncludeSpeakerLabels: true);
}
