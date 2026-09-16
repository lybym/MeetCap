using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// The M2 exit criterion that network, ASR and speaker modules can all be disabled with no
/// effect on recording (<c>docs/ROADMAP.md</c> M2, <c>docs/RELIABILITY.md</c> section 1).
/// </summary>
/// <remarks>
/// The behaviour is already proven: every recording test in this assembly runs the real
/// capture pipeline with no credentials, no network and no provider. This check pins the
/// structure that makes it true — the recording assembly must not be able to reach the
/// cloud, ASR, or speaker stack at all, so a future refactor cannot quietly reverse the
/// dependency direction documented in <c>docs/ARCHITECTURE.md</c> section 1.
/// </remarks>
public class CaptureIndependenceTests
{
    private static readonly string[] ForbiddenTokens =
    {
        "Asr",
        "Speaker",
        "Volcengine",
        "Http",
        "Polly",
        "Sherpa",
    };

    [Fact]
    public void AudioPipelineAssembly_ReferencesNoCloudAsrOrSpeakerAssembly()
    {
        var pipeline = typeof(ChunkSpool).Assembly;

        var violations = pipeline
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(name => ForbiddenTokens.Any(
                token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "MeetCap.AudioPipeline must not reach the cloud/ASR/speaker stack, but references: " +
            string.Join(", ", violations));
    }

    [Fact]
    public void RecoveryAndCaptureTypes_ExposeNoCloudOrProviderMember()
    {
        // The same rule from the public-API direction: no type the recording path exposes
        // may name a provider, a cloud client, or a network type.
        var pipeline = typeof(ChunkSpool).Assembly;
        var violations = new List<string>();

        foreach (var type in pipeline.GetExportedTypes())
        {
            foreach (var candidate in TypesOf(type))
            {
                var name = candidate.FullName ?? candidate.Name;
                if (ForbiddenTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{type.FullName} -> {name}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "MeetCap.AudioPipeline's public API must not expose provider or cloud types, but exposes: " +
            string.Join("; ", violations.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)));
    }

    [Fact]
    public void SessionArtifacts_AreOwnedByMeetCapAndNotByAFramework()
    {
        // docs/ARCHITECTURE.md section 23: the durable spool, crash recovery and artifact
        // contract are MeetCap-owned. Asserting the types exist keeps the semantics from
        // being delegated to a background-job framework.
        var pipeline = typeof(ChunkSpool).Assembly;
        string[] owned =
        {
            "MeetCap.AudioPipeline.ChunkSpool",
            "MeetCap.AudioPipeline.SessionRecoveryScanner",
            "MeetCap.AudioPipeline.SessionGapAuditor",
            "MeetCap.AudioPipeline.RecordingSession",
            "MeetCap.Core.Capture.CaptureTimeline",
            "MeetCap.Core.Capture.CaptureBacklogMonitor",
            "MeetCap.Core.Capture.AudioBufferHealth",
            "MeetCap.Core.Sessions.SessionAudit",
        };

        foreach (var name in owned)
        {
            Assert.NotNull(pipeline.GetType(name) ?? typeof(SessionStatus).Assembly.GetType(name));
        }
    }

    private static IEnumerable<Type> TypesOf(Type type)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly;

        foreach (var member in type.GetMembers(flags))
        {
            switch (member)
            {
                case System.Reflection.PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case System.Reflection.FieldInfo field:
                    yield return field.FieldType;
                    break;
                case System.Reflection.MethodInfo method:
                    yield return method.ReturnType;
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }

                    break;
                case System.Reflection.ConstructorInfo constructor:
                    foreach (var parameter in constructor.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }

                    break;
            }
        }
    }
}
