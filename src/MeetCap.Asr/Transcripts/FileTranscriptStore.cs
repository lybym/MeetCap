namespace MeetCap.Asr.Transcripts;

using System.Globalization;
using System.Text;
using MeetCap.Core.Transcripts;

/// <summary>
/// Filesystem implementation of <see cref="ITranscriptStore"/>.
/// </summary>
/// <remarks>
/// Raw normalized JSONL is written unconditionally: it is internally mandatory and
/// is the agent-facing interface (<c>docs/CONFIGURATION.md</c> section 10,
/// <c>docs/DATA_MODEL.md</c> section 13). Markdown is a rendering of the same data
/// and is gated by <c>[transcript]</c> settings.
/// </remarks>
public sealed class FileTranscriptStore : ITranscriptStore
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Anonymous label caveat printed once at the top of every Markdown transcript.</summary>
    private const string SpeakerLabelNotice =
        "Speaker labels such as `speaker_1` are anonymous, session-scoped provider labels. " +
        "They are not persistent identities and are never assigned to a person by this milestone.";

    public int AppendJsonl(string path, IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(segments);

        EnsureDirectory(path);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, s_utf8);
        foreach (var segment in segments)
        {
            writer.WriteLine(TranscriptJson.ToJsonLine(segment));
        }

        return segments.Count;
    }

    public void WriteJsonl(string path, IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(segments);

        EnsureDirectory(path);
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            builder.AppendLine(TranscriptJson.ToJsonLine(segment));
        }

        File.WriteAllText(path, builder.ToString(), s_utf8);
    }

    public IReadOnlyList<TranscriptSegment> ReadJsonl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return Array.Empty<TranscriptSegment>();
        }

        var segments = new List<TranscriptSegment>();
        foreach (var line in File.ReadLines(path, s_utf8))
        {
            var segment = TranscriptJson.FromJsonLine(line);
            if (segment is not null)
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    public void WriteMarkdown(
        string path,
        string sessionId,
        IReadOnlyList<TranscriptSegment> segments,
        TranscriptRenderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);

        EnsureDirectory(path);
        File.WriteAllText(path, Render(sessionId, segments, options), s_utf8);
    }

    private static string Render(
        string sessionId,
        IReadOnlyList<TranscriptSegment> segments,
        TranscriptRenderOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Meeting Transcript");
        builder.AppendLine();
        builder.AppendLine($"- Session: `{sessionId}`");
        builder.AppendLine($"- Segments: {segments.Count}");
        builder.AppendLine(
            $"- Generated: {DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture)}");
        builder.AppendLine();
        builder.AppendLine($"> {SpeakerLabelNotice}");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        foreach (var segment in segments)
        {
            builder.Append("**");
            if (options.IncludeTimestamps)
            {
                builder.Append(FormatTimestamp(segment.StartMs));
                builder.Append(" - ");
                builder.Append(FormatTimestamp(segment.EndMs));
            }

            if (options.IncludeSource)
            {
                if (options.IncludeTimestamps)
                {
                    builder.Append(" · ");
                }

                builder.Append('`').Append(segment.Source).Append('`');
            }

            if (options.IncludeSpeakerLabels && !string.IsNullOrEmpty(segment.SpeakerLabel))
            {
                if (options.IncludeSource || options.IncludeTimestamps)
                {
                    builder.Append(" · ");
                }

                builder.Append('`').Append(segment.SpeakerLabel).Append('`');
            }

            builder.AppendLine("**");
            builder.AppendLine(segment.RawText);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var value = Math.Max(0, milliseconds);
        var hours = value / 3_600_000;
        var minutes = value / 60_000 % 60;
        var seconds = value / 1000 % 60;
        var millis = value % 1000;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            hours,
            minutes,
            seconds,
            millis);
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
