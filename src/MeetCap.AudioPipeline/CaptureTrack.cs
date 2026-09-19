namespace MeetCap.AudioPipeline;

using System.Threading.Channels;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using MeetCap.Core.Time;
using MeetCap.Persistence.Storage;

/// <summary>
/// The per-track health snapshot a track reports to its session at completion
/// (docs/ROADMAP.md M5).
/// </summary>
internal sealed record CaptureTrackOutcome(
    string Source,
    AudioBufferHealth Health,
    long GapTotalMs,
    int GapCount,
    bool Degraded,
    string? EndReason,
    int ChunksClosed,
    long ClosedDataBytes,
    long LastTimelineMs,
    bool CaptureStarted);

/// <summary>
/// Runs one independent capture track: it owns the capture source, the bounded packet
/// queue, the chunk spool, the capture timeline, the bounded-buffer accounting and the
/// device-loss recovery for exactly one track (docs/ARCHITECTURE.md section 7,
/// docs/RELIABILITY.md section 3).
/// </summary>
/// <remarks>
/// <para>
/// A session owns one of these for the microphone track (offline and online) and a
/// second for the loopback track (online only). The two tracks never wait for one
/// another: each has its own capture callback, its own bounded queue and its own
/// consumer, so a slow or failed loopback track cannot stall the microphone track and
/// vice versa (docs/ARCHITECTURE.md section 6, docs/RELIABILITY.md section 3).
/// </para>
/// <para>
/// The track is source-agnostic: it is told how to (re)create its capture source and
/// how to re-resolve its endpoint after a loss, so the microphone and loopback paths
/// share one implementation. NAudio types stay behind the platform boundary because
/// <c>createSource</c> is a closure the composition root builds
/// (docs/DEVELOPMENT.md section 4).
/// </para>
/// <para>
/// A track that loses its device and cannot recover ends <em>itself</em> — it records
/// the loss, marks itself degraded, and returns from its capture loop. It does not end
/// the session, because the other track may still be healthy
/// (docs/RELIABILITY.md section 8). Only a stop request or a storage failure (shared
/// disk) ends the whole session.
/// </para>
/// </para>
/// </remarks>
internal sealed class CaptureTrack : IDisposable
{
    /// <summary>Assumed capture callback rate, used to bound the packet queue.</summary>
    internal const int PacketsPerSecondEstimate = 100;

    /// <summary>Backoff between device-recovery attempts.</summary>
    /// <remarks>
    /// One second, so a <see cref="CaptureSettings.DeviceRecoverySeconds"/> window of N
    /// seconds is N retries (<see cref="CaptureService.RecoveryAttemptsFor"/>). The value is
    /// fixed rather than configurable because the retry <em>rate</em> is not the policy an
    /// operator cares about — the recovery window is — and a one-second cadence is short
    /// enough that an endpoint which is coming back is noticed promptly
    /// (docs/RELIABILITY.md section 8).
    /// </remarks>
    internal const int DeviceRecoveryBackoffMs = 1_000;

    /// <summary>Rate limit for repeated stalled-consumer events.</summary>
    internal const int ConsumerStallEventIntervalMs = 1_000;

    /// <summary>
    /// The end reason a track records when the endpoint returned at a format the session
    /// refuses to splice into the audio it already captured. Named once because the capture
    /// path writes it and the terminal-gap wording reads it back
    /// (docs/RELIABILITY.md section 8).
    /// </summary>
    internal const string FormatChangedEndReason = "device_format_changed";

    /// <summary>
    /// The end reason a track records when its recovery window closed with the endpoint still
    /// gone (docs/RELIABILITY.md section 8.1).
    /// </summary>
    internal const string DeviceLostEndReason = "device_lost";

    /// <summary>
    /// Why a track is handing an unplaced outage to its consumer, which is what decides how
    /// that outage is worded in the durable log.
    /// </summary>
    /// <remarks>
    /// The interval and the reason of the resulting <c>capture.gap</c> are the same for every
    /// value; only the <c>detail</c> differs, and it is the one field that has to be true
    /// about <em>why</em> the audio after the outage is missing (docs/RELIABILITY.md
    /// section 8.2).
    /// </remarks>
    private enum TerminalDeviceLossCause
    {
        /// <summary>No outage is pending: nothing to account for.</summary>
        None = 0,

        /// <summary>
        /// The recovery window closed with the endpoint still gone, so the track ended
        /// fatally (<c>end_reason = "device_lost"</c>).
        /// </summary>
        RecoveryWindowClosed = 1,

        /// <summary>
        /// The session ended while the measured outage was still unplaced and the track had
        /// recovery time left: either the reopened endpoint never delivered a buffer, or the
        /// stop landed inside the retry loop itself.
        /// </summary>
        SessionStopped = 2,

        /// <summary>
        /// The endpoint came back at a format the session refuses to splice into the audio it
        /// already captured, so the track ended (<c>end_reason = "device_format_changed"</c>)
        /// while the measured outage was still unplaced.
        /// </summary>
        FormatChanged = 3,
    }

