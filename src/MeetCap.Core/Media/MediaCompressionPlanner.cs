namespace MeetCap.Core.Media;

/// <summary>
/// Whether a finalized lossless artifact should be compressed into a lossy archive
/// derivative, and the encoding target when it should. Pure policy: the same inputs
/// always yield the same plan, which is what makes the threshold boundary testable
/// (<c>docs/ASR_STRATEGY.md</c> section 12.2, issue #53 Part A).
/// </summary>
/// <remarks>
/// <para>
/// Compression is <strong>off by default</strong> (<c>compress = "none"</c>). The
/// threshold is mandatory only when compression is enabled: with no codec selected no
/// artifact is ever compressed, so "over five minutes are always compressed" applies
/// solely while a codec is selected. An artifact at or below the threshold is never
/// compressed even when a codec is enabled, so the live 300-second batch window stays
/// WAV-inline exactly as today.
/// </para>
/// <para>
/// The decision applies to a complete finalized session/import derivative, never to a
/// 300-second live batch window. Encoding happens off the capture callback and only from
/// the lossless artifact, so the realtime capture path and crash recovery are untouched
/// (<c>docs/RELIABILITY.md</c> section 3).
/// </para>
/// </remarks>
public sealed record MediaCompressionPlan
{
    public required bool Required { get; init; }

    /// <summary>Human-readable explanation, e.g. "artifact is at or below the 300 s threshold".</summary>
    public required string Reason { get; init; }

    /// <summary>Configured codec: <c>mp3</c> or <c>aac</c>. Meaningful only when <see cref="Required"/>.</summary>
    public required string Codec { get; init; }

    /// <summary>FFmpeg output container/format name, e.g. <c>mp3</c> or <c>adts</c>.</summary>
    public required string TargetFormat { get; init; }

    /// <summary>FFmpeg audio encoder name, e.g. <c>libmp3lame</c> or <c>aac</c>.</summary>
    public required string TargetAudioCodec { get; init; }

    /// <summary>Target bitrate in kbps.</summary>
    public required int TargetBitrateKbps { get; init; }

    /// <summary>Provider format label for the Seed-ASR <c>audio.format</c> field, e.g. <c>mp3</c> or <c>aac</c>.</summary>
    public required string ProviderFormat { get; init; }

    /// <summary>File extension (with leading dot) for the derivative, e.g. <c>.mp3</c>.</summary>
    public required string Extension { get; init; }
}

/// <summary>
/// Decides whether a finalized artifact is compressed. Pure policy with no I/O and no
/// config-section dependency: callers pass the resolved compression settings, so the
/// threshold boundary is a unit-testable decision rather than a buried branch.
/// </summary>
public static class MediaCompressionPlanner
{
    public const string CodecMp3 = "mp3";
    public const string CodecAac = "aac";
    public const string CodecNone = "none";

    /// <summary>
    /// Tolerance for the derivative's duration versus the lossless source. Lossy codecs
    /// add encoder delay/padding (an MP3 frame is ~26 ms, an AAC frame ~23 ms), so a
    /// small absolute tolerance is the right guard: it catches a wrong file, a
    /// truncation, or a concat mismatch without rejecting a legitimate frame of
    /// padding. The provider's timestamps map onto the uploaded file's wall-clock
    /// timeline, and this tolerance is what keeps <c>StartOffsetMs</c>/merger
    /// assumptions valid (<c>docs/ASR_STRATEGY.md</c> section 12.2).
    /// </summary>
    public const int DurationToleranceMs = 500;

    /// <summary>
    /// Plans compression for a finalized lossless artifact.
    /// </summary>
    /// <param name="source">The lossless artifact that would otherwise be the upload payload.</param>
    /// <param name="codec">Configured codec: <c>none</c>, <c>mp3</c>, or <c>aac</c>.</param>
    /// <param name="bitrateKbps">Target bitrate in kbps.</param>
    /// <param name="minSeconds">Artifacts at or below this many seconds are never compressed.</param>
    public static MediaCompressionPlan Plan(MediaInfo source, string codec, int bitrateKbps, int minSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.Equals(codec, CodecNone, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(codec))
        {
            return NotRequired("compression is disabled (retention.compress = \"none\")");
        }

        // The threshold applies to the complete finalized derivative. At or below the
        // threshold the artifact stays as the lossless WAV the ASR path already produces,
        // so behavior is byte-identical to today.
        var thresholdMs = (long)minSeconds * 1000;
        if (source.DurationMs <= thresholdMs)
        {
            return NotRequired(
                $"artifact duration {source.DurationMs} ms is at or below the " +
                $"{minSeconds} s compression threshold");
        }

        if (!TryResolveCodec(codec, out var target))
        {
            return NotRequired($"compression codec '{codec}' is not recognized");
        }

        return new MediaCompressionPlan
        {
            Required = true,
            Reason = $"artifact duration {source.DurationMs} ms exceeds the {minSeconds} s threshold; " +
                     $"encoding a {target.Codec} derivative at {bitrateKbps} kbps",
            Codec = target.Codec,
            TargetFormat = target.TargetFormat,
            TargetAudioCodec = target.TargetAudioCodec,
            TargetBitrateKbps = bitrateKbps,
            ProviderFormat = target.ProviderFormat,
            Extension = target.Extension,
        };
    }

    /// <summary>
    /// True when the derivative's duration matches the lossless source within
    /// <see cref="DurationToleranceMs"/>. This is the timeline-correctness guard the
    /// caller asserts before publishing the derivative as the ASR payload.
    /// </summary>
    public static bool DurationMatches(long sourceMs, long derivativeMs) =>
        Math.Abs(sourceMs - derivativeMs) <= DurationToleranceMs;

    private static MediaCompressionPlan NotRequired(string reason) => new()
    {
        Required = false,
        Reason = reason,
        Codec = CodecNone,
        TargetFormat = string.Empty,
        TargetAudioCodec = string.Empty,
        TargetBitrateKbps = 0,
        ProviderFormat = string.Empty,
        Extension = string.Empty,
    };

    private static bool TryResolveCodec(string codec, out (string Codec, string TargetFormat, string TargetAudioCodec, string ProviderFormat, string Extension) target)
    {
        if (string.Equals(codec, CodecMp3, StringComparison.OrdinalIgnoreCase))
        {
            target = (CodecMp3, "mp3", "libmp3lame", "mp3", ".mp3");
            return true;
        }

        if (string.Equals(codec, CodecAac, StringComparison.OrdinalIgnoreCase))
        {
            // ADTS (.aac) is the raw AAC transport stream the Seed-ASR submit contract
            // labels "aac"; m4a/mp4 would be labeled "m4a". ADTS keeps the provider
            // format label and the file extension in agreement.
            target = (CodecAac, "adts", "aac", "aac", ".aac");
            return true;
        }

        target = default;
        return false;
    }
}
