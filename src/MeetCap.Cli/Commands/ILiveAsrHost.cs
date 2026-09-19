namespace MeetCap.Cli.Commands;

using MeetCap.Asr;
using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using MeetCap.Core.Sessions;

/// <summary>
/// The ASR stack a recording or import command needs: the persistent job queue and the
/// processor that drives it.
/// </summary>
/// <remarks>
/// <para>
/// Commands depend on this instead of on the concrete composition class so the provider
/// boundary can be replaced in a test without reaching the real service. The interface
/// deliberately exposes no provider type: <c>docs/DEVELOPMENT.md</c> section 4 keeps
/// Volcengine concepts inside the adapter, and the CLI only ever sees MeetCap-owned
/// contracts.
/// </para>
/// <para>
/// Construction is where provider, API-key, and toolchain problems are reported —
/// before any session or artifact exists, so a misconfiguration cannot leave half-written
/// session state (<c>docs/DEVELOPMENT.md</c> section 7).
/// </para>
/// </remarks>
internal interface ILiveAsrHost : IDisposable
{
    /// <summary>The persistent ASR queue.</summary>
    IAsrJobStore Jobs { get; }

    /// <summary>Session index, used to advance a session's lifecycle once its queue is terminal.</summary>
    ISessionStore Sessions { get; }

    /// <summary>Durable session artifacts, used for the session's own completion event.</summary>
    ISessionArtifactWriter Artifacts { get; }

    /// <summary>Drives queued jobs to a terminal or resumable state.</summary>
    AsrJobProcessor Processor { get; }
}

/// <summary>
/// Builds the ASR stack for one invocation.
/// </summary>
/// <remarks>
/// Injected through <see cref="CliContext.TryCreateAsrHost"/> so a test can substitute the
/// provider's HTTP boundary while the command composition stays production code. The
/// production implementation is <see cref="AsrHostFactory.Create"/>.
/// </remarks>
internal delegate bool LiveAsrHostFactory(
    CliContext context,
    MeetCapConfiguration configuration,
    string dataRoot,
    out ILiveAsrHost? host,
    out int exitCode);

/// <summary>The production <see cref="LiveAsrHostFactory"/>: builds a real <see cref="AsrStack"/>.</summary>
internal static class AsrHostFactory
{
    public static bool Create(
        CliContext context,
        MeetCapConfiguration configuration,
        string dataRoot,
        out ILiveAsrHost? host,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);

        // A live recording only batches and queues; it never touches the media toolchain, so
        // a missing FFmpeg must not stop a meeting from being recorded and transcribed.
        var created = AsrStack.TryCreate(
            context,
            configuration,
            dataRoot,
            requireMedia: false,
            out var stack,
            out exitCode,
            context.AsrHttpHandler);

        host = stack;
        return created;
    }
}
