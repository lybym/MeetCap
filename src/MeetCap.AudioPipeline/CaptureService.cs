namespace MeetCap.AudioPipeline;

using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;

/// <summary>A recording session that is currently open.</summary>
public sealed record ActiveSessionInfo(
    string SessionId,
    string SessionDirectory,
    string Title,
    string Status,
    DateTimeOffset? StartedAt);

/// <summary>What <c>meetcap stop</c> managed to do.</summary>
public sealed record StopRequestOutcome(
    bool Signalled,
    string? SessionId,
    string Message,
    bool ConfirmedStopped);

/// <summary>
/// What <c>meetcap session repair</c> achieved for one session (or for every session when
/// no session was named).
/// </summary>
/// <remarks>
/// <see cref="RecoveryIncomplete"/> exists so the CLI can return a non-zero exit code
/// while a known gap remains. docs/RELIABILITY.md section 6 forbids claiming success in
/// that case, and an operator scripting a repair has to be able to see the difference.
/// </remarks>
public sealed record SessionRepairOutcome(string? RequestedSessionId, RecoveryReport Report)
{
    /// <summary>True when the requested session was found and processed.</summary>
    public bool Found
        => RequestedSessionId is null || Report.Sessions.Count > 0;

    /// <summary>True when a known gap remains, or the requested session does not exist.</summary>
    public bool RecoveryIncomplete => !Found || Report.RecoveryIncomplete;

    /// <summary>Missing audio across the repaired sessions, in milliseconds.</summary>
    public long RemainingGapMs => Report.RemainingGapMs;

    /// <summary>A one-line summary for the CLI.</summary>
    public string Describe()
    {
        if (!Found)
        {
            return $"session '{RequestedSessionId}' was not found under the data root";
        }

        return Report.Describe();
    }
}

/// <summary>
/// The recording operations the CLI drives: device listing, session preparation,
/// running a session, requesting a stop, and the startup recovery scan.
/// </summary>
/// <remarks>
/// Keeping this here instead of in the command handlers means <c>meetcap start</c>,
/// <c>meetcap stop</c> and <c>meetcap status</c> stay thin adapters over one
/// implementation (docs/DEVELOPMENT.md section 8).
/// </remarks>
public sealed class CaptureService
{
    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly CapturePlatform _platform;
    private readonly MeetCapDatabase _database;
    private readonly CaptureSettings _settings;
    private readonly int _maxDeviceRecoveryAttempts;

