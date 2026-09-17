using MeetCap.Core.Configuration;
using System.Reflection;
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

    /// <summary>
    /// M3 added the ASR, media, and transcript abstractions to Core. They are the
    /// contracts the new infrastructure projects implement, so the boundary is asserted
    /// from the other direction too: not only must Core avoid *referencing*
    /// FFMpegCore/Polly/Volcengine/SQLite assemblies, none of their types may appear in
    /// Core's public API surface, or a provider type would have leaked into the domain
    /// contract.
    /// </summary>
    [Fact]
    public void CorePublicApiSurface_ExposesNoInfrastructureTypes()
    {
        var assembly = typeof(MeetCapConfiguration).Assembly;
        var violations = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var candidate in Flatten(type))
            {
                RecordIfForbidden(candidate, $"{type.FullName} (declaration)", violations);
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(flags))
            {
                foreach (var candidate in TypesOf(member))
                {
                    foreach (var flattened in Flatten(candidate))
                    {
                        RecordIfForbidden(flattened, $"{type.FullName}.{member.Name}", violations);
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "MeetCap.Core's public API must expose no infrastructure types, but exposes: " +
            string.Join(
                "; ",
                violations.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The contracts M3 and M6 add are the ones infrastructure must implement. Asserting
    /// they exist keeps a future refactor from quietly moving a contract into an adapter.
    /// </summary>
    [Fact]
    public void CorePublicContracts_AreOwnedByCore()
    {
        var assembly = typeof(MeetCapConfiguration).Assembly;
        string[] expected =
        {
            "MeetCap.Core.Asr.IAsrProvider",
            "MeetCap.Core.Asr.IAsrJobStore",
            "MeetCap.Core.Asr.IAsrResponseNormalizer",
            "MeetCap.Core.Media.IMediaPipeline",
            "MeetCap.Core.Sessions.ISessionStore",
            "MeetCap.Core.Sessions.ISessionArtifactWriter",
            "MeetCap.Core.Transcripts.ITranscriptStore",
            "MeetCap.Core.Transcripts.TranscriptSegment",
            // M6: speaker identity and registry contracts (docs/ARCHITECTURE.md section 17).
            "MeetCap.Core.Speakers.ISpeakerIdentityProvider",
            "MeetCap.Core.Speakers.ISpeakerStore",
            "MeetCap.Core.Speakers.Speaker",
            "MeetCap.Core.Speakers.SpeakerEmbedding",
            "MeetCap.Core.Speakers.SpeakerCandidate",
            "MeetCap.Core.Speakers.SpeakerAssignment",
            "MeetCap.Core.Speakers.SpeakerMatchingPolicy",
        };

        foreach (var name in expected)
        {
            Assert.NotNull(assembly.GetType(name));
        }
    }

    private static void RecordIfForbidden(Type type, string owner, List<string> violations)
    {
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        if (IsForbidden(assemblyName))
        {
            violations.Add($"{owner} -> {type.FullName} [{assemblyName}]");
        }
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo property:
                yield return property.PropertyType;
                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case MethodInfo method:
                yield return method.ReturnType;
                foreach (var parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }

                break;
            case ConstructorInfo constructor:
                foreach (var parameter in constructor.GetParameters())
                {
                    yield return parameter.ParameterType;
                }

                break;
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in Flatten(argument))
                {
                    yield return nested;
                }
            }
        }

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in Flatten(element))
            {
                yield return nested;
            }
        }
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
