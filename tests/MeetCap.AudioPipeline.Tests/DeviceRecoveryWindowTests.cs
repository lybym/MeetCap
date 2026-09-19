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
    /// <em>each</em> track parks on it, which is the only moment at which a whole device outage
    /// can be dated before it is measured. <see cref="Entries"/> counts the tracks that have
    /// parked, so a test can wait until every track of interest is actually inside the gate
    /// instead of sleeping; later attempts pass straight through, because the budget is what ends
    /// a track and a gate that held every attempt would deadlock once the window was spent
    /// (docs/RELIABILITY.md section 7).
    /// </summary>
    /// <remarks>
    /// The gate keeps one entry per track rather than one for the session, because a single
    /// shared entry could only ever hold the first track that reached it: in an online session
    /// the microphone and the loopback lose their endpoint at the same instant, so which track
    /// that was would be decided by thread scheduling, and the other track's outage would be
    /// measured against a real <see cref="Task.Delay(int)"/> instead of the dated clock.
    /// <see cref="WaitFor"/> therefore hands each track its own seam
    /// (docs/DEVELOPMENT.md section 6).
    /// </remarks>
    private sealed class Gate : IDisposable
    {
        private readonly CancellationTokenSource _abort = new();
        private readonly SemaphoreSlim _permits = new(0);
        private readonly HashSet<AudioSource> _entered = new();
        private readonly object _sync = new();

        /// <summary>How many tracks have a recovery attempt parked on this gate.</summary>
        public int Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entered.Count;
                }
            }
        }

        /// <summary>
        /// The seam to install for one track. Each track gets its own function, so every track's
        /// first attempt parks independently of the others.
        /// </summary>
        public Func<CancellationToken, Task> WaitFor(AudioSource track)
            => cancellationToken => WaitAsync(track, cancellationToken);

        private async Task WaitAsync(AudioSource track, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                // Only the first attempt of this track is dated; the rest of its budget passes
                // straight through, because a gate that held every attempt would never let the
                // track exhaust its window.
                if (!_entered.Add(track))
                {
                    return;
                }
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

        /// <summary>Opens the gate for every attempt currently parked on it.</summary>
        public void ReleaseAll()
        {
            int parked;
            lock (_sync)
            {
                parked = _entered.Count;
            }

            if (parked > 0)
            {
                _permits.Release(parked);
            }
        }

        public void Dispose()
        {
            _abort.Cancel();
            _abort.Dispose();
            _permits.Dispose();
        }
    }

    /// <summary>
    /// Waits until the tracks in <paramref name="tracks"/> have all parked, then dates the
    /// outage on the fake clock and opens the gate.
    /// </summary>
    /// <remarks>
    /// The clock is advanced only once every listed track is inside its own seam, so the
    /// measurement each of them takes after the release always observes the advance. Nothing
    /// here depends on which track arrives first, because the entries are per track
    /// (docs/DEVELOPMENT.md section 6).
    /// </remarks>
    private static async Task AdvanceClockAndRelease(
        SessionHarness harness,
        Gate gate,
        int outageSeconds,
        params AudioSource[] tracks)
    {
        Assert.NotEmpty(tracks);
        Assert.True(
            await Wait.UntilAsync(() => gate.Entries >= tracks.Length, timeoutMs: 15_000),
            $"only {gate.Entries} of {tracks.Length} recovery attempt(s) reached the gate");

        harness.Clock.Advance(TimeSpan.FromSeconds(outageSeconds));
        gate.ReleaseAll();
    }

    [Fact]
    public void RecoveryAttemptsFor_SpendsTheConfiguredWindowAtOneAttemptPerBackoff()
    {
        // The window is the policy an operator configures; the attempt count is only how it is
        // spent. They are derived from one value so they cannot drift apart, and a longer
        // window must buy proportionally more attempts (docs/CONFIGURATION.md section 6).
        AssertAttempts(0, 0);
        AssertAttempts(3, 3);
        AssertAttempts(20, 20);
        AssertAttempts(60, 60);

        // Doubling the window doubles the budget, which is the "spends the window" property
        // stated as arithmetic rather than as a restatement of the derivation.
        Assert.Equal(
            2 * CaptureService.RecoveryAttemptsFor(10),
            CaptureService.RecoveryAttemptsFor(20));
    }

    [Fact]
    public void RecoveryAttemptsFor_ANegativeWindowIsTreatedAsNoRecovery()
    {
        // Validation rejects a negative window before a session starts, so reaching the
        // derivation with one means a caller bypassed configuration. Failing closed (no
        // retries) is the only safe reading: a negative window must never buy more attempts
        // than a zero window.
        AssertAttempts(-1, 0);
        AssertAttempts(int.MinValue, 0);
    }

    [Fact]
    public void RecoveryAttemptsFor_AWindowPastTheIntMillisecondBoundaryBuysMoreAttemptsNotFewer()
    {
        // Seconds times milliseconds overflows int above 2,147,483 seconds. The wrapped value is
        // negative, and both consumers clamp a negative budget to zero, so an operator who
        // configured a very long window silently got no recovery at all — the opposite of the
        // policy they configured, and the exact class of silent inversion issue #34 is about.
        // Every value here is one ConfigurationValidator accepts, so every one must buy at least
        // as many attempts as the window below it (docs/CONFIGURATION.md section 6).
        var boundary = int.MaxValue / 1000;          // 2,147,483 s — the last second that fits
        var justPastBoundary = boundary + 1;         // 2,147,484 s — overflows an int multiply
        var days = 365 * 24 * 60 * 60;               // 31,536,000 s — one year, still an int
        var beforeOverflow = CaptureService.RecoveryAttemptsFor(boundary);
        var pastOverflow = CaptureService.RecoveryAttemptsFor(justPastBoundary);

        Assert.True(
            pastOverflow > beforeOverflow,
            $"a window of {justPastBoundary} s bought {pastOverflow} attempts, no more than the {beforeOverflow} " +
            $"that {boundary} s bought");
        Assert.True(pastOverflow > 0, "a window past the int/millisecond boundary must still buy recovery attempts");

        // One year of window is one million one-second attempts, which is nowhere near the
        // saturation point, so the derivation is exact well past the overflow boundary.
        AssertAttempts(days, days);
        AssertAttempts(boundary, boundary);
        AssertAttempts(justPastBoundary, justPastBoundary);

        // The very longest window an int can express is exactly representable as an attempt count:
        // one second of window is one attempt, so int.MaxValue seconds is int.MaxValue attempts.
        // The boundary is exact rather than saturated, which is why the derivation narrows with a
        // checked conversion instead of clamping (docs/CONFIGURATION.md section 6).
        var longestWindow = CaptureService.RecoveryAttemptsFor(int.MaxValue);
        Assert.Equal(int.MaxValue, longestWindow);
        Assert.True(
            longestWindow >= CaptureService.RecoveryAttemptsFor(boundary),
            "a longer window must never buy fewer attempts than a shorter one");
    }

    [Fact]
    public void RecoveryAttemptsFor_AWindowNoIntCanExpressIsRefusedRatherThanWrapped()
    {
        // Configuration cannot reach this: validation carries the window as an int, and the longest
        // int the derivation accepts (int.MaxValue seconds) is exactly int.MaxValue attempts. The
        // guard exists because the alternative for a caller that could express a longer window is
        // the silent wrap to a negative budget that this derivation exists to remove — so it throws
        // rather than returning "no recovery at all" (docs/CONFIGURATION.md section 6).
        var tooLong = (long)int.MaxValue + 1;

        Assert.Throws<OverflowException>(() => CaptureService.RecoveryAttemptsFor(tooLong));

        // The last window that is still representable does not throw, so the guard is exactly at
        // the documented bound and not one attempt short of it.
        Assert.Equal(int.MaxValue, CaptureService.RecoveryAttemptsFor((long)int.MaxValue));
    }

    [Fact]
    public void CaptureService_AVeryLongConfiguredWindowStillGetsARecoveryBudget()
    {
        // Pins the whole path the finding described: validation accepts the window, the settings
        // carry it, and the production constructor derives a budget that is handed to the tracks.
        // A negative or zero budget here is the silent policy inversion, so this must be derived
        // and positive (docs/CONFIGURATION.md section 6, docs/RELIABILITY.md section 8.1).
        var veryLongWindow = int.MaxValue / 1000 + 1;
        using var harness = new SessionHarness(chunkSeconds: 60, deviceRecoverySeconds: veryLongWindow);

        var settings = new CaptureSettings(
            harness.DataRoot,
            chunkSeconds: 60,
            bufferSeconds: 60,
            flushIntervalMs: 1_000,
            minimumFreeSpaceGb: 5,
            microphoneDeviceId: "mic-default",
            configVersion: 1,
            deviceRecoverySeconds: veryLongWindow);

        var service = new CaptureService(
            harness.Platform,
            harness.Database,
            settings,
            maxDeviceRecoveryAttempts: null);

        Assert.Equal(
            CaptureService.RecoveryAttemptsFor(veryLongWindow),
            service.MaxDeviceRecoveryAttempts);
        Assert.True(
            service.MaxDeviceRecoveryAttempts > 0,
            $"a configured window of {veryLongWindow} s must still buy recovery attempts");
    }

    [Fact]
    public void CaptureService_ANonDefaultConfiguredWindowIsTheBudgetTheTrackSpends()
    {
        // The window a user writes in TOML is the window a track spends. This is checked with a
        // value that is not CaptureSettings' default, because a default-valued assertion cannot
        // fail if the setting were ignored altogether (docs/CONFIGURATION.md section 6,
        // docs/RELIABILITY.md section 8.1).
        const int configuredWindowSeconds = 7;
        using var harness = new SessionHarness(chunkSeconds: 60, deviceRecoverySeconds: configuredWindowSeconds);

        Assert.Equal(configuredWindowSeconds, harness.Settings.DeviceRecoverySeconds);

        // Derived through the shipped path and handed to the session, not restated by the test:
        // the harness builds its RecordingSession from the service's own derivation.
        Assert.Equal(configuredWindowSeconds, harness.DeviceRecoveryAttempts);
        Assert.Equal(configuredWindowSeconds, harness.Service.MaxDeviceRecoveryAttempts);
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
            beforeReopenAttempt: gate.WaitFor);

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
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, AudioSource.Mic),
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
            beforeReopenAttempt: gate.WaitFor);

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
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, AudioSource.Mic),
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

        // The window really closed, so the detail says so and must not borrow either of the other
        // terminal causes' wording (docs/RELIABILITY.md section 8.2).
        AssertTerminalGapDetail(
            gap,
            states: "did not return within the recovery window",
            contradicts: new[] { "session was stopped", "returned but at a different format" });

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
            beforeReopenAttempt: gate.WaitFor);

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

        // Each track parks on its own seam, so the outage is dated for the microphone and the
        // loopback alike: neither can measure its downtime before the clock has moved, and
        // neither depends on which of them reached the recovery path first.
        await Wait.ForAsync(
            AdvanceClockAndRelease(
                harness, gate, BluetoothOutageSeconds, AudioSource.Mic, AudioSource.Loopback),
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

    [Fact]
    public async Task RunAsync_OneTrackRecoversWhileTheOtherDoesNot_EachReportsItsOwnOutcome()
    {
        // The one-sided case a real headset produces when only one of its two endpoints finishes
        // re-enumerating: the microphone comes back inside the window while the render endpoint
        // stays gone. The recovered track must resume and quantify its outage, the stranded track
        // must end fatally and quantify its own — and neither outcome may be folded into the
        // other (issue #34 Acceptance Criteria 3 and 4, docs/RELIABILITY.md section 8).
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: 3,
            mode: SessionModes.Online,
            beforeReopenAttempt: gate.WaitFor);

        // Both tracks lose their endpoint at the same instant and both measure this much downtime:
        // the clock is advanced under the gate, and the loopback's whole attempt budget is spent
        // before it advances again.
        const int MeasuredOutageSeconds = 2;

        var micFirst = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var micSecond = new FakeCaptureSource(Format, harness.Device, AudioSource.Mic);
        var loopbackFirst = new FakeCaptureSource(Format, harness.RenderDevice, AudioSource.Loopback);
        harness.Sources.Enqueue(micFirst);
        harness.Sources.Enqueue(micSecond);
        harness.Sources.EnqueueLoopback(loopbackFirst);

        var session = harness.PrepareSessionDirect("One-Sided Bluetooth Loss", online: true);
        var paths = pathsFor(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(
            await Wait.UntilAsync(() => micFirst.StartCount == 1 && loopbackFirst.StartCount == 1),
            "both tracks did not start");

        const int RecordedMs = 5_000;
        TestAudio.EmitSeconds(micFirst, Format, 0, milliseconds: RecordedMs, AudioSource.Mic);
        TestAudio.EmitSeconds(loopbackFirst, Format, 0, milliseconds: RecordedMs, AudioSource.Loopback);

        micFirst.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));
        loopbackFirst.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        // The microphone endpoint is still being enumerated, so its track reopens it; the render
        // endpoint is not, so the loopback track spends its whole window and gives up.
        harness.Devices.SetRenderDevices();

        await Wait.ForAsync(
            AdvanceClockAndRelease(
                harness, gate, MeasuredOutageSeconds, AudioSource.Mic, AudioSource.Loopback),
            timeoutMs: 15_000,
            "both tracks to enter the recovery gate and the outage to be dated");

        // The loopback track runs out its window (three one-second backoffs against the fake
        // clock's fixed instant plus the real backoffs), so its fatal end is awaited, not assumed.
        Assert.True(
            await Wait.UntilAsync(
                () => HasEvent(paths, SessionEventNames.CaptureDeviceLostFatal),
                timeoutMs: 15_000),
            "the loopback track did not report a fatal device loss");

        Assert.True(
            await Wait.UntilAsync(() => micSecond.StartCount == 1, timeoutMs: 15_000),
            "the microphone track did not reopen its endpoint");

        // Nothing more is emitted from the reopened microphone, so the loopback's terminal gap is
        // never placed by a buffer and the microphone's measured outage stays pending until the
        // session stops — both of which exercise the accounting paths being asserted below.
        cancellation.Cancel();
        var outcome = await Finish(run);

        var events = ReadEvents(paths);

        // Exactly one track recovered and exactly one was declared fatally lost.
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceRestored));
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));
        Assert.Equal(
            "mic",
            Assert.Single(events, e => Name(e) == SessionEventNames.CaptureDeviceRestored)
                .GetProperty("source").GetString());

        var fatal = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureDeviceLostFatal);
        Assert.Equal("loopback", fatal.GetProperty("source").GetString());

        // Each track reports its own outage, and neither is attributed to the other.
        var gaps = events
            .Where(e => Name(e) == SessionEventNames.CaptureGap)
            .Select(e => (Source: e.GetProperty("source").GetString(), Ms: e.GetProperty("gap_ms").GetInt64()))
            .ToArray();
        Assert.Equal(2, gaps.Length);
        Assert.Contains(gaps, g => g.Source == "mic" && g.Ms == MeasuredOutageSeconds * 1000L);
        Assert.Contains(gaps, g => g.Source == "loopback" && g.Ms == MeasuredOutageSeconds * 1000L);

        // The manifest agrees per track: the microphone resumed and ended for no fault of its own;
        // the loopback is degraded with a named end reason and one durable chunk from before the loss.
        var manifest = ReadManifest(paths);
        var micHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic);
        var loopbackHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Loopback);
        Assert.Null(micHealth.EndReason);
        Assert.Equal("device_lost", loopbackHealth.EndReason);
        Assert.True(loopbackHealth.Degraded);
        Assert.Equal(1, loopbackHealth.ChunksClosed);

        // The audio each track captured before the loss is durable, and the loopback (which never
        // resumed) did not invent a chunk from a device it refused.
        Assert.Single(WavNames(paths, AudioSource.Mic));
        Assert.Single(WavNames(paths, AudioSource.Loopback));
        Assert.Equal(2, harness.Database.Chunks.ListForSession(session.SessionId).Count);

        // The session as a whole is degraded, because one of its tracks really is missing audio.
        Assert.True(outcome.Degraded);
    }

    [Fact]
    public async Task RunAsync_SessionStopsAfterARecoveryButBeforeItsFirstBuffer_StillCountsTheOutage()
    {
        // The endpoint comes back, the track reopens it, and the operator stops the session before
        // the reopened endpoint delivers a single buffer. The deferral that normally places the
        // measured outage — the next buffer does it — then never happens, so the outage used to be
        // dropped and the session reported a hole it had measured as no gap at all. The measured
        // outage is real missing audio on a span this track did capture, so it must still be
        // counted (issue #34 Acceptance Criterion 4, docs/RELIABILITY.md section 8.2).
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: BluetoothOutageSeconds + 1,
            beforeReopenAttempt: gate.WaitFor);

        var first = new FakeCaptureSource(Format, harness.Device);
        var second = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(first);
        harness.Sources.Enqueue(second);

        var session = harness.PrepareSessionDirect("Bluetooth Reconnect Then Stop");
        var paths = pathsFor(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1), "capture did not start");

        const int RecordedMs = 5_000;
        TestAudio.EmitSeconds(first, Format, 0, milliseconds: RecordedMs);
        first.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        // The outage is dated and the attempt released only once the track is inside the gate, so
        // the clock advance always lands between the outage being stamped and being measured.
        await Wait.ForAsync(
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, AudioSource.Mic),
            timeoutMs: 15_000,
            "the outage to be dated and the recovery gate released");

        Assert.True(
            await Wait.UntilAsync(() => second.StartCount == 1, timeoutMs: 15_000),
            "capture was not reopened after the endpoint returned");

        // Stop immediately: the reopened endpoint never delivers a buffer, so the outage has no
        // next buffer to place it.
        cancellation.Cancel();
        var outcome = await Finish(run);

        var events = ReadEvents(paths);

        // The recovery really happened, and the track did not end fatally.
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceRestored));
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));
        Assert.NotEqual("device_lost", outcome.EndReason);

        // The measured outage is still reported as a named gap on the session's own record, with
        // the same interval and reason the unrecoverable case uses.
        var gap = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureGap);
        Assert.Equal("mic", gap.GetProperty("source").GetString());
        Assert.Equal(RecordedMs, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(RecordedMs + (BluetoothOutageSeconds * 1000L), gap.GetProperty("gap_end_ms").GetInt64());
        Assert.Equal(BluetoothOutageSeconds * 1000L, gap.GetProperty("gap_ms").GetInt64());
        Assert.Equal(AudioGapReasons.NotCaptured, gap.GetProperty("reason").GetString());

        // The endpoint did come back and the session stopped afterwards, so the detail has to name
        // that cause; claiming the window closed, or that the format changed, would contradict the
        // capture.device_restored event next to it (docs/RELIABILITY.md section 8.2).
        AssertTerminalGapDetail(
            gap,
            states: "recording was stopped while this track was still recovering",
            contradicts: new[] { "did not return within the recovery window", "different format" });

        var manifest = ReadManifest(paths);
        Assert.Equal(1, manifest.GapCount);
        Assert.Equal(BluetoothOutageSeconds * 1000L, manifest.GapTotalMs);

        // The outage extends the recorded timeline, so the session lasts the audio time it really
        // accounts for rather than stopping at the last captured buffer.
        Assert.Equal(RecordedMs + (BluetoothOutageSeconds * 1000L), outcome.DurationMs);

        // The track is degraded, because audio is missing from a span it did capture.
        Assert.True(outcome.Degraded);
        Assert.True(manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic).Degraded);

        // Exactly one gap was counted: the outage is not also reported as a turn-of-stream gap when
        // the track finalizes.
        Assert.Equal(1, outcome.GapCount);
    }

    [Fact]
    public async Task RunAsync_SessionStopsWhileTheRecoveryWindowIsStillOpen_StillCountsTheOutage()
    {
        // The endpoint is still gone and the window is still open when the operator stops. The
        // outage measured up to that moment is real missing audio on a span this track did capture,
        // so it must be quantified: before this was fixed the cancellation returned before the
        // outage was ever measured, and a session whose own log carried capture.device_lost plus a
        // capture.discontinuity per failed retry reported gap_count: 0 / gap_total_ms: 0 — the same
        // class of defect as the exhausted window that was already fixed (issue #34 Acceptance
        // Criterion 4, docs/RELIABILITY.md section 8.2).
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: BluetoothOutageSeconds + 1,
            beforeReopenAttempt: gate.WaitFor);

        var first = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(first);

        var session = harness.PrepareSessionDirect("Bluetooth Loss Then Stop");
        var paths = pathsFor(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1), "capture did not start");

        const int RecordedMs = 5_000;
        TestAudio.EmitSeconds(first, Format, 0, milliseconds: RecordedMs);

        // The endpoint never comes back while the window is open: the empty enumerator makes every
        // reopen attempt fail, so the track keeps retrying against the real one-second backoff for
        // as long as the test lets it.
        harness.Devices.Replace();
        first.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        // The first attempt is dated on the gate, so the synthetic outage is deterministic and
        // measured whatever else the run does afterwards.
        const int DatedOutageSeconds = 2;
        await Wait.ForAsync(
            AdvanceClockAndRelease(harness, gate, DatedOutageSeconds, AudioSource.Mic),
            timeoutMs: 15_000,
            "the outage to be dated and the recovery gate released");

        // Let further attempts fail and measure real elapsed time, then stop with the window still
        // open: the track is inside TryRecoverDeviceAsync when the cancellation arrives.
        await Task.Delay(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var outcome = await Finish(run);

        var events = ReadEvents(paths);

        // The endpoint really never returned, and the track never ended fatally by itself.
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureDeviceRestored));
        Assert.Equal(0, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));
        Assert.NotEqual("device_lost", outcome.EndReason);
        Assert.True(
            CountEvents(events, SessionEventNames.CaptureDiscontinuity) > 0,
            "the retries that failed during the outage were not reported");

        // The outage measured before the stop is named, and its interval starts where capture
        // stopped: the synthetic advance plus the real backoff time the attempts spent.
        var gap = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureGap);
        Assert.Equal("mic", gap.GetProperty("source").GetString());
        Assert.Equal(RecordedMs, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(AudioGapReasons.NotCaptured, gap.GetProperty("reason").GetString());

        var measuredMs = gap.GetProperty("gap_ms").GetInt64();
        Assert.InRange(measuredMs, DatedOutageSeconds * 1000L, 10_000L);
        Assert.Equal(RecordedMs + measuredMs, gap.GetProperty("gap_end_ms").GetInt64());

        // The session's durable record agrees, so the outage is not only in the log.
        var manifest = ReadManifest(paths);
        Assert.Equal(1, manifest.GapCount);
        Assert.Equal(measuredMs, manifest.GapTotalMs);
        Assert.Equal(1, outcome.GapCount);
        Assert.Equal(measuredMs, outcome.GapTotalMs);

        // A stop is a stop: the track is not carrying an end reason of its own, but it is degraded,
        // because audio is missing from a span it did capture.
        var micHealth = manifest.TrackHealth.Single(h => h.Source == AudioSources.Mic);
        Assert.Null(micHealth.EndReason);
        Assert.True(micHealth.Degraded);

        // The detail names the measured outage as what the track knew, not as an estimate of the
        // whole time the device stayed away, and it does not claim the window closed — it had not:
        // the stop ended the recovery (docs/RELIABILITY.md section 8.2).
        AssertTerminalGapDetail(
            gap,
            states: "recording was stopped while this track was still recovering",
            contradicts: new[] { "did not return within the recovery window", "different format" });
    }

    [Fact]
    public async Task RunAsync_DeviceIsLostBeforeAnyBufferWasPlaced_ReportsNoFabricatedGap()
    {
        // The endpoint fails before a single buffer is placed, so the track has no origin: there is
        // no captured span for audio to be missing from. A positive outage is still measured (the
        // endpoint does come back, after the fake clock moved), so the `!_hasOrigin` guard in
        // CaptureTimeline.RecordTerminalDeviceLoss is the only thing standing between the session
        // and a fabricated gap — one that would inflate both gap_total_ms and duration_ms in the
        // direction a reader cannot check (docs/RELIABILITY.md section 7).
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: BluetoothOutageSeconds + 1,
            beforeReopenAttempt: gate.WaitFor);

        var neverStarted = new FakeCaptureSource(Format, harness.Device)
        {
            StartError = new DeviceUnavailableException("bluetooth endpoint disconnected"),
        };
        var second = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(neverStarted);
        harness.Sources.Enqueue(second);

        var session = harness.PrepareSessionDirect("Lost Before Any Buffer");
        var paths = pathsFor(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        // Capture could not start, so the track reports the loss with no audio captured at all.
        Assert.True(
            await Wait.UntilAsync(
                () => HasEvent(paths, SessionEventNames.CaptureDeviceLost),
                timeoutMs: 15_000),
            "the loss before any buffer was not reported");

        await Wait.ForAsync(
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, AudioSource.Mic),
            timeoutMs: 15_000,
            "the outage to be dated and the recovery gate released");

        // The endpoint is back and the track has reopened it, but no buffer has been placed, and
        // the operator then stops.
        Assert.True(
            await Wait.UntilAsync(() => second.StartCount == 1, timeoutMs: 15_000),
            "the endpoint was not reopened after it returned");

        cancellation.Cancel();
        var outcome = await Finish(run);

        var events = ReadEvents(paths);

        // The recovery really happened and was announced with the measured outage on it.
        var restored = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureDeviceRestored);
        Assert.Equal(BluetoothOutageSeconds * 1000L, restored.GetProperty("gap_ms").GetInt64());

        // No capture.gap is invented for a span that was never captured, so neither the session
        // record nor the timeline claims audio is missing from nothing.
        Assert.DoesNotContain(events, e => Name(e) == SessionEventNames.CaptureGap);

        var manifest = ReadManifest(paths);
        Assert.Equal(0, manifest.GapCount);
        Assert.Equal(0, manifest.GapTotalMs);
        Assert.Equal(0, outcome.GapCount);
        Assert.Equal(0, outcome.GapTotalMs);
        Assert.Equal(0, outcome.DurationMs);

        // Nothing was captured, so there is no audio to be durable and the timeline never advanced:
        // a fabricated gap would have inflated duration_ms away from zero here.
        Assert.Empty(WavNames(paths, AudioSource.Mic));
    }

    [Fact]
    public async Task RunAsync_EndpointReturnsWithADifferentFormat_EndsTheTrackAndKeepsTheOutageHonest()
    {
        // A headset can come back on a different profile — hands-free instead of stereo — so the
        // endpoint resolves to the same configured id with a format the session cannot splice into
        // the audio it already captured. The track must refuse the fabrication and end, and the
        // outage it measured while waiting is still real missing audio that must be quantified
        // (issue #34 Acceptance Criterion 4, docs/RELIABILITY.md section 8).
        var gate = new Gate();
        using var harness = new SessionHarness(
            chunkSeconds: 60,
            deviceRecoverySeconds: BluetoothOutageSeconds + 1,
            beforeReopenAttempt: gate.WaitFor);

        var first = new FakeCaptureSource(Format, harness.Device);
        var reopened = new FakeCaptureSource(
            new AudioFormat(44_100, 2, 32, AudioSampleFormat.IeeeFloat),
            harness.Device);
        harness.Sources.Enqueue(first);
        harness.Sources.Enqueue(reopened);

        var session = harness.PrepareSessionDirect("Bluetooth Format Change");
        var paths = pathsFor(harness, session);

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);
        Assert.True(await Wait.UntilAsync(() => first.StartCount == 1), "capture did not start");

        const int RecordedMs = 10_000;
        TestAudio.EmitSeconds(first, Format, 0, milliseconds: RecordedMs);
        first.Fail(new DeviceUnavailableException("bluetooth endpoint disconnected"));

        await Wait.ForAsync(
            AdvanceClockAndRelease(harness, gate, BluetoothOutageSeconds, AudioSource.Mic),
            timeoutMs: 15_000,
            "the outage to be dated and the recovery gate released");

        // The endpoint reopened with a format the session cannot use, so the track ends by itself
        // and the session ends with it.
        var outcome = await Finish(run);

        var events = ReadEvents(paths);

        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureFormatChanged));
        Assert.Equal(1, CountEvents(events, SessionEventNames.CaptureDeviceLostFatal));
        Assert.Equal("device_format_changed", outcome.EndReason);

        // The reopened device was refused rather than spliced in, so nothing was captured from it.
        Assert.Empty(WavNames(paths, AudioSource.Mic).Except(new[] { "000001.wav" }));

        // The outage measured while the endpoint was gone is still reported, exactly as it is when
        // the endpoint never returns at all.
        var gap = Assert.Single(events, e => Name(e) == SessionEventNames.CaptureGap);
        Assert.Equal(RecordedMs, gap.GetProperty("gap_start_ms").GetInt64());
        Assert.Equal(RecordedMs + (BluetoothOutageSeconds * 1000L), gap.GetProperty("gap_end_ms").GetInt64());
        Assert.Equal(BluetoothOutageSeconds * 1000L, gap.GetProperty("gap_ms").GetInt64());
        Assert.Equal(AudioGapReasons.NotCaptured, gap.GetProperty("reason").GetString());

        // The interval and the reason are identical to every other terminal case, so the `detail`
        // is the only thing that can tell a reader why this audio is missing — and it must not say
        // the session stopped or that the window closed, because neither happened: the endpoint
        // came back and the track ended because the session refused its format.
        AssertTerminalGapDetail(
            gap,
            states: "returned but at a different format",
            contradicts: new[] { "session was stopped", "did not return within the recovery window" });

        var manifest = ReadManifest(paths);
        Assert.Equal(1, manifest.GapCount);
        Assert.Equal(BluetoothOutageSeconds * 1000L, manifest.GapTotalMs);
        Assert.True(outcome.Degraded);
    }

    /// <summary>
    /// Asserts that a terminal <c>capture.gap</c> says what the track's own durable record says
    /// happened, and does not state a cause that contradicts it.
    /// </summary>
    /// <remarks>
    /// The interval, the reason and the accounting are the same for every terminal case, so this
    /// is the one assertion that keeps the <c>detail</c> from silently claiming "the session
    /// stopped" (or "the device never came back") for a track that ended for a different reason
    /// (docs/RELIABILITY.md section 8.2).
    /// </remarks>
    private static void AssertTerminalGapDetail(
        JsonElement gap,
        string states,
        string[] contradicts)
    {
        var detail = gap.GetProperty("detail").GetString()!;

        Assert.Contains(states, detail, StringComparison.Ordinal);
        Assert.All(
            contradicts,
            wrong => Assert.DoesNotContain(wrong, detail, StringComparison.Ordinal));
    }

    /// <summary>
    /// Asserts the derivation's contract for one window: the count is the window spent at one
    /// attempt per backoff, and it is never negative.
    /// </summary>
    private static void AssertAttempts(int deviceRecoverySeconds, int expected)
    {
        var attempts = CaptureService.RecoveryAttemptsFor(deviceRecoverySeconds);
        Assert.Equal(expected, attempts);
        Assert.True(attempts >= 0, $"a window of {deviceRecoverySeconds} s bought {attempts} attempts");
    }

    private static bool HasEvent(SessionPaths paths, string name)
        => ReadEvents(paths).Any(e => Name(e) == name);

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
