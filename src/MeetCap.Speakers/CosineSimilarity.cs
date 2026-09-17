namespace MeetCap.Speakers;

using System.Runtime.CompilerServices;

/// <summary>
/// Cosine similarity for speaker embeddings. For 3D-Speaker ERes2Net-base the
/// standard similarity metric is cosine similarity in [0, 1] (clamped at 0).
/// </summary>
/// <remarks>
/// This is the shared similarity computation used by identity providers and the
/// attribution pipeline. It is pure math with no infrastructure dependency
/// (<c>docs/ARCHITECTURE.md</c> section 17.3).
/// </remarks>
public static class CosineSimilarity
{
    /// <summary>
    /// Cosine similarity clamped to [0, 1]. Returns 0 for degenerate (zero) vectors.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Compute(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        if (denom == 0)
        {
            return 0;
        }

        var cos = dot / denom;
        return cos < 0 ? 0 : cos;
    }

    /// <summary>Overload accepting <c>float[]</c> for convenience.</summary>
    public static double Compute(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Compute(a.AsSpan(), b.AsSpan());
    }
}
