namespace MeetCap.Asr;

using MeetCap.Core.Asr;
using MeetCap.Core.Media;
using MeetCap.Core.Sessions;

/// <summary>Prepares a derived live ASR copy without changing native capture artifacts or job identity.</summary>
public sealed class NormalizingAsrProvider(IAsrProvider inner, Func<IMediaPipeline> mediaFactory, string dataRoot) : IAsrProvider
{
    public string Name => inner.Name;

    public async Task<AsrSubmission> SubmitFileAsync(AsrFileRequest request, CancellationToken cancellationToken = default)
    {
        // Import already owns normalization. Polling/release never need the media toolchain.
        if (request.Source is "mic" or "loopback")
        {
            try
            {
                var media = mediaFactory();
                var sourceInfo = media.Inspect(request.InputArtifactPath);
                var plan = MediaNormalizationPlanner.Plan(sourceInfo);
                if (plan.Required)
                {
                    var output = Path.Combine(new SessionArtifactPaths(dataRoot, request.SessionId)
                        .JobDirectory(request.JobId), "normalized.wav");
                    var part = output + ".part";
                    await media.NormalizeAsync(request.InputArtifactPath, part, plan, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var normalized = media.Inspect(part);
                    if (MediaNormalizationPlanner.Plan(normalized).Required ||
                        Math.Abs(normalized.DurationMs - sourceInfo.DurationMs) > 100)
                    {
                        throw new MediaProbeException("Normalized live ASR audio has an unexpected format or duration.");
                    }

                    File.Move(part, output, overwrite: true);
                    request = request with { InputArtifactPath = output, AudioFormat = plan.TargetProviderFormat };
                }
            }
            catch (Exception ex) when (ex is MediaToolingException or MediaProbeException or IOException or UnauthorizedAccessException)
            {
                throw new AsrTransientException("audio.normalization",
                    "Live ASR audio normalization failed; native recording is intact. " +
                    "Check media.ffmpeg_binary_folder and disk access, then run 'meetcap asr resume': " + ex.Message, ex,
                    requestNotSent: true);
            }
        }

        return await inner.SubmitFileAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<AsrPollResult> GetResultAsync(AsrSubmission submission, AsrFileRequest request,
        CancellationToken cancellationToken = default) => inner.GetResultAsync(submission, request, cancellationToken);

    public Task<AsrAudioRelease> ReleaseAudioAsync(AsrJob job, CancellationToken cancellationToken = default)
        => inner.ReleaseAudioAsync(job, cancellationToken);
}
