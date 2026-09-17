namespace MeetCap.Speakers.Tests.Fakes;

using MeetCap.Core.Speakers;

/// <summary>
/// A test double for <see cref="ISpeakerIdentityProvider"/> that produces deterministic
/// embeddings from audio samples and ranks enrolled speakers by cosine similarity.
/// This lets the registry and attribution pipeline be tested without the sherpa-onnx
/// native runtime or the real model
/// (<c>docs/DEVELOPMENT.md</c> section 7: mock the boundary for CI).
/// </summary>
public sealed class FakeSpeakerIdentityProvider : ISpeakerIdentityProvider
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
    /// Default extraction: produces a deterministic 4-dim embedding by hashing the
    /// sample bytes. Two samples from the same source produce the same direction, so
    /// cosine similarity is meaningful for tests.
    /// </summary>
    private static float[] DefaultExtract(SpeakerAudioSample sample)
    {
        // Use the sample's content to produce a stable embedding vector.
        var sum = 0f;
        foreach (var v in sample.Samples) sum += v;
        var len = (float)Math.Sqrt(sample.Samples.Length);
        var norm = len > 0 ? sum / len : 0;

        // Produce a 4-dim unit-ish vector.
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