    private readonly SessionPaths _paths;
    private readonly CaptureSettings _settings;
    private readonly CapturePlatform _platform;
    private readonly MeetCapDatabase _database;
    private readonly ISessionEventSink _events;
    private readonly IClock _clock;
    private readonly AudioSource _source;
    private readonly CaptureDeviceInfo _device;
    private readonly Func<CaptureDeviceInfo, IAudioCaptureSource> _createSource;
    private readonly Func<CaptureDeviceInfo?> _reopenDevice;
    private readonly int _maxDeviceRecoveryAttempts;
    private readonly Action? _afterPacketWritten;
    private readonly Action<ClosedAudioChunk> _onChunkClosed;
    private readonly Action<Exception> _onStorageFailure;

    /// <summary>
    /// Test seam: awaited after a device loss and before each reopen attempt, so a test can
    /// date a whole outage deterministically. Production passes <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Built per track rather than installed once for the session, because a seam that were
    /// shared could only ever hold the first track that reached it: a dual-track test that
    /// needs <em>both</em> outages dated would then be racing the tracks against one another
    /// instead of synchronising with them (docs/DEVELOPMENT.md section 6).
    /// </remarks>
    private readonly Func<CancellationToken, Task>? _beforeReopenAttempt;

    private Action<AudioPacket>? _packetHandler;
    private EventHandler<CaptureStoppedEventArgs>? _stoppedHandler;

    private AudioFormat? _format;
    private CaptureClock _captureClock = CaptureClock.DevicePosition;
    private CaptureTimeline? _timeline;
    private ChunkSpool? _spool;
    private Channel<AudioPacket>? _channel;
    private CaptureBacklogMonitor? _backlog;

    private TaskCompletionSource? _segmentEnded;
    private Exception? _segmentFault;

    private int _deviceRestarted;

    /// <summary>
    /// The unplaced outage waiting for the consumer to account for, encoded as the
    /// <see cref="TerminalDeviceLossCause"/> it has to be worded by, or
    /// <see cref="TerminalDeviceLossCause.None"/> when there is none.
    /// </summary>
    /// <remarks>
    /// The cause travels with the flag rather than being re-derived on the consumer side,
    /// because the reason this track ended is the only thing that can truthfully word the
    /// outage and the capture thread is the only place that knows it at the moment the
    /// track ends (docs/RELIABILITY.md section 8.2).
    /// </remarks>
    private int _terminalDeviceLoss;

    private int _flushRequested;
    private int _overflowSignalled;
    private int _timelineUnusable;
    private int _announcedChunkSequence;
    private long _pendingGapMs;
    private long _stallObservedMs;
    private int _closedChunkCountAtLastStallCheck;
    private DateTimeOffset? _lastStallEventAt;
    private volatile bool _degraded;
    private volatile bool _captureStarted;
    private Exception? _storageFailure;
    private string? _endReason;
    private bool _disposed;

    internal CaptureTrack(
        SessionPaths paths,
        CaptureSettings settings,
        CapturePlatform platform,
        MeetCapDatabase database,
        ISessionEventSink events,
        IClock clock,
        AudioSource source,
        CaptureDeviceInfo device,
        Func<CaptureDeviceInfo, IAudioCaptureSource> createSource,
        Func<CaptureDeviceInfo?> reopenDevice,
        int maxDeviceRecoveryAttempts,
        Action? afterPacketWritten,
        Action<ClosedAudioChunk> onChunkClosed,
        Action<Exception> onStorageFailure,
        Func<AudioSource, Func<CancellationToken, Task>>? beforeReopenAttempt = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _source = source;
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _createSource = createSource ?? throw new ArgumentNullException(nameof(createSource));
        _reopenDevice = reopenDevice ?? throw new ArgumentNullException(nameof(reopenDevice));
        _maxDeviceRecoveryAttempts = Math.Max(0, maxDeviceRecoveryAttempts);
        _afterPacketWritten = afterPacketWritten;
        _onChunkClosed = onChunkClosed ?? throw new ArgumentNullException(nameof(onChunkClosed));
        _onStorageFailure = onStorageFailure ?? throw new ArgumentNullException(nameof(onStorageFailure));

        // The seam is built for this track's own source, so a dual-track test can hold each
        // track's first recovery attempt independently (docs/DEVELOPMENT.md section 6).
        _beforeReopenAttempt = beforeReopenAttempt?.Invoke(source);
    }

    public AudioSource Source => _source;

    public CaptureDeviceInfo Device => _device;

    public AudioFormat? Format => _format;

    /// <summary>
    /// Which device timing this track's timeline is placed by — the clock the capture
    /// source declared (docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    public CaptureClock Clock => _captureClock;

    public bool CaptureStarted => _captureStarted;

    public bool Degraded => _degraded || _storageFailure is not null;

    public Exception? StorageFailure => _storageFailure;

    public string? EndReason => _endReason;

