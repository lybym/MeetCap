namespace MeetCap.Asr.Volcengine;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeetCap.Core.Asr;
using TOS;
using TOS.Error;
using TOS.Model;

/// <summary>
/// Infrastructure-only publisher for the oversized file-ASR path: it stages the audio as a
/// private object through the official Volcengine TOS .NET SDK and hands the provider a
/// time-limited presigned GET URL (<c>docs/DATA_MODEL.md</c> section 6.2).
/// </summary>
/// <remarks>
/// <para>
/// The official SDK owns client construction, the upload, the signed URL, and the delete.
/// Nothing here signs a TOS request by hand, and nothing here makes an object public: no ACL
/// is set, so the bucket's own policy keeps the object private.
/// </para>
/// <para>
/// The TOS .NET SDK exposes a synchronous <c>ITosClient</c> only (there is no
/// <c>PutObjectAsync</c> in 2.1.8), so the blocking SDK calls are dispatched to the thread
/// pool. That keeps an upload of tens of megabytes off whichever thread entered
/// <see cref="PublishAsync"/> — in particular off the capture callback path
/// (<c>docs/ARCHITECTURE.md</c> section 10.1, <c>docs/RELIABILITY.md</c> section 9).
/// </para>
/// <para>
/// The local artifact is never touched. It is the durable recording and the only provenance
/// of the transcript; the TOS object is a temporary transport copy.
/// </para>
/// </remarks>
public sealed class VolcengineTosAudioPublisher : IAsrAudioPublisher
{
    /// <summary>
    /// MeetCap transport policy, not a provider limit claim: at or below this size the audio
    /// travels inline as base64 <c>audio.data</c>, above it through TOS and <c>audio.url</c>.
    /// </summary>
    public const long InlineThresholdBytes = 20L * 1024 * 1024;

    /// <summary>Fixed presigned GET validity required by issue #29.</summary>
    public const int PresignedGetSeconds = 6 * 60 * 60;

    /// <summary>Prefix every staged ASR object lives under, for a prefix-scoped lifecycle rule.</summary>
    public const string ObjectKeyPrefix = "meetcap-asr/";

    private readonly VolcengineTosOptions? _options;

    /// <summary>
    /// Test-only client seam, or <c>null</c> in production. See the internal constructor overload
    /// for why it exists.
    /// </summary>
    private readonly Func<VolcengineTosOptions, ITosClient>? _clientFactory;

    public VolcengineTosAudioPublisher(VolcengineTosOptions? options) => _options = options;

    /// <summary>
    /// Test-only seam that substitutes the TOS client, mirroring the way
    /// <see cref="VolcengineAsrProvider"/> accepts an <see cref="System.Net.Http.HttpMessageHandler"/>
    /// for the same reason.
    /// </summary>
    /// <param name="options">
    /// The TOS configuration, or <c>null</c> to keep the publisher unconfigured. It is passed
    /// through to <paramref name="clientFactory"/> unchanged so a test can assert that the
    /// publisher actually used the configured bucket.
    /// </param>
    /// <param name="clientFactory">
    /// Builds the TOS client from the configuration. A test passes a recording fake so the
    /// transport policy (stream, not buffered bytes; exactly one <c>PutObject</c>; fixed
    /// presigned lifetime; stable bucket and key) is asserted without a bucket, credentials, or
    /// network access. Pass <c>null</c> to build the real client.
    /// </param>
    /// <remarks>
    /// <para>
    /// The real upload, signing and delete cannot be exercised in CI: the environment has no TOS
    /// bucket or credentials, and inventing a bucket URL or a fake success would violate
    /// <c>docs/DEVELOPMENT.md</c> section 7 ("No fake success"). The seam exists so the
    /// <em>shape</em> of the SDK calls <see cref="PublishAsync"/> issues is still proven in CI,
    /// while the real round trip to a live bucket stays the documented manual step in
    /// <c>docs/M1_WINDOWS_VALIDATION.md</c>.
    /// </para>
    /// <para>
    /// This is the same trade-off <see cref="VolcengineAsrProvider"/> already makes for the ASR
    /// HTTP boundary in <c>VolcengineAsrProviderTests</c>. It buys that coverage at the cost of a
    /// second constructor, which is kept <c>internal</c> so the production surface is still the
    /// single public <see cref="VolcengineTosAudioPublisher(VolcengineTosOptions?)"/> overload.
    /// </para>
    /// <para>
    /// Production behaviour is unchanged: <see cref="CreateClient"/> delegates to
    /// <see cref="BuildClient"/> — the real <see cref="TosClientBuilder"/> path — whenever the
    /// factory is <c>null</c>, which is every non-test construction site.
    /// </para>
    /// </remarks>
    internal VolcengineTosAudioPublisher(
        VolcengineTosOptions? options,
        Func<VolcengineTosOptions, ITosClient>? clientFactory)
    {
        _options = options;
        _clientFactory = clientFactory;
    }

    /// <summary>True when TOS is configured, i.e. the oversized path can be served.</summary>
    public bool IsConfigured => _options is not null;

