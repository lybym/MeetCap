using System.Buffers.Binary;
using System.Text;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public class WavHeaderTests
{
    private static readonly AudioFormat Mono48k = new(48_000, 1, 16, AudioSampleFormat.Pcm);
    private static readonly AudioFormat Stereo48kFloat = new(48_000, 2, 32, AudioSampleFormat.IeeeFloat);

    [Fact]
    public void Build_WritesACanonicalRiffWaveHeader()
    {
        var header = WavHeader.Build(Mono48k, 96_000);

        Assert.Equal(WavHeader.Size, header.Length);
        Assert.Equal("RIFF", Ascii(header, 0, 4));
        Assert.Equal(36u + 96_000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal("WAVE", Ascii(header, 8, 4));
        Assert.Equal("fmt ", Ascii(header, 12, 4));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
        Assert.Equal((ushort)Mono48k.Channels, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)));
        Assert.Equal((uint)Mono48k.SampleRate, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)));
        Assert.Equal((uint)Mono48k.AverageBytesPerSecond, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)));
        Assert.Equal((ushort)Mono48k.BlockAlign, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)));
        Assert.Equal((ushort)Mono48k.BitsPerSample, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)));
        Assert.Equal("data", Ascii(header, 36, 4));
        Assert.Equal(96_000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
    }

    [Fact]
    public void Build_MarksIeeeFloatWithFormatTagThree()
    {
        var header = WavHeader.Build(Stereo48kFloat, 0);

        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
    }

    [Theory]
    [InlineData(AudioSampleFormat.Pcm, 16)]
    [InlineData(AudioSampleFormat.IeeeFloat, 32)]
    public void Parse_RoundTripsEveryRecordedSampleFormat(AudioSampleFormat sampleFormat, int bits)
    {
        var format = new AudioFormat(48_000, 2, bits, sampleFormat);

        var parsed = WavHeader.Parse(WavHeader.Build(format, 1_234));

        Assert.True(parsed.IsValid);
        Assert.Equal(format, parsed.Format);
        Assert.Equal(1_234, parsed.DeclaredDataBytes);
    }

    [Fact]
    public void PatchDataLength_OnlyRewritesTheTwoSizeFields()
    {
        var header = WavHeader.Build(Mono48k, 0);
        var before = header.ToArray();

        WavHeader.PatchDataLength(header, 5_760_000);

        Assert.Equal(36u + 5_760_000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(5_760_000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));

        // Everything between the two size fields must be untouched.
        for (var i = 8; i < 40; i++)
        {
            Assert.Equal(before[i], header[i]);
        }
    }

    [Fact]
    public void Parse_RejectsATruncatedHeader()
    {
        var parsed = WavHeader.Parse(WavHeader.Build(Mono48k, 0).AsSpan(0, 20));

        Assert.False(parsed.IsValid);
        Assert.Contains("shorter", parsed.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsMissingSignatures()
    {
        var header = WavHeader.Build(Mono48k, 0);
        Encoding.ASCII.GetBytes("RIFX", header.AsSpan(0, 4));

        var parsed = WavHeader.Parse(header);

        Assert.False(parsed.IsValid);
        Assert.Contains("RIFF", parsed.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnUnsupportedFormatTag()
    {
        var header = WavHeader.Build(Mono48k, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 0x0055);

        var parsed = WavHeader.Parse(header);

        Assert.False(parsed.IsValid);
        Assert.Contains("format tag", parsed.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RefusesADataLengthThatCannotBeDescribed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavHeader.Build(Mono48k, WavHeader.MaxDataBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavHeader.Build(Mono48k, -1));
    }

    private static string Ascii(byte[] buffer, int offset, int length)
        => Encoding.ASCII.GetString(buffer, offset, length);
}
