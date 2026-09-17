using MeetCap.Core.Speakers;
using Xunit;

namespace MeetCap.Speakers.Tests;

public class CosineSimilarityTests
{
    [Fact]
    public void IdenticalVectors_ReturnOne()
    {
        var a = new float[] { 1, 0, 0, 0 };
        Assert.Equal(1.0, CosineSimilarity.Compute(a, a), precision: 6);
    }

    [Fact]
    public void OrthogonalVectors_ReturnZero()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { 0, 1 };
        Assert.Equal(0.0, CosineSimilarity.Compute(a, b), precision: 6);
    }

    [Fact]
    public void OppositeVectors_ReturnZero_Clamped()
    {
        // Cosine of opposite vectors is -1, but we clamp to [0, 1].
        var a = new float[] { 1, 0 };
        var b = new float[] { -1, 0 };
        Assert.Equal(0.0, CosineSimilarity.Compute(a, b), precision: 6);
    }

    [Fact]
    public void ZeroVector_ReturnsZero()
    {
        var a = new float[] { 0, 0, 0 };
        var b = new float[] { 1, 2, 3 };
        Assert.Equal(0.0, CosineSimilarity.Compute(a, b), precision: 6);
    }

    [Fact]
    public void DifferentLengths_ReturnsZero()
    {
        var a = new float[] { 1, 2, 3 };
        var b = new float[] { 1, 2 };
        Assert.Equal(0.0, CosineSimilarity.Compute(a, b));
    }

    [Fact]
    public void KnownSimilarity()
    {
        // (1,1) and (1,0): cos = 1/sqrt(2) ≈ 0.7071
        var a = new float[] { 1, 0 };
        var b = new float[] { 1, 1 };
        Assert.Equal(1.0 / Math.Sqrt(2), CosineSimilarity.Compute(a, b), precision: 6);
    }
}