    public async Task<AsrPublishedAudio> PublishAsync(
        AsrFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = new FileInfo(request.InputArtifactPath);
        if (!info.Exists)
        {
            throw new AsrPermanentException(
                "audio.missing",
                $"Audio artifact '{request.InputArtifactPath}' no longer exists, so it cannot be " +
                "published for transcription. Re-import the recording.");
        }

        if (info.Length <= InlineThresholdBytes)
        {
            var bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);
            return new AsrPublishedAudio
            {
                Transport = AsrTransports.Inline,
                InlineBase64 = Convert.ToBase64String(bytes),
                Bytes = info.Length,
            };
        }

        // TOS is optional until this point, and this is the only place the absence is fatal.
        // Splitting, downsampling, switching ASR tier, or opening a streaming request are all
        // explicitly not fallbacks: each would silently change what the user asked for
        // (issue #29, "non-goals").
        if (_options is null)
        {
            throw new AsrConfigurationException(
                $"The audio artifact '{request.InputArtifactPath}' is {info.Length} bytes, above the " +
                $"{InlineThresholdBytes}-byte inline limit, so it must be submitted through TOS. " +
                "Configure [asr.tos] with bucket, region, endpoint, access_key and secret_key " +
                "(docs/CONFIGURATION.md section 9). MeetCap will not split, downsample, or change " +
                "the ASR mode instead.");
        }

