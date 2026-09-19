namespace MeetCap.Core.Asr;

/// <summary>Publishes a durable local audio artifact into the representation consumed by file ASR.</summary>
public interface IAsrAudioPublisher
{
    Task<AsrPublishedAudio> PublishAsync(AsrFileRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral audio payload. Signed URLs are deliberately ephemeral.</summary>
public sealed record AsrPublishedAudio
{
    public required string Transport { get; init; }
    public string? InlineBase64 { get; init; }
    public string? Url { get; init; }
    public long Bytes { get; init; }
    public string? Bucket { get; init; }
    public string? ObjectKey { get; init; }
}
