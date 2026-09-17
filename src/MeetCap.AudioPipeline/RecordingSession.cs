namespace MeetCap.AudioPipeline;

using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using MeetCap.Core.Time;
using MeetCap.Persistence.Storage;

/// <summary>
/// How one track's capture source is (re)created and its endpoint re-resolved after a
/// device loss. Built by the composition root so <see cref="RecordingSession"/> stays
/// source-agnostic and NAudio types never leave the platform boundary
/// (docs/DEVELOPMENT.md section 4).
/// </summary>
internal sealed record CaptureTrackSpec(
    AudioSource Source,
    CaptureDeviceInfo Device,
    Func<CaptureDeviceInfo, IAudioCaptureSource> CreateSource,
    Func<CaptureDeviceInfo?> ReopenDevice);

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

    /// <summary>
    /// Per-track capture health. An online session has one entry per track; an offline
    /// session has one (docs/ROADMAP.md M5).
    /// </summary>
    public IReadOnlyList<TrackHealth> TrackHealth { get; init; } = Array.Empty<TrackHealth>();
}

/// <summary>
/// Runs one recording session: it owns the shared session artifacts, the disk-space
/// policy, the stop signal and the session lifecycle, and orchestrates one or more
/// independent capture tracks (docs/ARCHITECTURE.md section 4).
/// </summary>
/// <remarks>
/// <para>
/// An offline session runs one microphone track; an online session runs a microphone
/// and a loopback track. The tracks are fully independent — each owns its own capture
/// callback, bounded queue, chunk spool, timeline and device-loss recovery — so one
/// capture callback never waits for the other track and the loss of one track never
/// silently corrupts the other (docs/ARCHITECTURE.md section 6, docs/RELIABILITY.md
/// section 3).
/// </para>
/// <para>
/// All three ways a session can end go through the same teardown: the capture sources
/// are stopped, the queues are drained, the open chunks are finalized and the session
/// is marked. They are an external cancellation (Ctrl+C), a stop request written by
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

    private readonly SessionPaths _paths;
    private readonly CaptureSettings _settings;
    private readonly CapturePlatform _platform;
    private readonly MeetCapDatabase _database;
    private readonly ISessionEventSink _events;
    private readonly IClock _clock;
    private readonly SessionManifest _manifest;
    private readonly DiskSpaceMonitor _diskMonitor;
    private readonly SessionStopSignal _stopSignal;
    private readonly int _maxDeviceRecoveryAttempts;
    private readonly IReadOnlyList<CaptureTrackSpec> _trackSpecs;

    /// <summary>
    /// Test seam: invoked on the consumer thread after every packet is written.
    /// Applied to every track; production passes <c>null</c>.
    /// </summary>
    private readonly Action? _afterPacketWritten;

    private CancellationTokenSource? _endCts;
    private SessionRecordingLock? _recordingLock;
    private List<CaptureTrack>? _tracks;
    private volatile bool _postCaptureProcessingExpected;
    private int _diskProbeFailureReported;
    private volatile bool _degraded;
    private Exception? _storageFailure;
    private string? _endReason;
    private DateTimeOffset? _lastLowDiskSpaceEventAt;

    internal RecordingSession(
        SessionPaths paths,
        CaptureSettings settings,
        CapturePlatform platform,
        MeetCapDatabase database,
        ISessionEventSink events,
        IReadOnlyList<CaptureTrackSpec> trackSpecs,
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
        _trackSpecs = trackSpecs ?? throw new ArgumentNullException(nameof(trackSpecs));
        if (trackSpecs.Count == 0)
        {
            throw new ArgumentException("A recording session needs at least one capture track.", nameof(trackSpecs));
        }

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _maxDeviceRecoveryAttempts = Math.Max(0, maxDeviceRecoveryAttempts);
        _recordingLock = recordingLock;
        _afterPacketWritten = afterPacketWritten;

        _diskMonitor = new DiskSpaceMonitor(platform.DiskSpace, settings.MinimumFreeSpaceBytes);
        _stopSignal = new SessionStopSignal(paths.StopRequestPath);
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
    /// durably. Fires for every track, so a subscriber receives mic and loopback chunks
    /// alike and keeps them independent (docs/ARCHITECTURE.md section 6, section 10).
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

        // An external cancellation (Ctrl+C) is a stop request: it ends the session
        // cleanly rather than as a track that ended on its own. Registering it as a stop
        // keeps the original single-track semantics, where a cancelled recording is
        // "stop_requested" and not degraded (docs/ARCHITECTURE.md section 20).
        cancellationToken.Register(() => RequestEnd("stop_requested"));

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

        List<CaptureTrack> tracks;
        List<IAudioCaptureSource> sources;
        try
        {
            (tracks, sources) = CreateTracks();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AbortBeforeStart(ex);
        }

        _tracks = tracks;

        try
        {
            _formatInitializers(tracks);
            BeginRecording(tracks);

            var consumers = tracks.Select(t => Task.Run(t.ConsumeAsync)).ToList();
            var captures = tracks
                .Zip(sources, (t, s) => Task.Run(() => t.RunCaptureLoopAsync(s, _endCts!.Token)))
                .ToList();
            var housekeeping = Task.Run(HousekeepAsync);

            try
            {
                await Task.WhenAll(captures).ConfigureAwait(false);

                // All capture loops have ended. Transition to the documented FINALIZING
                // checkpoint (docs/ARCHITECTURE.md section 20) before draining the queues and
                // closing the final chunks, so a crash during that window leaves a session
                // that startup recovery treats as not-cleanly-stopped instead of RECORDING.
                BeginFinalizing(tracks);

                foreach (var track in tracks)
                {
                    track.CompleteWriter();
                }

                await Task.WhenAll(consumers).ConfigureAwait(false);

                CancelEnd();
                await housekeeping.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An exception escaping the awaits must not leave FINALIZING as the durable
                // session state with no explanation. Record it as a storage failure and fall
                // through to the same teardown, so the session always reaches a described
                // terminal state and the already-closed chunks stay durable
                // (docs/DEVELOPMENT.md section 8).
                RegisterStorageFailure(ex);
            }

            return Complete();
        }
        finally
        {
            foreach (var track in _tracks ?? new List<CaptureTrack>())
            {
                track.Dispose();
            }

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

    private (List<CaptureTrack> Tracks, List<IAudioCaptureSource> Sources) CreateTracks()
    {
        var tracks = new List<CaptureTrack>(_trackSpecs.Count);
        var sources = new List<IAudioCaptureSource>(_trackSpecs.Count);

        try
        {
            foreach (var spec in _trackSpecs)
            {
                var track = new CaptureTrack(
                    _paths,
                    _settings,
                    _platform,
                    _database,
                    _events,
                    _clock,
                    spec.Source,
                    spec.Device,
                    spec.CreateSource,
                    spec.ReopenDevice,
                    _maxDeviceRecoveryAttempts,
                    _afterPacketWritten,
                    AnnounceChunk,
                    RegisterStorageFailure);

                // Create the source and initialize the track's runtime from its native
                // format before capture starts. A source that cannot be created aborts the
                // session before capture starts (online mode requires every track).
                var source = track.CreateInitialSource();
                track.Initialize(source.Format);
                tracks.Add(track);
                sources.Add(source);
            }

            return (tracks, sources);
        }
        catch
        {
            // Release everything created before the failure so nothing is left owning a
            // device or an open chunk.
            foreach (var source in sources)
            {
                source.Dispose();
            }

            foreach (var track in tracks)
            {
                track.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Initializes each track's timeline/spool/queue from its source format. Kept
    /// separate from <see cref="CreateTracks"/> so the sources exist and their formats
    /// are known before any track is initialized; <c>CreateTracks</c> already calls
    /// <see cref="CaptureTrack.Initialize"/> so this is currently a no-op placeholder for
    /// any future shared initialization.
    /// </summary>
    private void _formatInitializers(List<CaptureTrack> tracks)
    {
        // Tracks are initialized in CreateTracks. Kept as a seam so the session can grow
        // shared pre-capture initialization without rewriting RunAsync.
        _ = tracks;
    }

    private void AnnounceChunk(ClosedAudioChunk chunk) => ChunkClosed?.Invoke(chunk);

    private void BeginRecording(List<CaptureTrack> tracks)
    {
        var startedAt = _clock.UtcNow;

        _manifest.Status = SessionStatus.Recording;
        _manifest.StartedAt = startedAt;
        _manifest.Capture = tracks
            .Select(t => CaptureTrackInfo.From(t.Source, t.Device, t.Format!))
            .ToArray();
        SessionManifestStore.Save(_paths.ManifestPath, _manifest);

        _database.Sessions.UpdateLifecycle(
            _paths.SessionId,
            SessionStatus.Recording,
            startedAt,
            stoppedAt: null,
            durationMs: 0);

        _events.Write(new SessionEvent(SessionEventNames.SessionStarted, 0)
        {
            Detail = DescribeTracks(tracks),
        });
    }

    private static string DescribeTracks(List<CaptureTrack> tracks)
    {
        var parts = tracks.Select(t =>
        {
            var format = t.Format;
            return format is null
                ? $"source='{t.Source.ToWireName()}' device='{t.Device.DisplayName}'"
                : $"source='{t.Source.ToWireName()}' device='{t.Device.DisplayName}' format='{format}'";
        });
        return "tracks: " + string.Join("; ", parts);
    }

    /// <summary>
    /// Writes the documented FINALIZING checkpoint (docs/ARCHITECTURE.md section 20):
    /// capture has ended and the recording artifacts are about to be drained and closed.
    /// A crash in that window leaves the session in FINALIZING, which startup recovery
    /// treats as not-cleanly-stopped (docs/RELIABILITY.md section 6), so the already-
    /// renamed final WAVs are reconciled rather than left unindexed.
    /// </summary>
    private void BeginFinalizing(List<CaptureTrack> tracks)
    {
        if (tracks.TrueForAll(t => !t.CaptureStarted))
        {
            // A session whose capture never started has no artifacts to finalize; it is
            // marked interrupted directly by Complete().
            return;
        }

        var atMs = tracks.Max(t => t.LastTimelineMs);

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
            Detail = "capture ended; closing recording artifacts before the session completes.",
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
                if (_stopSignal.IsRequested())
                {
                    _events.Write(new SessionEvent(SessionEventNames.SessionStopRequested, SessionTimelineMs())
                    {
                        Detail = _stopSignal.ReadReason() ?? "stop requested",
                    });
                    RequestEnd("stop_requested");
                    return;
                }

                var now = _clock.UtcNow;

                if ((now - lastFlush).TotalMilliseconds >= _settings.FlushIntervalMs)
                {
                    lastFlush = now;
                    if (_tracks is not null)
                    {
                        foreach (var track in _tracks)
                        {
                            track.RequestFlush();
                        }
                    }
                }

                if (_tracks is not null)
                {
                    foreach (var track in _tracks)
                    {
                        track.CheckConsumerBacklog();
                    }
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
                _events.Write(new SessionEvent(SessionEventNames.StorageProbeFailed, SessionTimelineMs())
                {
                    Detail = ex.Message + " Recording continues; free space is simply not being monitored.",
                });
            }

            return;
        }

        if (verdict.Level == DiskSpaceLevel.Critical)
        {
            _degraded = true;
            _events.Write(new SessionEvent(SessionEventNames.StorageDiskExhausted, SessionTimelineMs())
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
        _events.Write(new SessionEvent(SessionEventNames.StorageLowDiskSpace, SessionTimelineMs())
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
            Detail = "capture_start_failed: " + error.Message,
        });

        _stopSignal.Clear();
        _endCts?.Dispose();
        _endCts = null;

        var capacity = CaptureTrack.QueueCapacity(_settings);
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
            CaptureHealth = new AudioBufferHealth { CapacityPackets = capacity },
        };
    }

    private RecordingSessionOutcome Complete()
    {
        var tracks = _tracks ?? new List<CaptureTrack>();
        var durationMs = tracks.Count > 0 ? tracks.Max(t => t.LastTimelineMs) : 0;

        // A run where no track ever started capture produced no audio and did not stop
        // cleanly: that is an interrupted session, not a completed one.
        var anyStarted = tracks.Any(t => t.CaptureStarted);
        var anyTrackDegraded = tracks.Any(t => t.Degraded);

        // The session ended through a stop request or storage failure only when
        // RequestEnd was called; a track that ended on its own (device loss, format
        // change) leaves _endReason null, in which case the session's end reason is the
        // dominant track reason (docs/RELIABILITY.md section 8: a track's fatal loss is
        // explicit and never silently folded away).
        var status = !anyStarted || _storageFailure is not null
            ? SessionStatus.Interrupted
            // Post-capture work is wired (live file-ASR batching), so a clean stop lands on
            // the documented FINALIZING -> PROCESSING checkpoint instead of claiming the
            // session is finished while its ASR queue still holds work
            // (docs/ARCHITECTURE.md section 20).
            : _postCaptureProcessingExpected ? SessionStatus.Processing : SessionStatus.Completed;

        var endReason = !anyStarted
            ? "capture_start_failed"
            : _endReason ?? DominantTrackEndReason(tracks) ?? "capture_ended";

        var degraded = !anyStarted || _storageFailure is not null || anyTrackDegraded || _degraded;

        DateTimeOffset? stoppedAt = status is SessionStatus.Completed or SessionStatus.Processing
            ? _clock.UtcNow
            : null;

        // The gap and bounded-buffer accounting is written into the session document, not
        // only into the event log, so "how much audio is missing and how close did the
        // queue come to its bound" survives as part of the durable record
        // (docs/RELIABILITY.md sections 4 and 7). Per-track health is recorded separately
        // so the loss of one track is explicit (docs/ROADMAP.md M5).
        var trackHealth = tracks.Select(t => t.ToOutcome()).ToList();
        var aggregate = AggregateHealth(trackHealth);

        _manifest.GapCount = aggregate.GapCount;
        _manifest.GapTotalMs = aggregate.GapTotalMs;
        _manifest.CaptureHealth = aggregate;
        _manifest.TrackHealth = trackHealth
            .Select(o => new TrackHealth(
                o.Source,
                o.Health,
                o.GapTotalMs,
                o.GapCount,
                o.Degraded,
                o.EndReason,
                o.ChunksClosed,
                o.ClosedDataBytes))
            .ToList();
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
            EndMs = durationMs,
            GapMs = aggregate.GapTotalMs > 0 ? aggregate.GapTotalMs : null,
            Count = tracks.Sum(t => t.ClosedChunkCount),
            Detail = _storageFailure is null ? endReason : endReason + ": " + _storageFailure.Message,
        });

        var totalChunks = tracks.Sum(t => t.ClosedChunkCount);
        var totalBytes = tracks.Sum(t => t.ClosedDataBytes);

        return new RecordingSessionOutcome(
            _paths.SessionId,
            _paths.SessionDirectory,
            status,
            durationMs,
            totalChunks,
            totalBytes,
            degraded,
            endReason)
        {
            GapTotalMs = aggregate.GapTotalMs,
            GapCount = aggregate.GapCount,
            CaptureHealth = aggregate,
            TrackHealth = _manifest.TrackHealth,
        };
    }

    private static AudioBufferHealth AggregateHealth(IReadOnlyList<CaptureTrackOutcome> tracks)
    {
        if (tracks.Count == 0)
        {
            return new AudioBufferHealth();
        }

        if (tracks.Count == 1)
        {
            return tracks[0].Health;
        }

        var health = new AudioBufferHealth
        {
            CapacityPackets = tracks.Sum(t => t.Health.CapacityPackets),
            PeakQueuedPackets = tracks.Sum(t => t.Health.PeakQueuedPackets),
            DroppedPackets = tracks.Sum(t => t.Health.DroppedPackets),
            OverflowEvents = tracks.Sum(t => t.Health.OverflowEvents),
            LongestStallMs = tracks.Max(t => t.Health.LongestStallMs),
            StallEvents = tracks.Sum(t => t.Health.StallEvents),
            GapTotalMs = tracks.Sum(t => t.GapTotalMs),
            GapCount = tracks.Sum(t => t.GapCount),
        };
        // IsDegraded is computed from the summed buffer counters; the per-track degraded
        // verdict (which also covers device loss and format change) is carried separately
        // by TrackHealth and the manifest's Degraded flag.
        return health;
    }

    /// <summary>The session timeline position: the latest any track has reached.</summary>
    private long SessionTimelineMs()
        => _tracks is null || _tracks.Count == 0 ? 0 : _tracks.Max(t => t.LastTimelineMs);

    /// <summary>
    /// The first non-null track end reason, when the session ended because its tracks
    /// ended on their own (device loss, format change) rather than through a stop request
    /// or a storage failure. The single-track device-loss path keeps its original
    /// "device_lost" reason this way; a dual-track session whose tracks end independently
    /// reports the first one that ended (docs/RELIABILITY.md section 8).
    /// </summary>
    private static string? DominantTrackEndReason(List<CaptureTrack> tracks)
    {
        foreach (var track in tracks)
        {
            if (!string.IsNullOrEmpty(track.EndReason))
            {
                return track.EndReason;
            }
        }

        return null;
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
}
