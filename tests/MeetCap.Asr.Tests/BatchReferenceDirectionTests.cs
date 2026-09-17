using System.Reflection;
using MeetCap.Asr.Batching;
using Xunit;

namespace MeetCap.Asr.Tests;

/// <summary>
/// Guards the one edge M4 added to the dependency graph:
/// <c>MeetCap.Asr -&gt; MeetCap.AudioPipeline</c>.
/// </summary>
/// <remarks>
/// <para>
/// The direction is deliberate — the ASR layer consumes the recording artifact contract (the WAV
/// writers, the spool, the session event sink) and the recording layer must never reach the ASR
/// stack — and it is what
/// <c>tests/MeetCap.AudioPipeline.Tests/CaptureIndependenceTests.cs</c> asserts from the opposite
/// side. That test would only catch this edge indirectly and only once it had already become a
/// cycle, so the reference is pinned here as well
/// (<c>docs/ARCHITECTURE.md</c> section 3, <c>docs/RELIABILITY.md</c> section 15).
/// </para>
/// <para>
/// The check reads the project file rather than the built assembly on purpose: a reference that
/// is declared but not yet used by any type would not appear in the assembly's reference list,
/// so an assembly-only check can pass while the edge that matters is silently absent or present.
/// </para>
/// </remarks>
public class BatchReferenceDirectionTests
{
    [Fact]
    public void AsrProjectReferencesTheRecordingArtifactContract()
    {
        var references = ProjectReferences("MeetCap.Asr");

        Assert.Contains(
            references,
            reference => reference.EndsWith(
                Path.Combine("MeetCap.AudioPipeline", "MeetCap.AudioPipeline.csproj"),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AudioPipelineProjectDoesNotReferenceTheAsrStack()
    {
        // The other half of the rule lives in CaptureIndependenceTests against the built
        // assembly; this asserts the declaration, so a reference added without any type using it
        // yet is still caught here.
        var references = ProjectReferences("MeetCap.AudioPipeline");

        Assert.DoesNotContain(
            references,
            reference => reference.Contains("MeetCap.Asr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            references,
            reference => reference.Contains("Volcengine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AsrAssemblyConsumesTheRecordingArtifactContract()
    {
        // The dependency is real, not only declared: the live path is built on the Core
        // closed-chunk contract, and the batch builder materializes its window through the
        // recording layer's own WAV writer, so the audio it produces is exactly as validated as
        // a capture chunk.
        var assembly = typeof(AsrBatchBuilder).Assembly;
        var referenced = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("MeetCap.AudioPipeline", referenced);
        Assert.Contains("MeetCap.Core", referenced);
    }

    private static IReadOnlyList<string> ProjectReferences(string projectName)
    {
        var projectFile = Path.Combine(RepositoryRoot(), "src", projectName, projectName + ".csproj");
        Assert.True(File.Exists(projectFile), $"expected {projectFile} to exist");

        return File.ReadAllLines(projectFile)
            .Where(line => line.Contains("ProjectReference", StringComparison.Ordinal))
            .Where(line => line.Contains("Include=", StringComparison.Ordinal))
            .Select(line =>
            {
                var start = line.IndexOf("Include=\"", StringComparison.Ordinal);
                if (start < 0)
                {
                    return string.Empty;
                }

                start += "Include=\"".Length;
                var end = line.IndexOf('"', start);
                return end < 0 ? string.Empty : line[start..end].Replace('\\', Path.DirectorySeparatorChar);
            })
            .Where(reference => reference.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Walks up from the test output directory to the repository root, identified by the
    /// solution file, so the check works from any build configuration.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MeetCap.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (MeetCap.slnx) above {AppContext.BaseDirectory}.");
    }
}
