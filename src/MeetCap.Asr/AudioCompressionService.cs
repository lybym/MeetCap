namespace MeetCap.Asr;

using System.Security.Cryptography;
using MeetCap.Core.Configuration;
using MeetCap.Core.Media;
using MeetCap.Core.Sessions;

/// <summary>
/// Outcome of one compression attempt for a finalized lossless artifact
/// (issue #53 Part A).
/// </summary>
public sealed record AudioCompressionResult
{
    /// <summary>True when a compressed derivative was produced and its duration verified.</summary>
    public required bool Compressed { get; init; }

    /// <summary>Absolute path of the verified derivative, when produced.</summary>
    public string? DerivativePath { get; init; }

    /// <summary>The compressed source artifact (<c>compressed</c> role), when produced.</summary>
    public SourceArtifact? Artifact { get; init; }

    /// <summary>Inspected shape of the derivative, when produced.</summary>
    public MediaInfo? DerivativeMedia { get; init; }

    /// <summary>The compression plan that was applied, when produced.</summary>
    public MediaCompressionPlan? Plan { get; init; }

    /// <summary>
    /// Why compression failed, when it did. On this path the original is kept and the WAV
    /// remains the upload payload.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// True only when the lossless encode-input artifact was deleted
    /// (<c>compress_keep_original = false</c> AND a verified encode). The user's original
    /// source is never deleted here.
    /// </summary>
    public bool OriginalDeleted { get; init; }

    /// <summary>No compression was required or produced.</summary>
    public static AudioCompressionResult None { get; } = new() { Compressed = false };
}

/// <summary>
/// Encodes a compressed lossy derivative from a finalized lossless artifact, verifies its
/// duration, records it as a <c>compressed</c> source artifact, and never blocks ASR on
/// failure (issue #53 Part A).
/// </summary>
/// <remarks>
/// <para>
/// Compression runs once from the complete lossless artifact — never from a capture
/// callback and never while capture is running — so the realtime path and crash recovery
/// are untouched. The derivative is recorded in <c>session.json</c> with its own SHA-256
/// and duration alongside the <c>original</c>/<c>normalized</c> artifacts.
/// </para>
/// <para>
/// Compression failure is a no-loss fallback: the lossless WAV stays the upload payload,
/// the original is kept, and one <c>audio.compress_failed</c> session event is surfaced —
/// the same philosophy as <c>asr.audio.release_failed</c>.
/// </para>
/// <para>
/// The lossless encode-input artifact is deleted only after a verified successful encode,
/// only when <c>compress_keep_original = false</c>, and only when the caller confirms it is
/// a normalized derivative rather than the user's original source copy. Raw sources are
/// kept by default (<c>docs/CONFIGURATION.md</c> section 12, issue #53 review update).
/// </para>
/// </remarks>
public sealed class AudioCompressionService
{
    private readonly IMediaPipeline _media;
    private readonly ISessionArtifactWriter _artifacts;

