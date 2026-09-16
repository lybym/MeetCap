using MeetCap.Core.Asr;
using MeetCap.Core.Media;
using System.Text.Json.Nodes;

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

    /// <summary>When true, normalization fails, simulating an FFmpeg failure mid-import.</summary>
    public bool FailNormalization { get; set; }

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
        if (FailNormalization)
        {
            throw new MediaProbeException($"FFmpeg could not normalize '{inputPath}': simulated failure");
        }

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

        // Mirrors the contract the real adapter's sanitized metadata follows: provider facts plus
        // the audio's size, never the audio itself and never a credential. Keeping the shape
        // realistic is what makes the import-level sanitization assertions meaningful.
        var inlineBytes = new FileInfo(request.InputArtifactPath).Length;
        var sanitized = new JsonObject
        {
            ["provider"] = Name,
            ["endpoint_tier"] = request.ServiceTier,
            ["resource_id"] = "volc.bigasr.auc",
            ["job_id"] = request.JobId,
            ["session_id"] = request.SessionId,
            ["source"] = request.Source,
            ["duration_ms"] = request.DurationMs,
            ["provider_request_id"] = request.ProviderRequestId,
            ["speaker_info_requested"] = request.RequestSpeakerInfo,
            ["audio"] = new JsonObject
            {
                ["format"] = request.AudioFormat,
                ["inline_bytes"] = inlineBytes,
            },
            ["request"] = new JsonObject
            {
                ["model_name"] = "bigmodel",
                ["enable_speaker_info"] = request.RequestSpeakerInfo,
            },
        };

        return Task.FromResult(new AsrSubmission
        {
            ProviderRequestId = request.ProviderRequestId,
            SanitizedRequestJson = sanitized.ToJsonString(),
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