    public CaptureService(
        CapturePlatform platform,
        MeetCapDatabase database,
        CaptureSettings settings,
        int maxDeviceRecoveryAttempts = 3)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (maxDeviceRecoveryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDeviceRecoveryAttempts),
                maxDeviceRecoveryAttempts,
                "Device recovery attempts must not be negative.");
        }

        _maxDeviceRecoveryAttempts = maxDeviceRecoveryAttempts;
    }

    public CaptureSettings Settings => _settings;

    /// <summary>Active capture endpoints, default first.</summary>
    public IReadOnlyList<CaptureDeviceInfo> ListDevices() => OrderDevices(_platform.Devices.EnumerateCaptureDevices());

    /// <summary>
    /// Stable presentation order for device listings: the system default first, then
    /// alphabetical. Shared so <c>meetcap devices</c> does not need a database just to
    /// sort a list.
    /// </summary>
    public static IReadOnlyList<CaptureDeviceInfo> OrderDevices(IEnumerable<CaptureDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        return devices
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The endpoint a session would record from.</summary>
    /// <exception cref="DeviceUnavailableException">The configured device cannot be resolved.</exception>
    public CaptureDeviceInfo ResolveDevice()
        => AudioDeviceResolver.Resolve(_platform.Devices, _settings.MicrophoneDeviceId);

    /// <summary>Runs the startup scan (docs/RELIABILITY.md section 6).</summary>
    public RecoveryReport RunStartupRecovery()
    {
        _database.EnsureMigrated();
        return new SessionRecoveryScanner(_database, _platform.Clock).Scan(_settings.DataRoot);
    }

    /// <summary>
    /// Repairs one session, or every session when <paramref name="sessionId"/> is null,
    /// and audits the result (docs/RELIABILITY.md section 6).
    /// </summary>
    /// <remarks>
    /// This is the entry point behind <c>meetcap session repair</c>. It runs the same
    /// scanner the startup path runs, so an operator-triggered repair cannot classify an
    /// artifact differently from an automatic one.
    /// </remarks>
    public SessionRepairOutcome RepairSession(string? sessionId)
    {
        _database.EnsureMigrated();
        var scanner = new SessionRecoveryScanner(_database, _platform.Clock);
        var report = scanner.Scan(_settings.DataRoot, sessionId);
        return new SessionRepairOutcome(sessionId, report);
    }

    /// <summary>The newest session that still holds the recording surface, if any.</summary>
    public ActiveSessionInfo? FindActiveSession()
    {
        var record = _database.Sessions.FindActiveSession();
        if (record is null)
        {
            return null;
        }

        return new ActiveSessionInfo(
            record.Id,
            new SessionPaths(_settings.DataRoot, record.Id).SessionDirectory,
            record.Title,
            record.Status,
            record.StartedAt);
    }

    /// <summary>
    /// Creates the session directory, manifest and index row, then returns the runner
    /// for it. Everything that can fail before recording — device resolution and the
    /// free-space check — fails here, before any artifact is written.
    /// </summary>
    /// <remarks>
    /// The returned session already holds its exclusive liveness marker
    /// (<c>recording.lock</c>). Claiming ownership here rather than in
    /// <see cref="RecordingSession.RunAsync"/> is what makes publication and recovery
    /// exclusion atomic: <c>meetcap status</c> runs the startup recovery scan from another
    /// process, so a session that is visible as <c>CREATED</c> but not yet owned is
    /// indistinguishable from one abandoned by a killed recorder. Disposing the returned
    /// session without running it releases the marker, so an abandoned session is left
    /// recoverable (docs/ARCHITECTURE.md section 9.1).
    /// </remarks>
    /// <param name="title">Session title recorded in the manifest and the index row.</param>
    /// <param name="afterPacketWritten">
    /// Test seam forwarded to the recording session, so a test can make the consumer slow
    /// and observe bounded-buffer behaviour. Production callers omit it.
    /// </param>
    /// <exception cref="DeviceUnavailableException">No usable microphone.</exception>
    /// <exception cref="InsufficientDiskSpaceException">Not enough free space to record.</exception>
    /// <exception cref="MeetCapException">Another process already owns this session's marker.</exception>
    public RecordingSession PrepareSession(string title, Action? afterPacketWritten = null)
    {
        var device = ResolveDevice();

        new DiskSpaceMonitor(_platform.DiskSpace, _settings.MinimumFreeSpaceBytes)
            .EnsureSufficientAtStart(_settings.DataRoot);

        var now = _platform.Clock.UtcNow;
        var sessionId = SessionIds.Create(now);
        var paths = new SessionPaths(_settings.DataRoot, sessionId);
        paths.CreateDirectories();

        SessionRecordingLock? recordingLock = null;
        try
        {
            // Take the liveness marker before the session exists anywhere else, so there
            // is no instant at which a recovery scan can see it unowned.
            recordingLock = AcquireRecordingLock(paths);

            var manifest = new SessionManifest
            {
                SessionId = sessionId,
                Title = title,
                Mode = SessionModes.Offline,
                SourceType = SessionSourceTypes.Live,
                Status = SessionStatus.Created,
                ConfigVersion = _settings.ConfigVersion,
                Tracks = new[] { AudioSources.Mic },
                ChunkSeconds = _settings.ChunkSeconds,
            };
            SessionManifestStore.Save(paths.ManifestPath, manifest);

            _database.Sessions.Insert(new SessionRecord
            {
                Id = sessionId,
                Title = title,
                Mode = SessionModes.Offline,
                SourceType = SessionSourceTypes.Live,
                Status = SessionStatus.Created,
                ConfigVersion = _settings.ConfigVersion,
                ConfigSnapshot = CaptureConfigSnapshot.ToJson(_settings),
                Tracks = new[] { AudioSources.Mic },
                CreatedAt = now,
                UpdatedAt = now,
            });

            var session = new RecordingSession(
                paths,
                _settings,
                _platform,
                _database,
                new JsonlSessionEventSink(paths.EventsPath),
                device,
                _platform.Clock,
                manifest,
                _maxDeviceRecoveryAttempts,
                recordingLock,
                afterPacketWritten);

            recordingLock = null;
            return session;
        }
        finally
        {
            // Only reached when publishing failed: on success the marker is owned by the
            // returned session and must outlive this method.
            recordingLock?.Dispose();
        }
    }

    private static SessionRecordingLock AcquireRecordingLock(SessionPaths paths)
    {
        var acquired = SessionRecordingLock.TryAcquire(paths.RecordingLockPath);
        if (acquired is null)
        {
            throw new MeetCapException(
                $"session '{paths.SessionId}' is owned by another recording process; " +
                "refusing to publish a session whose liveness marker is already held.");
        }

        return acquired;
    }

    /// <summary>
    /// Signals the running recording process to stop and, when it is a live process,
    /// waits for the session to reach a terminal state.
    /// </summary>
    public StopRequestOutcome RequestStop(TimeSpan wait)
    {
        var active = FindActiveSession();
        if (active is null)
        {
            return new StopRequestOutcome(false, null, "no active recording session", false);
        }

        var paths = new SessionPaths(_settings.DataRoot, active.SessionId);
        if (!Directory.Exists(paths.SessionDirectory))
        {
            return new StopRequestOutcome(
                false,
                active.SessionId,
                $"session '{active.SessionId}' has no directory on disk; run 'meetcap status' to recover it",
                false);
        }

        new SessionStopSignal(paths.StopRequestPath).Request("meetcap stop");

        // Stopwatch rather than the injected clock: the wait must make progress even
        // when time is faked, and it must never be able to spin forever.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < wait)
        {
            Thread.Sleep(StopPollInterval);

            var current = _database.Sessions.Find(active.SessionId);
            if (current is null || !SessionStatus.IsRecordingOwned(current.Status))
            {
                return new StopRequestOutcome(true, active.SessionId, "recording stopped", true);
            }
        }

        return new StopRequestOutcome(
            true,
            active.SessionId,
            "stop requested; the recording process is still finalizing its last chunk",
            false);
    }
}
