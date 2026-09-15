namespace MeetCap.AudioPipeline;

/// <summary>
/// Cross-process stop request. <c>meetcap start</c> records in the foreground, so
/// <c>meetcap stop</c> — running in a different process — signals it by creating a
/// marker file inside the session directory, which the recording loop polls.
/// </summary>
/// <remarks>
/// This is intentionally the simplest durable mechanism available: a single marker
/// file with no lock, no IPC endpoint and no cleanup protocol. docs/DEVELOPMENT.md
/// section 3 forbids inventing coordination machinery that the milestone does not
/// require.
/// </remarks>
public sealed class SessionStopSignal
{
    public SessionStopSignal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>Full path of the marker file.</summary>
    public string Path { get; }

    /// <summary>Creates the marker. Idempotent.</summary>
    public void Request(string reason)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-to-temp then move: a reader either sees the whole marker or none of it.
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, reason);
        File.Move(temporary, Path, overwrite: true);
    }

    /// <summary>Whether a stop has been requested.</summary>
    public bool IsRequested() => File.Exists(Path);

    /// <summary>Removes the marker. Used when starting a fresh session in a reused directory.</summary>
    public void Clear()
    {
        try
        {
            File.Delete(Path);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to clear.
        }
    }

    /// <summary>Reads the requested reason, when present.</summary>
    public string? ReadReason()
    {
        try
        {
            return File.Exists(Path) ? File.ReadAllText(Path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
