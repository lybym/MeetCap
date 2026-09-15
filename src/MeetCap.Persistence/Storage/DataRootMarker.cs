namespace MeetCap.Persistence.Storage;

using System.Text;

/// <summary>
/// Marks the MeetCap data root as local-private data.
/// </summary>
/// <remarks>
/// <para>
/// The repository <c>.gitignore</c> only ignores <c>/sessions/</c> at the repository root,
/// because an unanchored <c>sessions/</c> pattern also swallowed the
/// <c>src/MeetCap.Core/Sessions</c> source folder. That narrowing means a data root placed
/// inside the working tree (a relative <c>storage.data_root</c>, or a one-shot
/// <c>--data-root</c>) would otherwise be committable, and recordings, transcripts, and
/// voiceprints are private local data (<c>docs/ARCHITECTURE.md</c> section 22).
/// </para>
/// <para>
/// Dropping a self-ignoring <c>.gitignore</c> (<c>*</c>) into the data root restores that
/// protection where the data actually lives, independently of where the data root is.
/// Best effort by design: a read-only or unusual data root must never fail a command.
/// </para>
/// </remarks>
public static class DataRootMarker
{
    public const string MarkerFileName = ".gitignore";

    private const string Content =
        "# MeetCap data root: recordings, transcripts, voiceprints and the database are\n" +
        "# private local data (docs/ARCHITECTURE.md section 22). Never commit them.\n" +
        "*\n";

    /// <summary>Creates the data root and, if absent, writes the self-ignoring marker into it.</summary>
    public static void EnsureSelfIgnoring(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dataRoot);
            var markerPath = Path.Combine(dataRoot, MarkerFileName);

            // Never overwrite: the repository root already has a real .gitignore, and a user
            // may have put their own rules in the data root.
            if (File.Exists(markerPath))
            {
                return;
            }

            File.WriteAllText(markerPath, Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A convenience marker must never block the actual work.
        }
    }
}
