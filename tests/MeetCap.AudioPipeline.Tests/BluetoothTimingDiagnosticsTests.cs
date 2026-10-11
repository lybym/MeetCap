using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public class BluetoothTimingDiagnosticsTests
{
    [Fact]
    public async Task SystemLoopback_RepeatedPositionAnomalies_PreserveCountsGapAndDurableAudio()
    {
        var format = TestAudio.Formats.Mono48kPcm;
        using var harness = new SessionHarness(chunkSeconds: 1, bufferSeconds: 30, mode: SessionModes.Online);
        var mic = new FakeCaptureSource(format, harness.Device);
        var loopback = new FakeCaptureSource(format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.Enqueue(mic);
        harness.Sources.EnqueueLoopback(loopback);
        var session = harness.Service.PrepareSession("Bluetooth position diagnostics");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => mic.StartCount == 1 && loopback.StartCount == 1));

        TestAudio.EmitSeconds(mic, format, 0, milliseconds: 1_200);
        // Reproduce the real Sony source-clock shape, without changing the mode's
        // declared clock: 480 audio frames, but only 160 position units per buffer.
        for (var i = 0; i < 100; i++)
            loopback.Emit(Packet(i * 160, qpcMs: i * 10));
        var resumePosition = 99 * 160 + 480 + 2_880;
        for (var i = 0; i < 10; i++)
            loopback.Emit(Packet(resumePosition + i * 480, qpcMs: 1_060 + i * 10));
        cancellation.Cancel();
        var outcome = await Wait.ForAsync(run, timeoutMs: 60_000, "the recording session");

        using var stream = new FileStream(paths.EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var events = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToList();
        var anomalies = events.Where(e => e.TryGetProperty("reason", out var reason)
            && reason.GetString() == "device_position_backwards").ToList();
        Assert.Equal(99, anomalies.Sum(e => e.GetProperty("count").GetInt32()));
        Assert.Equal(2, anomalies.Count); // first diagnostic and stop tail
        Assert.All(anomalies, e => Assert.Equal(AudioSources.Loopback, e.GetProperty("source").GetString()));
        Assert.Equal(99 * 160, anomalies[^1].GetProperty("device_position_frames").GetInt64());
        Assert.Equal(9_900_000, anomalies[^1].GetProperty("qpc_position_ticks").GetInt64());

        var gap = Assert.Single(events, e => e.GetProperty("event").GetString() == SessionEventNames.CaptureGap);
        Assert.Equal(60, gap.GetProperty("gap_ms").GetInt64());
        Assert.Equal(1_000, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(1_060, gap.GetProperty("gap_end_ms").GetInt64());
        Assert.Equal(1_060, gap.GetProperty("at_ms").GetInt64());
        Assert.Equal(1, outcome.GapCount);
        Assert.Equal(60, outcome.GapTotalMs);
        var auditGap = Assert.Single(new SessionGapAuditor(harness.Database).Audit(session.SessionId).Gaps);
        Assert.Equal(gap.GetProperty("gap_start_ms").GetInt64(), auditGap.StartMs);
        Assert.Equal(gap.GetProperty("gap_end_ms").GetInt64(), auditGap.EndMs);

        var chunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback).ToList();
        Assert.Equal(new long[] { 0, 1_060 }, chunks.Select(c => c.StartMs));
        Assert.Equal(new long[] { 1_000, 1_160 }, chunks.Select(c => c.EndMs));
        Assert.All(chunks, c => Assert.Equal(ChunkStates.Closed, c.Status));
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.part"));
        Assert.True(SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error), error);
        var micHealth = manifest!.TrackHealth.Single(h => h.Source == AudioSources.Mic);
        Assert.False(micHealth.Degraded);
        Assert.Equal(0, micHealth.GapCount);
        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.False(loopbackHealth.AudioContent!.AllSilent);
        Assert.Equal(0, loopbackHealth.BufferHealth.DroppedPackets);
        Assert.Equal(0, loopbackHealth.BufferHealth.StallEvents);

        AudioPacket Packet(long position, long qpcMs)
        {
            var sample = TestAudio.Packet(format, position, 480, AudioSource.Loopback);
            return new AudioPacket(AudioSource.Loopback, format, sample.Data, position,
                qpcMs * 10_000, DateTimeOffset.UnixEpoch);
        }
    }
}
