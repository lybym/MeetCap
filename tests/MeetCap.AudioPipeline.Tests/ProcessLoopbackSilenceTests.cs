using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// Issue #38: a process-loopback track that runs perfectly while carrying nothing but digital
/// zeros must not be reported as healthy and complete.
/// </summary>
/// <remarks>
/// <para>
/// The reported failure produced a live stream, a correct timeline and a full-length track in
/// which every sample was zero; MeetCap called it <c>healthy</c> and <c>COMPLETED</c>, so the
/// user had no way to tell an empty recording from a good one. These tests pin the replacement
/// behaviour: the track's content is recorded, an all-zero process-loopback track is marked
/// degraded with an explicit reason and one actionable event, and the verdict never fires for
/// the tracks where silence is normal — the baseline system loopback and the microphone.
/// </para>
/// <para>
/// The audio itself is not compromised by any of this: the chunk stays durable, the timeline is
/// untouched, and a track that captured audio keeps reporting healthy
/// (docs/RELIABILITY.md section 17).
/// </para>
/// </remarks>
public class ProcessLoopbackSilenceTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    [Fact]
    public async Task ProcessLoopbackTrack_ThatCapturedOnlyZeros_IsDegradedAndSaysWhy()
    {
        using var harness = OnlineHarness(loopbackMode: "process");
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Silent Process Loopback");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        // The issue #38 shape on the loopback track: a live QPC-only stream that delivers
        // nothing but zeros for the whole session, while the microphone records normally.
        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 12_000);
        TestAudio.EmitSilentSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 12_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        // The session still completes and still says its tracks degraded: the silence is
        // visible, not fatal.
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(outcome.Degraded);

        var manifest = ReadManifest(paths);
        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        var micHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic);

        Assert.True(loopbackHealth.Degraded);
        Assert.Equal(CaptureDegradedReasons.SilentProcessLoopback, loopbackHealth.DegradedReason);

        // It did not end early: the reason for the degradation is the content, not an end.
        Assert.Null(loopbackHealth.EndReason);

        // The content counters are the durable evidence a reader can check. The totals are asserted
        // exactly so a whole-span rule cannot silently become a windowed one.
        Assert.NotNull(loopbackHealth.AudioContent);
        Assert.True(loopbackHealth.AudioContent!.AllSilent);
        Assert.Equal(0, loopbackHealth.AudioContent.NonZeroSamples);
        Assert.Equal(576_000, loopbackHealth.AudioContent.TotalSamples); // 12 s of 48 kHz mono
        Assert.Equal(0d, loopbackHealth.AudioContent.PeakAbsSample);

        // The microphone track is untouched — including its own content counters, which show
        // what non-silent audio looks like in the same record.
        Assert.False(micHealth.Degraded);
        Assert.Null(micHealth.DegradedReason);
        Assert.NotNull(micHealth.AudioContent);
        Assert.False(micHealth.AudioContent!.AllSilent);
        Assert.Equal(576_000, micHealth.AudioContent.TotalSamples);
        Assert.Equal(576_000, micHealth.AudioContent.NonZeroSamples);

        // One explicit, actionable statement at the end of the track — not one per buffer.
        var silent = Assert.Single(EventsNamed(paths, SessionEventNames.CaptureSilentTrack));
        Assert.Equal(AudioSources.Loopback, silent.Source);
        Assert.Equal(CaptureDegradedReasons.SilentProcessLoopback, silent.Reason);
        Assert.Contains("no non-zero sample", silent.Detail, StringComparison.Ordinal);
        Assert.Contains("loopback_mode = \"system\"", silent.Detail, StringComparison.Ordinal);

        // The audio it did capture is durable: the chunk was closed and indexed exactly as a
        // healthy track's would be.
        var loopbackChunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback).ToList();
        var closed = Assert.Single(loopbackChunks);
        Assert.Equal(ChunkStates.Closed, closed.Status);
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.part"));
    }

    [Fact]
    public async Task ProcessLoopbackTrack_ThatCapturedAudio_IsNotFlaggedSilentAndItsChunkCarriesTheAudio()
    {
        // The regression seam issue #38 asks for: non-zero packets have to reach the artifact,
        // not merely be counted. The telemetry and the WAV are read back and both must show the
        // captured audio (docs/DEVELOPMENT.md section 5).
        using var harness = OnlineHarness(loopbackMode: "process");
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Audible Process Loopback");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 5_000);
        TestAudio.EmitSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 5_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.False(outcome.Degraded);

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.False(loopbackHealth.Degraded);
        Assert.Null(loopbackHealth.DegradedReason);
        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureSilentTrack));

        Assert.NotNull(loopbackHealth.AudioContent);
        Assert.False(loopbackHealth.AudioContent!.AllSilent);
        Assert.Equal(240_000, loopbackHealth.AudioContent.TotalSamples); // 5 s of 48 kHz mono
        Assert.Equal(240_000, loopbackHealth.AudioContent.NonZeroSamples);
        Assert.Equal(0.25d, loopbackHealth.AudioContent.PeakAbsSample, precision: 6);

        // The recorded chunk really holds the audio: at least one non-zero sample byte after
        // the WAV header.
        var chunk = harness.Database.Chunks.ListForSession(session.SessionId)
            .Single(c => c.Source == AudioSource.Loopback);
        Assert.Equal(ChunkStates.Closed, chunk.Status);

        var chunkPath = Assert.Single(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.wav"));
        var bytes = File.ReadAllBytes(chunkPath);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains(bytes[WavHeader.Size..], b => b != 0);
    }

    [Fact]
    public async Task ProcessLoopbackTrack_OfNonFiniteSamples_IsFlaggedAndStillFinalizes()
    {
        // A broken tap can hand over samples that are not decodable audio, and a non-finite peak
        // cannot be serialized into session.json at all: the exception used to escape the consumer
        // and abort finalization, so the session never reached COMPLETED and no session.stopped
        // was written (issue #38 review, finding B1). The loopback track is a float track here
        // because PCM bit patterns are always finite integers.
        var floatFormat = TestAudio.Formats.Mono48kFloat;
        using var harness = OnlineHarness(loopbackMode: "process");
        var micSource = MicSource(harness);
        var loopbackSource = new FakeCaptureSource(floatFormat, harness.RenderDevice, AudioSource.Loopback)
        {
            Clock = CaptureClock.Qpc,
        };
        harness.Sources.EnqueueLoopback(loopbackSource);

        var session = harness.Service.PrepareSession("Non-Finite Process Loopback");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 5_000);
        for (var frame = 0L; frame < 48_000; frame += TestAudio.TenMsFrames)
        {
            loopbackSource.Emit(TestAudio.NonFinitePacketWithoutDevicePosition(floatFormat, frame, TestAudio.TenMsFrames));
        }

        cancellation.Cancel();

        // Finalization completes at all: the manifest is written with the verdict in it, which is
        // what the infinite peak used to prevent.
        var outcome = await Finish(run);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(outcome.Degraded);

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal(CaptureDegradedReasons.SilentProcessLoopback, loopbackHealth.DegradedReason);
        Assert.NotNull(loopbackHealth.AudioContent);
        Assert.True(double.IsFinite(loopbackHealth.AudioContent!.PeakAbsSample));
        Assert.Equal(0d, loopbackHealth.AudioContent.PeakAbsSample);
        Assert.Equal(0, loopbackHealth.AudioContent.NonZeroSamples);
        Assert.Equal(48_000, loopbackHealth.AudioContent.TotalSamples); // 1 s of 48 kHz mono
        Assert.True(loopbackHealth.AudioContent.AllSilent);

        var silent = Assert.Single(EventsNamed(paths, SessionEventNames.CaptureSilentTrack));
        Assert.Equal(CaptureDegradedReasons.SilentProcessLoopback, silent.Reason);
    }

    [Fact]
    public async Task ProcessLoopbackTrack_ThatDeliveredNoSamples_EndsEmptyAndDegraded()
    {
        // A started process-loopback stream that produces nothing at all would otherwise be a
        // completed, healthy, empty track: zero chunks and zero bytes with degraded: false.
        using var harness = OnlineHarness(loopbackMode: "process");
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Empty Process Loopback");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 3_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(outcome.Degraded);

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal(CaptureDegradedReasons.EmptyProcessLoopback, loopbackHealth.DegradedReason);
        Assert.Null(loopbackHealth.EndReason);
        Assert.Equal(0, loopbackHealth.ChunksClosed);
        Assert.Equal(0, loopbackHealth.ClosedDataBytes);
        Assert.NotNull(loopbackHealth.AudioContent);
        Assert.Equal(0, loopbackHealth.AudioContent!.TotalSamples);
        Assert.False(loopbackHealth.AudioContent.AllSilent);

        var silent = Assert.Single(EventsNamed(paths, SessionEventNames.CaptureSilentTrack));
        Assert.Equal(AudioSources.Loopback, silent.Source);
        Assert.Equal(CaptureDegradedReasons.EmptyProcessLoopback, silent.Reason);
        Assert.Contains("No samples reached the track", silent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemLoopbackTrack_ThatCapturedOnlyZeros_IsNotFlaggedSilent()
    {
        // The baseline loopback is the render endpoint's mix: an all-zero track there is what a
        // quiet machine sounds like, and issue #33's guarantee that the baseline stays healthy
        // must not be traded away for issue #38's detection. The counters are still recorded,
        // so the all-zero span is visible without being called a failure.
        using var harness = OnlineHarness(loopbackMode: "system");
        var micSource = MicSource(harness);
        var loopbackSource = SystemLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Quiet System Loopback");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 5_000);
        TestAudio.EmitSilentSeconds(loopbackSource, Format, 0, milliseconds: 5_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.False(outcome.Degraded);

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.False(loopbackHealth.Degraded);
        Assert.Null(loopbackHealth.DegradedReason);
        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureSilentTrack));

        // The quiet span is still part of the durable record.
        Assert.NotNull(loopbackHealth.AudioContent);
        Assert.True(loopbackHealth.AudioContent!.AllSilent);
    }

    [Fact]
    public async Task MicrophoneTrack_ThatCapturedOnlyZeros_IsNotFlaggedSilent()
    {
        // An empty room is the microphone's normal silence, and the microphone is not the
        // process-loopback path this detection exists for.
        using var harness = OnlineHarness(loopbackMode: "process");
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Quiet Microphone");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSilentSeconds(micSource, Format, 0, milliseconds: 5_000);
        TestAudio.EmitSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 5_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.False(outcome.Degraded);
        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureSilentTrack));

        var micHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Mic);
        Assert.False(micHealth.Degraded);
        Assert.True(micHealth.AudioContent!.AllSilent);
    }

    [Fact]
    public async Task ProcessLoopbackTrack_ThatEndedForItsOwnReason_IsNotRestatedAsSilent()
    {
        // A track that already ended with a fatal diagnostic keeps that reason. Restating the
        // silence on top of it would report two failures where the reader has one, so the
        // verdict stands down and only the content counters remain.
        using var harness = OnlineHarness(loopbackMode: "process");
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Silent And Unusable");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSilentSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 1_000);
        loopbackSource.Emit(TestAudio.PacketWithoutAnyTiming(Format, TestAudio.TenMsFrames, AudioSource.Loopback));

        Assert.True(
            await Wait.UntilAsync(() => EventsNamed(paths, SessionEventNames.CaptureTimelineUnusable).Count > 0),
            "the process-loopback track did not report its unusable timeline");

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 30_000);
        cancellation.Cancel();
        await Finish(run);

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal("timeline_unusable", loopbackHealth.EndReason);
        Assert.Null(loopbackHealth.DegradedReason);
        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureSilentTrack));

        // The audio that was placed before the failure is still accounted for.
        Assert.NotNull(loopbackHealth.AudioContent);
        Assert.True(loopbackHealth.AudioContent!.TotalSamples > 0);
        Assert.True(loopbackHealth.AudioContent.AllSilent);
    }

    private static SessionHarness OnlineHarness(string loopbackMode)
        => new(chunkSeconds: 60, mode: "online", loopbackMode: loopbackMode, loopbackProcessName: "ffplay");

    /// <summary>A loopback source shaped like Windows process loopback: QPC-only timing.</summary>
    private static FakeCaptureSource ProcessLoopbackSource(SessionHarness harness)
    {
        var source = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback)
        {
            Clock = CaptureClock.Qpc,
        };

        harness.Sources.EnqueueLoopback(source);
        return source;
    }

    /// <summary>A loopback source shaped like the baseline: placed by the render endpoint.</summary>
    private static FakeCaptureSource SystemLoopbackSource(SessionHarness harness)
    {
        var source = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.EnqueueLoopback(source);
        return source;
    }

    private static FakeCaptureSource MicSource(SessionHarness harness)
    {
        var source = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        harness.Sources.Enqueue(source);
        return source;
    }

    private static Task<RecordingSessionOutcome> Finish(Task<RecordingSessionOutcome> run)
        => Wait.ForAsync(run, timeoutMs: 60_000, "the online recording session");

    private static SessionManifest ReadManifest(SessionPaths paths)
    {
        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error);
        Assert.True(manifest is not null, error);
        return manifest!;
    }

    private static List<CapturedEvent> EventsNamed(SessionPaths paths, string eventName)
    {
        var captured = new List<CapturedEvent>();
        if (!File.Exists(paths.EventsPath))
        {
            return captured;
        }

        using var stream = new FileStream(paths.EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("event", out var name) || name.GetString() != eventName)
                {
                    continue;
                }

                captured.Add(new CapturedEvent(
                    root.TryGetProperty("source", out var source) ? source.GetString() : null,
                    root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
                    root.TryGetProperty("detail", out var detail) ? detail.GetString() : null));
            }
        }

        return captured;
    }

    private sealed record CapturedEvent(string? Source, string? Reason, string? Detail);
}