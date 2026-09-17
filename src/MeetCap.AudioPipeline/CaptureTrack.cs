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
    internal const int DeviceRecoveryBackoffMs = 1_000;

    /// <summary>Rate limit for repeated stalled-consumer events.</summary>
    internal const int ConsumerStallEventIntervalMs = 1_000;

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

    private Action<AudioPacket>? _packetHandler;
    private EventHandler<CaptureStoppedEventArgs>? _stoppedHandler;

    private AudioFormat? _format;
    private CaptureTimeline? _timeline;
    private ChunkSpool? _spool;
    private Channel<AudioPacket>? _channel;
    private CaptureBacklogMonitor? _backlog;

    private TaskCompletionSource? _segmentEnded;
    private Exception? _segmentFault;

    private int _deviceRestarted;
    private int _flushRequested;
    private int _overflowSignalled;
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
        Action<Exception> onStorageFailure)
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
    }

    public AudioSource Source => _source;

    public CaptureDeviceInfo Device => _device;

    public AudioFormat? Format => _format;

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
    /// from the capture source's native format. Called once the source exists, before
    /// capture starts.
    /// </summary>
    public void Initialize(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _format = format;
        _timeline = new CaptureTimeline(format);
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
                // A stop request or a storage failure ended the session. This track is
                // not degraded by a clean stop.
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
                EndTrack("device_lost");
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
                EndTrack("device_format_changed");
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
    /// session — which would make the "still closed and durable" claim in the fatal event
    /// false (docs/RELIABILITY.md section 8, docs/ARCHITECTURE.md section 7.2). The spool
    /// itself stays consumer-thread-only; this only signals that no more packets follow.
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

            var replacement = await TryReopenDeviceAsync(attempts, cancellationToken).ConfigureAwait(false);
            if (replacement is null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return (null, attempts, false);
                }

                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                replacement.Dispose();
                return (null, attempts, false);
            }

            var downtimeMs = (long)Math.Max(0, (_clock.UtcNow - downtimeStart).TotalMilliseconds);
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

        return (null, attempts, true);
    }

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
    private void EndTrack(string reason)
    {
        _degraded = true;
        Interlocked.CompareExchange(ref _endReason, reason, null);
    }

    private async Task WaitForSegmentEndAsync(CancellationToken cancellationToken)
    {
        var ended = _segmentEnded!;
        using var registration = cancellationToken.Register(() => ended.TrySetResult());
        await ended.Task.ConfigureAwait(false);
    }

    private void OnPacketAvailable(AudioPacket packet)
    {
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
        var restarted = Interlocked.Exchange(ref _deviceRestarted, 0) == 1;
        var pendingGap = Interlocked.Exchange(ref _pendingGapMs, 0);

        if (restarted)
        {
            _timeline!.RecordDeviceLoss(pendingGap);
        }

        var wasFirstPacketOfSession = !_timeline!.HasOrigin;
        var timing = _timeline.Observe(packet);

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