    public AudioCompressionService(IMediaPipeline media, ISessionArtifactWriter artifacts)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    /// <summary>
    /// Produces a compressed derivative of <paramref name="losslessArtifactPath"/> when the
    /// configured policy requires it.
    /// </summary>
    /// <param name="losslessArtifactPath">The complete finalized lossless WAV to encode from.</param>
    /// <param name="losslessMedia">Inspected shape of that artifact.</param>
    /// <param name="retention">Retention/compression configuration.</param>
    /// <param name="paths">Session artifact paths (for event logging).</param>
    /// <param name="derivativeDirectory">Directory the derivative is written into.</param>
    /// <param name="derivativeBaseName">Derivative file stem, e.g. <c>compressed</c>; the codec extension is appended.</param>
    /// <param name="canDeleteLossless">
    /// True only when <paramref name="losslessArtifactPath"/> is a normalized derivative
    /// (not the user's original source copy), so <c>keep_original = false</c> may delete it.
    /// </param>
    /// <param name="atMs">Session-relative timestamp for the emitted event.</param>
    public async Task<AudioCompressionResult> CompressAsync(
        string losslessArtifactPath,
        MediaInfo losslessMedia,
        RetentionSection retention,
        SessionArtifactPaths paths,
        string derivativeDirectory,
        string derivativeBaseName,
        bool canDeleteLossless,
        long atMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(losslessArtifactPath);
        ArgumentNullException.ThrowIfNull(losslessMedia);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(derivativeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(derivativeBaseName);

        var plan = MediaCompressionPlanner.Plan(
            losslessMedia,
            retention.Compress,
            retention.CompressBitrateKbps,
            retention.CompressMinSeconds);

        if (!plan.Required)
        {
            return AudioCompressionResult.None;
        }

        Directory.CreateDirectory(derivativeDirectory);
        var derivativePath = Path.Combine(derivativeDirectory, derivativeBaseName + plan.Extension);

        try
        {
            await _media.CompressAsync(losslessArtifactPath, derivativePath, plan, cancellationToken)
                .ConfigureAwait(false);

            var derivative = _media.Inspect(derivativePath);

            // Timeline-correctness guard: the derivative's duration must match the lossless
            // source within the documented tolerance before it can be published as the ASR
            // payload. A mismatch means the encode is not a faithful timeline copy and must
            // not replace the WAV.
            if (!MediaCompressionPlanner.DurationMatches(losslessMedia.DurationMs, derivative.DurationMs))
            {
                throw new MediaProbeException(
                    $"Compressed derivative '{derivativePath}' duration {derivative.DurationMs} ms " +
                    $"does not match the lossless source {losslessMedia.DurationMs} ms within the " +
                    $"{MediaCompressionPlanner.DurationToleranceMs} ms tolerance. The derivative is " +
                    "not a faithful timeline copy and will not be used.");
            }

            var artifact = BuildArtifact(
                SourceArtifact.Roles.Compressed,
                losslessArtifactPath,
                derivativePath,
                derivative.DurationMs);

            var originalDeleted = false;
            if (!retention.CompressKeepOriginal && canDeleteLossless)
            {
                // Only the normalized lossless derivative is ever deleted, and only after the
                // encode was verified. The user's original source copy is never touched here.
                TryDelete(losslessArtifactPath);
                originalDeleted = true;
            }

            _artifacts.AppendEvent(
                paths,
                SessionEvents.AudioCompressed,
                atMs,
                new Dictionary<string, object?>
                {
                    ["codec"] = plan.Codec,
                    ["bitrate_kbps"] = plan.TargetBitrateKbps,
                    ["derivative_path"] = artifact.StoredPath,
                    ["bytes"] = artifact.ByteLength,
                    ["sha256"] = artifact.Sha256,
                    ["duration_ms"] = derivative.DurationMs,
                    ["original_deleted"] = originalDeleted,
                });

            return new AudioCompressionResult
            {
                Compressed = true,
                DerivativePath = derivativePath,
                Artifact = artifact,
                DerivativeMedia = derivative,
                Plan = plan,
                OriginalDeleted = originalDeleted,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No-loss fallback: a failed encode never blocks ASR. The lossless WAV stays the
            // upload payload, the original is kept, and one session event records the failure.
            TryDelete(derivativePath);

            _artifacts.AppendEvent(
                paths,
                SessionEvents.AudioCompressFailed,
                atMs,
                new Dictionary<string, object?>
                {
                    ["codec"] = plan.Codec,
                    ["bitrate_kbps"] = plan.TargetBitrateKbps,
                    ["reason"] = ScrubPath(ex.Message),
                });

            return new AudioCompressionResult
            {
                Compressed = false,
                FailureReason = ScrubPath(ex.Message),
            };
        }
    }

    private static SourceArtifact BuildArtifact(string role, string originalPath, string storedPath, long durationMs)
    {
        var info = new FileInfo(storedPath);
        return new SourceArtifact
        {
            Role = role,
            OriginalPath = originalPath,
            StoredPath = storedPath,
            FileName = info.Name,
            ByteLength = info.Length,
            DurationMs = durationMs,
            Sha256 = Sha256OfFile(storedPath),
        };
    }

    /// <summary>SHA-256 of a file, for provenance and re-verification.</summary>
    private static string Sha256OfFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A best-effort cleanup of a partial derivative or a now-unneeded lossless copy
            // must never turn a successful encode into a failure. The artifact is durable
            // either way: the compressed derivative exists (or does not), and retention
            // aging is the backstop.
        }
    }

    /// <summary>
    /// Keeps the failure reason free of absolute paths that could leak a user's directory
    /// layout into the durable event log. The exception type and message stay informative
    /// without embedding filesystem structure.
    /// </summary>
    private static string ScrubPath(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        // Replace anything that looks like an absolute Windows or POSIX path with a bare
        // filename, preserving the informative parts of an FFprobe/FFmpeg message.
        return System.Text.RegularExpressions.Regex.Replace(
            message,
            @"[A-Za-z]:\\[^'\s]+|/[^'\s]+",
            match => Path.GetFileName(match.Value.TrimEnd('\\', '/')));
    }
}
