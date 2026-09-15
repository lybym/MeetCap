namespace MeetCap.Core.Storage;

/// <summary>
/// Reports free space for the volume that holds a path. Abstracted so the
/// low-disk-space policy in docs/RELIABILITY.md section 10 is testable without
/// filling a real volume.
/// </summary>
public interface IDiskSpaceProbe
{
    /// <summary>
    /// Available free bytes on the volume containing <paramref name="path"/>. The
    /// path itself need not exist yet; its volume does.
    /// </summary>
    long GetAvailableFreeBytes(string path);
}
