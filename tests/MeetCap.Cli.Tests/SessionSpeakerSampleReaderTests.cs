using System.Text;
using System.Text.Json;
using MeetCap.Cli.Commands;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.Cli.Tests;

public sealed class SessionSpeakerSampleReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meetcap-sample-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData("normalized")]
    [InlineData("original")]
    public void Import_FollowsStoredArtifact_AndReadsExactRange(string role)
    {
        var paths = new SessionArtifactPaths(_root, "ses_1");
        var wav = Path.Combine(paths.ImportAudioDirectory, "stored.wav");
        WriteWav(wav, 12345);
        Directory.CreateDirectory(paths.SessionDirectory);
        File.WriteAllText(paths.SessionJson, JsonSerializer.Serialize(new
        {
            session_id = "ses_1", title = "test", mode = "import", source_type = "import",
            source_artifacts = new[] { new { role, stored_path = wav, original_path = "unavailable.wav", file_name = "stored.wav", sha256 = "test" } },
        }));
        var sample = SessionSpeakerSampleReader.Extract(paths, "import", "speaker_1", 200, 700);
        Assert.NotNull(sample);
        Assert.Equal(8000, sample.Samples.Length);
        Assert.Equal(16000, sample.SampleRate);
        Assert.All(sample.Samples, v => Assert.Equal(12345 / 32768f, v));
        Assert.Equal(200, sample.StartMs);
        Assert.Equal(700, sample.EndMs);
        Assert.Null(SessionSpeakerSampleReader.Extract(paths, "import", "speaker_1", 700, 1200));
    }

    [Fact]
    public void Live_UsesRequestedTrackAndBatchOffset()
    {
        var paths = new SessionArtifactPaths(_root, "ses_1");
        foreach (var source in new[] { "mic", "loopback" })
        {
            var wav = Path.Combine(paths.AsrBatchesDirectory, source, "batch-000001.wav");
            WriteWav(wav, source == "mic" ? (short)0 : (short)16384);
            File.WriteAllText(Path.ChangeExtension(wav, ".json"), "{\"start_ms\":1000,\"end_ms\":2000}");
        }
        var sample = SessionSpeakerSampleReader.Extract(paths, "loopback", "speaker_1", 1200, 1700);
        Assert.NotNull(sample);
        Assert.All(sample.Samples, v => Assert.Equal(0.5f, v));
        Assert.Null(SessionSpeakerSampleReader.Extract(paths, "loopback", "speaker_1", 500, 1500));
    }

    private static void WriteWav(string path, short value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(32000);
        for (var i = 0; i < 16000; i++) writer.Write(value);
    }
}
