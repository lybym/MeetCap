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

    /// <summary>Audio time the capture timeline reported as missing, in milliseconds.</summary>
    public long GapTotalMs { get; init; }

    /// <summary>How many discontinuities produced <see cref="GapTotalMs"/>.</summary>
    public int GapCount { get; init; }

    /// <summary>The bounded-buffer accounting for this run (docs/RELIABILITY.md section 4).</summary>
    public AudioBufferHealth CaptureHealth { get; init; } = AudioBufferHealth.Empty;
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
/// <para>
/// The instance owns its session's exclusive liveness marker from construction, not from
/// <see cref="RunAsync"/>: <see cref="CaptureService.PrepareSession"/> claims the marker
/// before it publishes the session manifest and index row, so a concurrent
/// <c>meetcap status</c> recovery scan can never observe a published-but-unowned session.
/// Running the session, or disposing it without running it, releases the marker.
/// </para>
/// </remarks>
public sealed class RecordingSession : IDisposable
{
    /// <summary>How often the stop signal, flush schedule and disk policy are checked.</summary>
    internal const int HousekeepingIntervalMs = 250;

    /// <summary>How often free space is re-checked while recording.</summary>
    internal const int DiskCheckIntervalMs = 10_000;

    /// <summary>Rate limit for repeated low-disk-space events.</summary>
    internal const int LowDiskSpaceEventIntervalMs = 60_000;

    /// <summary>Backoff between device-recovery attempts.</summary>
    internal const int DeviceRecoveryBackoffMs = 1_000;

    /// <summary>
    /// Rate limit for repeated stalled-consumer events. The stall itself is measured to
    /// the housekeeping interval; this only keeps one long stall from filling the log.
    /// </summary>
    internal const int ConsumerStallEventIntervalMs = 1_000;

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

    /// <summary>
    /// Test seam: invoked on the consumer thread after every packet is written.
    /// </summary>
    /// <remarks>
    /// docs/RELIABILITY.md section 4 requires bounded memory to hold when a downstream
    /// consumer cannot keep up, and the committed behaviour has to be provable in CI. A
    /// real slow disk cannot be produced on demand, so a test supplies this hook to make
    /// the consumer itself slow; production passes <c>null</c>.
    /// </remarks>
    private readonly Action? _afterPacketWritten;

    private CancellationTokenSource? _endCts;
    private SessionRecordingLock? _recordingLock;
    private TaskCompletionSource? _segmentEnded;
    private Exception? _segmentFault;
    private CaptureTimeline? _timeline;
    private ChunkSpool? _spool;
    private Channel<AudioPacket>? _channel;
    private CaptureBacklogMonitor? _backlog;
    private AudioFormat? _format;

