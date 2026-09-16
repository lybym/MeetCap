namespace MeetCap.AudioPipeline;

/// <summary>
/// Exclusive liveness marker for one recording session: whoever holds this lock owns
/// the session's chunk surface right now.
/// </summary>
/// <remarks>
/// <para>
/// The startup recovery scan is destructive by design — it patches headers, renames
/// <c>.part</c> files and rewrites session state — so it must never run against a
/// recording that is still in progress. Artifact presence alone cannot express that:
/// a live recording usually has no <c>.part</c> file at the instant <c>meetcap status</c>
/// runs, and its session row is legitimately <c>RECORDING</c>, which is also exactly
/// what an abandoned session looks like after a crash.
/// </para>
/// <para>
/// An exclusively-opened file is the cheapest mechanism that distinguishes the two: the
/// operating system releases the handle when the process exits for any reason, including
/// a forced kill, so a crashed recorder never leaves a false liveness marker behind. No
/// pid file, heartbeat or owner field is needed (docs/DEVELOPMENT.md section 3).
/// </para>
/// <para>
/// The lock is only ever acquired by the recording process. A recovery scan never takes
/// the lock for itself; it only asks whether someone else holds it.
/// </para>
/// </remarks>
internal sealed class SessionRecordingLock : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;

    private SessionRecordingLock(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    /// <summary>Full path of the liveness marker.</summary>
    public string Path => _path;

    /// <summary>
    /// Takes the lock for this process, or returns <c>null</c> when another process
    /// already holds it.
    /// </summary>
    public static SessionRecordingLock? TryAcquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                // Write access, so a reader that merely opens the file read-only cannot
                // be mistaken for the owner.
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            return new SessionRecordingLock(path, stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sharing violation means another live recorder owns this session.
            return null;
        }
    }

    /// <summary>Whether another process currently owns this session.</summary>
    public static bool IsHeld(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        _stream = null;
        stream.Dispose();

        // The marker has no meaning once it is unlocked, and leaving it behind would
        // make a completed session directory look like it still owns the recording
        // surface. Releasing the handle first also means a concurrent scan can never
        // observe an owned-but-deleted marker.
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An undeletable leftover is harmless: it is not held, so recovery treats
            // the session as stopped.
        }
    }
}