    public int ClosedChunkCount => _spool?.ClosedChunkCount ?? 0;

    public long ClosedDataBytes => _spool?.ClosedDataBytes ?? 0;

    public long LastTimelineMs => _timeline?.LastEndMs ?? 0;

    public long GapTotalMs => _timeline?.GapTotalMs ?? 0;

    public int GapCount => _timeline?.GapCount ?? 0;

    public AudioBufferHealth Health
        => _backlog?.Snapshot(GapTotalMs, GapCount)
           ?? new AudioBufferHealth
           {
               CapacityPackets = QueueCapacity(_settings),
               GapTotalMs = GapTotalMs,
               GapCount = GapCount,
           };

    public CaptureTrackOutcome ToOutcome()
        => new(
            _source.ToWireName(),
            Health,
            GapTotalMs,
            GapCount,
            Degraded,
            _endReason,
            ClosedChunkCount,
            ClosedDataBytes,
            LastTimelineMs,
            _captureStarted);

    /// <summary>Creates the first capture source for this track. Throws on failure.</summary>
    public IAudioCaptureSource CreateInitialSource() => _createSource(_device);

    /// <summary>
    /// Initializes the per-track runtime — timeline, spool, bounded queue and backlog —
    /// from the capture source's native format and declared capture clock. Called once the
    /// source exists, before capture starts.
    /// </summary>
    public void Initialize(IAudioCaptureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var format = source.Format;
        _format = format;

        // The clock comes from the source that produced the format, so the timeline is
        // placed by timing this stream actually reports rather than by a value inferred
        // from the buffers (docs/ARCHITECTURE.md section 8.1).
        _captureClock = source.Clock;
        _timeline = new CaptureTimeline(format, _captureClock);
        _spool = new ChunkSpool(
            _paths,
            _source,
            format,
            _settings.ChunkSeconds,
            _database,
            _events,
            _clock);
        _backlog = new CaptureBacklogMonitor(QueueCapacity(_settings));
        _channel = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(_backlog.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _packetHandler = OnPacketAvailable;
        _stoppedHandler = OnCaptureStopped;
    }

    /// <summary>
    /// Runs capture segments for this track. When the device is lost the track tries to
    /// bring capture back; when it cannot, the track ends itself (degraded) without
    /// ending the session, so the other track may keep recording
    /// (docs/RELIABILITY.md section 8).
    /// </summary>
    public async Task RunCaptureLoopAsync(IAudioCaptureSource source, CancellationToken cancellationToken)
    {
        // An unexpected failure in this track's capture loop is treated as a storage-
        // class failure: the source is released and the session is asked to end, so the
        // other track cannot be left waiting on a loop that will never return. The
        // normal device-loss path ends only this track (EndTrack) and never throws
        // (docs/RELIABILITY.md section 2).
        try
        {
            await RunCaptureSegmentsAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception disposeEx) when (disposeEx is not OperationCanceledException)
            {
                // Releasing a device that already disappeared must not mask the failure.
            }

            RegisterStorageFailure(ex);
        }
    }

    private async Task RunCaptureSegmentsAsync(IAudioCaptureSource source, CancellationToken cancellationToken)
    {
        var attempts = 0;

        while (true)
        {
            var started = TryStartSegment(source);

            if (started)
            {
                await WaitForSegmentEndAsync(cancellationToken).ConfigureAwait(false);
                StopSegment(source);
            }
            else
            {
                DetachSegmentHandlers(source);
                source.Dispose();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // A stop request or a storage failure ended the session, so this track is not
                // degraded by the clean stop itself. An outage measured before the stop is still
                // missing audio, though, so it is handed to the consumer — the timeline's only
                // writer — to be recorded rather than dropped with the track. Nothing is handed
                // over when there is no pending measurement, and a track that never placed a
                // buffer has no span for audio to be missing from, so the consumer records no gap
                // for that case either (docs/RELIABILITY.md section 8.2).
                if (Volatile.Read(ref _pendingGapMs) > 0)
                {
                    Interlocked.Exchange(ref _terminalDeviceLoss, (int)TerminalDeviceLossCause.SessionStopped);
                }

                return;
            }

            if (_endReason is not null)
            {
                // The track ended for its own reason while its segment was being stopped
                // (an unusable timeline), so this is not a device loss and there is nothing
                // to recover. Returning without reporting one keeps the record honest.
                CompleteWriter();
                return;
            }

            // Capture ended without anyone asking it to, so the device went away.
            _degraded = true;

            var downtimeStart = _clock.UtcNow;
            _events.Write(new SessionEvent(SessionEventNames.CaptureDeviceLost, CurrentTimelineMs())
            {
                Source = _source.ToWireName(),
                Detail = started
                    ? _segmentFault?.Message ?? "capture stopped unexpectedly"
                    : "capture could not be started",
            });

            var (replacement, nextAttempts, exhausted) =
                await TryRecoverDeviceAsync(attempts, downtimeStart, cancellationToken).ConfigureAwait(false);
            attempts = nextAttempts;

            if (exhausted)
            {
                WriteDeviceLostFatal(attempts);
                EndTrack(DeviceLostEndReason);
                CompleteWriter();
                return;
            }

            if (replacement is null)
            {
                // The session ended while capture was being recovered.
                CompleteWriter();
                return;
            }

            if (!FormatsMatch(_format, replacement.Format))
            {
                WriteFormatChangedFatal(_format!, replacement.Format);
                replacement.Dispose();
                EndTrack(FormatChangedEndReason);
                CompleteWriter();
                return;
            }

            source = replacement;
        }
    }

