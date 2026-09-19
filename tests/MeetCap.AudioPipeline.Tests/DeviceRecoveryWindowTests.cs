using System.Runtime.CompilerServices;
using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// Issue #34: a configured endpoint that disappears and comes back — which is what a
/// Bluetooth headset does when it is switched off and on again, or simply walks back into
/// range — must be recovered within a bounded window instead of being declared fatally lost
/// after a fixed handful of seconds, and an outage that really is unrecoverable must be
/// quantified as a gap rather than reported as <c>gap_count: 0</c>.
/// </summary>
/// <remarks>
/// These are the tests behind the issue's first Acceptance Criterion (an automated regression
/// test for temporary endpoint loss followed by endpoint recovery). The issue's real-hardware
/// criterion needs a physical Bluetooth microphone and loopback render endpoint and is
/// therefore <em>not</em> covered here; see docs/M1_WINDOWS_VALIDATION.md.
/// </remarks>
public class DeviceRecoveryWindowTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    /// <summary>
    /// How long the endpoint is gone in these tests. Deliberately longer than the three
    /// one-second retries the pre-#34 code performed, so a test only passes when the
    /// configured window — not the old fixed budget — is what keeps the track alive.
    /// </summary>
    private const int BluetoothOutageSeconds = 12;

    /// <summary>
    /// A recovery-attempt gate for a <see cref="SessionHarness"/>: the first recovery attempt of
    /// each track parks on it, which is the only moment at which a whole device outage can be
    /// dated before it is measured. <see cref="Entries"/> counts the tracks that have parked, so
    /// a test can wait until every track of interest is actually inside the gate instead of
    /// sleeping; later attempts pass straight through, because the budget is what ends a track
    /// and a gate that held every attempt would deadlock once the window was spent
    /// (docs/RELIABILITY.md section 7).
    /// </summary>
    private sealed class Gate : IDisposable
    {
        private readonly CancellationTokenSource _abort = new();
        private readonly SemaphoreSlim _permits = new(0);
        private int _entries;

        /// <summary>How many recovery attempts have parked on this gate.</summary>
        public int Entries => Volatile.Read(ref _entries);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            // Only the first attempt of each track is dated; the rest of the budget passes
            // straight through, because a gate that held every attempt would never let a track
            // exhaust its window.
            if (Interlocked.Increment(ref _entries) > 1)
            {
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _abort.Token);

            try
            {
                await _permits.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The session ended (or the test gave up) while this attempt was parked. The
                // track's own cancellation path treats that the same way a cancelled reopen does.
            }
        }

        /// <summary>Opens the gate for <paramref name="count"/> parked attempts.</summary>
        public void Release(int count) => _permits.Release(count);

        public void Dispose()
        {
            _abort.Cancel();
            _abort.Dispose();
            _permits.Dispose();
        }
    }

    /// <summary>
    /// Waits until <paramref name="expectedEntries"/> tracks have parked, then dates the outage
    /// and opens the gate. Runs as a separate task because <see cref="FakeCaptureSource.Fail"/>
    /// does not return until the capture loop parks.
    /// </summary>
    private static async Task AdvanceClockAndRelease(
        SessionHarness harness,
        Gate gate,
        int outageSeconds,
        int expectedEntries)
    {
        Assert.True(
            await Wait.UntilAsync(() => gate.Entries >= expectedEntries, timeoutMs: 15_000),
            $"only {gate.Entries} of {expectedEntries} recovery attempt(s) reached the gate");

        // Advanced while every tracked attempt is parked, so the measurement that follows the
        // release always observes it.
        harness.Clock.Advance(TimeSpan.FromSeconds(outageSeconds));
        gate.Release(gate.Entries);
    }

    [Fact]
    public void RecoveryAttemptsFor_SpendsTheConfiguredWindowAtOneAttemptPerBackoff()
    {
        // The window is the policy an operator configures; the attempt count is only how it is
        // spent. They are derived from one value so they cannot drift apart, and a longer
        // window must buy proportionally more attempts (docs/CONFIGURATION.md section 6).
        var backoffSeconds = CaptureTrack.DeviceRecoveryBackoffMs / 1000;

        Assert.Equal(0, CaptureService.RecoveryAttemptsFor(0));
        Assert.Equal(3, CaptureService.RecoveryAttemptsFor(3));
        Assert.Equal(20, CaptureService.RecoveryAttemptsFor(20));
        Assert.Equal(60, CaptureService.RecoveryAttemptsFor(60));
        Assert.Equal(
            CaptureService.RecoveryAttemptsFor(10) * backoffSeconds,
            CaptureService.RecoveryAttemptsFor(10 * backoffSeconds));
    }

    [Fact]
    public void RecoveryAttemptsFor_ANegativeWindowIsTreatedAsNoRecovery()
    {
        // Validation rejects a negative window before a session starts, so reaching the
        // derivation with one means a caller bypassed configuration. Failing closed (no
        // retries) is the only safe reading: a negative window must never buy more attempts
        // than a zero window.
        Assert.Equal(0, CaptureService.RecoveryAttemptsFor(-1));
        Assert.Equal(0, CaptureService.RecoveryAttemptsFor(int.MinValue));
    }

    [Fact]
    public async Task RunAsync_EndpointReturnsAfterTwelveSeconds_RecoversWithinTheConfiguredWindow()
    {
        // The pre-#34 budget was three one-second retries, so a headset that needed longer than
        // three seconds was already fatal. The window is 13 seconds here and the endpoint is
        // gone for 12, so this recovers only because the window is honoured.
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: BluetoothOutageSeconds + 1,
            beforeReopenAttempt: gate.WaitAsync);

        var first = new FakeCaptureSource(Format, harness.Device);
        var second = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(first);
        harness.Sources.Enqueue(second);

        var session = harness.PrepareSessionDirect("Bluetooth Reconnect");
        var paths = pathsFor(harness, session);
        var firstChunkClosed = ClosedChunkSignal(session, AudioSource.Mic);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1), "capture did not start");

        TestAudio.EmitSeconds(first, Format, 0, milliseconds: 60_000);

        // The endpoint is gone exactly as a headset switching off is: capture stops with a
        // fault while the session is otherwise healthy.
        first.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        // The recovery attempt is parked on the gate, so the outage can be dated before it is
        // measured: the track stamps the outage's start when it notices the fault and measures
        // it when an attempt reopens the endpoint.
        await Wait.ForAsync(
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, expectedEntries: 1),
            timeoutMs: 15_000,
            "the outage to be dated and the recovery gate released");

        Assert.True(
            await Wait.UntilAsync(() => second.StartCount == 1, timeoutMs: 15_000),
            "capture was not reopened after the endpoint returned");

        TestAudio.EmitSeconds(second, Format, 0, milliseconds: 20_000);
        cancellation.Cancel();

        var outcome = await Finish(run);

        // The first chunk closed before the loss, so the audio captured before the outage is
        // durable rather than trapped in an open chunk.
        await Wait.ForAsync(firstChunkClosed.Task, timeoutMs: 5_000, "the pre-loss chunk to close");

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLost));
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceRestored));

        // The fix's whole point: the track recovered instead of ending fatally.
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));
        Assert.NotEqual("device_lost", outcome.EndReason);

        // The outage is quantified, and quantified as the whole measured window rather than the
        // fixed retry budget.
        var restored = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureDeviceRestored);
        Assert.Equal(
            BluetoothOutageSeconds * 1000L,
            restored.GetProperty("gap_ms").GetInt64());

        // The gap is carried on the session's own record, not only in the log.
        var manifest = ReadManifest(paths);
        Assert.Equal(1, manifest.GapCount);
        Assert.Equal(BluetoothOutageSeconds * 1000L, manifest.GapTotalMs);

        // Reconnecting produced a second segment, and the resumed audio is placed after the
        // measured outage instead of at the position the dead stream left behind.
        Assert.Equal(2, harness.Database.Chunks.ListForSession(session.SessionId).Count);
    }

    [Fact]
    public async Task RunAsync_EndpointNeverReturns_EndsFatallyWithTheUnrecoveredOutageAsAGap()
    {
        // The endpoint stays gone: the window closes, the track ends, and the session must not
        // claim nothing was lost. Before #34 the outage measured while retrying was discarded
        // with the track, so a session whose own log described a multi-second hole reported
        // gap_count: 0 (issue #34, docs/RELIABILITY.md section 7).
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: 3,
            beforeReopenAttempt: gate.WaitAsync);

        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.PrepareSessionDirect("Unrecoverable Bluetooth Loss");
        var paths = pathsFor(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1), "capture did not start");

        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 10_000);

        // The render/capture endpoint never comes back, so every reopen attempt fails.
        harness.Devices.Replace();
        harness.Sources.EnqueueFailure(new DeviceUnavailableException("bluetooth endpoint disconnected"));
        harness.Sources.EnqueueFailure(new DeviceUnavailableException("bluetooth endpoint disconnected"));
        source.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        await Wait.ForAsync(
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, expectedEntries: 1),
            timeoutMs: 15_000,
            "the outage to be dated and the recovery gate released");

        var outcome = await Finish(run);

        Assert.True(outcome.Degraded);
        Assert.Equal("device_lost", outcome.EndReason);

        var events = ReadEvents(paths);
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));

        // The unrecovered outage is named as a gap with its own interval and the documented
        // reason for "the audio was never captured" (docs/DATA_MODEL.md section 4).
        var gap = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureGap);
        Assert.Equal("mic", gap.GetProperty("source").GetString());
        Assert.Equal(10_000, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(10_000 + (BluetoothOutageSeconds * 1000L), gap.GetProperty("gap_end_ms").GetInt64());
        Assert.Equal(BluetoothOutageSeconds * 1000L, gap.GetProperty("gap_ms").GetInt64());
        Assert.Equal(AudioGapReasons.NotCaptured, gap.GetProperty("reason").GetString());

        // The session's durable record agrees, so a reader of session.json sees the missing
        // audio without having to parse the event log.
        var manifest = ReadManifest(paths);
        Assert.Equal(1, manifest.GapCount);
        Assert.Equal(BluetoothOutageSeconds * 1000L, manifest.GapTotalMs);
        Assert.Equal(1, outcome.GapCount);
        Assert.Equal(BluetoothOutageSeconds * 1000L, outcome.GapTotalMs);

        // The session timeline now includes the lost stretch, so the reported duration is the
        // audio time the session really accounted for, not just the last captured buffer.
        Assert.Equal(10_000 + (BluetoothOutageSeconds * 1000L), outcome.DurationMs);

        // A track that ends still closes its audio: the tail is durable, not a .part.
        Assert.Single(Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.wav"));
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.part"));
    }

    [Fact]
    public async Task RunAsync_OnlineSession_BothTracksRecoverFromTheirOwnBluetoothLoss()
    {
        // Issue #34 configures both the microphone and the render endpoint to the same headset,
        // so both tracks lose their endpoint at the same moment. The reconnect behaviour must be
        // deterministic for both, not only for the microphone
        // (issue #34 Acceptance Criteria, docs/RELIABILITY.md section 8).
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: BluetoothOutageSeconds + 1,
            mode: SessionModes.Online,
            beforeReopenAttempt: gate.WaitAsync);

        var micFirst = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var micSecond = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var loopbackFirst = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        var loopbackSecond = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.Enqueue(micFirst);
        harness.Sources.Enqueue(micSecond);
        harness.Sources.EnqueueLoopback(loopbackFirst);
        harness.Sources.EnqueueLoopback(loopbackSecond);

        var session = harness.PrepareSessionDirect("Online Bluetooth Reconnect", online: true);
        var paths = pathsFor(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(
            await Wait.UntilAsync(() => micFirst.StartCount == 1 && loopbackFirst.StartCount == 1),
            "both tracks did not start");

        TestAudio.EmitSeconds(micFirst, Format, 0, milliseconds: 10_000, AudioSource.Mic);
        TestAudio.EmitSeconds(loopbackFirst, Format, 0, milliseconds: 10_000, AudioSource.Loopback);

        // The headset disappears from both ends at once.
        micFirst.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));
        loopbackFirst.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        // Both tracks park on the shared seam, so the outage is dated for the microphone and the
        // loopback alike: neither can measure its downtime before the clock has moved.
        await Wait.ForAsync(
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, expectedEntries: 2),
            timeoutMs: 15_000,
            "the outage to be dated and the recovery gate released");

        Assert.True(
            await Wait.UntilAsync(() => micSecond.StartCount == 1, timeoutMs: 15_000),
            "the microphone track did not reopen its endpoint");
        Assert.True(
            await Wait.UntilAsync(() => loopbackSecond.StartCount == 1, timeoutMs: 15_000),
            "the loopback track did not reopen its endpoint");

        TestAudio.EmitSeconds(micSecond, Format, 0, milliseconds: 10_000, AudioSource.Mic);
        TestAudio.EmitSeconds(loopbackSecond, Format, 0, milliseconds: 10_000, AudioSource.Loopback);
        cancellation.Cancel();

        var outcome = await Finish(run);

        var events = ReadEvents(paths);

        // One loss and one restore per track, and no track was declared fatally lost.
        Assert.Equal(2, CountEvents(events, SessionEventNames.CaptureDeviceRestored));
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));
        Assert.DoesNotContain(events, e => Name(e) == SessionEventNames.CaptureDeviceLostFatal);

        var restoredSources = events
            .Where(e => Name(e) == SessionEventNames.CaptureDeviceRestored)
            .Select(e => e.GetProperty("source").GetString())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "loopback", "mic" }, restoredSources);

        // Both tracks wrote the audio from both sides of the outage.
        var manifest = ReadManifest(paths);
        Assert.Equal(2, manifest.TrackHealth.Count);
        Assert.All(manifest.TrackHealth, health => Assert.Equal(2, health.ChunksClosed));
        Assert.All(manifest.TrackHealth, health => Assert.Null(health.EndReason));

        // The session did not end because of the loss, and it is degraded — the gap is real.
        Assert.True(outcome.Degraded);
        Assert.NotEqual("device_lost", outcome.EndReason);

        // Both tracks wrote a second chunk after recovering, so each really resumed capture
        // rather than only reopening the endpoint.
        Assert.Equal(4, harness.Database.Chunks.ListForSession(session.SessionId).Count);
        Assert.Equal(2, WavNames(paths, AudioSource.Mic).Length);
        Assert.Equal(2, WavNames(paths, AudioSource.Loopback).Length);
    }

    /// <summary>
    /// Dates the outage and releases the gated recovery attempt(s). Runs as a separate task
    /// because <see cref="FakeCaptureSource.Fail"/> does not return until the capture loop
    /// parks on the gate.
    /// </summary>
    /// <remarks>
    /// The release happens only after a track has actually entered the gate
    /// (<paramref name="gateEntries"/>), so the clock is always advanced inside the window
    /// between the outage being stamped and being measured. Sleeping instead would be a race.
    /// </remarks>
    private static async Task AdvanceClockAndRelease(
        SessionHarness harness,
        StrongBox<int> gateEntries,
        SemaphoreSlim gate,
        int outageSeconds)
    {
        Assert.True(
            await Wait.UntilAsync(() => Volatile.Read(ref gateEntries.Value) > 0, timeoutMs: 15_000),
            "no recovery attempt reached the gate");

        // The gates are opened after the clock is advanced, so a track that took the gate after
        // the read still blocks: it cannot measure the outage before the clock moved.
        var waiters = Volatile.Read(ref gateEntries.Value);
        harness.Clock.Advance(TimeSpan.FromSeconds(outageSeconds));
        gate.Release(waiters);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static TaskCompletionSource<ClosedAudioChunk> ClosedChunkSignal(
        RecordingSession session,
        AudioSource source)
    {
        var signal = new TaskCompletionSource<ClosedAudioChunk>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wireName = source.ToWireName();
        session.ChunkClosed += chunk =>
        {
            if (string.Equals(chunk.Source, wireName, StringComparison.Ordinal))
            {
                signal.TrySetResult(chunk);
            }
        };
        return signal;
    }

    private static SessionPaths pathsFor(SessionHarness harness, RecordingSession session)
        => new(harness.DataRoot, session.SessionId);

    private static Task<RecordingSessionOutcome> Finish(Task<RecordingSessionOutcome> run)
        => Wait.ForAsync(run, timeoutMs: 60_000, "the recording session");

    private static string[] WavNames(SessionPaths paths, AudioSource source)
        => Directory.GetFiles(paths.AudioDirectory(source), "*.wav")
            .Select(p => Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static SessionManifest ReadManifest(SessionPaths paths)
    {
        SessionManifestStore.TryLoad(paths.ManifestPath, out var manifest, out var error);
        Assert.True(manifest is not null, error);
        return manifest!;
    }

    private static string? Name(JsonElement element)
        => element.TryGetProperty("event", out var name) ? name.GetString() : null;

    private static int CountEvents(IReadOnlyList<JsonElement> events, string name)
        => events.Count(e => Name(e) == name);

    private static IReadOnlyList<JsonElement> ReadEvents(SessionPaths paths)
    {
        if (!File.Exists(paths.EventsPath))
        {
            return Array.Empty<JsonElement>();
        }

        string[] lines;
        using (var stream = new FileStream(paths.EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            lines = reader.ReadToEnd().Split('\n');
        }

        var elements = new List<JsonElement>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            elements.Add(document.RootElement.Clone());
        }

        return elements;
    }
}
