namespace MeetCap.AudioPipeline;

using MeetCap.Core.Media;

/// <summary>The resolved FFmpeg toolchain: the two external executables MeetCap invokes.</summary>
public sealed record FfmpegBinaries
{
    public required string FfmpegPath { get; init; }

    public required string FfprobePath { get; init; }

    /// <summary>Directory containing both executables.</summary>
    public string BinaryFolder => Path.GetDirectoryName(FfmpegPath) ?? string.Empty;
}

/// <summary>
/// Locates <c>ffmpeg.exe</c> and <c>ffprobe.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order, most explicit first:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>media.ffmpeg_binary_folder</c> from configuration. An <c>env:NAME</c> value is
/// resolved through the injected environment lookup, which keeps environment access
/// inside the configuration/secret resolver
/// (<c>docs/ARCHITECTURE.md</c> section 18).
/// </description></item>
/// <item><description>Well-known install locations (program files, local app data).</description></item>
/// <item><description><c>PATH</c>, as a last resort only.</description></item>
/// </list>
/// <para>
/// Nothing here falls back to invoking a bare <c>ffmpeg</c> command name, so failure
/// is reported before any capture or transcription work starts rather than as an
/// opaque process-start exception halfway through a job.
/// </para>
/// </remarks>
public static class FfmpegBinaryLocator
{
    public const string FfmpegExecutable = "ffmpeg.exe";
    public const string FfprobeExecutable = "ffprobe.exe";

    /// <summary>Builds the candidate list from real process environment values.</summary>
    public static IReadOnlyList<string> DefaultCandidateFolders(Func<string, string?> environment) =>
        new[]
        {
            Combine(environment("ProgramFiles"), "ffmpeg", "bin"),
            Combine(environment("ProgramFiles(x86)"), "ffmpeg", "bin"),
            Combine(environment("LOCALAPPDATA"), "ffmpeg", "bin"),
            Combine(environment("ProgramData"), "chocolatey", "bin"),
        };

    /// <summary>Splits a <c>PATH</c>-style value into directories.</summary>
    public static IReadOnlyList<string> SplitPathList(string? pathValue) =>
        string.IsNullOrWhiteSpace(pathValue)
            ? Array.Empty<string>()
            : pathValue
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    public static FfmpegBinaries Resolve(
        string? configuredBinaryFolder,
        Func<string, string?> environment,
        IReadOnlyList<string>? candidateFolders = null,
        IReadOnlyList<string>? pathFolders = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var resolved = ResolveConfiguredFolder(configuredBinaryFolder, environment);
        var searched = new List<string>();

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            searched.Add(resolved);
            if (TryResolveIn(resolved, searched, out var explicitBinaries))
            {
                return explicitBinaries;
            }
        }

        foreach (var folder in candidateFolders ?? DefaultCandidateFolders(environment))
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            searched.Add(folder);
            if (TryResolveIn(folder, searched, out var binaries))
            {
                return binaries;
            }
        }

        foreach (var folder in pathFolders ?? SplitPathList(environment("PATH")))
        {
            searched.Add(folder);
            if (TryResolveIn(folder, searched, out var binaries))
            {
                return binaries;
            }
        }

        throw new MediaToolingException(
            "FFmpeg and FFprobe could not be located. Install FFmpeg, then set " +
            "media.ffmpeg_binary_folder in config.toml to the directory containing " +
            $"{FfmpegExecutable} and {FfprobeExecutable}. Locations checked: " +
            (searched.Count == 0 ? "(none)" : string.Join("; ", searched.Distinct(StringComparer.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// Resolves the FFmpeg working directory, creating nothing but returning a stable
    /// per-user default when no override is configured.
    /// </summary>
    public static string ResolveTemporaryFolder(
        string? configuredTemporaryFolder,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var resolved = ResolveConfiguredFolder(configuredTemporaryFolder, environment);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        var root = environment("TEMP") ?? environment("TMP") ?? Path.GetTempPath();
        return Path.Combine(root, "MeetCap", "ffmpeg");
    }

    /// <summary>Resolves an <c>env:NAME</c> reference or returns the literal value.</summary>
    public static string ResolveConfiguredFolder(string? value, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const string scheme = "env:";
        if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            var name = value[scheme.Length..].Trim();
            return name.Length == 0 ? string.Empty : environment(name) ?? string.Empty;
        }

        return value.Trim();
    }

    private static bool TryResolveIn(string folder, List<string> searched, out FfmpegBinaries binaries)
    {
        var ffmpeg = Path.Combine(folder, FfmpegExecutable);
        var ffprobe = Path.Combine(folder, FfprobeExecutable);
        if (File.Exists(ffmpeg) && File.Exists(ffprobe))
        {
            binaries = new FfmpegBinaries { FfmpegPath = ffmpeg, FfprobePath = ffprobe };
            return true;
        }

        binaries = null!;
        return false;
    }

    private static string Combine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        var segments = new string[parts.Length + 1];
        segments[0] = root;
        Array.Copy(parts, 0, segments, 1, parts.Length);
        return Path.Combine(segments);
    }
}
