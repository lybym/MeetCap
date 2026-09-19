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

    /// <summary>
    /// Creates the capture service for one session's settings.
    /// </summary>
    /// <param name="maxDeviceRecoveryAttempts">
    /// Test seam: an explicit cap on device-recovery attempts, used by tests that must not
    /// spend the whole production recovery window in real time. Production passes
    /// <c>null</c>, and the budget is then derived from
    /// <see cref="CaptureSettings.DeviceRecoverySeconds"/>
    /// (docs/RELIABILITY.md section 8).
    /// </param>
    public CaptureService(
        CapturePlatform platform,
        MeetCapDatabase database,
        CaptureSettings settings,
        int? maxDeviceRecoveryAttempts = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (maxDeviceRecoveryAttempts is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDeviceRecoveryAttempts),
                maxDeviceRecoveryAttempts,
                "Device recovery attempts must not be negative.");
        }

        _maxDeviceRecoveryAttempts = maxDeviceRecoveryAttempts
            ?? RecoveryAttemptsFor(settings.DeviceRecoverySeconds);
    }

    /// <summary>
    /// How many device-recovery attempts realise a recovery window of
    /// <paramref name="deviceRecoverySeconds"/> seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is the policy; the attempt count is only how it is spent. A track waits
    /// <see cref="CaptureTrack.DeviceRecoveryBackoffMs"/> before each retry, so a window of N
    /// seconds is N retries and the budget is the window divided by that backoff. The two
    /// cannot drift apart because neither is configured separately
    /// (docs/CONFIGURATION.md section 6).
    /// </para>
    /// <para>
    /// The count is the loop bound rather than a wall-clock deadline read from the injected
    /// clock, because the injected clock is domain time that tests freeze deliberately: a
    /// deadline read from a frozen clock would never expire and the recovery loop could not
    /// terminate. A count is bounded whatever the clock does.
    /// </para>
    /// <para>
    /// The multiplication is done in <see cref="long"/> and only then narrowed. Seconds times
    /// milliseconds overflows <see cref="int"/> above 2,147,483 seconds, and the wrapped value
    /// is negative — which both downstream consumers clamp to zero, turning an operator's very
    /// long window into <em>no</em> recovery at all. That is the exact silent inversion of the
    /// configured policy this derivation exists to remove, so the arithmetic cannot be done in
    /// <see cref="int"/> (docs/CONFIGURATION.md section 6). Every window validation accepts as a
    /// non-negative value is therefore honoured: the result is never negative and never
    /// decreases as the window grows.
    /// </para>
    /// <para>
    /// The narrowing itself is checked rather than clamped. A window of
    /// <see cref="int.MaxValue"/> seconds — the longest one an <see cref="int"/> can carry, and
    /// the longest this overload can be given — yields exactly
    /// <see cref="int.MaxValue"/> attempts, so the checked conversion succeeds for every value
    /// reachable from configuration and the boundary is exact rather than approximate. A longer
    /// window, which only a caller handing this overload a <see cref="long"/> could express,
    /// has no representable attempt count; it throws instead of silently wrapping to a negative
    /// budget, which is the one failure mode the derivation exists to prevent
    /// (docs/CONFIGURATION.md section 6).
    /// </para>
    /// </remarks>
    /// <exception cref="OverflowException">
    /// The window's attempt count exceeds <see cref="int.MaxValue"/> and so cannot be expressed
    /// as a loop bound. Unreachable from configuration: validation's window is an
    /// <see cref="int"/> and its longest value is exactly representable.
    /// </exception>
    internal static int RecoveryAttemptsFor(int deviceRecoverySeconds)
        => RecoveryAttemptsFor((long)deviceRecoverySeconds);

    /// <inheritdoc cref="RecoveryAttemptsFor(int)"/>
    internal static int RecoveryAttemptsFor(long deviceRecoverySeconds)
    {
        var attempts = Math.Max(0L, deviceRecoverySeconds) * 1_000L / CaptureTrack.DeviceRecoveryBackoffMs;

        // Not Math.Min: a clamp here could never fire for any window an int can express, so it
        // would advertise a bound the derivation does not have. Checked narrowing is the guard
        // that is actually true of the arithmetic (docs/CONFIGURATION.md section 6).
        return checked((int)attempts);
    }

    public CaptureSettings Settings => _settings;

    /// <summary>
    /// The device-recovery attempt budget this service actually hands to its tracks.
    /// </summary>
    /// <remarks>
    /// Exposed for tests so the derivation can be checked through the production constructor
    /// rather than only as a standalone calculation
    /// (<see cref="RecoveryAttemptsFor(int)"/>, docs/CONFIGURATION.md section 6).
    /// </remarks>
    internal int MaxDeviceRecoveryAttempts => _maxDeviceRecoveryAttempts;

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

    /// <summary>
    /// Resolves the render endpoint an online session would loopback from, or
    /// <c>null</c> for an offline session (docs/ROADMAP.md M5).
    /// </summary>
    public CaptureDeviceInfo? ResolveRenderDevice()
        => _settings.Online is null
            ? null
            : AudioDeviceResolver.ResolveRender(_platform.Devices, _settings.Online.RenderDeviceId);

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
    /// for it. Everything that can fail before recording — device resolution, the
    /// free-space check and the loopback mode — fails here, before any artifact is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned session already holds its exclusive liveness marker
    /// (<c>recording.lock</c>). Claiming ownership here rather than in
    /// <see cref="RecordingSession.RunAsync"/> is what makes publication and recovery
    /// exclusion atomic: <c>meetcap status</c> runs the startup recovery scan from another
    /// process, so a session that is visible as <c>CREATED</c> but not yet owned is
    /// indistinguishable from one abandoned by a killed recorder. Disposing the returned
    /// session without running it releases the marker, so an abandoned session is left
    /// recoverable (docs/ARCHITECTURE.md section 9.1).
    /// </para>
    /// <para>
    /// An offline session resolves one microphone track; an online session resolves the
    /// microphone and a loopback track (docs/ROADMAP.md M5). The endpoints, the free-space
    /// level and <c>capture.online.loopback_mode</c> are all resolved and validated before
    /// the session exists, so those failures leave nothing behind.
    /// </para>
    /// <para>
    /// The capture <em>sources</em> are built later, by
    /// <see cref="RecordingSession.RunAsync"/>, because the process-loopback target cannot
    /// be resolved until the platform audio boundary constructs the source. A target that
    /// is not running therefore still fails after publication: the session is marked
    /// <c>INTERRUPTED</c> with <c>capture_start_failed</c> and the actionable reason
    /// surfaces on <see cref="RecordingSessionOutcome.StartFailureDetail"/>, which
    /// <c>meetcap start</c> prints to stderr (docs/RELIABILITY.md section 16).
    /// </para>
    /// </remarks>
    /// <param name="title">Session title recorded in the manifest and the index row.</param>
    /// <param name="afterPacketWritten">
    /// Test seam forwarded to the recording session, so a test can make the consumer slow
    /// and observe bounded-buffer behaviour. Production callers omit it.
    /// </param>
    /// <exception cref="DeviceUnavailableException">No usable microphone, or (online) no usable render endpoint.</exception>
    /// <exception cref="InsufficientDiskSpaceException">Not enough free space to record.</exception>
    /// <exception cref="MeetCapException">Another process already owns this session's marker.</exception>
    public RecordingSession PrepareSession(string title, Action? afterPacketWritten = null)
    {
        var micDevice = ResolveDevice();
        CaptureDeviceInfo? renderDevice = null;
        if (_settings.IsOnline)
        {
            renderDevice = ResolveRenderDevice();
        }

        new DiskSpaceMonitor(_platform.DiskSpace, _settings.MinimumFreeSpaceBytes)
            .EnsureSufficientAtStart(_settings.DataRoot);

        // Validate the loopback mode before publishing anything. The mode is parsed again
        // when the track specs are built below, but that happens inside the publication
        // block, so an invalid mode would otherwise surface only after the session
        // directory, manifest, sessions row and audio/loopback/ already existed
        // (docs/M1_WINDOWS_VALIDATION.md section 13, docs/RELIABILITY.md section 16).
        ValidateLoopbackMode();

        var now = _platform.Clock.UtcNow;
        var sessionId = SessionIds.Create(now);
        var paths = new SessionPaths(_settings.DataRoot, sessionId);
        paths.CreateDirectories(includeLoopback: _settings.IsOnline);

        var tracks = _settings.IsOnline
            ? new[] { AudioSources.Mic, AudioSources.Loopback }
            : new[] { AudioSources.Mic };

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
                Mode = _settings.Mode,
                SourceType = SessionSourceTypes.Live,
                Status = SessionStatus.Created,
                ConfigVersion = _settings.ConfigVersion,
                Tracks = tracks,
                ChunkSeconds = _settings.ChunkSeconds,
            };
            SessionManifestStore.Save(paths.ManifestPath, manifest);

            _database.Sessions.Insert(new SessionRecord
            {
                Id = sessionId,
                Title = title,
                Mode = _settings.Mode,
                SourceType = SessionSourceTypes.Live,
                Status = SessionStatus.Created,
                ConfigVersion = _settings.ConfigVersion,
                ConfigSnapshot = CaptureConfigSnapshot.ToJson(_settings),
                Tracks = tracks,
                CreatedAt = now,
                UpdatedAt = now,
            });

            var trackSpecs = BuildTrackSpecs(micDevice, renderDevice);

            var session = new RecordingSession(
                paths,
                _settings,
                _platform,
                _database,
                new JsonlSessionEventSink(paths.EventsPath),
                trackSpecs,
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

    /// <summary>
    /// Builds the per-track capture specs for this session: one microphone track for an
    /// offline session, plus a loopback track for an online session. Each spec carries
    /// the closures that (re)create its capture source and re-resolve its endpoint after
    /// a device loss, so the recording session stays source-agnostic and NAudio types
    /// never leave the platform boundary (docs/DEVELOPMENT.md section 4).
    /// </summary>
    private IReadOnlyList<CaptureTrackSpec> BuildTrackSpecs(
        CaptureDeviceInfo micDevice,
        CaptureDeviceInfo? renderDevice)
    {
        var specs = new List<CaptureTrackSpec>(2);

        var micDeviceId = _settings.MicrophoneDeviceId;
        specs.Add(new CaptureTrackSpec(
            AudioSource.Mic,
            micDevice,
            device => _platform.CaptureSources.Create(AudioSource.Mic, device),
            () => AudioDeviceResolver.TryResolve(_platform.Devices, micDeviceId)));

        if (_settings.IsOnline && _settings.Online is { } online && renderDevice is not null)
        {
            // The mode and the process name were validated before the session was published
            // (see ValidateLoopbackMode). Parsing again here keeps the spec the single
            // source of truth for the request the loopback track is built from.
            var loopbackMode = LoopbackModes.Parse(online.LoopbackMode);
            var processName = online.ProcessName;
            var renderDeviceId = online.RenderDeviceId;

            specs.Add(new CaptureTrackSpec(
                AudioSource.Loopback,
                renderDevice,
                device => _platform.CaptureSources.CreateLoopback(
                    new LoopbackCaptureRequest(device, loopbackMode, processName)),
                () => AudioDeviceResolver.TryResolveRender(_platform.Devices, renderDeviceId)));
        }

        return specs;
    }

    /// <summary>
    /// Rejects an unusable <c>capture.online.loopback_mode</c> before the session is
    /// published, so the failure leaves no session artifacts behind.
    /// </summary>
    /// <remarks>
    /// Only meaningful for an online session: the key configures the loopback track, which
    /// an offline session never builds. <c>start</c> reports the same problem from
    /// configuration validation, but the pipeline owns the value it actually parses, so it
    /// refuses it here too rather than trusting every caller
    /// (docs/DEVELOPMENT.md section 8).
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <c>capture.online.loopback_mode</c> is not one of the documented modes.
    /// </exception>
    private void ValidateLoopbackMode()
    {
        if (_settings.Online is { } online)
        {
            LoopbackModes.Parse(online.LoopbackMode);
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
