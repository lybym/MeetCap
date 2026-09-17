namespace MeetCap.Speakers.SherpaOnnx;

using System.Runtime.Versioning;
using MeetCap.Core.Speakers;

/// <summary>
/// sherpa-onnx + 3D-Speaker ERes2Net-base speaker identity provider
/// (<c>docs/ARCHITECTURE.md</c> section 17.3). This is the default local identity
/// stack behind the <c>MeetCap.Speakers</c> boundary. It does not require Python
/// or PyTorch: the sherpa-onnx .NET runtime wraps the ONNX model natively.
/// </summary>
/// <remarks>
/// The provider is resilient by design. A missing model file is reported as a
/// <see cref="SpeakerProviderConfigurationException"/> before any session state is
/// created, so a misconfiguration cannot compromise a recording
/// (<c>docs/DEVELOPMENT.md</c> section 7, <c>docs/ARCHITECTURE.md</c> section 20).
/// At runtime, an extraction failure surfaces as an exception the caller contains;
/// the raw ASR artifacts and recorded audio are never touched by this class.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SherpaOnnxSpeakerIdentityProvider : ISpeakerIdentityProvider, IDisposable
{
    private readonly global::SherpaOnnx.SpeakerEmbeddingExtractor _extractor;
    private readonly string _modelName;
    private readonly string _modelVersion;
    private bool _disposed;

    /// <summary>
    /// Creates the provider. Throws <see cref="SpeakerProviderConfigurationException"/> when
    /// the model is missing or cannot be loaded, so the caller can fail visibly before
    /// touching session state.
    /// </summary>
    public SherpaOnnxSpeakerIdentityProvider(
        string modelPath,
        string modelName,
        string modelVersion,
        int numThreads = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (!File.Exists(modelPath))
        {
            throw new SpeakerProviderConfigurationException(
                $"Speaker identity model not found at '{modelPath}'. Download the 3D-Speaker " +
                $"ERes2Net-base model ('3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx') " +
                $"and place it at the configured path, or set speakers.sherpa_onnx.model_path.");
        }

        var config = new global::SherpaOnnx.SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = Math.Max(1, numThreads),
            Debug = 0,
            Provider = "cpu",
        };

        try
        {
            _extractor = new global::SherpaOnnx.SpeakerEmbeddingExtractor(config);
        }
        catch (Exception ex) when (ex is not SpeakerProviderConfigurationException)
        {
            throw new SpeakerProviderConfigurationException(
                $"Could not load the speaker identity model at '{modelPath}': {ex.Message}");
        }

        _modelName = modelName;
        _modelVersion = modelVersion;
    }

    /// <summary>Stable provider name recorded on every embedding (<c>sherpa_onnx_3dspeaker</c>).</summary>
    public string Name => "sherpa_onnx_3dspeaker";

    public string ModelName => _modelName;

    public string ModelVersion => _modelVersion;

    /// <summary>The embedding dimension produced by this model (192 for ERes2Net-base).</summary>
    public int EmbeddingDimension => _extractor.Dim;

    /// <summary>
    /// Extracts a speaker embedding from a clean audio sample. The extraction runs on
    /// a background thread (it is CPU-bound) so the caller's thread is not blocked.
    /// </summary>
    public Task<ExtractedEmbedding> ExtractAsync(
        SpeakerAudioSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(sample.SampleRate, sample.Samples);
            stream.InputFinished();

            if (!_extractor.IsReady(stream))
            {
                throw new InvalidOperationException(
                    "The speaker embedding extractor produced no output for the given sample. " +
                    "The sample may be too short or silent.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var embedding = _extractor.Compute(stream);

            return new ExtractedEmbedding
            {
                Values = embedding,
                ModelName = _modelName,
                ModelVersion = _modelVersion,
                QualityScore = null,
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Ranks enrolled speakers by cosine similarity to the probe embedding. The best
    /// similarity across all of a speaker's stored embeddings is used, so multiple
    /// enrollments improve identification. The provider owns the similarity metric;
    /// MeetCap owns the threshold + margin policy applied to the ranked list.
    /// </summary>
    public Task<IReadOnlyList<SpeakerCandidate>> IdentifyAsync(
        ExtractedEmbedding probe,
        IReadOnlyList<EnrolledSpeaker> enrolled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(enrolled);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = new List<SpeakerCandidate>(enrolled.Count);
            foreach (var speaker in enrolled)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Best similarity across all of this speaker's embeddings: multiple
                // enrollments per person are compared independently, and the highest
                // score wins (docs/DATA_MODEL.md section 9: "Store multiple embeddings
                // per person").
                var bestScore = 0.0;
                foreach (var emb in speaker.Embeddings)
                {
                    var score = CosineSimilarity.Compute(probe.Values, emb.Values);
                    if (score > bestScore)
                    {
                        bestScore = score;
                    }
                }

                candidates.Add(new SpeakerCandidate
                {
                    SpeakerId = speaker.SpeakerId,
                    DisplayName = speaker.DisplayName,
                    Score = bestScore,
                });
            }

            // Rank by descending similarity so the best candidate is first.
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            return (IReadOnlyList<SpeakerCandidate>)candidates;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _extractor.Dispose();
    }
}
