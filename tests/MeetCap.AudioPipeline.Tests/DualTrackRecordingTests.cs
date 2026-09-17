using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// The M5 online dual-track behaviour: a microphone and a loopback track are captured
/// concurrently and independently, so neither track waits for the other and the loss of
/// one never silently corrupts the other (docs/ARCHITECTURE.md section 5/6,
/// docs/ROADMAP.md M5).
/// </summary>
public class DualTrackRecordingTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    [Fact]
    public async Task OnlineSession_CreatesIndependentMicAndLoopbackChunkTrees()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, mode: "online");
        var micSource = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var loopbackSource = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.Enqueue(micSource);
        harness.Sources.EnqueueLoopback(loopbackSource);

        var session = harness.Service.PrepareSession("Online Standup");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        var closedSources = new List<string>();
        session.ChunkClosed += chunk => closedSources.Add(chunk.Source);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        // Both tracks advance past one chunk boundary so each produces a durable chunk.
        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 130_000);
        TestAudio.EmitSeconds(loopbackSource, Format, 0, milliseconds: 130_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(outcome.IsClean);

        // Independent chunk trees under audio/mic/ and audio/loopback/.
        Assert.Equal(new[] { "000001.wav", "000002.wav", "000003.wav" }, WavNames(paths, AudioSource.Mic));
        Assert.Equal(new[] { "000001.wav", "000002.wav", "000003.wav" }, WavNames(paths, AudioSource.Loopback));

        // The chunk index records each chunk under its own source, 1-based per track.
        var chunks = harness.Database.Chunks.ListForSession(session.SessionId);
        Assert.Equal(6, chunks.Count);
        Assert.Equal(new[] { "mic", "mic", "mic", "loopback", "loopback", "loopback" }.OrderBy(s => s),
            chunks.Select(c => c.Source.ToWireName()).OrderBy(s => s));
        Assert.All(chunks.Where(c => c.Source == AudioSource.Mic), c => Assert.Equal(ChunkStates.Closed, c.Status));
        Assert.All(chunks.Where(c => c.Source == AudioSource.Loopback), c => Assert.Equal(ChunkStates.Closed, c.Status));

        // ChunkClosed announced both sources, so a downstream consumer (the M4 batch
        // builder) receives mic and loopback chunks alike.
        Assert.Contains("mic", closedSources);
        Assert.Contains("loopback", closedSources);

        var manifest = ReadManifest(paths);
        Assert.Equal(new[] { AudioSources.Mic, AudioSources.Loopback }, manifest.Tracks);
        Assert.Equal(2, manifest.Capture.Count);
        Assert.Equal(new[] { "mic", "loopback" }, manifest.Capture.Select(c => c.Source));
        Assert.Equal(2, manifest.TrackHealth.Count);
        Assert.Equal(new[] { "mic", "loopback" }, manifest.TrackHealth.Select(h => h.Source));
    }

    [Fact]
    public async Task OneTrackDeviceLoss_DoesNotCorruptTheOtherTrack()
    {
        // The loopback render endpoint disappears mid-session. Recovery cannot find it,
        // so the loopback track ends fatally while the microphone track keeps recording —
        // the loss of one track is explicit and never silently corrupts the other
        // (docs/RELIABILITY.md section 8, docs/ROADMAP.md M5).
        using var harness = new SessionHarness(chunkSeconds: 60, maxDeviceRecoveryAttempts: 1, mode: "online");
        var micSource = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var loopbackSource = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.Enqueue(micSource);
        harness.Sources.EnqueueLoopback(loopbackSource);

        var session = harness.Service.PrepareSession("Online Partial Loss");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        // Both tracks record one chunk.
        var micFrame = TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 60_000);
        TestAudio.EmitSeconds(loopbackSource, Format, 0, milliseconds: 60_000);

        // The loopback render endpoint disappears. The mic endpoint stays, so the mic
        // track's recovery would still succeed; only the loopback track is stranded.
        harness.Devices.SetRenderDevices();
        loopbackSource.Fail(new InvalidOperationException("render endpoint unplugged"));

        // Wait for the loopback track to give up (one recovery attempt with a 1 s backoff).
        Assert.True(await Wait.UntilAsync(() => HasEvent(paths, SessionEventNames.CaptureDeviceLostFatal), timeoutMs: 15_000),
            "the loopback track did not report a fatal device loss");

        // The mic track keeps recording another chunk after the loopback was lost, picking
        // up from the device position it had reached.
        TestAudio.EmitSeconds(micSource, Format, micFrame, milliseconds: 60_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        Assert.False(outcome.IsClean);

        // The mic track kept recording: two durable chunks.
        Assert.Equal(2, WavNames(paths, AudioSource.Mic).Length);
        // The loopback track captured one chunk before the loss and it is still durable.
        Assert.Equal(new[] { "000001.wav" }, WavNames(paths, AudioSource.Loopback));

        var manifest = ReadManifest(paths);
        var micHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic);
        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);

        Assert.False(micHealth.Degraded);
        Assert.Equal(2, micHealth.ChunksClosed);
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal("device_lost", loopbackHealth.EndReason);
        Assert.Equal(1, loopbackHealth.ChunksClosed);
    }

    [Fact]
    public async Task OnlineSession_PreservesSessionRelativeTimelinePerTrack()
    {
        using var harness = new SessionHarness(chunkSeconds: 60, mode: "online");
        var micSource = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var loopbackSource = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.Enqueue(micSource);
        harness.Sources.EnqueueLoopback(loopbackSource);

        var session = harness.Service.PrepareSession("Online Timeline");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1));

        // Both tracks start at device position 0 and advance 60 s, so each track's first
        // chunk spans session-relative 0..60000 on its own independent timeline.
        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 60_000);
        TestAudio.EmitSeconds(loopbackSource, Format, 0, milliseconds: 60_000);
        cancellation.Cancel();

        await Finish(run);

        var micChunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Mic).OrderBy(c => c.Sequence).ToList();
        var loopbackChunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback).OrderBy(c => c.Sequence).ToList();

        Assert.Equal(0, micChunks[0].StartMs);
        Assert.Equal(60_000, micChunks[0].EndMs);
        Assert.Equal(0, loopbackChunks[0].StartMs);
        Assert.Equal(60_000, loopbackChunks[0].EndMs);
        // Each track numbers its chunks from 1 independently.
        Assert.Equal(1, micChunks[0].Sequence);
        Assert.Equal(1, loopbackChunks[0].Sequence);
    }

    [Fact]
    public async Task AFatallyEndedTrackFinalizesItsPartialChunkWhileTheOtherTrackKeepsRecording()
    {
        // The loopback render endpoint disappears while its chunk is still partial (10 s of
        // a 30 s chunk). Recovery cannot find it, so the loopback track ends fatally — but
        // the microphone track keeps recording, so the session does not end.
        //
        // The failed track's tail must still become durable the moment that track ends: its
        // capture loop has returned, so no further packet can ever arrive for it and
        // ProcessPacket can never close the chunk. Without finalizing on the fatal path the
        // chunk would stay `NNNNNN.wav.part` with an open index row, and its FileStream
        // buffer unflushed, for the rest of the session, contradicting the fatal event's own
        // "the audio already captured still closed" claim (docs/RELIABILITY.md section 8).
        using var harness = new SessionHarness(
            chunkSeconds: 30,
            maxDeviceRecoveryAttempts: 1,
            mode: "online");
        var micSource = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var loopbackSource = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.Enqueue(micSource);
        harness.Sources.EnqueueLoopback(loopbackSource);

        var session = harness.Service.PrepareSession("Online Fatal Partial Chunk");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        // ChunkClosed fires on the consumer thread after the chunk is durably closed, its index
        // row is written and it has been renamed out of .part. Waiting on it is a deterministic
        // signal that the failed track's tail was finalized — polling the filesystem instead
        // races the rename against the index update.
        var loopbackChunkClosed = new TaskCompletionSource<ClosedAudioChunk>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.ChunkClosed += chunk =>
        {
            if (chunk.Source == AudioSources.Loopback)
            {
                loopbackChunkClosed.TrySetResult(chunk);
            }
        };

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        // 10 s into a 30 s chunk, so a partial chunk is open on the loopback track. The mic
        // track stays silent so its own chunk state cannot mask the loopback assertion.
        TestAudio.EmitSeconds(loopbackSource, Format, 0, milliseconds: 10_000);

        harness.Devices.SetRenderDevices();
        loopbackSource.Fail(new InvalidOperationException("render endpoint unplugged"));

        Assert.True(await Wait.UntilAsync(() => HasEvent(paths, SessionEventNames.CaptureDeviceLostFatal), timeoutMs: 15_000),
            "the loopback track did not report a fatal device loss");

        // The failed track's tail is finalized without waiting for the session to end. The mic
        // track is deliberately still recording here: the session is not over. Without
        // finalizing on the fatal path this never completes and the chunk stays .part.
        var finalized = await Wait.ForAsync(
            loopbackChunkClosed.Task,
            timeoutMs: 15_000,
            "the fatally ended loopback track to finalize its partial chunk while the mic track kept recording");

        Assert.Equal(AudioSources.Loopback, finalized.Source);
        Assert.Equal(1, finalized.Sequence);
        Assert.Equal(10_000, finalized.EndMs);
        Assert.True(finalized.DataBytes > 0, "the finalized chunk carried no audio");
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.part"));

        // The index agrees: the chunk is durable, carries the audio that was captured, and
        // is no longer reported as open.
        var loopbackChunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback).ToList();
        var closed = Assert.Single(loopbackChunks);
        Assert.Equal(ChunkStates.Closed, closed.Status);
        Assert.Equal(10_000, closed.EndMs);
        Assert.Equal(finalized.DataBytes, closed.ByteLength);

        // The audio is a real, independently readable WAV at the track format.
        var validation = WaveChunkValidator.ValidateClosedFile(
            paths.ChunkFinalPath(AudioSource.Loopback, 1),
            Format);
        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal(closed.ByteLength, validation.DataBytes);

        // Only now does the healthy track end.
        cancellation.Cancel();
        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.Equal("device_lost", loopbackHealth.EndReason);
        Assert.Equal(1, loopbackHealth.ChunksClosed);
    }

    private static Task<RecordingSessionOutcome> Finish(Task<RecordingSessionOutcome> run)
        => Wait.ForAsync(run, timeoutMs: 60_000, "the online recording session");

    private static SessionManifest ReadManifest(SessionPaths paths)
    {
        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error);
        Assert.True(manifest is not null, error);
        return manifest!;
    }

    private static string[] WavNames(SessionPaths paths, AudioSource source)
        => Directory.GetFiles(paths.AudioDirectory(source), "*.wav")
            .Select(p => Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static bool HasEvent(SessionPaths paths, string eventName)
    {
        if (!File.Exists(paths.EventsPath))
        {
            return false;
        }

        using var stream = new FileStream(paths.EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        foreach (var line in content.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("event", out var name) &&
                    name.GetString() == eventName)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // The recorder may be mid-write; ignore partial lines.
            }
        }

        return false;
    }
}
