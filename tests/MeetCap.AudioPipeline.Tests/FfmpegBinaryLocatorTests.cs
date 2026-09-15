using MeetCap.AudioPipeline;
using MeetCap.Core.Media;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// FFmpeg is an external runtime dependency, so locating it must be explicit and
/// diagnosable rather than an opaque process-start failure
/// (docs/ARCHITECTURE.md sections 2 and 13).
/// </summary>
public class FfmpegBinaryLocatorTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void ConfiguredFolder_Wins()
    {
        using var scratch = new BinaryScratch();
        var configured = scratch.CreateBinaryFolder("configured");
        var other = scratch.CreateBinaryFolder("other");

        var binaries = FfmpegBinaryLocator.Resolve(
            configured,
            Env(),
            candidateFolders: new[] { other },
            pathFolders: Array.Empty<string>());

        Assert.Equal(Path.Combine(configured, "ffmpeg.exe"), binaries.FfmpegPath);
        Assert.Equal(Path.Combine(configured, "ffprobe.exe"), binaries.FfprobePath);
        Assert.Equal(configured, binaries.BinaryFolder);
    }

    [Fact]
    public void EnvReference_IsResolvedThroughTheInjectedLookup()
    {
        using var scratch = new BinaryScratch();
        var configured = scratch.CreateBinaryFolder("env-bin");

        var binaries = FfmpegBinaryLocator.Resolve(
            "env:MEETCAP_FFMPEG_BIN",
            Env(("MEETCAP_FFMPEG_BIN", configured)),
            candidateFolders: Array.Empty<string>(),
            pathFolders: Array.Empty<string>());

        Assert.Equal(configured, binaries.BinaryFolder);
    }

    [Fact]
    public void FallsBackToCandidateFoldersThenPath()
    {
        using var scratch = new BinaryScratch();
        var candidate = scratch.CreateBinaryFolder("program-files");
        var onPath = scratch.CreateBinaryFolder("path");

        var fromCandidate = FfmpegBinaryLocator.Resolve(
            null,
            Env(),
            candidateFolders: new[] { @"C:\does\not\exist", candidate },
            pathFolders: new[] { onPath });
        Assert.Equal(candidate, fromCandidate.BinaryFolder);

        var fromPath = FfmpegBinaryLocator.Resolve(
            null,
            Env(),
            candidateFolders: new[] { @"C:\does\not\exist" },
            pathFolders: new[] { onPath });
        Assert.Equal(onPath, fromPath.BinaryFolder);
    }

    [Fact]
    public void MissingToolchain_FailsWithTheConfiguredKeyAndSearchedLocations()
    {
        using var scratch = new BinaryScratch();
        // A folder with only ffmpeg.exe is not usable: ffprobe is required for inspection.
        var half = Path.Combine(scratch.Root, "half");
        Directory.CreateDirectory(half);
        File.WriteAllText(Path.Combine(half, "ffmpeg.exe"), string.Empty);

        var ex = Assert.Throws<MediaToolingException>(() => FfmpegBinaryLocator.Resolve(
            null,
            Env(),
            candidateFolders: new[] { half },
            pathFolders: Array.Empty<string>()));

        Assert.Contains("media.ffmpeg_binary_folder", ex.Message, StringComparison.Ordinal);
        Assert.Contains(half, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitPathList_HandlesEmptyAndMultiEntryValues()
    {
        Assert.Empty(FfmpegBinaryLocator.SplitPathList(null));
        Assert.Empty(FfmpegBinaryLocator.SplitPathList("   "));

        var entries = FfmpegBinaryLocator.SplitPathList($"C:\\a{Path.PathSeparator}C:\\b");
        Assert.Equal(new[] { "C:\\a", "C:\\b" }, entries);
    }

    [Fact]
    public void TemporaryFolder_DefaultsUnderTheUserTempDirectory()
    {
        var folder = FfmpegBinaryLocator.ResolveTemporaryFolder(null, Env(("TEMP", @"C:\temp")));

        Assert.Equal(Path.Combine(@"C:\temp", "MeetCap", "ffmpeg"), folder);
    }

    [Fact]
    public void TemporaryFolder_HonoursAnOverride()
    {
        var folder = FfmpegBinaryLocator.ResolveTemporaryFolder(@"C:\scratch", Env());

        Assert.Equal(@"C:\scratch", folder);
    }

    /// <summary>Creates throwaway directories containing fake ffmpeg/ffprobe executables.</summary>
    private sealed class BinaryScratch : IDisposable
    {
        public BinaryScratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "meetcap-ffmpeg-locate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateBinaryFolder(string name)
        {
            var folder = Path.Combine(Root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "ffmpeg.exe"), string.Empty);
            File.WriteAllText(Path.Combine(folder, "ffprobe.exe"), string.Empty);
            return folder;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