    private int _deviceRestarted;
    private int _flushRequested;
    private int _overflowSignalled;
    private int _diskProbeFailureReported;
    private int _announcedChunkSequence;
    private long _pendingGapMs;
    private long _stallObservedMs;
    private int _closedChunkCountAtLastStallCheck;
    private DateTimeOffset? _lastStallEventAt;
    private volatile bool _degraded;
    private volatile bool _captureStarted;
    private volatile bool _postCaptureProcessingExpected;
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
        int maxDeviceRecoveryAttempts,
        SessionRecordingLock? recordingLock,
        Action? afterPacketWritten = null)
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
        _recordingLock = recordingLock;
        _afterPacketWritten = afterPacketWritten;

        _diskMonitor = new DiskSpaceMonitor(platform.DiskSpace, settings.MinimumFreeSpaceBytes);
        _stopSignal = new SessionStopSignal(paths.StopRequestPath);
        _packetHandler = OnPacketAvailable;
        _stoppedHandler = OnCaptureStopped;
    }

    public string SessionId => _paths.SessionId;

    public string SessionDirectory => _paths.SessionDirectory;

    /// <summary>
    /// This session's operational event sink, so a downstream consumer wired to
    /// <see cref="ChunkClosed"/> can append to the same <c>events.jsonl</c>.
    /// </summary>
    /// <remarks>
    /// The recorder holds that file open for append while it runs. Sharing the sink keeps
    /// one append lock on the log, so a consumer's event cannot interleave with the
    /// recorder's own line and the log stays one JSONL object per line
    /// (<c>docs/DATA_MODEL.md</c> section 4).
    /// </remarks>
    public ISessionEventSink Events => _events;

    /// <summary>
    /// Raised on the recording consumer thread, after a capture chunk has been closed
    /// durably. This is the hand-off point for optional downstream work such as ASR
    /// batching (<c>docs/ARCHITECTURE.md</c> section 10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event is not the capture callback. It fires once per closed chunk on the same
    /// thread that already writes chunks and updates the chunk index, so a subscriber may
    /// use the filesystem there — but it must never perform HTTP or another unbounded
    /// operation, because a subscriber that blocks would slow the consumer that
    /// <c>docs/RELIABILITY.md</c> section 4 requires to stay bounded. The M4 subscriber
    /// appends the chunk to a local batch file and queues a persistent job; submission
    /// happens later and elsewhere.
    /// </para>
    /// <para>
    /// A subscriber that throws cannot fail the recording: the audio is already durable and
    /// a transcript is optional. The failure is recorded as an explicit
    /// <c>capture.discontinuity</c> event so a degraded transcript is visible rather than
    /// silent (<c>docs/RELIABILITY.md</c> section 2).
    /// </para>
    /// </remarks>
    public event Action<ClosedAudioChunk>? ChunkClosed;

    /// <summary>
    /// Declares that this session owns post-capture work (live file-ASR batching), so a
    /// clean stop lands on the documented <c>FINALIZING -&gt; PROCESSING</c> checkpoint
    /// instead of <c>COMPLETED</c>.
    /// </summary>
    /// <remarks>
    /// Set by the composition root before <see cref="RunAsync"/>, when the ASR batch
    /// builder is actually wired. The session is then completed by the ASR job store when
    /// every job reaches a terminal state, and a session with no jobs at all is completed
    /// by the caller. Without the flag a session would be reported as finished while its
    /// queue still held work (<c>docs/ARCHITECTURE.md</c> section 20).
    /// </remarks>
    public void BeginPostCaptureProcessing() => _postCaptureProcessingExpected = true;

    /// <summary>
    /// Records until a stop is requested, the process is cancelled, or capture and
    /// storage both fail. Always leaves the session artifacts in a described state.
    /// </summary>
    public async Task<RecordingSessionOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        _endCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The liveness marker was already claimed by CaptureService.PrepareSession, before
        // this session became visible to a recovery scan. There is deliberately no
        // acquisition here: a marker claimed at this point would leave a published session
        // unowned for as long as the caller took to reach RunAsync, and a concurrent
        // `meetcap status` scan could rewrite the healthy session to INTERRUPTED (which
        // also makes `meetcap stop` unable to find it).
        if (_recordingLock is null)
        {
            // Defensive: a session whose marker could not be claimed must not record, or a
            // scan could reconcile the chunk surface out from under it. Fail loudly instead.
            throw new MeetCapException(
                $"session '{_paths.SessionId}' does not own its recording liveness marker; " +
                "it was released or never acquired, so recording would race startup recovery.");
        }

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

            // The bound is fixed at start so the accounting records the bound that was
            // actually used, not the bound the configuration happens to hold later.
            _backlog = new CaptureBacklogMonitor(QueueCapacity(_settings));
            _channel = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(_backlog.Capacity)
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
            var capture = Task.Run(() => RunCaptureLoopAsync(source));
            var housekeeping = Task.Run(HousekeepAsync);

            try
            {
                await capture.ConfigureAwait(false);

                // A stalled or slow downstream consumer leaves packets queued while capture
                // is still running. The capture loop only returns once the device has
                // stopped delivering, so the callback cannot fire again and it is safe to
                // complete the writer: the drain below then runs to completion instead of
                // racing a late callback against a closed channel.
                //
                // Capture has ended. Transition to the documented FINALIZING checkpoint
                // (docs/ARCHITECTURE.md section 20) before draining the queue and closing
                // the final chunk, so a crash during that window leaves a session that
                // startup recovery treats as not-cleanly-stopped instead of RECORDING.
                BeginFinalizing();

                _channel.Writer.TryComplete();
                await consumer.ConfigureAwait(false);

                CancelEnd();
                await housekeeping.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An exception escaping the capture loop or the finalization awaits must
                // not leave FINALIZING as the durable session state with no explanation.
                // Record it as a storage failure and fall through to the same teardown,
                // so the session always reaches a described terminal state and the
                // already-closed chunks stay durable (docs/DEVELOPMENT.md section 8).
                RegisterStorageFailure(ex);
            }

            return Complete();
        }
        finally
        {
            _spool?.Dispose();

            // The liveness marker is released only after Complete() has written the terminal
            // status, so a scan can never see a finished session as an unowned work-in-progress.
            // The event log deliberately stays open: M4's post-stop work (flushing the final ASR
            // batch and draining the queue) still appends session events, and closing the sink
            // here would make those writes fail (docs/DATA_MODEL.md section 4). The caller that
            // owns the session disposes it once that work is done.
            ReleaseRecordingLock();

            _endCts?.Dispose();
            _endCts = null;
        }
    }

    /// <summary>
    /// Releases this session's exclusive liveness marker without running the recording.
    /// </summary>
    /// <remarks>
    /// A caller that prepares a session and then abandons it must dispose it so the marker
    /// is freed. An unheld marker is exactly what tells a later startup scan that the
    /// session was not cleanly stopped and may be adopted (docs/ARCHITECTURE.md section
    /// 9.1). Disposing after <see cref="RunAsync"/> is a no-op, because the recording
    /// already released the marker during its teardown.
    /// </remarks>
    public void Dispose()
    {
        ReleaseRecordingLock();

        // The event log is closed here rather than at the end of RunAsync, so a consumer that
        // is still appending to it — the M4 ASR batch builder during the post-stop flush —
        // keeps working until the session object itself is released.
        (_events as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Releases this session's exclusive liveness marker without closing its artifacts.
    /// Idempotent. The operating system would release the handle anyway when the process
    /// exits; releasing it explicitly is what tells a later scan the session is no longer
    /// being recorded.
    /// </summary>
    private void ReleaseRecordingLock()
    {
        _recordingLock?.Dispose();
        _recordingLock = null;
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

            if (!FormatsMatch(_format, replacement.Format))
            {
                // The reopened endpoint delivers a different mix format — a realistic
                // outcome when Windows changes the shared-mode mix format, or when a
                // different device replaces the configured one. Continuing would write
                // the new PCM bytes under the session's original WAV header and chunk
                // index: internally consistent, so validation would pass, and the
                // resulting chunk would play at the wrong speed with the wrong channel
                // mapping. docs/RELIABILITY.md forbids silently ignoring a condition
                // that changes the meaning of the audio, so end the session visibly
                // instead of corrupting the artifact.
                WriteFormatChangedFatal(_format!, replacement.Format);
                replacement.Dispose();
                RequestEnd("device_format_changed");
                return;
            }

            source = replacement;
        }
    }

    /// <summary>
    /// Whether a reopened endpoint delivers the same audio format the session's chunk
    /// headers, chunk index and capture timeline are already written against.
    /// </summary>
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
            Source = Track.ToWireName(),
            Detail =
                $"the capture endpoint came back with a different format ('{current}' instead of " +
                $"'{previous}'); the session ends so the audio already captured stays honestly labeled.",
        });

        _events.Write(new SessionEvent(SessionEventNames.CaptureDeviceLostFatal, CurrentTimelineMs())
        {
            Source = Track.ToWireName(),
            Detail = "capture could not be resumed at the session format; the session ends with the audio already captured still closed.",
        });
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
        if (_channel!.Writer.TryWrite(packet))
        {
            _backlog!.RecordProduced();
            return;
        }

        // The queue is full, so the packet is dropped instead of blocking the capture
        // callback (docs/RELIABILITY.md section 4 step 1). The drop is counted rather
        // than hidden; ReportOverflowIfNeeded turns it into an explicit event.
        _backlog!.RecordDropped();
        Interlocked.Exchange(ref _overflowSignalled, 1);
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

    private void CloseActiveChunk()
    {
        try
        {
            // The spool counts every chunk it closes, including the ones it rotates
            // internally at a chunk boundary.
            _spool!.CloseCurrentChunk();
            AnnounceClosedChunk();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RegisterStorageFailure(ex);
        }
    }

    /// <summary>
    /// Announces the chunk the spool most recently closed durably, without letting a
    /// subscriber's failure reach the recording (<see cref="ChunkClosed"/>).
    /// </summary>
    /// <remarks>
    /// The spool rotates chunks inside <see cref="ChunkSpool.Append"/> as well as on an
    /// explicit close, so "what was just closed" is read from the spool rather than from
    /// the result of the caller's own close call. The announced sequence number is
    /// remembered so a rotation followed by the teardown close cannot announce one chunk
    /// twice.
    /// </remarks>
    private void AnnounceClosedChunk()
    {
        var handler = ChunkClosed;
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
                Source = Track.ToWireName(),
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
                Source = Track.ToWireName(),
                // The gap's own interval, not this buffer's position: recovery re-derives the
                // same hole from the chunk index, and both records have to name it the same
                // way or the log describes one hole twice (docs/RELIABILITY.md section 7).
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
            Source = Track.ToWireName(),
            Count = _backlog?.Dropped,
            Detail =
                "the recording queue was full, so audio was dropped rather than blocking the capture " +
                "callback because the disk could not keep up.",
        });
    }

    /// <summary>
    /// Reports a downstream consumer that cannot keep up with capture
    /// (docs/RELIABILITY.md section 4 step 3).
    /// </summary>
    /// <remarks>
    /// A backlog that is still growing after <c>capture.buffer_seconds</c> of wall-clock
    /// time is already deeper than the bound was configured to hold, so the condition is
    /// reported even while the queue has not overflowed yet. Recording keeps priority
    /// either way: this only observes and reports, and never throttles capture.
    /// </remarks>
    private void CheckConsumerBacklog()
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

        _stallObservedMs += HousekeepingIntervalMs;
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
            Source = Track.ToWireName(),
            Count = queued,
            Detail =
                $"the recording consumer has not drained the queue for {_stallObservedMs} ms while " +
                $"{queued} of {backlog.Capacity} packet slots are still occupied; audio capture is " +
                "unaffected and the backlog stays inside its configured bound.",
        });
    }

    private void EndStallObservation()
    {
        if (Interlocked.Exchange(ref _stallObservedMs, 0) > 0)
        {
            // The stall ended, so the rate limit for the next one restarts.
            _lastStallEventAt = null;
        }
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
                if (_stopSignal.IsRequested())
                {
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

                CheckConsumerBacklog();

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
            EndReason: "capture_start_failed")
        {
            CaptureHealth = new AudioBufferHealth { CapacityPackets = QueueCapacity(_settings) },
        };
    }

    private RecordingSessionOutcome Complete()
    {
        var durationMs = CurrentTimelineMs();

        // A run where capture never started produced no audio and did not stop cleanly:
        // that is an interrupted session, not a completed one.
        var started = _captureStarted;
        var status = !started || _storageFailure is not null
            ? SessionStatus.Interrupted
            // Post-capture work is wired (live file-ASR batching), so a clean stop lands on
            // the documented FINALIZING -> PROCESSING checkpoint instead of claiming the
            // session is finished while its ASR queue still holds work
            // (docs/ARCHITECTURE.md section 20).
            : _postCaptureProcessingExpected ? SessionStatus.Processing : SessionStatus.Completed;

        var endReason = !started
            ? "capture_start_failed"
            : _endReason ?? (_storageFailure is null ? "stop_requested" : "storage_error");

        var degraded = _degraded || _storageFailure is not null || !started;

        DateTimeOffset? stoppedAt = status is SessionStatus.Completed or SessionStatus.Processing
            ? _clock.UtcNow
            : null;

        // The gap and bounded-buffer accounting is written into the session document, not
        // only into the event log, so "how much audio is missing and how close did the
        // queue come to its bound" survives as part of the durable record
        // (docs/RELIABILITY.md sections 4 and 7).
        var health = CurrentCaptureHealth();
        _manifest.GapCount = health.GapCount;
        _manifest.GapTotalMs = health.GapTotalMs;
        _manifest.CaptureHealth = health;
        _manifest.Status = status;
        _manifest.Degraded = degraded;
        _manifest.EndReason = endReason;
        _manifest.StoppedAt = stoppedAt;

        SessionManifestStore.Save(_paths.ManifestPath, _manifest);

        _database.Sessions.UpdateLifecycle(
            _paths.SessionId,
            status,
            _clock.UtcNow,
            stoppedAt,
            durationMs);

        _stopSignal.Clear();

        _events.Write(new SessionEvent(SessionEventNames.SessionStopped, durationMs)
        {
            Source = Track.ToWireName(),
            EndMs = durationMs,
            GapMs = health.GapTotalMs > 0 ? health.GapTotalMs : null,
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
            endReason)
        {
            GapTotalMs = health.GapTotalMs,
            GapCount = health.GapCount,
            CaptureHealth = health,
        };
    }

    /// <summary>The current bounded-buffer and gap accounting for this session.</summary>
    private AudioBufferHealth CurrentCaptureHealth()
        => _backlog?.Snapshot(_timeline?.GapTotalMs ?? 0, _timeline?.GapCount ?? 0)
           ?? new AudioBufferHealth
           {
               CapacityPackets = QueueCapacity(_settings),
               GapTotalMs = _timeline?.GapTotalMs ?? 0,
               GapCount = _timeline?.GapCount ?? 0,
           };

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
