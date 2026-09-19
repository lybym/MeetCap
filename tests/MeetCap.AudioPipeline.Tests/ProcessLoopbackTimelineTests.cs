using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// Issue #33: the process-loopback track's timeline.
/// </summary>
/// <remarks>
/// <para>
/// On the machine that produced the bug report the baseline sources report a device
/// position while process loopback reports none: every buffer carried
/// <c>device_position_frames = 0</c>, the QPC timestamp advanced by exactly 10 ms per
/// buffer, and the loopback track was reported <c>degraded (unknown)</c> with one
/// <c>capture.discontinuity</c> ("device position moved backwards") per buffer.
/// </para>
/// <para>
/// These tests run the whole online session against a scripted process-loopback source
/// shaped like that stream, and assert the four things issue #33 asks for: a monotonic
/// timeline with no repeated false discontinuities, a track that is not degraded for
/// stable audio, real drops and gaps that stay observable, and an actionable failure when
/// the environment provides no device timing at all (docs/ARCHITECTURE.md section 8.1,
/// docs/RELIABILITY.md sections 7 and 8).
/// </para>
/// </remarks>
public class ProcessLoopbackTimelineTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    [Fact]
    public async Task ProcessLoopbackTrack_WithOnlyQpcTiming_IsHealthyAndRecordsNoDiscontinuities()
    {
        using var harness = OnlineProcessLoopbackHarness();
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Process Loopback Timeline");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        // 130 s on both tracks: the microphone reports device positions, the loopback track
        // reports none and advances only its QPC timestamp.
        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 130_000);
        TestAudio.EmitSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 130_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.False(outcome.Degraded);

        var manifest = ReadManifest(paths);
        var micHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic);
        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);

        // Stable process audio is not degraded, and the false discontinuities are gone.
        Assert.False(loopbackHealth.Degraded);
        Assert.Null(loopbackHealth.EndReason);
        Assert.Equal(0, loopbackHealth.GapCount);
        Assert.Equal(0, loopbackHealth.GapTotalMs);
        Assert.False(micHealth.Degraded);

        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureDiscontinuity, AudioSources.Loopback));
        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureGap, AudioSources.Loopback));

        // The session's opening record says which device timing each track is placed by, so
        // "the loopback track was placed by QPC because its stream has no position of its
        // own" is something a reader is told rather than something they infer
        // (docs/ARCHITECTURE.md section 8.1).
        var started = Assert.Single(EventsNamed(paths, SessionEventNames.SessionStarted));
        Assert.Contains("source='loopback'", started.Detail, StringComparison.Ordinal);
        Assert.Contains("clock='qpc'", started.Detail, StringComparison.Ordinal);
        Assert.Contains("clock='device_position'", started.Detail, StringComparison.Ordinal);

        // The track's timeline is monotonic and complete: three chunks covering 0..130000,
        // the same span the device-position microphone track records.
        var loopbackChunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback).OrderBy(c => c.Sequence).ToList();

        Assert.Equal(new[] { 1, 2, 3 }, loopbackChunks.Select(c => c.Sequence));
        Assert.Equal(new[] { 0L, 60_000L, 120_000L }, loopbackChunks.Select(c => c.StartMs));
        Assert.Equal(130_000, loopbackChunks[^1].EndMs);
    }

    [Fact]
    public async Task ProcessLoopbackAndSystemLoopback_PlaceTheSameAudioOnTheSameTimeline()
    {
        // "Preserve the reliable system-loopback baseline" (issue #33): the QPC clock must
        // reproduce the baseline's timeline for identical audio, not merely differ from the
        // broken one.
        var devicePositioned = await RecordLoopbackTrackAsync(clock: CaptureClock.DevicePosition);
        var qpcPlaced = await RecordLoopbackTrackAsync(clock: CaptureClock.Qpc);

        Assert.Equal(devicePositioned, qpcPlaced);
        Assert.Equal(
            new[] { (0L, 60_000L), (60_000L, 120_000L), (120_000L, 130_000L) },
            qpcPlaced);
    }

    [Fact]
    public async Task ProcessLoopbackTrack_MissingAudioInItsOwnClock_IsStillReportedAsAGap()
    {
        // "Actual drops, stalls, and gaps should remain observable and should not be
        // hidden" (issue #33). A second of missing QPC time is a second of missing audio,
        // reported as a gap — never smoothed away and never mistaken for a restarted
        // stream (docs/RELIABILITY.md section 7).
        using var harness = OnlineProcessLoopbackHarness();
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Process Loopback Gap");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 21_000);

        // 10 s of audio, one second that the target process produced nothing, then 10 s more.
        var nextFrame = TestAudio.EmitSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 10_000);
        nextFrame = TestAudio.EmitSecondsWithoutDevicePosition(
            loopbackSource,
            Format,
            nextFrame + TestAudio.Frames(Format, 1_000),
            milliseconds: 10_000);
        Assert.Equal(TestAudio.Frames(Format, 21_000), nextFrame);

        cancellation.Cancel();
        await Finish(run);

        var gap = Assert.Single(EventsNamed(paths, SessionEventNames.CaptureGap, AudioSources.Loopback));
        Assert.Equal(1_000L, gap.GapMs);
        Assert.Equal(10_000L, gap.GapStartMs);
        Assert.Equal(11_000L, gap.GapEndMs);

        // The hole is a gap, not a discontinuity: one event, named for what actually happened.
        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureDiscontinuity, AudioSources.Loopback));

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal(1, loopbackHealth.GapCount);
        Assert.Equal(1_000, loopbackHealth.GapTotalMs);
    }

    [Fact]
    public async Task ProcessLoopbackTrack_WithoutAnyDeviceTiming_EndsWithAnActionableDiagnostic()
    {
        // Issue #33's fourth expectation: an unsupported process-loopback environment fails
        // with an actionable diagnostic instead of silently producing degraded audio. The
        // track ends, states why once, keeps the audio it already captured durable, and
        // leaves the microphone track alone (docs/RELIABILITY.md section 8).
        using var harness = OnlineProcessLoopbackHarness();
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Process Loopback No Timing");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        // One second of placeable audio, then a buffer from a stream that reports neither a
        // device position nor a QPC timestamp.
        TestAudio.EmitSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 1_000);
        loopbackSource.Emit(TestAudio.PacketWithoutAnyTiming(Format, TestAudio.TenMsFrames, AudioSource.Loopback));
        loopbackSource.Emit(TestAudio.PacketWithoutAnyTiming(Format, TestAudio.TenMsFrames, AudioSource.Loopback));

        Assert.True(
            await Wait.UntilAsync(() => EventsNamed(paths, SessionEventNames.CaptureTimelineUnusable).Count > 0),
            "the process-loopback track did not report its unusable timeline");

        // The healthy track is untouched by the loopback track's failure.
        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 60_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);

        var manifest = ReadManifest(paths);
        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        var micHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic);

        Assert.True(loopbackHealth.Degraded);
        Assert.Equal("timeline_unusable", loopbackHealth.EndReason);
        Assert.False(micHealth.Degraded);
        Assert.Equal(1, micHealth.ChunksClosed);

        // One explicit statement, carrying the reason and what to do about it — not one
        // event per buffer.
        var unusable = Assert.Single(EventsNamed(paths, SessionEventNames.CaptureTimelineUnusable));
        Assert.Equal(AudioSources.Loopback, unusable.Source);
        Assert.Contains("QPC", unusable.Detail, StringComparison.Ordinal);
        Assert.Contains("loopback_mode = \"system\"", unusable.Detail, StringComparison.Ordinal);

        // The audio captured before the failure is durable: the track closed and renamed its
        // chunk when it ended rather than leaving it as a .part.
        var loopbackChunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback).ToList();

        var closed = Assert.Single(loopbackChunks);
        Assert.Equal(ChunkStates.Closed, closed.Status);
        Assert.Equal(1_000, closed.EndMs);
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.part"));
    }

    /// <summary>
    /// An online harness whose configuration asks for process loopback
    /// (<c>capture.online.loopback_mode = "process"</c>, target <c>ffplay</c>) — the
    /// reproduction in issue #33.
    /// </summary>
    private static SessionHarness OnlineProcessLoopbackHarness()
        => new(chunkSeconds: 60, mode: "online", loopbackMode: "process", loopbackProcessName: "ffplay");

    /// <summary>
    /// A scripted loopback source shaped like Windows process loopback: it declares the QPC
    /// clock, so the track is never judged by the device position it does not have.
    /// </summary>
    private static FakeCaptureSource ProcessLoopbackSource(SessionHarness harness)
    {
        var source = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback)
        {
            Clock = CaptureClock.Qpc,
        };

        harness.Sources.EnqueueLoopback(source);
        return source;
    }

    /// <summary>The microphone track of the same online session, scripted and started.</summary>
    private static FakeCaptureSource MicSource(SessionHarness harness)
    {
        var source = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        harness.Sources.Enqueue(source);
        return source;
    }

    private static async Task<(long StartMs, long EndMs)[]> RecordLoopbackTrackAsync(CaptureClock clock)
    {
        using var harness = OnlineProcessLoopbackHarness();
        var micSource = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var loopbackSource = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback)
        {
            Clock = clock,
        };

        harness.Sources.Enqueue(micSource);
        harness.Sources.EnqueueLoopback(loopbackSource);

        var session = harness.Service.PrepareSession($"Loopback Clock {clock}");
        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 130_000);
        if (clock == CaptureClock.Qpc)
        {
            TestAudio.EmitSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 130_000);
        }
        else
        {
            TestAudio.EmitSeconds(loopbackSource, Format, 0, milliseconds: 130_000);
        }

        cancellation.Cancel();
        await Finish(run);

        return harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback)
            .OrderBy(c => c.Sequence)
            .Select(c => (c.StartMs, c.EndMs))
            .ToArray();
    }

    private static Task<RecordingSessionOutcome> Finish(Task<RecordingSessionOutcome> run)
        => Wait.ForAsync(run, timeoutMs: 60_000, "the online process-loopback recording session");

    private static SessionManifest ReadManifest(SessionPaths paths)
    {
        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error);
        Assert.True(manifest is not null, error);
        return manifest!;
    }

    private static List<CapturedEvent> EventsNamed(
        SessionPaths paths,
        string eventName,
        string? source = null)
    {
        if (!File.Exists(paths.EventsPath))
        {
            return new List<CapturedEvent>();
        }

        var captured = new List<CapturedEvent>();
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
                // The recorder may be mid-write; ignore partial lines.
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("event", out var name) || name.GetString() != eventName)
                {
                    continue;
                }

                var eventSource = root.TryGetProperty("source", out var sourceValue)
                    ? sourceValue.GetString()
                    : null;

                if (source is not null && !string.Equals(eventSource, source, StringComparison.Ordinal))
                {
                    continue;
                }

                captured.Add(new CapturedEvent(
                    eventSource,
                    root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
                    root.TryGetProperty("gap_start_ms", out var gapStart) ? gapStart.GetInt64() : null,
                    root.TryGetProperty("gap_end_ms", out var gapEnd) ? gapEnd.GetInt64() : null,
                    root.TryGetProperty("gap_ms", out var gapMs) ? gapMs.GetInt64() : null));
            }
        }

        return captured;
    }

    private sealed record CapturedEvent(
        string? Source,
        string? Detail,
        long? GapStartMs,
        long? GapEndMs,
        long? GapMs);
}