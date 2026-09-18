namespace MeetCap.Cli.Commands;

using System.Globalization;
using MeetCap.Asr.Importing;
using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using MeetCap.Core.Media;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap import &lt;file&gt; [--title &lt;title&gt;]</c>
/// (<c>docs/ARCHITECTURE.md</c> section 21, <c>docs/ROADMAP.md</c> M3).
/// </summary>
/// <remarks>
/// Parse/output only: inspection, normalization, session creation, job queuing, and
/// transcription all live behind the domain interfaces this command composes. Every
/// failure returns a non-zero exit code with an actionable message and never prints a
/// secret (<c>docs/DEVELOPMENT.md</c> section 8).
/// </remarks>
internal static class ImportCommand
{
    public static async Task<int> RunAsync(
        CliContext context,
        string file,
        string? title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            context.Error.WriteLine("meetcap import: a recording path is required.");
            return 2;
        }

        // Report a missing source before anything else: it is the cheapest precondition and
        // the most common mistake, and it must not depend on the FFmpeg toolchain or the
        // provider being reachable.
        var sourcePath = Path.GetFullPath(file);
        if (!File.Exists(sourcePath))
        {
            context.Error.WriteLine($"meetcap import: import source not found: {sourcePath}");
            return 1;
        }

        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return failure;
        }

        if (!AsrStack.TryCreate(
                context,
                configuration,
                dataRoot,
                requireMedia: true,
                out var stack,
                out var stackFailure) || stack is null)
        {
            return stackFailure;
        }

        using var ownedStack = stack;

        var options = new ImportOptions
        {
            DataRoot = dataRoot,
            ProviderName = stack.Provider.Name,
            DefaultTitle = configuration.App.DefaultTitle,
            RequestSpeakerInfo = configuration.Asr.Volcengine.RequestSpeakerInfo,
            CostPerHourCny = configuration.Asr.Volcengine.CostPerHourCny,
            ConfigSnapshotJson = ImportSessionService.SnapshotJson(configuration),
            ConfigVersion = SchemaVersion.Current,
        };

        var service = new ImportSessionService(
            stack.Media!,
            stack.Sessions,
            stack.Jobs,
            stack.Artifacts,
            stack.Transcripts,
            stack.Processor,
            options);

        ImportResult result;
        try
        {
            result = await service.ImportAsync(
                new ImportRequest { SourcePath = sourcePath, Title = title },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MediaProbeException ex)
        {
            context.Error.WriteLine($"meetcap import: {ex.Message}");
            return 1;
        }
        catch (MediaToolingException ex)
        {
            context.Error.WriteLine($"meetcap import: {ex.Message}");
            return 1;
        }
        catch (IOException ex)
        {
            context.Error.WriteLine($"meetcap import: {ex.Message}");
            return 1;
        }

        WriteSummary(context, result);

        var logger = context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
        logger.LogInformation(
            "import: session={SessionId} job={JobId} status={Status} segments={Segments} normalized={Normalized} estimatedCostCny={Cost}",
            result.SessionId,
            result.JobId,
            AsrJobStatuses.ToWire(result.JobStatus),
            result.SegmentCount,
            result.Normalized,
            result.EstimatedCostCny);

        if (result.Succeeded)
        {
            return 0;
        }

        if (result.StillRunning)
        {
            context.Error.WriteLine(
                $"meetcap import: transcription is still running. Session state is persisted; " +
                $"run 'meetcap asr resume --session {result.SessionId}' to continue.");
            return 1;
        }

        context.Error.WriteLine(
            $"meetcap import: ASR job {AsrJobStatuses.ToWire(result.JobStatus)}" +
            (result.ErrorCode is null ? string.Empty : $" [{result.ErrorCode}]") +
            (string.IsNullOrEmpty(result.ErrorMessage) ? string.Empty : $": {result.ErrorMessage}"));
        context.Error.WriteLine(
            $"meetcap import: the imported audio is intact at '{result.SessionDirectory}'; " +
            "fix the reported problem and run 'meetcap asr resume' to retry.");
        return 1;
    }

    private static void WriteSummary(CliContext context, ImportResult result)
    {
        var media = result.SourceMedia;
        context.Out.WriteLine("Import complete.");
        context.Out.WriteLine($"  session:    {result.SessionId}");
        context.Out.WriteLine($"  directory:  {result.SessionDirectory}");
        context.Out.WriteLine(
            $"  source:     {media.Path} ({FormatDuration(media.DurationMs)}, " +
            $"{media.AudioCodec ?? "no audio"}, {media.SampleRateHz} Hz, {media.Channels} ch)");
        context.Out.WriteLine($"  normalized: {(result.Normalized ? $"yes ({result.NormalizedArtifactPath})" : "not required")}");
        context.Out.WriteLine(
            $"  asr job:    {result.JobId} {AsrJobStatuses.ToWire(result.JobStatus)} " +
            $"({result.SegmentCount} segment(s), estimated {result.EstimatedCostCny.ToString("0.######", CultureInfo.InvariantCulture)} CNY)");
        context.Out.WriteLine($"  transcript: {result.RawTranscriptPath}");
        if (result.MarkdownTranscriptPath is not null)
        {
            context.Out.WriteLine($"  markdown:   {result.MarkdownTranscriptPath}");
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        var value = Math.Max(0, milliseconds);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            value / 3_600_000,
            value / 60_000 % 60,
            value / 1000 % 60,
            value % 1000);
    }
}
