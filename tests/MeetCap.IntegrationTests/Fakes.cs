using MeetCap.Core.Asr;
using MeetCap.Core.Media;

namespace MeetCap.IntegrationTests;

/// <summary>
/// Media boundary stub. Real FFprobe/FFmpeg behaviour is covered by
/// MeetCap.AudioPipeline.Tests; the integration tests care about what the import
/// pipeline does with the answers.
/// </summary>
internal sealed class FakeMediaPipeline : IMediaPipeline
{
    /// <summary>Describes the imported source file.</summary>
    public Func<string, MediaInfo> SourceDescriptor { get; set; } = path => Source(path, "wav", "pcm_s16le", 16000, 1);

    /// <summary>Every normalization the pipeline was asked to perform.</summary>
    public List<string> Normalizations { get; } = new();

    public static MediaInfo Source(
        string path,
        string format,
        string codec,
        int sampleRate,
        int channels,
        long durationMs = 754_000) => new()
    {
        Path = path,
        FormatName = format,
        DurationMs = durationMs,
        ByteLength = new FileInfo(path).Length,
        AudioCodec = codec,
        SampleRateHz = sampleRate,
        Channels = channels,
        BitDepth = 16,
        BitRate = 256_000,
    };

    public static MediaInfo Target(string path) => Source(path, "wav", "pcm_s16le", 16000, 1);

    public MediaInfo Inspect(string path)
    {
        // The normalized derivative must satisfy the ASR target shape, otherwise the
        // import is expected to fail.
        if (string.Equals(Path.GetFileName(path), "normalized.wav", StringComparison.OrdinalIgnoreCase))
        {
            return Target(path);
        }

        return SourceDescriptor(path);
    }

    public Task NormalizeAsync(
        string inputPath,
        string outputPath,
        MediaNormalizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        Normalizations.Add($"{inputPath} -> {outputPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, new byte[64]);
        return Task.CompletedTask;
    }
}

/// <summary>Provider boundary stub. No integration test reaches the live service.</summary>
internal sealed class FakeAsrProvider : IAsrProvider
{
    private readonly Queue<AsrPollResult> _polls = new();

    public string Name => "volcengine";

    public List<AsrFileRequest> Submissions { get; } = new();

    public int PollCount { get; private set; }

    public Func<AsrFileRequest, AsrSubmission>? OnSubmit { get; set; }

    public void EnqueuePoll(params AsrPollResult[] results)
    {
        foreach (var result in results)
        {
            _polls.Enqueue(result);
        }
    }

    public Task<AsrSubmission> SubmitFileAsync(AsrFileRequest request, CancellationToken cancellationToken = default)
    {
        Submissions.Add(request);
        if (OnSubmit is not null)
        {
            return Task.FromResult(OnSubmit(request));
        }

        return Task.FromResult(new AsrSubmission
        {
            ProviderRequestId = request.ProviderRequestId,
            SanitizedRequestJson = "{\"provider\":\"volcengine\",\"speaker_info_requested\":true}",
        });
    }

    public Task<AsrPollResult> GetResultAsync(
        AsrSubmission submission,
        AsrFileRequest request,
        CancellationToken cancellationToken = default)
    {
        PollCount++;
        return Task.FromResult(_polls.Count > 0 ? _polls.Dequeue() : AsrPollResult.Pending());
    }
}
