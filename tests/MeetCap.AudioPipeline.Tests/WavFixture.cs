using System.Text;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// Writes a minimal PCM WAV file so inspection tests do not need FFmpeg to produce
/// their input. That keeps the "does FFmpeg work" question separate from "can we make
/// a test fixture".
/// </summary>
internal static class WavFixture
{
    public static string WriteSine(
        string path,
        int sampleRateHz,
        int channels,
        double seconds,
        short amplitude = 8000)
    {
        var frames = (int)(sampleRateHz * seconds);
        var bytesPerSample = 2;
        var dataBytes = frames * channels * bytesPerSample;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRateHz);
        writer.Write(sampleRateHz * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write((short)(bytesPerSample * 8));

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);

        for (var frame = 0; frame < frames; frame++)
        {
            var tone = (short)(amplitude * Math.Sin(2 * Math.PI * 440 * frame / sampleRateHz));
            for (var channel = 0; channel < channels; channel++)
            {
                writer.Write(tone);
            }
        }

        writer.Flush();
        return path;
    }
}
