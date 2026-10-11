namespace MeetCap.Cli.Commands;

using System.Text.Json;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Sessions;
using MeetCap.Core.Speakers;
using MeetCap.Persistence.Storage;

internal static class SessionSpeakerSampleReader
{
    internal static SpeakerAudioSample? Extract(SessionArtifactPaths paths, string source,
        string label, long startMs, long endMs)
    {
        if (source == "import")
        {
            var document = new FileSessionArtifactWriter().ReadSessionDocument(paths);
            // Follow the durable mapping, including imports already in the target shape.
            var artifact = document?.SourceArtifacts.OrderByDescending(a => a.Role == SourceArtifact.Roles.Normalized)
                .FirstOrDefault();
            return artifact is null ? null : Read(artifact.StoredPath, startMs, endMs, paths.SessionId, label, startMs, endMs);
        }

        if (source is not ("mic" or "loopback")) return null;
        var directory = Path.Combine(paths.AsrBatchesDirectory, source);
        if (!Directory.Exists(directory)) return null;
        foreach (var wav in Directory.EnumerateFiles(directory, "batch-*.wav").OrderBy(p => p, StringComparer.Ordinal))
        {
            var manifest = Path.ChangeExtension(wav, ".json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var first = doc.RootElement.GetProperty("start_ms").GetInt64();
                var last = doc.RootElement.GetProperty("end_ms").GetInt64();
                if (startMs < first || endMs > last) continue;
                var sample = Read(wav, startMs - first, endMs - first, paths.SessionId, label, startMs, endMs);
                if (sample is not null) return sample;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A malformed/unreadable artifact never justifies guessing an identity.
                continue;
            }
        }
        return null;
    }

    private static SpeakerAudioSample? Read(string path, long first, long last, string sessionId,
        string label, long startMs, long endMs)
    {
        try
        {
            var (samples, rate) = WavSampleReader.ReadRange(path, first, last);
            return new SpeakerAudioSample
            {
                Samples = samples, SampleRate = rate, SourceSessionId = sessionId,
                SpeakerLabel = label, StartMs = startMs, EndMs = endMs,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }
}
