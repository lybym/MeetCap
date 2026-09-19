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

    [Fact]
    public async Task ProcessLoopbackTrack_UnusableOnTheVeryFirstBuffer_StillNamesTheReasonAndOpensNoChunk()
    {
        // The other half of the unusable-timeline case: the diagnostic must survive a track
        // that never placed a single buffer. Nothing was captured on this track, so it has to
        // be able to say why without a chunk, an error log or a session that hung waiting for
        // audio that could never come (docs/RELIABILITY.md sections 7 and 8).
        using var harness = OnlineProcessLoopbackHarness();
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Process Loopback Unusable First");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        // No placeable audio at all: the stream reports neither a device position nor a QPC
        // timestamp from its first buffer on.
        loopbackSource.Emit(TestAudio.PacketWithoutAnyTiming(Format, TestAudio.TenMsFrames, AudioSource.Loopback));

        Assert.True(
            await Wait.UntilAsync(() => EventsNamed(paths, SessionEventNames.CaptureTimelineUnusable).Count > 0),
            "the process-loopback track did not report its unusable timeline");

        // The other track keeps recording, which is what makes this a per-track failure.
        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 60_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);

        var manifest = ReadManifest(paths);
        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        var micHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic);

        // The reason is durable even though this track closed nothing at all.
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal("timeline_unusable", loopbackHealth.EndReason);
        Assert.Equal(0, loopbackHealth.ChunksClosed);
        Assert.False(micHealth.Degraded);

        // Exactly one statement, and it is the actionable one.
        var unusable = Assert.Single(EventsNamed(paths, SessionEventNames.CaptureTimelineUnusable));
        Assert.Equal(AudioSources.Loopback, unusable.Source);
        Assert.Contains("QPC", unusable.Detail, StringComparison.Ordinal);

        // No chunk was ever opened, so nothing half-written was left behind for recovery to
        // find: no index row, no .part and no finalized file.
        Assert.DoesNotContain(
            harness.Database.Chunks.ListForSession(session.SessionId),
            c => c.Source == AudioSource.Loopback);
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.part"));
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Loopback), "*.wav"));
    }

    [Fact]
    public async Task ProcessLoopbackTrack_ASecondUnusableBurst_IsStillOnlyOneEvent()
    {
        // "Once per track" has to hold across separate bursts, not just within the first one:
        // the guard is a terminal state of the track, so a later packet that cannot be placed
        // must not restate the diagnostic (docs/RELIABILITY.md section 8).
        using var harness = OnlineProcessLoopbackHarness();
        var micSource = MicSource(harness);
        var loopbackSource = ProcessLoopbackSource(harness);

        var session = harness.Service.PrepareSession("Process Loopback Repeated Unusable");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && loopbackSource.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSecondsWithoutDevicePosition(loopbackSource, Format, 0, milliseconds: 1_000);
        loopbackSource.Emit(TestAudio.PacketWithoutAnyTiming(Format, TestAudio.TenMsFrames, AudioSource.Loopback));

        Assert.True(
            await Wait.UntilAsync(() => EventsNamed(paths, SessionEventNames.CaptureTimelineUnusable).Count > 0),
            "the process-loopback track did not report its unusable timeline");

        // A later burst of unplaceable buffers, delivered after the track already ended.
        for (var i = 0; i < 5; i++)
        {
            loopbackSource.Emit(TestAudio.PacketWithoutAnyTiming(Format, TestAudio.TenMsFrames, AudioSource.Loopback));
        }

        TestAudio.EmitSeconds(micSource, Format, 0, milliseconds: 60_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        Assert.Single(EventsNamed(paths, SessionEventNames.CaptureTimelineUnusable));

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.Equal("timeline_unusable", loopbackHealth.EndReason);

        // The audio placed before the failure stayed durable and was closed exactly once.
        var closed = Assert.Single(
            harness.Database.Chunks.ListForSession(session.SessionId),
            c => c.Source == AudioSource.Loopback);
        Assert.Equal(ChunkStates.Closed, closed.Status);
        Assert.Equal(1_000, closed.EndMs);
    }

    [Fact]
    public async Task ProcessLoopbackTrack_DeviceLossAndRecovery_ReportsTheOutageOnceAndStartsANewSegment()
    {
        // Issue #33's third expectation on a QPC-placed track, end to end: a real outage has to
        // appear exactly once as a gap, the audio captured before it has to become durable on
        // its own chunk, and the recovered stream has to stay on its declared clock instead of
        // displacing the measured outage with the new stream's own reading
        // (docs/RELIABILITY.md sections 7 and 8, docs/ARCHITECTURE.md section 8.1).
        using var harness = OnlineProcessLoopbackHarness();
        var micSource = MicSource(harness);
        var firstLoopback = ProcessLoopbackSource(harness);

        // The replacement stream, scripted like a real reopen: its own QPC starts from a fresh
        // origin that says nothing about session time.
        var reopened = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback)
        {
            Clock = CaptureClock.Qpc,
        };
        harness.Sources.EnqueueLoopback(reopened);

        var session = harness.Service.PrepareSession("Process Loopback Device Loss");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => micSource.StartCount == 1 && firstLoopback.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSecondsWithoutDevicePosition(firstLoopback, Format, 0, milliseconds: 20_000);

        // The outage is measured from the clock, and the track stamps its start when it notices
        // the fault but reads it only once the endpoint is reopened. A clock advanced outside
        // that window races the capture loop; advanced here it lands between the two, so the
        // three-second outage is always observed. Without a measured outage there would be no
        // gap to report at all.
        harness.Sources.OnCreateLoopback = () => harness.Clock.Advance(TimeSpan.FromSeconds(3));
        firstLoopback.Fail(new InvalidOperationException("loopback endpoint unplugged"));

        Assert.True(
            await Wait.UntilAsync(() => reopened.StartCount == 1, timeoutMs: 15_000),
            "the loopback endpoint was not reopened");

        // The reopened stream continues, but its own QPC sits far from where the outage ended.
        TestAudio.EmitSecondsWithoutDevicePosition(
            reopened, Format, 0, milliseconds: 20_000, qpcDeltaTicks: 900_000_000);

        cancellation.Cancel();
        var outcome = await Finish(run);

        var loopbackEvents = EventsNamed(paths, SessionEventNames.CaptureGap, AudioSources.Loopback);

        // One outage, one gap — not one per buffer of the reopened stream.
        var gap = Assert.Single(loopbackEvents);
        Assert.True(gap.GapMs > 0, "the measured outage was not reported as a gap");
        Assert.NotNull(gap.GapStartMs);
        Assert.NotNull(gap.GapEndMs);
        Assert.Equal(gap.GapStartMs + gap.GapMs, gap.GapEndMs);

        // No false discontinuity on either side of the outage.
        Assert.Empty(EventsNamed(paths, SessionEventNames.CaptureDiscontinuity, AudioSources.Loopback));

        // The audio captured before the outage is durable on its own chunk, and the recovered
        // stream starts a new one — which is how the pre-outage audio survives an outage
        // shorter than a chunk.
        var loopbackChunks = harness.Database.Chunks.ListForSession(session.SessionId)
            .Where(c => c.Source == AudioSource.Loopback).OrderBy(c => c.Sequence).ToList();

        Assert.Equal(2, loopbackChunks.Count);
        Assert.All(loopbackChunks, c => Assert.Equal(ChunkStates.Closed, c.Status));
        Assert.Equal(0, loopbackChunks[0].StartMs);
        Assert.True(loopbackChunks[0].EndMs >= 20_000);
        Assert.Equal(loopbackChunks[0].EndMs + gap.GapMs, loopbackChunks[1].StartMs);

        var loopbackHealth = ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal(1, loopbackHealth.GapCount);
        Assert.Equal(gap.GapMs, loopbackHealth.GapTotalMs);

        // The microphone track is untouched by the loopback track's outage.
        Assert.False(ReadManifest(paths).TrackHealth.Single(h => h.Source == AudioSources.Mic).Degraded);
    }

    [Fact]
    public void ProcessLoopbackTrack_UnusableEndReasonSurvivesARecoveryRoundTrip()
    {
        // `timeline_unusable` is free-form text, so nothing but a test pins it. A session whose
        // process-loopback track ended this way and was then found by startup recovery must
        // still report the track's own end reason, or the honest label this change added would
        // be lost exactly when a reader needs it (docs/DATA_MODEL.md sections 3 and 5).
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Recording);
        var paths = workspace.Paths;

        SessionManifestStore.Save(paths.ManifestPath, new SessionManifest
        {
            SessionId = paths.SessionId,
            Title = "Interrupted process loopback",
            Mode = SessionModes.Online,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Recording,
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic, AudioSources.Loopback },
            ChunkSeconds = 60,
            Degraded = true,
            TrackHealth = new[]
            {
                new TrackHealth(AudioSources.Mic, AudioBufferHealth.Empty, 0, 0, false, null, 1, 0),
                new TrackHealth(AudioSources.Loopback, AudioBufferHealth.Empty, 0, 0, true, "timeline_unusable", 0, 0),
            },
        });

        var report = new SessionRecoveryScanner(workspace.Database, new FakeClock()).Scan(workspace.DataRoot);

        Assert.Equal(1, report.RecoveredSessions);
        Assert.True(SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error), error);

        // Recovery rewrote the session's status, and the per-track reason is still there.
        Assert.Equal(SessionStatus.Interrupted, manifest!.Status);
        Assert.NotNull(manifest.RecoveredAt);

        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.Equal("timeline_unusable", loopbackHealth.EndReason);
        Assert.True(loopbackHealth.Degraded);
        Assert.Null(manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic).EndReason);
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