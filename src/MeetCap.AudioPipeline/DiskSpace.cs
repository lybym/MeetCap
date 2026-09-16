namespace MeetCap.AudioPipeline;

using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Storage;

/// <summary>
/// Production disk-space probe. Reports free space for the volume that holds a path,
/// walking up to the nearest existing ancestor so it works before the session
/// directory has been created.
/// </summary>
public sealed class DriveInfoDiskSpaceProbe : IDiskSpaceProbe
{
    public long GetAvailableFreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var anchor = ResolveExistingAnchor(path);
        try
        {
            return new DriveInfo(anchor).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new MeetCapException(
                $"Free space for '{path}' could not be determined (volume '{anchor}'): {ex.Message}",
                ex);
        }
    }

    private static string ResolveExistingAnchor(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MeetCapException($"'{path}' is not a usable path: {ex.Message}", ex);
        }

        var candidate = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(candidate) && !Directory.Exists(candidate))
        {
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.Ordinal))
            {
                break;
            }

            candidate = parent;
        }

        if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
        {
            return candidate;
        }

        return Path.GetPathRoot(full) ?? full;
    }
}

/// <summary>How urgent the free-space situation is.</summary>
public enum DiskSpaceLevel
{
    Ok,

    /// <summary>Below the configured <c>storage.minimum_free_space_gb</c> but still usable.</summary>
    Low,

    /// <summary>Below the hard floor; recording must stop before the volume fills.</summary>
    Critical,
}

/// <summary>Free-space observation.</summary>
public readonly record struct DiskSpaceVerdict(DiskSpaceLevel Level, long FreeBytes)
{
    public bool IsOk => Level == DiskSpaceLevel.Ok;
}

/// <summary>
/// Implements the disk-space policy of docs/RELIABILITY.md section 10: check before
/// starting, warn during recording, and stop visibly before the volume is exhausted
/// rather than corrupting the session.
/// </summary>
public sealed class DiskSpaceMonitor
{
    private readonly IDiskSpaceProbe _probe;

    public DiskSpaceMonitor(
        IDiskSpaceProbe probe,
        long minimumFreeBytes,
        long criticalFreeBytes = CaptureSettings.CriticalFreeSpaceBytes)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

        if (minimumFreeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumFreeBytes),
                minimumFreeBytes,
                "Minimum free space must be positive.");
        }

        if (criticalFreeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(criticalFreeBytes),
                criticalFreeBytes,
                "Critical free space must be positive.");
        }

        MinimumFreeBytes = minimumFreeBytes;
        CriticalFreeBytes = criticalFreeBytes;
    }

    public long MinimumFreeBytes { get; }

    public long CriticalFreeBytes { get; }

    /// <summary>Observes free space and classifies it for the current policy.</summary>
    public DiskSpaceVerdict Inspect(string path)
    {
        var free = _probe.GetAvailableFreeBytes(path);

        var level = free < CriticalFreeBytes
            ? DiskSpaceLevel.Critical
            : free < MinimumFreeBytes
                ? DiskSpaceLevel.Low
                : DiskSpaceLevel.Ok;

        return new DiskSpaceVerdict(level, free);
    }

    /// <summary>
    /// Pre-start check. Throws instead of returning a verdict: docs/RELIABILITY.md
    /// section 10 requires recording to fail visibly rather than start onto a volume
    /// it cannot finish writing to.
    /// </summary>
    /// <exception cref="InsufficientDiskSpaceException">Free space is below the configured minimum.</exception>
    public void EnsureSufficientAtStart(string path)
    {
        var verdict = Inspect(path);
        if (verdict.IsOk)
        {
            return;
        }

        throw new InsufficientDiskSpaceException(
            $"Only {Format(verdict.FreeBytes)} free on the volume holding '{path}', but " +
            $"storage.minimum_free_space_gb requires at least {Format(MinimumFreeBytes)}. " +
            "Free some space, lower storage.minimum_free_space_gb, or point storage.data_root at another volume.");
    }

    /// <summary>Human-readable byte count for messages and events.</summary>
    public static string Format(long bytes)
    {
        const double GiB = 1024d * 1024d * 1024d;
        const double MiB = 1024d * 1024d;

        return bytes >= GiB
            ? $"{bytes / GiB:F2} GB"
            : $"{bytes / MiB:F1} MB";
    }
}
