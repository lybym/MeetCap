namespace MeetCap.Asr.Batching;

using System.Text.Json;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Asr;

/// <summary>Refuses ambiguous legacy capture batches before billing or timestamp normalization.</summary>
internal static class BatchTimelineGuard
{
    public const string ErrorCode = "asr.timeline_ambiguous";

    public static string? Check(AsrJob job, string inputPath)
    {
        if (!job.InputArtifact.Replace('\\', '/').StartsWith("asr/batches/", StringComparison.Ordinal))
            return null; // imported recordings have their own media contract

        try
        {
            using var wav = File.OpenRead(inputPath);
            var header = new byte[WavHeader.Size];
            wav.ReadExactly(header);
            var parsed = WavHeader.Parse(header);
            if (!parsed.IsValid || parsed.Format is null)
                return Diagnostic("The capture batch WAV header is unreadable.");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(inputPath, ".json")));
            var chunks = manifest.RootElement.GetProperty("chunks");
            if (chunks.GetArrayLength() == 0) return Diagnostic("The capture batch has no source mapping.");
            long? previousEnd = null;
            foreach (var chunk in chunks.EnumerateArray())
            {
                var start = chunk.GetProperty("start_ms").GetInt64();
                var end = chunk.GetProperty("end_ms").GetInt64();
                var bytes = chunk.GetProperty("data_bytes").GetInt64();
                var audioMs = (double)bytes / parsed.Format.BlockAlign * 1000 / parsed.Format.SampleRate;
                if ((previousEnd is { } prior && prior != start) ||
                    Math.Abs(end - start - audioMs) > 2)
                    return Diagnostic("The source mapping contains a gap, overlap or an intra-chunk timing ambiguity.");
                previousEnd = end;
            }
            var batchAudioMs = (double)(wav.Length - WavHeader.Size) / parsed.Format.BlockAlign * 1000 / parsed.Format.SampleRate;
            if (Math.Abs(job.EndMs - job.StartMs - batchAudioMs) > 2)
                return Diagnostic("The batch timeline span differs from its captured frame duration.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Diagnostic($"The batch timeline could not be verified: {ex.Message}");
        }
    }

    private static string Diagnostic(string reason) => reason +
        " Original chunks, batch and any raw provider response are retained. Inspect the session gap events " +
        "and import explicitly selected contiguous source spans to recover speech; do not retry this " +
        "ambiguous batch or assume an imported span preserves its old session placement.";
}
