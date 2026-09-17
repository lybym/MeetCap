namespace MeetCap.Speakers;

using MeetCap.Core.Ids;
using MeetCap.Core.Speakers;
using MeetCap.Core.Transcripts;

/// <summary>
/// Extracts a <see cref="SpeakerAudioSample"/> for a speaker label and time range.
/// Provided by the CLI composition root, which owns the audio infrastructure; this
/// assembly has no audio dependency.
/// </summary>
public delegate Task<SpeakerAudioSample?> SpeakerSampleExtractor(
    string speakerLabel,
    long startMs,
    long endMs,
    CancellationToken cancellationToken);

/// <summary>
/// The per-session speaker attribution pipeline
/// (<c>docs/ARCHITECTURE.md</c> section 17.3, <c>docs/DATA_MODEL.md</c> section 10).
/// Resolves each anonymous provider speaker label to a persistent identity by applying
/// the priority <c>manual &gt; high-confidence voiceprint &gt; unknown</c>, then
/// produces attributed transcript segments for <c>final.jsonl</c>/<c>final.md</c>
/// without modifying <c>raw_text</c>.
/// </summary>
/// <remarks>
/// The service is resilient: if the identity provider fails (missing model, runtime
/// error), the affected labels stay unknown and the raw ASR artifacts are untouched
/// (<c>docs/ARCHITECTURE.md</c> section 20: <c>SPEAKER_PROVIDER_FAILED</c> is a
/// degraded condition, not a fatal one).
/// </remarks>
public sealed class SpeakerAttributionService
{
    private readonly ISpeakerStore _store;
    private readonly ISpeakerIdentityProvider _provider;
    private readonly SpeakerMatchPolicy _policy;
    private readonly int _sampleMinSeconds;
    private readonly int _sampleMaxSeconds;

