namespace MeetCap.Asr.Volcengine;

using MeetCap.Core.Asr;
using TOS;
using TOS.Error;
using TOS.Model;

/// <summary>Infrastructure-only publisher using the official Volcengine TOS .NET SDK.</summary>
public sealed class VolcengineTosAudioPublisher : IAsrAudioPublisher
{
    public const long InlineThresholdBytes = 20L * 1024 * 1024;
    public const int PresignedGetSeconds = 6 * 60 * 60;
    private readonly VolcengineTosOptions? _options;

    public VolcengineTosAudioPublisher(VolcengineTosOptions? options) => _options = options;

    public async Task<AsrPublishedAudio> PublishAsync(AsrFileRequest request, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(request.InputArtifactPath);
        if (!info.Exists) throw new AsrPermanentException("audio.missing", $"Audio artifact '{request.InputArtifactPath}' no longer exists.");
        if (info.Length <= InlineThresholdBytes)
        {
            var bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);
            return new AsrPublishedAudio { Transport = "inline", InlineBase64 = Convert.ToBase64String(bytes), Bytes = info.Length };
        }
        if (_options is null) throw new AsrConfigurationException("Audio is larger than 20 MiB and requires [asr.tos]. Configure bucket, region, endpoint, access_key and secret_key; MeetCap will not split, downsample, or switch ASR modes.");

        var key = $"meetcap-asr/{Random.Shared.NextInt64():x16}/{DateTime.UtcNow:yyyy/MM/dd}/{request.JobId}.wav";
        try
        {
            var client = TosClientBuilder.Builder().SetAk(_options.AccessKey).SetSk(_options.SecretKey).SetRegion(_options.Region).SetEndpoint(_options.Endpoint).Build();
            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Official SDK simple streaming upload; no ACL is set, so the object remains private.
            client.PutObject(new PutObjectInput { Bucket = _options.Bucket, Key = key, Content = stream, ContentLength = info.Length, ContentType = "audio/wav" });
            var signed = client.PreSignedURL(new PreSignedURLInput { Bucket = _options.Bucket, Key = key, HttpMethod = HttpMethodType.HttpMethodGet, Expires = PresignedGetSeconds });
            return new AsrPublishedAudio { Transport = "tos", Url = signed.SignedUrl, Bytes = info.Length, Bucket = _options.Bucket, ObjectKey = key };
        }
        catch (Exception ex) when (ex is IOException or TosClientException)
        {
            throw new AsrTransientException("tos.unavailable", "TOS upload or URL signing failed; the local audio artifact remains durable and the ASR job can retry.", ex);
        }
    }
}

public sealed record VolcengineTosOptions(string Bucket, string Region, string Endpoint, string AccessKey, string SecretKey);
