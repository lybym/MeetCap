namespace MeetCap.AudioPipeline;

using System.Threading.Channels;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using MeetCap.Core.Time;
using MeetCap.Persistence.Storage;

/// <summary>
/// Result of one recording session.
/// </summary>
public sealed record RecordingSessionOutcome(
    string SessionId,
    string SessionDirectory,
    string Status,
    long DurationMs,
    int ChunksClosed,
    long ClosedDataBytes,
    bool Degraded,
    string? EndReason)
{
    /// <summary>
    /// True when the session ended through a clean stop with every artifact closed.
    /// The CLI uses this to decide its exit code (docs/DEVELOPMENT.md section 8).
    /// </summary>
    public bool IsClean
        => !Degraded && string.Equals(Status, SessionStatus.Completed, StringComparison.Ordinal);
}

/// <summary>
/// Runs one offline microphone recording: it owns the capture source, the bounded
/// packet queue, the chunk spool, the session timeline, the disk-space policy and the
/// session artifacts.
/// </summary>
/// <remarks>
/// <para>
/// The capture callback only copies the buffer into a MeetCap packet and pushes it at
/// a bounded queue; it never touches the filesystem, SQLite or the event log
/// (docs/ARCHITECTURE.md section 7 and docs/RELIABILITY.md section 3).
/// </para>
/// <para>
/// All three ways a session can end go through the same teardown: the capture source
/// is stopped, the queue is drained, the open chunk is finalized and the session is
/// marked. They are an external cancellation (Ctrl+C), a stop request written by
/// <c>meetcap stop</c>, and an unrecoverable capture or storage failure.
/// </para>
/// </remarks>
public sealed class RecordingSession
{
    /// <summary>How often the stop signal, flush schedule and disk policy are checked.</summary>
    internal const int HousekeepingIntervalMs = 250;

    /// <summary>How often free space is re-checked while recording.</summary>
    internal const int DiskCheckIntervalMs = 10_000;

    /// <summary>Rate limit for repeated low-disk-space events.</summary>
    internal const int LowDiskSpaceEventIntervalMs = 60_000;

    /// <summary>Backoff between device-recovery attempts.</summary>
    internal const int DeviceRecoveryBackoffMs = 1_000;

    /// <summary>Assumed capture callback rate, used to bound the packet queue.</summary>
    internal const int PacketsPerSecondEstimate = 100;

    private const AudioSource Track = AudioSource.Mic;

    private readonly SessionPaths _paths;
    private readonly CaptureSettings _settings;
    private readonly CapturePlatform _platform;
    private readonly MeetCapDatabase _database;
    private readonly ISessionEventSink _events;
    private readonly CaptureDeviceInfo _device;
    private readonly IClock _clock;
    private readonly SessionManifest _manifest;
    private readonly DiskSpaceMonitor _diskMonitor;
    private readonly SessionStopSignal _stopSignal;
    private readonly int _maxDeviceRecoveryAttempts;

    private readonly Action<AudioPacket> _packetHandler;
    private readonly EventHandler<CaptureStoppedEventArgs> _stoppedHandler;

    private CancellationTokenSource? _endCts;
    private TaskCompletionSource? _segmentEnded;
    private Exception? _segmentFault;
    private CaptureTimeline? _timeline;
    private ChunkSpool? _spool;
    private Channel<AudioPacket>? _channel;
    private AudioFormat? _format;

    private int _deviceRestarted;
    private int _flushRequested;
    private int _overflowSignalled;
    private int _diskProbeFailureReported;
    private long _pendingGapMs;
    private long _droppedPackets;
    private volatile bool _degraded;
    private volatile bool _captureStarted;
    private string? _endReason;
    private Exception? _storageFailure;
    private DateTimeOffset? _lastLowDiskSpaceEventAt;

    internal RecordingSession(
        SessionPaths paths,
        CaptureSettings settings,
        CapturePlatform platform,
        MeetCapDatabase database,
        ISessionEventSink events,
        CaptureDeviceInfo device,
        IClock clock,
        SessionManifest manifest,
        int maxDeviceRecoveryAttempts = 3)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _maxDeviceRecoveryAttempts = Math.Max(0, maxDeviceRecoveryAttempts);