    /// <summary>
    /// Completes this track's packet writer so its consumer can drain to completion and
    /// finalize the track's open chunk in its own <c>finally</c>.
    /// </summary>
    /// <remarks>
    /// Called by the session once every capture loop has ended, and by
    /// <see cref="RunCaptureSegmentsAsync"/> on the path where <em>this</em> track ends on
    /// its own. It must be called on that path too: the fatal branch returns from the
    /// capture loop, so no further packet can arrive for this track and
    /// <c>ProcessPacket</c> can never run again. Without it the consumer stays parked
    /// until every other track ends, leaving this track's tail as a <c>.part</c> with an
    /// open index row and an unflushed <c>FileStream</c> buffer for the rest of the
    /// session — so the fatal event's "its final chunk is closed and indexed as it ends"
    /// would be false (docs/RELIABILITY.md section 8, docs/ARCHITECTURE.md section 7.2). The
    /// spool itself stays consumer-thread-only; this only signals that no more packets follow.
    /// </remarks>
    public void CompleteWriter() => _channel?.Writer.TryComplete();

    /// <summary>The consumer drain for this track's bounded queue.</summary>
    public async Task ConsumeAsync()
    {
        try
        {
            while (_channel is not null && await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var packet))
                {
                    _backlog!.RecordConsumed();
                    ProcessPacket(packet);
                    ReportOverflowIfNeeded();
                    _afterPacketWritten?.Invoke();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RegisterStorageFailure(ex);
        }
        finally
        {
            // A track that ended on its own (device loss, format change) measured an outage
            // it never got to place on the timeline, because placing an outage is what the
            // <em>next</em> buffer does. There will be no next buffer, so the outage has to be
            // accounted for here — on the consumer thread, in the same finally that closes this
            // track's final chunk, after every packet the track will ever receive has been
            // placed (docs/RELIABILITY.md section 7).
            ApplyTerminalDeviceLoss();
            CloseActiveChunk();
        }
    }

    /// <summary>Requests a flush of this track's open chunk on the next packet boundary.</summary>
    public void RequestFlush() => Interlocked.Exchange(ref _flushRequested, 1);

    /// <summary>Reports a downstream consumer that cannot keep up with this track.</summary>
    public void CheckConsumerBacklog()
    {
        var backlog = _backlog;
        if (backlog is null)
        {
            return;
        }

        var queued = backlog.Queued;
        var closedChunks = ClosedChunkCount;
        var madeProgress = closedChunks != _closedChunkCountAtLastStallCheck;
        _closedChunkCountAtLastStallCheck = closedChunks;

        if (queued <= 0 || madeProgress)
        {
            EndStallObservation();
            backlog.RecordStallObservation(0);
            return;
        }

        _stallObservedMs += RecordingSession.HousekeepingIntervalMs;
        if (_stallObservedMs < (long)_settings.BufferSeconds * 1000)
        {
            return;
        }

        _degraded = true;
        backlog.RecordStallObservation(_stallObservedMs);

        var now = _clock.UtcNow;
        if (_lastStallEventAt is { } previous &&
            (now - previous).TotalMilliseconds < ConsumerStallEventIntervalMs)
        {
            return;
        }

        _lastStallEventAt = now;
        _events.Write(new SessionEvent(SessionEventNames.CaptureConsumerStalled, CurrentTimelineMs())
        {
            Source = _source.ToWireName(),
            Count = queued,
            Detail =
                $"the recording consumer has not drained the {_source.ToWireName()} queue for {_stallObservedMs} ms while " +
                $"{queued} of {backlog.Capacity} packet slots are still occupied; audio capture is " +
                "unaffected and the backlog stays inside its configured bound.",
        });
    }

    private void EndStallObservation()
    {
        if (Interlocked.Exchange(ref _stallObservedMs, 0) > 0)
        {
            _lastStallEventAt = null;
        }
    }

    // ---------------------------------------------------------------- capture side

    private bool TryStartSegment(IAudioCaptureSource source)
    {
        _segmentFault = null;
        _segmentEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        source.PacketAvailable += _packetHandler!;
        source.Stopped += _stoppedHandler!;

        try
        {
            source.Start();
            _captureStarted = true;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _segmentFault = ex;
            DetachSegmentHandlers(source);
            return false;
        }
    }

