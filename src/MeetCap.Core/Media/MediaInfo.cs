namespace MeetCap.Core.Media;

/// <summary>
/// Result of inspecting a media file with FFprobe. Deliberately provider-neutral:
/// no FFMpegCore type appears here, so domain code can consume it
/// (<c>docs/ARCHITECTURE.md</c> section 13).
/// </summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }

    /// <summary>Container format name reported by FFprobe, e.g. <c>mov,mp4,m4a,3gp,3g2,mj2</c>.</summary>
    public required string FormatName { get; init; }

    public required long DurationMs { get; init; }

    public required long ByteLength { get; init; }

    /// <summary>FFmpeg audio codec name, e.g. <c>aac</c> or <c>pcm_s16le</c>. Null when no audio stream exists.</summary>
    public string? AudioCodec { get; init; }

    public int SampleRateHz { get; init; }

    public int Channels { get; init; }

    public int? BitDepth { get; init; }

    public long BitRate { get; init; }

    public bool HasVideoStream { get; init; }

    /// <summary>True when the file carries at least one decodable audio stream.</summary>
    public bool HasAudio => !string.IsNullOrEmpty(AudioCodec) && SampleRateHz > 0 && Channels > 0;
}
