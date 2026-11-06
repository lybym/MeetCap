using MeetCap.Core.Asr;
using MeetCap.Core.Media;
using MeetCap.Core.Speakers;
using MeetCap.Speakers;
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

    /// <summary>Every compression the pipeline was asked to perform (issue #53).</summary>
    public List<string> Compressions { get; } = new();

    /// <summary>When true, compression fails, simulating an FFmpeg encode failure.</summary>
    public bool FailCompression { get; set; }

    /// <summary>
    /// Offset added to the derivative's reported duration versus the source, so a test can
    /// script a duration mismatch that exceeds the verification tolerance (issue #53).
    /// </summary>
    public long DerivativeDurationOffsetMs { get; set; }

    private string? _lastDerivativePath;
    private long _compressedSourceDurationMs;

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
        // The compressed derivative is recognized as the file CompressAsync just wrote, so its
        // inspected shape reflects the codec the plan selected and a duration that matches the
        // source within the verification tolerance.
        if (_lastDerivativePath is not null && path == _lastDerivativePath)
        {
            return Source(path, "mp3", "mp3", 16000, 1, _compressedSourceDurationMs + DerivativeDurationOffsetMs);
        }

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

    public Task CompressAsync(
        string inputPath,
        string outputPath,
        MediaCompressionPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (FailCompression)
        {
            throw new MediaProbeException($"FFmpeg could not compress '{inputPath}': simulated failure");
        }

        Compressions.Add($"{inputPath} -> {outputPath} ({plan.Codec}/{plan.TargetBitrateKbps}kbps)");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // A small "compressed" file: small enough that inline-vs-TOS selection (20 MiB) keeps
        // it inline, which is what the inline-threshold-shift assertion relies on.
        File.WriteAllBytes(outputPath, new byte[48]);
        _compressedSourceDurationMs = Inspect(inputPath).DurationMs;
        _lastDerivativePath = outputPath;
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

    public List<string> PolledRequestIds { get; } = new();

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
            ["model"] = "bigmodel",
            ["resource_id"] = "volc.seedasr.auc",
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
        PolledRequestIds.Add(submission.ProviderRequestId);
        return Task.FromResult(_polls.Count > 0 ? _polls.Dequeue() : AsrPollResult.Pending());
    }
}

/// <summary>
/// Speaker identity provider boundary stub. Real sherpa-onnx + 3D-Speaker validation is
/// NOT verified in CI: the environment has no model file, and <c>docs/DEVELOPMENT.md</c>
/// section 7 forbids claiming otherwise. This fake produces deterministic embeddings and
/// ranks enrolled speakers by cosine similarity, so the attribution pipeline can be
/// driven end-to-end against ASR-produced transcripts without the native runtime.
/// </summary>
internal sealed class FakeSpeakerIdentityProvider : ISpeakerIdentityProvider
{
    private readonly Func<SpeakerAudioSample, float[]> _extract;

    public FakeSpeakerIdentityProvider(Func<SpeakerAudioSample, float[]>? extract = null)
    {
        _extract = extract ?? DefaultExtract;
    }

    public string Name => "fake";
    public string ModelName => "fake-model";
    public string ModelVersion => "1";
    public int EmbeddingDimension => 4;

    public Task<ExtractedEmbedding> ExtractAsync(
        SpeakerAudioSample sample,
        CancellationToken cancellationToken = default)
    {
        var values = _extract(sample);
        return Task.FromResult(new ExtractedEmbedding
        {
            Values = values,
            ModelName = ModelName,
            ModelVersion = ModelVersion,
        });
    }

    public Task<IReadOnlyList<SpeakerCandidate>> IdentifyAsync(
        ExtractedEmbedding probe,
        IReadOnlyList<EnrolledSpeaker> enrolled,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<SpeakerCandidate>(enrolled.Count);
        foreach (var speaker in enrolled)
        {
            var best = 0.0;
            foreach (var emb in speaker.Embeddings)
            {
                var score = CosineSimilarity.Compute(probe.Values, emb.Values);
                if (score > best) best = score;
            }

            candidates.Add(new SpeakerCandidate
            {
                SpeakerId = speaker.SpeakerId,
                DisplayName = speaker.DisplayName,
                Score = best,
            });
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return Task.FromResult<IReadOnlyList<SpeakerCandidate>>(candidates);
    }

    /// <summary>
    /// Default extraction: produces a deterministic 4-dim embedding by hashing the sample
    /// bytes. Two samples from the same source produce the same direction, so cosine
    /// similarity is meaningful for tests.
    /// </summary>
    private static float[] DefaultExtract(SpeakerAudioSample sample)
    {
        var sum = 0f;
        foreach (var v in sample.Samples) sum += v;
        var len = (float)Math.Sqrt(sample.Samples.Length);
        var norm = len > 0 ? sum / len : 0;
        var seed = (int)(norm * 1000) ^ sample.Samples.Length;
        var rand = new Random(seed);
        var values = new float[4];
        for (var i = 0; i < 4; i++) values[i] = (float)(rand.NextDouble() * 2 - 1);
        return values;
    }

    /// <summary>Produces a provider that extracts a fixed embedding regardless of input.</summary>
    public static FakeSpeakerIdentityProvider WithFixedEmbedding(float[] embedding) =>
        new(_ => embedding);
}
