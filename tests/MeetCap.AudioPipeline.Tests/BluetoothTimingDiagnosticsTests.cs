using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public class BluetoothTimingDiagnosticsTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    [Fact]
    public async Task FailedReplacementStarts_AreBoundedAndReportTheCauseWithoutFalseRestore()
    {
        using var harness = new SessionHarness(deviceRecoverySeconds: 2, mode: SessionModes.Online);
        for (var i = 0; i < 3; i++) harness.Sources.Enqueue(new FakeCaptureSource(Format, harness.Device)
        {
            StartError = new InvalidOperationException("Endpoint could not start",
                new System.Runtime.InteropServices.COMException("Access is denied", unchecked((int)0x80070005))),
        });
        var loop = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.EnqueueLoopback(loop);
        var session = harness.Service.PrepareSession("Failed replacement starts");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        Assert.True(await Wait.UntilAsync(() => ReadEvents(paths).Any(e =>
            e.GetProperty("event").GetString() == SessionEventNames.CaptureDeviceLostFatal), timeoutMs: 15_000));
        Assert.False(run.IsCompleted); // independent loopback is still available
        TestAudio.EmitSeconds(loop, Format, 0, milliseconds: 1_000, AudioSource.Loopback);
        stop.Cancel();
        await Wait.ForAsync(run, timeoutMs: 60_000, "failed starts to finish");
        var events = ReadEvents(paths);
        Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == SessionEventNames.CaptureDeviceRestored);
        var failures = events.Where(e => e.GetProperty("event").GetString() == SessionEventNames.CaptureDeviceLost).ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(failures, e =>
        {
            Assert.Contains("Access is denied", e.GetProperty("detail").GetString());
            Assert.Contains("HRESULT=0x80070005", e.GetProperty("detail").GetString());
        });
        var fatal = Assert.Single(events, e => e.GetProperty("event").GetString() == SessionEventNames.CaptureDeviceLostFatal);
        Assert.Equal(2, fatal.GetProperty("count").GetInt32());
        Assert.Single(harness.Database.Chunks.ListForSession(session.SessionId), c => c.Source == AudioSource.Loopback);
        Assert.DoesNotContain(harness.Database.Chunks.ListForSession(session.SessionId), c => c.Source == AudioSource.Mic);
    }

    [Theory]
    [InlineData(CaptureClock.Qpc)]
    [InlineData(CaptureClock.DevicePosition)]
    public async Task RestartBoundary_FollowsOldQueuedAudio_AndIgnoresDelayedConsumption(CaptureClock clock)
    {
        using var harness = new SessionHarness(chunkSeconds: 60, mode: SessionModes.Online);
        var mic = new FakeCaptureSource(Format, harness.Device);
        var first = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = clock };
        var second = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = clock };
        harness.Sources.Enqueue(mic);
        harness.Sources.EnqueueLoopback(first);
        harness.Sources.EnqueueLoopback(second);
        using var releaseConsumer = new ManualResetEventSlim();
        var consumerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = 0;
        var session = harness.PrepareSessionDirect("Queued restart boundary", online: true, afterPacketWritten: () =>
        {
            if (Interlocked.Increment(ref written) != 1) return;
            consumerBlocked.TrySetResult();
            Assert.True(releaseConsumer.Wait(TimeSpan.FromSeconds(15)), "consumer release timed out");
        });
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        try
        {
            Assert.True(await Wait.UntilAsync(() => first.StartCount == 1));
            first.Emit(At(0, 0, 48_000));
            await Wait.ForAsync(consumerBlocked.Task, timeoutMs: 5_000, "consumer to pause");
            first.Emit(At(48_000, 1_000, 48_000)); // old stream, still queued
            harness.Clock.Advance(TimeSpan.FromSeconds(2));
            harness.Sources.OnCreateLoopback = () => harness.Clock.Advance(TimeSpan.FromSeconds(3));
            first.Fail(new InvalidOperationException("endpoint removed with old packets queued"));
            Assert.True(await Wait.UntilAsync(() => second.StartCount == 1, timeoutMs: 15_000));
            harness.Clock.Advance(TimeSpan.FromSeconds(14));
            second.Emit(At(0, 19_000, 480));
            // Delay the consumer by a further minute after the capture callback.
            stop.Cancel();
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        finally
        {
            releaseConsumer.Set();
            stop.Cancel();
        }
        var outcome = await Wait.ForAsync(run, timeoutMs: 60_000, "queued recording to drain");
        Assert.Equal(17_000, outcome.GapTotalMs);
        Assert.Equal(1, outcome.GapCount);
        var chunks = harness.Database.Chunks.ListForSession(session.SessionId).Where(c => c.Source == AudioSource.Loopback).ToList();
        Assert.Equal(new long[] { 0, 19_000 }, chunks.Select(c => c.StartMs));
        Assert.Equal(new long[] { 2_000, 19_010 }, chunks.Select(c => c.EndMs));
        var auditGap = Assert.Single(new SessionGapAuditor(harness.Database).Audit(session.SessionId).Gaps);
        Assert.Equal(17_000, auditGap.GapMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RestartWithUnusableQpc_EndsOnlyThatTrackAndKeepsItsPreviousAudioDurable(int failure)
    {
        using var harness = new SessionHarness(chunkSeconds: 60, mode: SessionModes.Online);
        var mic = new FakeCaptureSource(Format, harness.Device);
        var first = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = CaptureClock.Qpc };
        var second = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = CaptureClock.Qpc };
        harness.Sources.Enqueue(mic);
        harness.Sources.EnqueueLoopback(first);
        harness.Sources.EnqueueLoopback(second);
        var session = harness.Service.PrepareSession("Unusable restart timestamp");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1));
        TestAudio.EmitSeconds(first, Format, 0, milliseconds: 10_000);
        harness.Sources.OnCreateLoopback = () => harness.Clock.Advance(TimeSpan.FromSeconds(3));
        first.Fail(new InvalidOperationException("endpoint removed"));
        Assert.True(await Wait.UntilAsync(() => second.StartCount == 1, timeoutMs: 15_000));
        harness.Clock.Advance(TimeSpan.FromSeconds(14));
        var sample = At(0, 27_000, 480);
        second.Emit(new AudioPacket(AudioSource.Loopback, Format, sample.Data, 0,
            failure == 0 ? null : failure == 2 ? 0 : sample.QpcPositionTicks,
            DateTimeOffset.UnixEpoch, failure == 1 ? AudioBufferFlags.TimestampError : AudioBufferFlags.None));
        Assert.True(await Wait.UntilAsync(() => ReadEvents(paths).Any(e =>
            e.GetProperty("event").GetString() == SessionEventNames.CaptureTimelineUnusable)));
        Assert.False(run.IsCompleted); // the independent microphone is still active
        TestAudio.EmitSeconds(mic, Format, 0, milliseconds: 1_000);
        stop.Cancel();
        await Wait.ForAsync(run, timeoutMs: 60_000, "unusable restart to close");
        Assert.True(SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error), error);
        var health = manifest!.TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.Equal("timeline_unusable", health.EndReason);
        Assert.True(health.Degraded);
        Assert.Equal(17_000, health.GapTotalMs);
        Assert.Equal(1, health.GapCount);
        Assert.False(manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic).Degraded);
        var chunk = Assert.Single(harness.Database.Chunks.ListForSession(session.SessionId), c => c.Source == AudioSource.Loopback);
        Assert.Equal(ChunkStates.Closed, chunk.Status);
        Assert.Equal(10_000, chunk.EndMs);
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.part"));
        var diagnostic = Assert.Single(ReadEvents(paths), e => e.GetProperty("event").GetString() == SessionEventNames.CaptureTimelineUnusable);
        Assert.Contains("QPC", diagnostic.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EarlyQpcCorrections_StayBoundedAndNameTheActualClockWithoutFalseGaps()
    {
        using var harness = new SessionHarness(chunkSeconds: 1, bufferSeconds: 30, mode: SessionModes.Online);
        var mic = new FakeCaptureSource(Format, harness.Device);
        var loop = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = CaptureClock.Qpc };
        harness.Sources.Enqueue(mic);
        harness.Sources.EnqueueLoopback(loop);
        var session = harness.Service.PrepareSession("Early QPC corrections");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        Assert.True(await Wait.UntilAsync(() => loop.StartCount == 1));
        for (var i = 0; i < 500; i++)
        {
            var sample = At(i * 160, i * 10, 480);
            var ticks = i switch { 0 => 0L, 1 => 100_000L, 2 => 102_133L,
                _ => i * 100_000L - 110_660L + (i % 3) * 500L };
            loop.Emit(new AudioPacket(AudioSource.Loopback, Format, sample.Data, i * 160,
                10_000_000 + ticks, DateTimeOffset.UnixEpoch));
        }
        stop.Cancel();
        var outcome = await Wait.ForAsync(run, timeoutMs: 60_000, "QPC correction recording");
        Assert.Equal(0, outcome.GapCount);
        var events = ReadEvents(paths);
        var anomalies = events.Where(e => e.TryGetProperty("reason", out var reason)
            && reason.GetString() == "qpc_position_backwards").ToList();
        Assert.Equal(2, anomalies.Count); // first plus stop tail, not one event per packet
        Assert.Equal(498, anomalies.Sum(e => e.GetProperty("count").GetInt32()));
        Assert.All(anomalies, e => Assert.Contains("QPC", e.GetProperty("detail").GetString()));
        Assert.DoesNotContain(events, e => e.TryGetProperty("reason", out var reason)
            && reason.GetString() == "device_position_backwards");
        var chunks = harness.Database.Chunks.ListForSession(session.SessionId).Where(c => c.Source == AudioSource.Loopback).ToList();
        Assert.Equal(5_000, chunks.Last().EndMs);
        Assert.Empty(new SessionGapAuditor(harness.Database).Audit(session.SessionId).Gaps);
    }

    [Fact]
    public async Task SystemQpc_MismatchedPositionRate_ReportsAFullEightSecondSilentInterval()
    {
        using var harness = new SessionHarness(chunkSeconds: 1, bufferSeconds: 30, mode: SessionModes.Online);
        var mic = new FakeCaptureSource(Format, harness.Device);
        var loop = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = CaptureClock.Qpc };
        harness.Sources.Enqueue(mic);
        harness.Sources.EnqueueLoopback(loop);
        var session = harness.Service.PrepareSession("System QPC rate mismatch");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        Assert.True(await Wait.UntilAsync(() => mic.StartCount == 1 && loop.StartCount == 1));
        for (var i = 0; i < 100; i++) loop.Emit(At(i * 160, i * 10, 480));
        loop.Emit(At(144_000, 9_000, 480));
        stop.Cancel();
        var outcome = await Wait.ForAsync(run, timeoutMs: 60_000, "QPC recording");
        var gap = Assert.Single(ReadEvents(paths), e => e.GetProperty("event").GetString() == SessionEventNames.CaptureGap);
        Assert.Equal(8_000, gap.GetProperty("gap_ms").GetInt64());
        Assert.Equal(1_000, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(9_000, gap.GetProperty("gap_end_ms").GetInt64());
        Assert.DoesNotContain(ReadEvents(paths), e => e.TryGetProperty("reason", out var reason)
            && reason.GetString() == "device_position_backwards");
        Assert.Equal(8_000, outcome.GapTotalMs);
    }

    [Theory]
    [InlineData(CaptureClock.Qpc, 17_000)]
    [InlineData(CaptureClock.DevicePosition, 19_000)]
    public async Task ReopenedEndpoint_DelayedFirstBuffer_UsesQpcOrCaptureBoundaryWithoutConsumerDelay(
        CaptureClock clock, long expectedGap)
    {
        using var harness = new SessionHarness(chunkSeconds: 10, mode: SessionModes.Online);
        var mic = new FakeCaptureSource(Format, harness.Device);
        var first = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = clock };
        var reopened = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = clock };
        harness.Sources.Enqueue(mic);
        harness.Sources.EnqueueLoopback(first);
        harness.Sources.EnqueueLoopback(reopened);
        var session = harness.Service.PrepareSession("Delayed first capture buffer");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1));
        TestAudio.EmitSeconds(first, Format, 0, milliseconds: 10_000);
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        harness.Sources.OnCreateLoopback = () => harness.Clock.Advance(TimeSpan.FromSeconds(3));
        first.Fail(new InvalidOperationException("endpoint removed"));
        Assert.True(await Wait.UntilAsync(() => reopened.StartCount == 1, timeoutMs: 15_000));
        // The first frame exists at 27 s, then the callback is observed 2 s later.
        harness.Clock.Advance(TimeSpan.FromSeconds(16));
        reopened.Emit(At(0, 27_000, 480));
        stop.Cancel();
        var outcome = await Wait.ForAsync(run, timeoutMs: 60_000, "delayed capture buffer");
        var events = ReadEvents(paths);
        var restored = Assert.Single(events, e => e.GetProperty("event").GetString() == SessionEventNames.CaptureDeviceRestored);
        Assert.Equal(3_000, restored.GetProperty("gap_ms").GetInt64());
        var gap = Assert.Single(events, e => e.GetProperty("event").GetString() == SessionEventNames.CaptureGap);
        Assert.Equal(expectedGap, gap.GetProperty("gap_ms").GetInt64());
        Assert.Equal(10_000, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(10_000 + expectedGap, gap.GetProperty("gap_end_ms").GetInt64());
        Assert.Equal(1, outcome.GapCount);
        var chunks = harness.Database.Chunks.ListForSession(session.SessionId).Where(c => c.Source == AudioSource.Loopback).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(10_000 + expectedGap, chunks[1].StartMs);
        Assert.All(chunks, c => Assert.Equal(ChunkStates.Closed, c.Status));
    }

    [Theory]
    [InlineData(false, 17_000)]
    [InlineData(true, 22_000)]
    public async Task ReopenedEndpoint_NoFirstBufferBeforeStopOrAnotherLoss_CountsTheWholeObservedOutage(
        bool anotherLoss, long expectedGap)
    {
        using var harness = new SessionHarness(chunkSeconds: 10, mode: SessionModes.Online);
        var mic = new FakeCaptureSource(Format, harness.Device);
        var first = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = CaptureClock.Qpc };
        var second = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = CaptureClock.Qpc };
        var third = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback) { Clock = CaptureClock.Qpc };
        harness.Sources.Enqueue(mic);
        harness.Sources.EnqueueLoopback(first);
        harness.Sources.EnqueueLoopback(second);
        harness.Sources.EnqueueLoopback(third);
        var session = harness.Service.PrepareSession("No replacement buffer");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);
        using var stop = new CancellationTokenSource();
        var run = session.RunAsync(stop.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1));
        TestAudio.EmitSeconds(first, Format, 0, milliseconds: 10_000);
        harness.Sources.OnCreateLoopback = () => harness.Clock.Advance(TimeSpan.FromSeconds(3));
        first.Fail(new InvalidOperationException("first loss"));
        Assert.True(await Wait.UntilAsync(() => second.StartCount == 1, timeoutMs: 15_000));
        harness.Clock.Advance(TimeSpan.FromSeconds(14));
        if (anotherLoss)
        {
            second.Fail(new InvalidOperationException("another loss before its first buffer"));
            Assert.True(await Wait.UntilAsync(() => third.StartCount == 1, timeoutMs: 15_000));
            harness.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        stop.Cancel();
        var outcome = await Wait.ForAsync(run, timeoutMs: 60_000, "stop without a replacement buffer");
        var gap = Assert.Single(ReadEvents(paths), e => e.GetProperty("event").GetString() == SessionEventNames.CaptureGap);
        Assert.Equal(expectedGap, gap.GetProperty("gap_ms").GetInt64());
        Assert.Equal(10_000, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(10_000 + expectedGap, gap.GetProperty("gap_end_ms").GetInt64());
        Assert.Equal(AudioGapReasons.NotCaptured, gap.GetProperty("reason").GetString());
        Assert.Equal(1, outcome.GapCount);
        Assert.Equal(expectedGap, outcome.GapTotalMs);
        Assert.Single(harness.Database.Chunks.ListForSession(session.SessionId), c => c.Source == AudioSource.Loopback);
    }

    private static AudioPacket At(long position, long qpcMs, int frames)
    {
        var sample = TestAudio.Packet(Format, position, frames, AudioSource.Loopback);
        return new AudioPacket(AudioSource.Loopback, Format, sample.Data, position, qpcMs * 10_000, DateTimeOffset.UnixEpoch);
    }

    private static List<JsonElement> ReadEvents(SessionPaths paths)
    {
        using var stream = new FileStream(paths.EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToList();
    }

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