        var key = BuildObjectKey(request.JobId, DateTime.UtcNow);
        try
        {
            var client = CreateClient(_options);
            await Task.Run(
                () =>
                {
                    using var stream = new FileStream(
                        info.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.SequentialScan);
                    // Official SDK simple streaming upload, one request per object: the SDK's
                    // simple-upload ceiling is 5 GiB, so multipart is out of scope for this path.
                    client.PutObject(new PutObjectInput
                    {
                        Bucket = _options.Bucket,
                        Key = key,
                        Content = stream,
                        ContentLength = info.Length,
                        ContentType = "audio/wav",
                    });
                },
                cancellationToken).ConfigureAwait(false);

            // The presigned GET URL is minted on its own endpoint when one is configured
            // (asr.tos.presign_endpoint): PreSignedURL is a purely local signing operation,
            // so the internal-endpoint client never needs to be reachable from this
            // machine. The signature binds the URL's host, so the provider-side fetch of
            // that URL resolves over the bucket's internal network and is billed as
            // internal egress (free in cn-beijing/cn-shanghai) instead of public. Without
            // the override the URL is signed on the same endpoint the upload used.
            var presignClient = string.IsNullOrWhiteSpace(_options.PresignEndpoint)
                ? client
                : CreateClient(_options with { Endpoint = _options.PresignEndpoint });

            var signed = await Task.Run(
                () => presignClient.PreSignedURL(new PreSignedURLInput
                {
                    Bucket = _options.Bucket,
                    Key = key,
                    HttpMethod = HttpMethodType.HttpMethodGet,
                    Expires = PresignedGetSeconds,
                }),
                cancellationToken).ConfigureAwait(false);

            return new AsrPublishedAudio
            {
                Transport = AsrTransports.Tos,
                Url = signed.SignedUrl,
                Bytes = info.Length,
                Bucket = _options.Bucket,
                ObjectKey = key,
                RoleTrn = _options.RoleTrn,
            };
        }
        catch (TosServerException ex)
        {
            throw ClassifyPublishFailure(ex);
        }
        catch (Exception ex) when (ex is IOException or TosClientException)
        {
            // TOS staging happens before any provider submit. No ASR task could have been
            // accepted; durable retries may safely try this job again.
            throw new AsrTransientException(
                "tos.transport_failed",
                $"TOS upload or URL signing failed for {_options.Bucket}/{key}. " +
                "Check the TOS endpoint and network; the local audio artifact is intact.",
                ex,
                requestNotSent: true);
        }
    }

    public async Task DeleteAsync(
        AsrPublishedAudio published,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (!published.HasRemoteCopy)
        {
            return;
        }

        if (_options is null)
        {
            throw new AsrConfigurationException(
                $"Object {published.Bucket}/{published.ObjectKey} still needs to be released, but " +
                "[asr.tos] is not configured in this process. Restore the TOS configuration and " +
                "run 'meetcap asr resume' to finish the cleanup; the local transcript is unaffected.");
        }

        try
        {
            var client = CreateClient(_options);
            // Idempotent by contract: TOS DeleteObject succeeds for an absent key, so a retry
            // after a partial failure is always safe and never reports a phantom object.
            await Task.Run(
                () => client.DeleteObject(new DeleteObjectInput
                {
                    Bucket = published.Bucket,
                    Key = published.ObjectKey,
                }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TosClientException or TosServerException)
        {
            // Even a permanent TOS access rejection must remain cleanup debt, never an
            // ASR transcript failure. The operator can correct credentials and resume.
            throw new AsrTransientException(
                "tos.cleanup_failed",
                $"Releasing the staged TOS object {published.Bucket}/{published.ObjectKey} failed. " +
                "The transcript is unaffected; the cleanup stays recorded on the job and is " +
                "retried by a later 'meetcap asr resume'.",
                ex);
        }
    }

    /// <summary>
    /// Translate TOS SDK service errors before they escape the durable ASR job taxonomy.
    /// Only bounded response metadata is displayed: the SDK error message can contain
    /// request details and must never be propagated to CLI / session artifacts verbatim.
    /// </summary>
    private static Exception ClassifyPublishFailure(TosServerException error)
    {
        var detail = $"TOS HTTP {error.StatusCode}, code={error.Code ?? "unknown"}, " +
            $"request_id={error.RequestID ?? "unknown"}.";

        if (error.StatusCode is 401 or 403)
        {
            return new AsrPermanentException(
                "tos.auth_failed",
                $"{detail} Check [asr.tos] access_key/secret_key, bucket permissions and " +
                "region/endpoint; SignatureDoesNotMatch commonly indicates an incorrect " +
                "or mis-copied secret.", error);
        }

        // Rate limiting and request timeouts can clear without configuration changes.
        if (error.StatusCode is 408 or 429 || error.StatusCode >= 500 || error.StatusCode < 400)
        {
            return new AsrTransientException(
                "tos.transport_failed",
                $"{detail} TOS is temporarily unavailable; retry from the durable ASR queue.",
                error,
                requestNotSent: true);
        }

        return new AsrPermanentException(
            "tos.request_rejected",
            $"{detail} TOS rejected the upload or presigned URL request. Verify [asr.tos] " +
            "bucket, region, endpoint and account policy.", error);
    }

    /// <summary>
    /// Builds the object key for a job: <c>meetcap-asr/&lt;shard&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/&lt;job&gt;.wav</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shard is a stable non-zero hash of the job id, not a counter and not a timestamp, so
    /// keys are not purely lexicographically increasing: a prefix listing does not concentrate
    /// every object under one sequential partition.
    /// </para>
    /// <para>
    /// Deriving it from the job id also makes the key reproducible for a given job, which is
    /// what keeps a re-publish after a crash from scattering a second object under a fresh
    /// random name. The key still carries no meeting title, speaker name, or credential, and
    /// the job id it does carry is MeetCap's own opaque identifier.
    /// </para>
    /// </remarks>
    internal static string BuildObjectKey(string jobId, DateTimeOffset utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var shard = ShardFor(jobId);
        // Invariant culture, because a key's layout must not depend on the process locale.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ObjectKeyPrefix}{shard}/{utcNow:yyyy}/{utcNow:MM}/{utcNow:dd}/{jobId}.wav");
    }

    /// <summary>The 16-hex-digit shard for a job id. Stable for the same id, never all zeroes.</summary>
    internal static string ShardFor(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(jobId));
        var shard = Convert.ToHexStringLower(hash.AsSpan(0, 8));
        return shard;
    }

    /// <summary>
    /// Creates the TOS client for one publish or delete, honouring the test-only
    /// <see cref="_clientFactory"/> seam when one was supplied.
    /// </summary>
    /// <remarks>
    /// The factory branch is the only thing the seam changes. With a <c>null</c> factory this
    /// delegates to <see cref="BuildClient"/>, which is exactly the real
    /// <see cref="TosClientBuilder"/> path production has always taken, so the seam cannot alter
    /// production behaviour. <see cref="BuildClient"/> itself is deliberately left untouched: it
    /// stays the single static proof that this adapter reaches the official SDK rather than a
    /// hand-rolled signer.
    /// </remarks>
    private ITosClient CreateClient(VolcengineTosOptions options) =>
        _clientFactory is not null ? _clientFactory(options) : BuildClient(options);

    private static ITosClient BuildClient(VolcengineTosOptions options) =>
        TosClientBuilder.Builder()
            .SetAk(options.AccessKey)
            .SetSk(options.SecretKey)
            .SetRegion(options.Region)
            .SetEndpoint(options.Endpoint)
            .Build();
}

/// <summary>
/// Adapter-owned TOS configuration. Never persisted and never logged: the access key and
/// secret key are resolved through the shared secret resolver and are redacted from
/// <c>config show</c> and the log sinks (<c>docs/CONFIGURATION.md</c> sections 8 and 9).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoleTrn"/> and <see cref="PresignEndpoint"/> are the optional cross-service
/// authorization pair (issue #59): with an IAM role trusted by <c>speech_asr_prod</c> (the
/// <c>AucAccessTosRole</c> flow) and a presign endpoint on the bucket's internal host, the
/// provider-side fetch of <c>audio.url</c> is authorized and billed as internal egress
/// instead of public. Both are non-secret; the TRN may reach <c>request.json</c>.
/// </para>
/// </remarks>
public sealed record VolcengineTosOptions(
    string Bucket,
    string Region,
    string Endpoint,
    string AccessKey,
    string SecretKey,
    string RoleTrn = "",
    string PresignEndpoint = "");