    public SpeakerAttributionService(
        ISpeakerStore store,
        ISpeakerIdentityProvider provider,
        SpeakerMatchPolicy policy,
        int sampleMinSeconds,
        int sampleMaxSeconds)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(policy);
        _store = store;
        _provider = provider;
        _policy = policy;
        _sampleMinSeconds = sampleMinSeconds;
        _sampleMaxSeconds = sampleMaxSeconds;
    }

    /// <summary>
    /// Builds the attribution artifact and attributed segments for one session.
    /// </summary>
    /// <param name="sessionId">The session whose labels are being resolved.</param>
    /// <param name="segments">The raw normalized transcript segments.</param>
    /// <param name="sampleExtractor">
    /// A delegate that produces an audio sample for a (label, start, end) range, or
    /// null when no audio is available. Provided by the CLI, which owns audio I/O.
    /// </param>
    public async Task<SpeakerAttributionResult> BuildAsync(
        string sessionId,
        IReadOnlyList<TranscriptSegment> segments,
        SpeakerSampleExtractor sampleExtractor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(sampleExtractor);

        var labels = segments
            .Select(s => s.SpeakerLabel)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        var entries = new List<SpeakerAttributionEntry>(labels.Count);
        var resolutionByLabel = new Dictionary<string, SpeakerResolution>(StringComparer.Ordinal);
        var warnings = new List<string>();

        var enrolled = LoadEnrolledSafely(warnings);

        foreach (var label in labels)
        {
            var labelSegments = segments.Where(s => s.SpeakerLabel == label).ToList();
            var manual = _store.GetAssignment(sessionId, label);

            SpeakerResolution resolution;
            if (manual is not null && (manual.Locked || manual.Source == SpeakerAssignmentSource.Manual))
            {
                // Manual assignment always wins and is locked against automatic rematching.
                resolution = SpeakerMatchingPolicy.Resolve(label, manual, [], _policy);
            }
            else
            {
                // Try voiceprint identification. If the provider fails, the label stays unknown
                // and the raw artifacts are untouched (docs/ARCHITECTURE.md section 20).
                var candidates = await IdentifySafelyAsync(
                    label, labelSegments, sampleExtractor, enrolled, warnings, cancellationToken);

                resolution = SpeakerMatchingPolicy.Resolve(label, manual, candidates, _policy);

                // Persist the voiceprint resolution so it survives a re-run and so the
                // attribution artifact is reproducible. Only non-manual, non-locked results
                // are written here; manual assignments are written by the assign command.
                if (resolution.Source != SpeakerAssignmentSource.Unknown)
                {
                    PersistVoiceprintAssignment(sessionId, resolution);
                }
            }

            entries.Add(resolution.ToEntry());
            resolutionByLabel[label] = resolution;
        }

        // Produce attributed segments: copy each segment with the resolved speaker fields.
        // raw_text is never modified (docs/DATA_MODEL.md section 7, docs/PRD.md section 2.3).
        var attributedSegments = new List<TranscriptSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var resolution = segment.SpeakerLabel is { } label && resolutionByLabel.TryGetValue(label, out var r)
                ? r
                : null;

            attributedSegments.Add(segment with
            {
                SpeakerId = resolution?.SpeakerId,
                SpeakerName = resolution?.SpeakerName,
                SpeakerConfidence = resolution?.Confidence,
                ManualSpeakerLock = resolution?.Locked ?? false,
            });
        }

        // Keep the timeline order: final.jsonl is ordered by start_ms (docs/ARCHITECTURE.md section 16).
        attributedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        var artifact = new SpeakerAttributionArtifact
        {
            SessionId = sessionId,
            Entries = entries,
        };

        return new SpeakerAttributionResult(artifact, attributedSegments, warnings);
    }

    private IReadOnlyList<EnrolledSpeaker> LoadEnrolledSafely(List<string> warnings)
    {
        try
        {
            return _store.ListActiveSpeakers()
                .Select(s => new EnrolledSpeaker
                {
                    SpeakerId = s.Id,
                    DisplayName = s.DisplayName,
                    Embeddings = _store.ListEmbeddings(s.Id).Select(e => e.ToExtracted()).ToList(),
                })
                .Where(e => e.Embeddings.Count > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"speaker registry could not be loaded: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<SpeakerCandidate>> IdentifySafelyAsync(
        string label,
        IReadOnlyList<TranscriptSegment> labelSegments,
        SpeakerSampleExtractor sampleExtractor,
        IReadOnlyList<EnrolledSpeaker> enrolled,
        List<string> warnings,
        CancellationToken ct)
    {
        if (enrolled.Count == 0)
        {
            return [];
        }

        var ranges = CleanSampleSelector.Select(labelSegments, _sampleMinSeconds, _sampleMaxSeconds);
        if (ranges.Count == 0)
        {
            return [];
        }

        foreach (var range in ranges)
        {
            ct.ThrowIfCancellationRequested();

            SpeakerAudioSample? sample;
            try
            {
                sample = await sampleExtractor(label, range.StartMs, range.EndMs, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"audio sample extraction failed for {label} at {range.StartMs}-{range.EndMs}ms: {ex.Message}");
                continue;
            }

            if (sample is null)
            {
                continue;
            }

            try
            {
                var probe = await _provider.ExtractAsync(sample, ct).ConfigureAwait(false);
                return await _provider.IdentifyAsync(probe, enrolled, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"speaker identity provider failed for {label}: {ex.Message}");
                return [];
            }
        }

        return [];
    }

    private void PersistVoiceprintAssignment(string sessionId, SpeakerResolution resolution)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            _store.UpsertAssignment(new SpeakerAssignment
            {
                Id = Ids.NewSpeakerAssignmentId(),
                SessionId = sessionId,
                SpeakerLabel = resolution.SpeakerLabel,
                SpeakerId = resolution.SpeakerId,
                SpeakerName = resolution.SpeakerName,
                Confidence = resolution.Confidence,
                Source = SpeakerAssignmentSource.Voiceprint,
                Locked = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Persisting the assignment is best-effort; the attribution artifact is
            // still returned. A store failure does not compromise the transcript.
        }
    }
}

/// <summary>
/// The attribution pipeline's output: the artifact, the attributed segments, and any
/// non-fatal warnings (e.g. provider unavailable, audio extraction failed).
/// </summary>
public sealed record SpeakerAttributionResult(
    SpeakerAttributionArtifact Artifact,
    IReadOnlyList<TranscriptSegment> AttributedSegments,
    IReadOnlyList<string> Warnings);
