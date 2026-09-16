namespace MeetCap.Asr.Volcengine;

using System.Text.Json;
using MeetCap.Core.Asr;
using MeetCap.Core.Ids;
using MeetCap.Core.Transcripts;

/// <summary>
/// Parses a raw Volcengine file-ASR response into normalized
/// <see cref="TranscriptSegment"/> objects.
/// </summary>
/// <remarks>
/// <para>
/// The response envelope (<c>result.utterances[]</c> with <c>text</c>,
/// <c>start_time</c>, <c>end_time</c>, and a speaker field) is provider knowledge and
/// stays inside this assembly.
/// </para>
/// <para>
/// Speaker values are turned into the documented anonymous vocabulary
/// <c>speaker_&lt;n&gt;</c> (<c>docs/ARCHITECTURE.md</c> section 15). They are never
/// resolved to a person here: <c>speaker_id</c>, <c>speaker_name</c>, and
/// <c>speaker_confidence</c> stay null and <c>manual_speaker_lock</c> stays false.
/// Identity matching is a separate milestone.
/// </para>
/// </remarks>
public sealed class VolcengineResponseNormalizer : IAsrResponseNormalizer
{
    public AsrNormalizationResult Normalize(string rawResponseJson, AsrNormalizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A silent recording legitimately comes back with no body; that is an empty
        // transcript, not a parse failure.
        if (string.IsNullOrWhiteSpace(rawResponseJson))
        {
            return Empty();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawResponseJson);
        }
        catch (JsonException ex)
        {
            throw new AsrNormalizationException(
                $"The retained provider response is not valid JSON ({ex.Message}).",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AsrNormalizationException(
                    $"The retained provider response is a JSON {root.ValueKind}, not an object.");
            }

            if (TryReadError(root, out var errorCode, out var errorMessage))
            {
                return new AsrNormalizationResult
                {
                    Segments = Array.Empty<TranscriptSegment>(),
                    SpeakerInfoReturned = false,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage,
                };
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                // No result object and no error envelope: treat as an empty transcript
                // rather than inventing speech.
                return Empty();
            }

            return Offset(ReadResult(result, context), context);
        }
    }

    /// <summary>
    /// Moves provider-relative timestamps onto the session timeline.
    /// </summary>
    /// <remarks>
    /// The provider reports <c>start_time</c> / <c>end_time</c> relative to the audio it was
    /// given. An imported file starts at zero, but a live ASR batch is one window of a
    /// longer recording, so without this shift every batch after the first would place its
    /// segments at the start of the meeting (<c>docs/DATA_MODEL.md</c> section 6). Only the
    /// timestamps move: the raw response is already retained unchanged, and
    /// <see cref="TranscriptSegment.RawText"/> is never rewritten.
    /// </remarks>
    private static AsrNormalizationResult Offset(AsrNormalizationResult result, AsrNormalizationContext context)
    {
        if (context.StartOffsetMs == 0 || result.Segments.Count == 0)
        {
            return result;
        }

        var offset = context.StartOffsetMs;
        var shifted = new List<TranscriptSegment>(result.Segments.Count);
        foreach (var segment in result.Segments)
        {
            shifted.Add(segment with
            {
                StartMs = segment.StartMs + offset,
                EndMs = segment.EndMs + offset,
            });
        }

        return result with { Segments = shifted };
    }

    private static AsrNormalizationResult ReadResult(JsonElement result, AsrNormalizationContext context)
    {
        var segments = new List<TranscriptSegment>();
        var speakerInfoReturned = false;
        var index = 0;

        if (result.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array)
        {
            foreach (var utterance in utterances.EnumerateArray())
            {
                if (utterance.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                var speakerLabel = ReadSpeakerLabel(utterance);
                if (speakerLabel is not null)
                {
                    speakerInfoReturned = true;
                }

                var text = ReadString(utterance, "text") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    index++;
                    continue;
                }

                var start = ReadLong(utterance, "start_time") ?? 0;
                var end = ReadLong(utterance, "end_time") ?? start;
                if (end < start)
                {
                    end = start;
                }

                segments.Add(new TranscriptSegment
                {
                    SegmentId = Ids.NewSegmentId(context.JobId, index),
                    SessionId = context.SessionId,
                    Source = context.Source,
                    StartMs = start,
                    EndMs = end,
                    RawText = text,
                    SpeakerLabel = speakerLabel,
                    SpeakerId = null,
                    SpeakerName = null,
                    SpeakerConfidence = null,
                    ManualSpeakerLock = false,
                    ProviderJobId = context.JobId,
                });

                index++;
            }
        }

        if (segments.Count == 0)
        {
            var wholeText = ReadString(result, "text");
            if (!string.IsNullOrWhiteSpace(wholeText))
            {
                segments.Add(new TranscriptSegment
                {
                    SegmentId = Ids.NewSegmentId(context.JobId, 0),
                    SessionId = context.SessionId,
                    Source = context.Source,
                    StartMs = 0,
                    EndMs = ReadAudioDurationMs(result) ?? 0,
                    RawText = wholeText,
                    SpeakerLabel = null,
                    SpeakerId = null,
                    SpeakerName = null,
                    SpeakerConfidence = null,
                    ManualSpeakerLock = false,
                    ProviderJobId = context.JobId,
                });
            }
        }

        return new AsrNormalizationResult
        {
            Segments = segments,
            SpeakerInfoReturned = speakerInfoReturned,
        };
    }

    private static AsrNormalizationResult Empty() => new()
    {
        Segments = Array.Empty<TranscriptSegment>(),
        SpeakerInfoReturned = false,
    };

    private static bool TryReadError(JsonElement root, out string code, out string message)
    {
        code = string.Empty;
        message = string.Empty;

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = ReadString(error, "code") ?? "provider.error";
            message = ReadString(error, "message") ?? "The provider reported an error.";
            return true;
        }

        var rawCode = ReadString(root, "code");
        if (!string.IsNullOrEmpty(rawCode) && !string.Equals(rawCode, "0", StringComparison.Ordinal)
            && !string.Equals(rawCode, "success", StringComparison.OrdinalIgnoreCase))
        {
            code = rawCode;
            message = ReadString(root, "message") ?? "The provider reported an error.";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads an anonymous speaker label from either the documented
    /// <c>speaker</c> field or the nested <c>additions.speaker</c> shape.
    /// </summary>
    private static string? ReadSpeakerLabel(JsonElement utterance)
    {
        var raw = ReadScalar(utterance, "speaker");
        if (raw is null && utterance.TryGetProperty("additions", out var additions)
            && additions.ValueKind == JsonValueKind.Object)
        {
            raw = ReadScalar(additions, "speaker");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith("speaker", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var sanitized = new string(value
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')
            .ToArray());
        return "speaker_" + sanitized;
    }

    private static long? ReadAudioDurationMs(JsonElement result)
    {
        if (result.TryGetProperty("audio_info", out var audioInfo)
            && audioInfo.ValueKind == JsonValueKind.Object)
        {
            return ReadLong(audioInfo, "duration");
        }

        return null;
    }

    private static string? ReadScalar(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string? ReadString(JsonElement element, string name) => ReadScalar(element, name);

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var real) => (long)Math.Round(real),
            JsonValueKind.String when long.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }
}