    private void StopSegment(IAudioCaptureSource source)
    {
        DetachSegmentHandlers(source);

        try
        {
            source.Stop();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, CurrentTimelineMs())
            {
                Source = _source.ToWireName(),
                Detail = "stopping the capture device reported: " + ex.Message,
            });
        }
        finally
        {
            source.Dispose();
        }
    }

    private void DetachSegmentHandlers(IAudioCaptureSource source)
    {
        if (_packetHandler is not null)
        {
            source.PacketAvailable -= _packetHandler;
        }

        if (_stoppedHandler is not null)
        {
            source.Stopped -= _stoppedHandler;
        }
    }

    private async Task<(IAudioCaptureSource? Source, int Attempts, bool Exhausted)> TryRecoverDeviceAsync(
        int attempts,
        DateTimeOffset downtimeStart,
        CancellationToken cancellationToken)
    {
        while (attempts < _maxDeviceRecoveryAttempts)
        {
            attempts++;

            if (_beforeReopenAttempt is { } seam)
            {
                // Test seam only: it parks a test's clock control between the outage's start and
                // its measurement, which is the one window the recovery path cannot otherwise be
                // dated from outside (docs/DEVELOPMENT.md section 6).
                await seam(cancellationToken).ConfigureAwait(false);
            }

            var replacement = await TryReopenDeviceAsync(attempts, cancellationToken).ConfigureAwait(false);
            if (replacement is null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // The session ended while the endpoint was still gone and the window was
                    // still open. The outage measured up to this moment is real missing audio on
                    // a span this track did capture, and no attempt will ever place it, so it is
                    // handed to the consumer exactly as an exhausted window's is. Leaving it out
                    // would report gap_count: 0 for a stretch the log itself describes with
                    // capture.device_lost and one capture.discontinuity per failed retry
                    // (issue #34 Acceptance Criterion 4, docs/RELIABILITY.md section 8.2).
                    HandOverUnplacedOutage(downtimeStart);
                    return (null, attempts, false);
                }

                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                replacement.Dispose();

                // The endpoint did come back, but the session ended before this attempt could
                // hand it to the capture loop, so the measured outage is unplaced here too.
                HandOverUnplacedOutage(downtimeStart);
                return (null, attempts, false);
            }

            var downtimeMs = MeasuredDowntime(downtimeStart);
            Interlocked.Add(ref _pendingGapMs, downtimeMs);

            // Signals the consumer that the next buffer starts a fresh device stream, so
            // it must close the audio captured before the outage.
            Interlocked.Exchange(ref _deviceRestarted, 1);

            _events.Write(new SessionEvent(SessionEventNames.CaptureDeviceRestored, CurrentTimelineMs())
            {
                Source = _source.ToWireName(),
                GapMs = downtimeMs,
                Detail = $"device='{replacement.Device.DisplayName}'",
            });

            return (replacement, attempts, false);
        }

        // The recovery window closed with the endpoint still gone. The outage measured while
        // retrying is real missing audio even though no buffer will ever place it, so it is
        // carried to the consumer — which owns the timeline — instead of vanishing with the
        // track (docs/RELIABILITY.md section 8.2).
        Interlocked.Add(ref _pendingGapMs, MeasuredDowntime(downtimeStart));
        return (null, attempts, true);
    }

    /// <summary>
    /// How long the device has been gone, as read from the same clock the outage is stamped
    /// with. Never negative: an injected clock that moves backwards must not produce a
    /// backwards gap.
    /// </summary>
    private long MeasuredDowntime(DateTimeOffset downtimeStart)
        => (long)Math.Max(0, (_clock.UtcNow - downtimeStart).TotalMilliseconds);

    private async Task<IAudioCaptureSource?> TryReopenDeviceAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DeviceRecoveryBackoffMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var device = _reopenDevice();
        if (device is null)
        {
            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, CurrentTimelineMs())
            {
                Source = _source.ToWireName(),
                Count = attempt,
                Detail = "the configured capture device is still unavailable; retrying.",
            });
            return null;
        }

        try
        {
            return _createSource(device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, CurrentTimelineMs())
            {
                Source = _source.ToWireName(),
                Count = attempt,
                Detail = "the capture device could not be reopened: " + ex.Message,
            });
            return null;
        }
    }

    private static bool FormatsMatch(AudioFormat? expected, AudioFormat actual)
        => expected is not null &&
           expected.SampleRate == actual.SampleRate &&
           expected.Channels == actual.Channels &&
           expected.BitsPerSample == actual.BitsPerSample &&
           expected.SampleFormat == actual.SampleFormat;

    private void WriteFormatChangedFatal(AudioFormat previous, AudioFormat current)
    {
        _degraded = true;

        _events.Write(new SessionEvent(SessionEventNames.CaptureFormatChanged, CurrentTimelineMs())
        {
            Source = _source.ToWireName(),
            Detail =
                $"the capture endpoint came back with a different format ('{current}' instead of " +
                $"'{previous}'); the track ends so the audio already captured stays honestly labeled.",
        });

        _events.Write(new SessionEvent(SessionEventNames.CaptureDeviceLostFatal, CurrentTimelineMs())
        {
            Source = _source.ToWireName(),
            Detail =
                $"capture on the '{_source.ToWireName()}' track could not be resumed at the session format; " +
                "the track is ending and its final chunk is closed and indexed as it ends. " +
                "The other track is unaffected.",
        });
    }

    private void WriteDeviceLostFatal(int attempts)
    {
        _events.Write(new SessionEvent(SessionEventNames.CaptureDeviceLostFatal, CurrentTimelineMs())
        {
            Source = _source.ToWireName(),
            Count = attempts,
            Detail =
                $"capture on the '{_source.ToWireName()}' track could not be recovered; the track is " +
                "ending and its final chunk is closed and indexed as it ends. The other track is unaffected.",
        });
    }

    /// <summary>
    /// Marks this track as ended for its own reason (device loss, format change) without
    /// ending the session: the other track may still be healthy
    /// (docs/RELIABILITY.md section 8).
    /// </summary>
    /// <param name="reason">
    /// The terminal end reason written to the session record. It also decides how this
    /// track's unplaced outage is worded, because it is the only account of why the track
    /// ended (docs/RELIABILITY.md section 8.2).
    /// </param>
    private void EndTrack(string reason)
    {
        _degraded = true;
        Interlocked.CompareExchange(ref _endReason, reason, null);

        // The track is ending, so no further buffer can place a measured outage on the
        // timeline. The flag hands whatever was measured to the consumer, which is the only
        // thread that writes the timeline, so the outage is counted exactly once instead of
        // being lost (docs/RELIABILITY.md section 7). The cause travels with it: a window that
        // closed and a format the session refused are both terminal, but they are not the same
        // statement and must not be worded as one.
        Interlocked.Exchange(
            ref _terminalDeviceLoss,
            (int)(string.Equals(reason, FormatChangedEndReason, StringComparison.Ordinal)
                ? TerminalDeviceLossCause.FormatChanged
                : TerminalDeviceLossCause.RecoveryWindowClosed));
    }

    /// <summary>
    /// Accounts for the outage that was still unplaced when this track stopped receiving buffers,
    /// and names it in the event log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on the consumer thread, in the same <c>finally</c> that closes this track's final
    /// chunk and after every packet it will ever receive has been placed: the capture loop has
    /// returned and the writer is complete, so the timeline has no writer left but this one
    /// (docs/ARCHITECTURE.md section 7.2). Recording the outage anywhere else would race the
    /// consumer's own <c>Observe</c> calls.
    /// </para>
    /// <para>
    /// Three situations reach here with an outage that was measured but never placed: the
    /// recovery window closed with the endpoint still gone and the track ended fatally, the
    /// track successfully reopened the endpoint and the session then stopped before that
    /// endpoint delivered a single buffer, and the endpoint came back at a format the session
    /// refused to splice. All three are real missing audio on a span this track did capture, so
    /// all three are reported; they share an interval and a reason and differ only in the
    /// wording of the <c>detail</c>, which is taken from the cause the capture thread recorded
    /// when the track ended rather than from anything inferred after the fact
    /// (docs/RELIABILITY.md section 8.2).
    /// </para>
    /// <para>
    /// A track that placed no buffer reports no gap: there is no span of captured audio for
    /// anything to be missing <em>from</em>, and inventing one would overstate the loss in
    /// exactly the direction a reader cannot check (docs/RELIABILITY.md section 7).
    /// </para>
    /// </remarks>
    private void ApplyTerminalDeviceLoss()
    {
        var cause = (TerminalDeviceLossCause)Interlocked.Exchange(ref _terminalDeviceLoss, 0);
        if (cause == TerminalDeviceLossCause.None)
        {
            return;
        }

        var pending = Interlocked.Exchange(ref _pendingGapMs, 0);
        var gap = _timeline?.RecordTerminalDeviceLoss(pending);
        if (gap is not { } terminal)
        {
            return;
        }

        _events.Write(new SessionEvent(SessionEventNames.CaptureGap, terminal.GapEndMs)
        {
            Source = _source.ToWireName(),
            GapStartMs = terminal.GapStartMs,
            GapEndMs = terminal.GapEndMs,
            GapMs = terminal.GapMs,
            Reason = AudioGapReasons.NotCaptured,
            Detail = TerminalDeviceLossDetail(cause),
        });
    }

    /// <summary>
    /// The one statement that is true about why this track's unplaced outage is missing audio.
    /// </summary>
    /// <remarks>
    /// Every branch says the same thing about the measurement — the outage is what was measured
    /// while retrying, not an estimate of what the rest of the session would have captured — and
    /// differs only in the cause, which is the field a reader uses to tell the terminal cases
    /// apart (docs/RELIABILITY.md section 8.2).
    /// </remarks>
    private string TerminalDeviceLossDetail(TerminalDeviceLossCause cause)
        => cause switch
        {
            TerminalDeviceLossCause.FormatChanged =>
                "the capture endpoint returned but at a different format, so the track ended instead of " +
                "splicing audio the session cannot place into what it already captured; this stretch of the " +
                "track's timeline has no captured audio. The outage is what was measured while retrying, not " +
                "an estimate of what a successful recovery would have captured.",

            TerminalDeviceLossCause.SessionStopped =>
                "the recording was stopped while this track was still recovering its capture device, with " +
                "recovery time left in its window, so this stretch of the track's timeline has no captured " +
                "audio. The outage is what was measured while retrying, not an estimate of what the rest of " +
                "the session would have captured.",

            _ =>
                "the capture device did not return within the recovery window, so this stretch of the track's " +
                "timeline has no captured audio. The track ends here; the outage is what was measured while " +
                "retrying, not an estimate of what a successful recovery would have captured.",
        };

    /// <summary>
    /// Measures the outage a cancelled recovery spent and hands it to the consumer, which owns the
    /// timeline and is the only thread that can record it.
    /// </summary>
    /// <remarks>
    /// The measurement and the handoff belong together: an outage that is measured but not handed
    /// over is an outage the session reports as <c>gap_count: 0</c>, which is the defect this
    /// exists to remove (docs/RELIABILITY.md section 8.2).
    /// </remarks>
    private void HandOverUnplacedOutage(DateTimeOffset downtimeStart)
    {
        Interlocked.Add(ref _pendingGapMs, MeasuredDowntime(downtimeStart));
        Interlocked.Exchange(ref _terminalDeviceLoss, (int)TerminalDeviceLossCause.SessionStopped);
    }

    /// <summary>
    /// Ends this track because its buffers cannot be placed on the session timeline by the
    /// timing its stream reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the unsupported-process-loopback outcome docs/ARCHITECTURE.md section 8.1
    /// requires: one explicit, actionable statement and a track that ends, instead of a
    /// track that keeps running while every buffer is quietly reported as a backwards
    /// device position and the whole recording is written off as degraded
    /// (docs/RELIABILITY.md section 7).
    /// </para>
    /// <para>
    /// Only this track ends. The other track may still be recording, and the audio already
    /// captured on this one stays durable because completing the writer lets the consumer
    /// drain and close the open chunk exactly as a fatal device loss does.
    /// </para>
    /// </remarks>
    private void EndUnusableTimeline(Exception error)
    {
        Interlocked.Exchange(ref _timelineUnusable, 1);
        EndTrack("timeline_unusable");

        _events.Write(new SessionEvent(SessionEventNames.CaptureTimelineUnusable, CurrentTimelineMs())
        {
            Source = _source.ToWireName(),
            Detail = error.Message,
        });

        // Ends this capture segment deliberately: no further buffer from it can be placed,
        // and the capture loop must not mistake this for a device loss and try to recover.
        _segmentEnded?.TrySetResult();

        // No more packets can be placed, so the consumer may finalize what it already has.
        CompleteWriter();
    }

    private async Task WaitForSegmentEndAsync(CancellationToken cancellationToken)
    {
        var ended = _segmentEnded!;
        using var registration = cancellationToken.Register(() => ended.TrySetResult());
        await ended.Task.ConfigureAwait(false);
    }

    private void OnPacketAvailable(AudioPacket packet)
    {
        if (Volatile.Read(ref _timelineUnusable) == 1)
        {
            // The track has already ended because its buffers cannot be placed on the
            // session timeline at all. Refusing the audio is stated once, by
            // EndUnusableTimeline, rather than restated as one overflow per packet.
            return;
        }

        if (_channel!.Writer.TryWrite(packet))
        {
            _backlog!.RecordProduced();
            return;
        }

        // The queue is full, so the packet is dropped instead of blocking the capture
        // callback (docs/RELIABILITY.md section 4 step 1).
        _backlog!.RecordDropped();
        Interlocked.Exchange(ref _overflowSignalled, 1);
    }

    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        _segmentFault = e.Error;
        _segmentEnded?.TrySetResult();
    }

    // --------------------------------------------------------------- consumer side

    private void CloseActiveChunk()
    {
        // Closing the chunk is recording work; announcing it is not. They are deliberately
        // separate so a failure of the second can never be classified as a failure of the first:
        // docs/ARCHITECTURE.md section 9.3 promises that a subscriber cannot fail the recording,
        // and RegisterStorageFailure would end the session as INTERRUPTED if a subscriber's
        // exception reached it (docs/RELIABILITY.md section 2).
        try
        {
            _spool?.CloseCurrentChunk();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RegisterStorageFailure(ex);
            return;
        }

        AnnounceClosedChunk();
    }

    private void AnnounceClosedChunk()
    {
        var handler = _onChunkClosed;
        var chunk = _spool?.LastClosed;
        if (handler is null || chunk is null || chunk.Sequence == _announcedChunkSequence)
        {
            return;
        }

        _announcedChunkSequence = chunk.Sequence;

        try
        {
            handler(chunk);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The chunk is durable and the recording is healthy; only the downstream
            // consumer of this chunk failed. Report it and keep recording.
            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, CurrentTimelineMs())
            {
                Source = _source.ToWireName(),
                Chunk = Path.GetFileName(chunk.FilePath),
                Detail =
                    "a downstream consumer of the closed chunk failed and was skipped: " + ex.Message +
                    " The chunk itself is durable and the recording is unaffected.",
            });
        }
    }

    private void ProcessPacket(AudioPacket packet)
    {
        if (Volatile.Read(ref _timelineUnusable) == 1)
        {
            // The track already ended on its first unplaceable buffer; the remaining
            // buffered packets cannot be placed either and must not restate the same
            // diagnostic once per packet.
            return;
        }

        var restarted = Interlocked.Exchange(ref _deviceRestarted, 0) == 1;
        var pendingGap = Interlocked.Exchange(ref _pendingGapMs, 0);

        if (restarted)
        {
            // The outage this track measured has now been placed by the first buffer of the
            // restarted stream, and the pending measurement is consumed here, so a later stop
            // cannot report it again as a terminal gap.
            _timeline!.RecordDeviceLoss(pendingGap);
        }

        var wasFirstPacketOfSession = !_timeline!.HasOrigin;

        PacketTiming timing;
        try
        {
            timing = _timeline.Observe(packet);
        }
        catch (CaptureFailedException ex)
        {
            // The track's declared clock cannot place this buffer at all, so there is no
            // honest position to write it at (docs/RELIABILITY.md section 7). The track
            // ends itself with one explicit diagnostic instead of degrading silently, and
            // the other track keeps recording (docs/RELIABILITY.md section 8).
            EndUnusableTimeline(ex);
            return;
        }

        if (timing.IsNewSegment)
        {
            // Capture restarted on a fresh device stream: close what was captured before
            // the outage so it is durable without waiting for the chunk boundary.
            CloseActiveChunk();
        }

        if (timing.HasGap)
        {
            _degraded = true;
            _events.Write(new SessionEvent(SessionEventNames.CaptureGap, timing.StartMs)
            {
                Source = _source.ToWireName(),
                GapStartMs = timing.GapStartMs,
                GapEndMs = timing.GapEndMs,
                GapMs = timing.GapMs,
                DevicePositionFrames = packet.DevicePositionFrames,
                QpcPositionTicks = packet.QpcPositionTicks,
                Detail = "the device skipped audio between buffers.",
            });
        }

        if (timing.Discontinuity)
        {
            var isStreamStart = wasFirstPacketOfSession || timing.IsNewSegment;
            if (!isStreamStart)
            {
                _degraded = true;
            }

            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, timing.StartMs)
            {
                Source = _source.ToWireName(),
                DevicePositionFrames = packet.DevicePositionFrames,
                QpcPositionTicks = packet.QpcPositionTicks,
                Detail = isStreamStart
                    ? "device reported buffer flags on the first buffer of the stream: " + packet.Flags
                    : "device reported buffer flags: " + packet.Flags,
            });
        }

        if (timing.DevicePositionAnomaly)
        {
            _degraded = true;
            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, timing.StartMs)
            {
                Source = _source.ToWireName(),
                DevicePositionFrames = packet.DevicePositionFrames,
                QpcPositionTicks = packet.QpcPositionTicks,
                Detail = "device position moved backwards; the session timeline was kept monotonic.",
            });
        }

        _spool!.Append(packet, timing);

        // A packet that crosses the chunk boundary rotates the chunk inside Append, so
        // the announcement happens here rather than only around an explicit close.
        AnnounceClosedChunk();

        if (Interlocked.Exchange(ref _flushRequested, 0) == 1)
        {
            _spool.Flush();
        }
    }

    private void ReportOverflowIfNeeded()
    {
        if (Interlocked.Exchange(ref _overflowSignalled, 0) == 0)
        {
            return;
        }

        _degraded = true;
        _events.Write(new SessionEvent(SessionEventNames.CaptureBufferOverflow, CurrentTimelineMs())
        {
            Source = _source.ToWireName(),
            Count = _backlog?.Dropped,
            Detail =
                $"the {_source.ToWireName()} recording queue was full, so audio was dropped rather than " +
                "blocking the capture callback because the disk could not keep up.",
        });
    }

    private void RegisterStorageFailure(Exception error)
    {
        _storageFailure ??= error;
        _degraded = true;
        // A storage failure is shared (the disk is one resource), so it ends the whole
        // session rather than just this track (docs/RELIABILITY.md section 2).
        _onStorageFailure(error);
    }

    private long CurrentTimelineMs() => _timeline?.LastEndMs ?? 0;

    internal static int QueueCapacity(CaptureSettings settings)
        => Math.Max(8, settings.BufferSeconds * PacketsPerSecondEstimate);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _spool?.Dispose();
    }
}