        _diskMonitor = new DiskSpaceMonitor(platform.DiskSpace, settings.MinimumFreeSpaceBytes);
        _stopSignal = new SessionStopSignal(paths.StopRequestPath);
        _packetHandler = OnPacketAvailable;
        _stoppedHandler = OnCaptureStopped;
    }

    public string SessionId => _paths.SessionId;

    public string SessionDirectory => _paths.SessionDirectory;

    /// <summary>
    /// Records until a stop is requested, the process is cancelled, or capture and
    /// storage both fail. Always leaves the session artifacts in a described state.
    /// </summary>
    public async Task<RecordingSessionOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        _endCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IAudioCaptureSource source;
        try
        {
            source = _platform.CaptureSources.Create(Track, _device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AbortBeforeStart(ex);
        }

        try
        {
            _format = source.Format;
            _timeline = new CaptureTimeline(_format);
            _spool = new ChunkSpool(
                _paths,
                Track,
                _format,
                _settings.ChunkSeconds,
                _database,
                _events,
                _clock);
            _channel = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(QueueCapacity(_settings))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

            BeginRecording();

            // NOTE: the stop marker is deliberately NOT cleared here. The session
            // directory is unique to this session, so there is no stale marker to
            // remove, and `meetcap stop` may legitimately have written one in the window
            // between the session row being created and capture starting. Clearing it
            // would silently discard that request and the recording would never stop.
            var consumer = Task.Run(ConsumeAsync);
            var housekeeping = Task.Run(HousekeepAsync);

            await RunCaptureLoopAsync(source).ConfigureAwait(false);
            Console.Error.WriteLine("[DIAG] RS: captureLoopDone");

            // Capture has ended. Transition to the documented FINALIZING checkpoint
            // (docs/ARCHITECTURE.md section 20) before draining the queue and closing
            // the final chunk, so a crash during that window leaves a session that
            // startup recovery treats as not-cleanly-stopped instead of RECORDING.
            BeginFinalizing();
            Console.Error.WriteLine("[DIAG] RS: beginFinalizingDone");

            _channel.Writer.TryComplete();
            Console.Error.WriteLine("[DIAG] RS: channelComplete");
            await consumer.ConfigureAwait(false);
            Console.Error.WriteLine("[DIAG] RS: consumerDone");

            CancelEnd();
            Console.Error.WriteLine("[DIAG] RS: cancelEnd");
            await housekeeping.ConfigureAwait(false);
            Console.Error.WriteLine("[DIAG] RS: housekeepingDone");

            var diagOutcome = Complete();
            Console.Error.WriteLine($"[DIAG] RS: completeDone status={diagOutcome.Status}");
            return diagOutcome;
        }
        finally
        {
            _spool?.Dispose();

            // The runner owns the event log: it must be closed and flushed before the
            // command reports the session outcome.
            (_events as IDisposable)?.Dispose();

            _endCts?.Dispose();
            _endCts = null;
        }
    }

    private void BeginRecording()
    {
        var startedAt = _clock.UtcNow;
        var format = _format!;

        _manifest.Status = SessionStatus.Recording;
        _manifest.StartedAt = startedAt;
        _manifest.Capture = new[] { CaptureTrackInfo.From(Track, _device, format) };
        SessionManifestStore.Save(_paths.ManifestPath, _manifest);

        _database.Sessions.UpdateLifecycle(
            _paths.SessionId,
            SessionStatus.Recording,
            startedAt,
            stoppedAt: null,
            durationMs: 0);

        _events.Write(new SessionEvent(SessionEventNames.SessionStarted, 0)
        {
            Source = Track.ToWireName(),
            Detail = $"device='{_device.DisplayName}' format='{format}'",
        });
    }

    /// <summary>
    /// Writes the documented FINALIZING checkpoint (docs/ARCHITECTURE.md section 20):
    /// capture has ended and the recording artifacts are about to be drained and closed.
    /// A crash in that window leaves the session in FINALIZING, which startup recovery
    /// treats as not-cleanly-stopped (docs/RELIABILITY.md section 6), so the already-
    /// renamed final WAVs are reconciled rather than left unindexed.
    /// </summary>
    private void BeginFinalizing()
    {
        if (!_captureStarted)
        {
            // A session whose capture never started has no artifacts to finalize; it is
            // marked interrupted directly by Complete().
            return;
        }

        var atMs = CurrentTimelineMs();

        _manifest.Status = SessionStatus.Finalizing;
        SessionManifestStore.Save(_paths.ManifestPath, _manifest);

        _database.Sessions.UpdateLifecycle(
            _paths.SessionId,
            SessionStatus.Finalizing,
            _clock.UtcNow,
            stoppedAt: null,
            durationMs: atMs);

        _events.Write(new SessionEvent(SessionEventNames.SessionStopping, atMs)
        {
            Source = Track.ToWireName(),
            Detail = "capture ended; closing recording artifacts before the session completes.",
        });
    }

    // ---------------------------------------------------------------- capture side

    /// <summary>
    /// Runs capture segments. When the device is lost the session tries to bring
    /// capture back instead of ending (docs/RELIABILITY.md section 8); when it cannot,
    /// the session ends with everything already captured still closed and durable.
    /// </summary>
    private async Task RunCaptureLoopAsync(IAudioCaptureSource source)
    {
        var attempts = 0;

        while (true)
        {
            var started = TryStartSegment(source);

            if (started)
            {
                await WaitForSegmentEndAsync().ConfigureAwait(false);
                StopSegment(source);
            }
            else
            {
                DetachSegmentHandlers(source);
                source.Dispose();
            }

            if (_endCts!.IsCancellationRequested)
            {
                return;
            }

            // Capture ended without anyone asking it to, so the device went away.
            _degraded = true;

            var downtimeStart = _clock.UtcNow;
            _events.Write(new SessionEvent(SessionEventNames.CaptureDeviceLost, CurrentTimelineMs())
            {
                Source = Track.ToWireName(),
                Detail = started
                    ? _segmentFault?.Message ?? "capture stopped unexpectedly"
                    : "capture could not be started",
            });

            var (replacement, nextAttempts, exhausted) =
                await TryRecoverDeviceAsync(attempts, downtimeStart).ConfigureAwait(false);
            attempts = nextAttempts;

            if (exhausted)
            {
                WriteDeviceLostFatal(attempts);
                RequestEnd("device_lost");
                return;
            }

            if (replacement is null)
            {
                // The session ended while capture was being recovered.
                return;
            }

            source = replacement;
        }
    }

    private async Task<(IAudioCaptureSource? Source, int Attempts, bool Exhausted)> TryRecoverDeviceAsync(
        int attempts,
        DateTimeOffset downtimeStart)
    {
        while (attempts < _maxDeviceRecoveryAttempts)
        {
            attempts++;

            var replacement = await TryReopenDeviceAsync(attempts).ConfigureAwait(false);
            if (replacement is null)
            {
                if (_endCts!.IsCancellationRequested)
                {
                    return (null, attempts, false);
                }

                continue;
            }

            if (_endCts!.IsCancellationRequested)
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
                Source = Track.ToWireName(),
                GapMs = downtimeMs,
                Detail = $"device='{replacement.Device.DisplayName}'",
            });

            return (replacement, attempts, false);
        }

        return (null, attempts, true);
    }

    private bool TryStartSegment(IAudioCaptureSource source)
    {
        _segmentFault = null;
        _segmentEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        source.PacketAvailable += _packetHandler;
        source.Stopped += _stoppedHandler;

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
                Source = Track.ToWireName(),
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
        source.PacketAvailable -= _packetHandler;
        source.Stopped -= _stoppedHandler;
    }

    private async Task<IAudioCaptureSource?> TryReopenDeviceAsync(int attempt)
    {
        try
        {
            await Task.Delay(DeviceRecoveryBackoffMs, _endCts!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var device = AudioDeviceResolver.TryResolve(_platform.Devices, _settings.MicrophoneDeviceId);
        if (device is null)
        {
            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, CurrentTimelineMs())
            {
                Source = Track.ToWireName(),
                Count = attempt,
                Detail = "the configured capture device is still unavailable; retrying.",
            });
            return null;
        }

        try
        {
            return _platform.CaptureSources.Create(Track, device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, CurrentTimelineMs())
            {
                Source = Track.ToWireName(),
                Count = attempt,
                Detail = "the capture device could not be reopened: " + ex.Message,
            });
            return null;
        }
    }

    private void WriteDeviceLostFatal(int attempts)
    {
        _events.Write(new SessionEvent(SessionEventNames.CaptureDeviceLostFatal, CurrentTimelineMs())
        {
            Source = Track.ToWireName(),
            Count = attempts,
            Detail = "capture could not be recovered; the session ends with the audio already captured still closed.",
        });
    }

    private async Task WaitForSegmentEndAsync()
    {
        var ended = _segmentEnded!;
        using var registration = _endCts!.Token.Register(() => ended.TrySetResult());
        await ended.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Capture-thread callback. Its only job is to hand the packet to the bounded
    /// queue; every filesystem and database operation happens on the consumer thread.
    /// </summary>
    private void OnPacketAvailable(AudioPacket packet)
    {
        if (!_channel!.Writer.TryWrite(packet))
        {
            Interlocked.Increment(ref _droppedPackets);
            Interlocked.Exchange(ref _overflowSignalled, 1);
        }
    }

    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        _segmentFault = e.Error;
        _segmentEnded?.TrySetResult();
    }

    // --------------------------------------------------------------- consumer side

    private async Task ConsumeAsync()
    {
        try
        {
            while (await _channel!.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var packet))
                {
                    ProcessPacket(packet);
                    ReportOverflowIfNeeded();
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

    private void CloseActiveChunk()
    {
        try
        {
            // The spool counts every chunk it closes, including the ones it rotates
            // internally at a chunk boundary.
            _spool!.CloseCurrentChunk();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RegisterStorageFailure(ex);
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
                Source = Track.ToWireName(),
                GapMs = timing.GapMs,
                DevicePositionFrames = packet.DevicePositionFrames,
                QpcPositionTicks = packet.QpcPositionTicks,
                Detail = "the device skipped audio between buffers.",
            });
        }

        if (timing.Discontinuity)
        {
            // WASAPI routinely flags the first buffer of a stream with
            // DataDiscontinuity, because there is no preceding buffer inside this
            // stream to be continuous with. That says nothing about lost audio, so it is
            // recorded but does not degrade the session or its exit code. Everywhere
            // else the flag is a real signal and does degrade it.
            var isStreamStart = wasFirstPacketOfSession || timing.IsNewSegment;
            if (!isStreamStart)
            {
                _degraded = true;
            }

            _events.Write(new SessionEvent(SessionEventNames.CaptureDiscontinuity, timing.StartMs)
            {
                Source = Track.ToWireName(),
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
                Source = Track.ToWireName(),
                DevicePositionFrames = packet.DevicePositionFrames,
                QpcPositionTicks = packet.QpcPositionTicks,
                Detail = "device position moved backwards; the session timeline was kept monotonic.",
            });
        }

        _spool!.Append(packet, timing);

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

        var dropped = Interlocked.Exchange(ref _droppedPackets, 0);
        _degraded = true;
        _events.Write(new SessionEvent(SessionEventNames.CaptureBufferOverflow, CurrentTimelineMs())
        {
            Source = Track.ToWireName(),
            Count = (int)Math.Min(dropped, int.MaxValue),
            Detail =
                "the recording queue was full, so audio was dropped rather than blocking the capture " +
                "callback because the disk could not keep up.",
        });
    }

    // --------------------------------------------------------------- housekeeping

    private async Task HousekeepAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HousekeepingIntervalMs));
        var lastFlush = _clock.UtcNow;
        var lastDiskCheck = _clock.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(_endCts!.Token).ConfigureAwait(false))
            {
                var stopRequested = _stopSignal.IsRequested();
                Console.Error.WriteLine($"[DIAG] HK: tick stopRequested={stopRequested}");
                if (stopRequested)
                {
                    Console.Error.WriteLine("[DIAG] HK: stop detected -> RequestEnd");
                    _events.Write(new SessionEvent(SessionEventNames.SessionStopRequested, CurrentTimelineMs())
                    {
                        Source = Track.ToWireName(),
                        Detail = _stopSignal.ReadReason() ?? "stop requested",
                    });
                    RequestEnd("stop_requested");
                    return;
                }

                var now = _clock.UtcNow;

                if ((now - lastFlush).TotalMilliseconds >= _settings.FlushIntervalMs)
                {
                    lastFlush = now;
                    Interlocked.Exchange(ref _flushRequested, 1);
                }

                if ((now - lastDiskCheck).TotalMilliseconds >= DiskCheckIntervalMs)
                {
                    lastDiskCheck = now;
                    CheckDiskSpace();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended.
        }
    }

    private void CheckDiskSpace()
    {
        DiskSpaceVerdict verdict;
        try
        {
            verdict = _diskMonitor.Inspect(_settings.DataRoot);
        }
        catch (MeetCapException ex)
        {
            if (Interlocked.Exchange(ref _diskProbeFailureReported, 1) == 0)
            {
                _events.Write(new SessionEvent(SessionEventNames.StorageProbeFailed, CurrentTimelineMs())
                {
                    Detail = ex.Message + " Recording continues; free space is simply not being monitored.",
                });
            }

            return;
        }

        if (verdict.Level == DiskSpaceLevel.Critical)
        {
            _degraded = true;
            _events.Write(new SessionEvent(SessionEventNames.StorageDiskExhausted, CurrentTimelineMs())
            {
                FreeBytes = verdict.FreeBytes,
                Detail =
                    $"free space fell to {DiskSpaceMonitor.Format(verdict.FreeBytes)}; recording stops now so " +
                    "the audio already captured stays readable.",
            });
            RequestEnd("disk_exhausted");
            return;
        }

        if (verdict.Level != DiskSpaceLevel.Low)
        {
            return;
        }

        var now = _clock.UtcNow;
        if (_lastLowDiskSpaceEventAt is { } previous &&
            (now - previous).TotalMilliseconds < LowDiskSpaceEventIntervalMs)
        {
            return;
        }

        _lastLowDiskSpaceEventAt = now;
        _degraded = true;
        _events.Write(new SessionEvent(SessionEventNames.StorageLowDiskSpace, CurrentTimelineMs())
        {
            FreeBytes = verdict.FreeBytes,
            Detail =
                $"free space is {DiskSpaceMonitor.Format(verdict.FreeBytes)}, below the configured minimum " +
                $"{DiskSpaceMonitor.Format(_diskMonitor.MinimumFreeBytes)}.",
        });
    }

    // -------------------------------------------------------------------- teardown

    private RecordingSessionOutcome AbortBeforeStart(Exception error)
    {
        var stoppedAt = _clock.UtcNow;
        _manifest.Status = SessionStatus.Interrupted;
        _manifest.Degraded = true;
        _manifest.EndReason = "capture_start_failed";
        SessionManifestStore.Save(_paths.ManifestPath, _manifest);

        _database.Sessions.UpdateLifecycle(
            _paths.SessionId,
            SessionStatus.Interrupted,
            stoppedAt,
            stoppedAt,
            durationMs: 0);

        _events.Write(new SessionEvent(SessionEventNames.SessionStopped, 0)
        {
            Source = Track.ToWireName(),
            Detail = "capture_start_failed: " + error.Message,
        });

        _stopSignal.Clear();
        _endCts?.Dispose();
        _endCts = null;

        return new RecordingSessionOutcome(
            _paths.SessionId,
            _paths.SessionDirectory,
            SessionStatus.Interrupted,
            DurationMs: 0,
            ChunksClosed: 0,
            ClosedDataBytes: 0,
            Degraded: true,
            EndReason: "capture_start_failed");
    }

    private RecordingSessionOutcome Complete()
    {
        var durationMs = CurrentTimelineMs();

        // A run where capture never started produced no audio and did not stop cleanly:
        // that is an interrupted session, not a completed one.
        var started = _captureStarted;
        var status = started && _storageFailure is null ? SessionStatus.Completed : SessionStatus.Interrupted;

        var endReason = !started
            ? "capture_start_failed"
            : _endReason ?? (_storageFailure is null ? "stop_requested" : "storage_error");

        var degraded = _degraded || _storageFailure is not null || !started;

        _manifest.Status = status;
        _manifest.Degraded = degraded;
        _manifest.EndReason = endReason;
        _manifest.StoppedAt = status == SessionStatus.Completed ? _clock.UtcNow : null;

        SessionManifestStore.Save(_paths.ManifestPath, _manifest);

        _database.Sessions.UpdateLifecycle(
            _paths.SessionId,
            status,
            _clock.UtcNow,
            _manifest.StoppedAt,
            durationMs);

        _stopSignal.Clear();

        _events.Write(new SessionEvent(SessionEventNames.SessionStopped, durationMs)
        {
            Source = Track.ToWireName(),
            EndMs = durationMs,
            Count = ClosedChunkCount,
            Detail = _storageFailure is null ? endReason : endReason + ": " + _storageFailure.Message,
        });

        _spool?.Dispose();

        return new RecordingSessionOutcome(
            _paths.SessionId,
            _paths.SessionDirectory,
            status,
            durationMs,
            ClosedChunkCount,
            _spool?.ClosedDataBytes ?? 0,
            degraded,
            endReason);
    }

    /// <summary>Chunks closed durably so far, including rotations done by the spool.</summary>
    private int ClosedChunkCount => _spool?.ClosedChunkCount ?? 0;

    private void RegisterStorageFailure(Exception error)
    {
        _storageFailure ??= error;
        _degraded = true;
        RequestEnd("storage_error");
    }

    private void RequestEnd(string reason)
    {
        Interlocked.CompareExchange(ref _endReason, reason, null);
        CancelEnd();
    }

    private void CancelEnd()
    {
        try
        {
            _endCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session already finished.
        }
    }

    private long CurrentTimelineMs() => _timeline?.LastEndMs ?? 0;

    internal static int QueueCapacity(CaptureSettings settings)
        => Math.Max(8, settings.BufferSeconds * PacketsPerSecondEstimate);
}
