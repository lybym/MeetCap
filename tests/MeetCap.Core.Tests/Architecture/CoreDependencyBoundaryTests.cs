using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Core.Tests.Architecture;

/// <summary>
/// Guards the dependency rule in <c>docs/ARCHITECTURE.md</c> section 3 and the
/// provider-isolation rule in <c>docs/DEVELOPMENT.md</c> section 4: MeetCap.Core is
/// the domain project, so adopting the OSS stack (NAudio, Volcengine, sherpa-onnx,
/// Microsoft.Data.Sqlite, Tomlyn, FFMpegCore, Polly, System.CommandLine, Serilog)
/// must not leak any of those types into it.
/// </summary>
/// <remarks>
/// Issue #9 makes "reuse mature OSS for infrastructure, keep MeetCap-owned domain
/// semantics explicit" the architectural baseline. That baseline is only durable if
/// it is asserted: the check below fails the build as soon as infrastructure types
/// reach the domain project, before a milestone can accidentally depend on them.
/// </remarks>
public class CoreDependencyBoundaryTests
{
    private static readonly string[] s_forbiddenTokens =
    {
        "naudio",
        "volcengine",
        "sherpa",
        "3dspeaker",
        "sqlite",
        "tomlyn",
        "ffmpeg",
        "polly",
        "serilog",
        "commandline",
        "fluentmigrator",
        "onnxruntime",
    };

    [Fact]
    public void CoreAssembly_ReferencesNoInfrastructureAssemblies()
    {
        var violations = typeof(MeetCapConfiguration).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(IsForbidden)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "MeetCap.Core must not reference infrastructure assemblies, but references: " +
            string.Join(", ", violations));
    }

    [Fact]
    public void CoreProject_DeclaresNoPackageReferences()
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", "MeetCap.Core", "MeetCap.Core.csproj");
        Assert.True(File.Exists(csproj), $"expected {csproj} to exist");

        Assert.DoesNotContain(
            "PackageReference",
            File.ReadAllText(csproj),
            StringComparison.Ordinal);
    }

    private static bool IsForbidden(string assemblyName)
        => s_forbiddenTokens.Any(token => assemblyName.Contains(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Walks up from the test output directory to the repository root, identified by
    /// the solution file, so the check works from any build configuration.
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
