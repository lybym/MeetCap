namespace MeetCap.Core.Asr;

/// <summary>
/// Publishes a durable local audio artifact into the representation a file-ASR
/// provider consumes, and releases it again once the job is terminal.
/// </summary>
/// <remarks>
/// <para>
/// This is the transport boundary required by issue #29: the provider adapter owns the
/// Seed-ASR protocol only, and never the object-storage upload, signing, or deletion logic.
/// The returned <see cref="AsrPublishedAudio"/> carries the stable, non-secret identity the
/// provider turns into <c>audio.data</c> or <c>audio.url</c> (<c>docs/ARCHITECTURE.md</c>
/// section 11).
/// </para>
/// <para>
/// The local artifact stays authoritative and is never deleted by an implementation: it is
/// the durable recording, while a published object is a temporary transport copy
/// (<c>docs/DATA_MODEL.md</c> sections 6.2 and 12).
/// </para>
/// </remarks>
public interface IAsrAudioPublisher
{
    /// <summary>Publishes the artifact and returns the ephemeral plus stable identity.</summary>
    Task<AsrPublishedAudio> PublishAsync(AsrFileRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a previously published copy. Idempotent: deleting an object that is already
    /// gone is success, so a retry after a partial failure is always safe.
    /// </summary>
    /// <param name="published">The published identity. The signed URL is deliberately not required.</param>
    Task DeleteAsync(AsrPublishedAudio published, CancellationToken cancellationToken = default);
}

/// <summary>
/// The provider-neutral audio payload handed to a file-ASR adapter.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="InlineBase64"/> and <see cref="Url"/> is set, selected by
/// <see cref="Transport"/>. The signed URL is deliberately ephemeral: it is never durable
/// identity and is never persisted (<c>docs/DATA_MODEL.md</c> section 6.2).
/// </para>
/// <para>
/// <see cref="Bucket"/> and <see cref="ObjectKey"/> are the durable identity, and they are
/// only populated for a transport that owns a remote copy. Credentials never appear on this
/// type: an implementation holds them, and nothing that reaches SQLite or an artifact can
/// carry them.
/// </para>
/// </remarks>
public sealed record AsrPublishedAudio
{
    /// <summary>One of <see cref="AsrTransports.Inline"/> or <see cref="AsrTransports.Tos"/>.</summary>
    public required string Transport { get; init; }

    /// <summary>Base64 audio for the inline transport, otherwise <c>null</c>.</summary>
    public string? InlineBase64 { get; init; }

    /// <summary>Time-limited signed GET URL, otherwise <c>null</c>. Never persisted.</summary>
    public string? Url { get; init; }

    /// <summary>Size of the published audio in bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Durable bucket identity, or <c>null</c> for the inline transport.</summary>
    public string? Bucket { get; init; }

    /// <summary>Durable object key, or <c>null</c> for the inline transport.</summary>
    public string? ObjectKey { get; init; }

    /// <summary>True when a remote copy exists and must be released after the job is terminal.</summary>
    public bool HasRemoteCopy =>
        string.Equals(Transport, AsrTransports.Tos, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(Bucket)
        && !string.IsNullOrWhiteSpace(ObjectKey);
}

/// <summary>
/// The wire values of <see cref="AsrPublishedAudio.Transport"/> and of the durable
/// <c>asr_jobs.audio_transport</c> column (<c>docs/DATA_MODEL.md</c> section 6.2).
/// </summary>
public static class AsrTransports
{
    /// <summary>Small inputs travel inline as base64 <c>audio.data</c>.</summary>
    public const string Inline = "inline";

    /// <summary>Oversized inputs travel through a private object plus a presigned <c>audio.url</c>.</summary>
    public const string Tos = "tos";
}
