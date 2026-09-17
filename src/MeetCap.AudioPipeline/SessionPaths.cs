namespace MeetCap.AudioPipeline;

using System.Globalization;
using MeetCap.Core.Capture;

/// <summary>
/// Canonical filesystem layout of one session
/// (docs/DATA_MODEL.md section 2 and docs/ARCHITECTURE.md section 18).
/// </summary>
/// <remarks>
/// Everything the pipeline writes goes through this type, so the artifact contract
/// is expressed exactly once.
/// </remarks>
public sealed class SessionPaths
{
    public const string SessionsFolderName = "sessions";
    public const string ManifestFileName = "session.json";
    public const string EventsFileName = "events.jsonl";
    public const string StopRequestFileName = "stop.request";
    public const string RecordingLockFileName = "recording.lock";
    public const string AudioFolderName = "audio";
    public const string PartSuffix = ".part";
    public const string WaveExtension = ".wav";

    public SessionPaths(string dataRoot, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        DataRoot = Path.GetFullPath(dataRoot);
        SessionId = sessionId;
    }

    public string DataRoot { get; }

    public string SessionId { get; }

    public string SessionsRoot => Path.Combine(DataRoot, SessionsFolderName);

    public string SessionDirectory => Path.Combine(SessionsRoot, SessionId);

    public string ManifestPath => Path.Combine(SessionDirectory, ManifestFileName);

    public string EventsPath => Path.Combine(SessionDirectory, EventsFileName);

    /// <summary>
    /// Control file written by <c>meetcap stop</c> and polled by the recording
    /// process. A plain marker file keeps cross-process stop free of any lock
    /// protocol (docs/DEVELOPMENT.md section 3).
    /// </summary>
    public string StopRequestPath => Path.Combine(SessionDirectory, StopRequestFileName);

    /// <summary>
    /// Liveness marker held exclusively by the recording process while a session owns
    /// the recording surface. Recovery scans use it to tell a live recording apart from
    /// a session abandoned by a killed process (docs/ARCHITECTURE.md section 9.1).
    /// </summary>
    public string RecordingLockPath => Path.Combine(SessionDirectory, RecordingLockFileName);

    public string AudioDirectory(AudioSource source)
        => Path.Combine(SessionDirectory, AudioFolderName, source.ToWireName());

    /// <summary>Creates the session directory tree. Safe to call repeatedly.</summary>
    /// <param name="includeLoopback">
    /// When true, also creates <c>audio/loopback/</c> for an online session. Offline
    /// sessions omit it (docs/DATA_MODEL.md section 2: for offline mode,
    /// <c>audio/loopback/</c> is absent).
    /// </param>
    public void CreateDirectories(bool includeLoopback = false)
    {
        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(AudioDirectory(AudioSource.Mic));
        if (includeLoopback)
        {
            Directory.CreateDirectory(AudioDirectory(AudioSource.Loopback));
        }
    }

    /// <summary>The durable chunk name, e.g. <c>000001.wav</c>.</summary>
    public static string ChunkFileName(int sequence)
        => sequence.ToString("D6", CultureInfo.InvariantCulture) + WaveExtension;

    public string ChunkFinalPath(AudioSource source, int sequence)
        => Path.Combine(AudioDirectory(source), ChunkFileName(sequence));

    public string ChunkPartPath(AudioSource source, int sequence)
        => ChunkFinalPath(source, sequence) + PartSuffix;

    /// <summary>
    /// Path stored in the chunk index, relative to the session directory and with
    /// forward slashes so the index survives a move between platforms.
    /// </summary>
    public string RelativeChunkPath(AudioSource source, int sequence)
        => string.Join(
            '/',
            AudioFolderName,
            source.ToWireName(),
            ChunkFileName(sequence));

    /// <summary>Enumerates existing session directories, newest name last.</summary>
    public static IReadOnlyList<string> EnumerateSessionDirectories(string dataRoot)
    {
        var sessionsRoot = Path.Combine(Path.GetFullPath(dataRoot), SessionsFolderName);
        if (!Directory.Exists(sessionsRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(sessionsRoot)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }
}
