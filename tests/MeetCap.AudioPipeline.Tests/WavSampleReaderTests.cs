using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public sealed class WavSampleReaderTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "meetcap-reader-" + Guid.NewGuid().ToString("N") + ".wav");
    public void Dispose() => File.Delete(_file);

    [Fact]
    public void FloatStereo_IsDownmixedAndReadOnlyInsideRequestedRange()
    {
        var format = new AudioFormat(48000, 2, 32, AudioSampleFormat.IeeeFloat);
        using (var writer = new BinaryWriter(File.Create(_file)))
        {
            writer.Write(WavHeader.Build(format, 48000 * 8));
            for (var i = 0; i < 48000; i++) { writer.Write(i < 24000 ? float.NaN : 0.8f); writer.Write(0.2f); }
        }
        var (samples, rate) = WavSampleReader.ReadRange(_file, 500, 750);
        Assert.Equal(48000, rate);
        Assert.Equal(12000, samples.Length);
        Assert.All(samples, v => Assert.Equal(0.5f, v));
        Assert.Throws<FormatException>(() => WavSampleReader.ReadRange(_file, 0, 100));
    }

    [Fact]
    public void TruncatedData_IsRejectedInsteadOfReturningAPartialSample()
    {
        File.WriteAllBytes(_file, WavHeader.Build(new AudioFormat(16000, 1, 16, AudioSampleFormat.Pcm), 32000));
        Assert.Throws<FormatException>(() => WavSampleReader.ReadRange(_file, 0, 500));
    }
}
