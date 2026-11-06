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
            var multipleTracks = labelSegments.Select(s => s.Source)
                .Distinct(StringComparer.Ordinal).Take(2).Count() > 1;
            var multipleJobs = labelSegments.Select(s => s.ProviderJobId)
                .Distinct(StringComparer.Ordinal).Take(2).Count() > 1;
            var missingJobProvenance = labelSegments.Any(s => string.IsNullOrWhiteSpace(s.ProviderJobId));
            var ambiguous = multipleTracks || multipleJobs || missingJobProvenance;
            if (ambiguous)
            {
                var scope = missingJobProvenance ? "missing provider job provenance"
                    : multipleTracks && multipleJobs ? "multiple tracks and provider jobs"
                    : multipleTracks ? "multiple tracks" : "multiple provider jobs";
                warnings.Add($"anonymous label {label} has {scope}; automatic identity matching is skipped. " +
                    "A manual assignment applies to every segment with this label in the session.");
            }

            // The identification attempt is captured for every label so the diagnostics
            // block can record what the voiceprint path saw even when a manual lock later
            // overrides it. Ambiguous labels are never scored (issue #45 invariant), and a
            // manual-locked label is not attempted at all.
            var attempt = IdentificationAttempt.Empty;
            SpeakerResolution resolution;
            if (manual is not null && (manual.Locked || manual.Source == SpeakerAssignmentSource.Manual))
            {
                // Manual assignment always wins and is locked against automatic rematching.
                resolution = SpeakerMatchingPolicy.Resolve(label, manual, [], _policy);
            }
            else
            {
                // Anonymous labels can be reused by independent ASR jobs. One session-wide
                // identity would then name unrelated voices, so only an unambiguous label
                // can reach the voiceprint provider.
                attempt = ambiguous
                    ? IdentificationAttempt.Empty
                    : await IdentifySafelyAsync(
                        label, labelSegments, sampleExtractor, enrolled, warnings, cancellationToken);

                resolution = SpeakerMatchingPolicy.Resolve(label, manual, attempt.Candidates, _policy);

                // Replace a prior automatic binding if a later job reuses its label.
                // Manual assignments are written only by the assign command.
                if (resolution.Source != SpeakerAssignmentSource.Unknown || (ambiguous && manual is not null))
                {
                    var currentManual = PersistAutomaticAssignment(sessionId, resolution, warnings);
                    if (currentManual is not null)
                    {
                        resolution = SpeakerMatchingPolicy.Resolve(label, currentManual, [], _policy);
                    }
                }
                else
                {
                    // A manual assignment may have arrived while the provider was running,
                    // even when no automatic result was eligible for persistence.
                    var current = _store.GetAssignment(sessionId, label);
                    if (current is { Locked: true } or { Source: SpeakerAssignmentSource.Manual })
                    {
                        resolution = SpeakerMatchingPolicy.Resolve(label, current, [], _policy);
                    }
                }
            }

            var reason = ComputeResolutionReason(ambiguous, enrolled, attempt, resolution, _policy);
            var diagnostics = BuildDiagnostics(labelSegments, reason, attempt, _policy, enrolled);
            EmitPerLabelSummary(warnings, label, labelSegments, reason, attempt);

            entries.Add(resolution.ToEntry(diagnostics));
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

    private async Task<IdentificationAttempt> IdentifySafelyAsync(
        string label,
        IReadOnlyList<TranscriptSegment> labelSegments,
        SpeakerSampleExtractor sampleExtractor,
        IReadOnlyList<EnrolledSpeaker> enrolled,
        List<string> warnings,
        CancellationToken ct)
    {
        if (enrolled.Count == 0)
        {
            // No enrolled speakers: no ranges are selected and no provider is called.
            // The resolution reason distinguishes this (registry_empty) from a label that
            // had speech but no eligible clean range (no_clean_ranges).
            return IdentificationAttempt.Empty;
        }

        var ranges = CleanSampleSelector.Select(labelSegments, _sampleMinSeconds, _sampleMaxSeconds);
        if (ranges.Count == 0)
        {
            return new IdentificationAttempt([], SpeakerSampleOutcome.NoRanges, [], NoAudioCount: 0, ProviderFailed: false);
        }

        var noAudioCount = 0;
        IReadOnlyList<SpeakerCandidate>? candidates = null;

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
                // An extraction failure is an audio-infrastructure failure, not a provider
                // failure. It is folded into the single bounded per-label summary (issue #50
                // review update) rather than warned per-attempt, and counted as unavailable
                // audio so missing audio and lack of eligible speech stay distinct.
                noAudioCount++;
                continue;
            }

            if (sample is null)
            {
                noAudioCount++;
                continue;
            }

            try
            {
                var probe = await _provider.ExtractAsync(sample, ct).ConfigureAwait(false);
                candidates = await _provider.IdentifyAsync(probe, enrolled, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"speaker identity provider failed for {label}: {ex.Message}");
                return new IdentificationAttempt(
                    ranges, SpeakerSampleOutcome.ProviderFailed, [], NoAudioCount: noAudioCount, ProviderFailed: true);
            }
        }

        if (candidates is null)
        {
            // Every selected range was exhausted without a successful extraction and
            // identification: audio was unavailable (null) or unreadable (threw) for all of them.
            return new IdentificationAttempt(
                ranges, SpeakerSampleOutcome.NoAudio, [], NoAudioCount: noAudioCount, ProviderFailed: false);
        }

        return new IdentificationAttempt(
            ranges, SpeakerSampleOutcome.Extracted, candidates, NoAudioCount: noAudioCount, ProviderFailed: false);
    }

    /// <summary>
    /// Determines the explicit <see cref="SpeakerResolutionReason"/> for one label from the
    /// resolution outcome, the ambiguity flag, the registry, the identification attempt, and
    /// the policy. This mirrors the decisions <see cref="SpeakerMatchingPolicy.Resolve"/>
    /// already made, so it is read-only reasoning over the same inputs rather than a second
    /// copy of the matching logic.
    /// </summary>
    private static SpeakerResolutionReason ComputeResolutionReason(
        bool ambiguous,
        IReadOnlyList<EnrolledSpeaker> enrolled,
        IdentificationAttempt attempt,
        SpeakerResolution resolution,
        SpeakerMatchPolicy policy)
    {
        // Manual assignment always wins (whether or not the label is ambiguous).
        if (resolution.Source == SpeakerAssignmentSource.Manual)
        {
            return SpeakerResolutionReason.ManualLocked;
        }

        // A colliding label is never voiceprint-scored (issue #45 invariant).
        if (ambiguous)
        {
            return SpeakerResolutionReason.AmbiguousLabel;
        }

        if (resolution.Source == SpeakerAssignmentSource.Voiceprint)
        {
            return SpeakerResolutionReason.Matched;
        }

        // The label stayed unknown — classify why.
        if (enrolled.Count == 0)
        {
            return SpeakerResolutionReason.RegistryEmpty;
        }

        if (attempt.SampleOutcome == SpeakerSampleOutcome.NoRanges)
        {
            return SpeakerResolutionReason.NoCleanRanges;
        }

        if (attempt.SampleOutcome == SpeakerSampleOutcome.NoAudio)
        {
            return SpeakerResolutionReason.AudioUnavailable;
        }

        if (attempt.ProviderFailed)
        {
            return SpeakerResolutionReason.ProviderFailed;
        }

        // Candidates existed but did not clear the policy. The policy accepts only when the
        // best clears the threshold AND (it is the only candidate OR leads the runner-up by
        // the margin), so a non-match with candidates is either below the threshold or a
        // too-small margin.
        if (attempt.Candidates.Count > 0)
        {
            return attempt.Candidates[0].Score < policy.Threshold
                ? SpeakerResolutionReason.BelowThreshold
                : SpeakerResolutionReason.MarginTooSmall;
        }

        // Defensive fallback: an unknown resolution with no candidates and no classified
        // outcome is treated as having no clean range.
        return SpeakerResolutionReason.NoCleanRanges;
    }

    /// <summary>Builds the additive per-label diagnostics block (issue #50).</summary>
    private static SpeakerLabelDiagnostics BuildDiagnostics(
        IReadOnlyList<TranscriptSegment> labelSegments,
        SpeakerResolutionReason reason,
        IdentificationAttempt attempt,
        SpeakerMatchPolicy policy,
        IReadOnlyList<EnrolledSpeaker> enrolled)
    {
        // Provider-job provenance: the single job id when the label is unambiguous, null
        // when it spans multiple jobs or lacks provenance (issue #50 review update point 3).
        var distinctJobs = labelSegments
            .Select(s => s.ProviderJobId)
            .Where(j => !string.IsNullOrWhiteSpace(j))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var providerJob = distinctJobs.Count == 1 ? distinctJobs[0] : null;

        // Top-N candidates with similarities. Speaker names plus ranked similarities are
        // sensitive identity metadata kept in the local artifact; CLI printing is opt-in.
        const int maxCandidates = 3;
        var candidates = attempt.Candidates
            .Take(maxCandidates)
            .Select(c => new DiagnosticCandidate { SpeakerName = c.DisplayName, Similarity = c.Score })
            .ToList();

        var ranges = attempt.RangesConsidered
            .Select(r => new DiagnosticSampleRange { StartMs = r.StartMs, EndMs = r.EndMs })
            .ToList();

        return new SpeakerLabelDiagnostics
        {
            ResolutionReason = SpeakerResolutionReasons.ToWire(reason),
            RangesConsidered = ranges,
            SampleOutcome = SpeakerSampleOutcomes.ToWire(attempt.SampleOutcome),
            Candidates = candidates,
            Threshold = policy.Threshold,
            Margin = policy.Margin,
            EnrolledSpeakers = enrolled.Count,
            EnrolledEmbeddings = enrolled.Sum(e => e.Embeddings.Count),
            ProviderJob = providerJob,
        };
    }

    /// <summary>
    /// Emits one bounded per-label summary warning (issue #50 review update: one per label,
    /// not per failed sample attempt) for the non-match paths that were previously silent.
    /// Provider failures are already warned inside <see cref="IdentifySafelyAsync"/> (which
    /// holds the exception message), and ambiguous labels are already warned in
    /// <see cref="BuildAsync"/>; below-threshold and margin failures are normal non-matches
    /// surfaced only through the diagnostics artifact and the opt-in CLI summary.
    /// </summary>
    private static void EmitPerLabelSummary(
        List<string> warnings,
        string label,
        IReadOnlyList<TranscriptSegment> labelSegments,
        SpeakerResolutionReason reason,
        IdentificationAttempt attempt)
    {
        switch (reason)
        {
            case SpeakerResolutionReason.RegistryEmpty:
                warnings.Add(
                    $"speaker attribution: label {label} left unknown — speaker registry is empty");
                break;

            case SpeakerResolutionReason.NoCleanRanges:
                warnings.Add(
                    $"speaker attribution: label {label} left unknown — no eligible clean speech range found across {labelSegments.Count} segment(s)");
                break;

            case SpeakerResolutionReason.AudioUnavailable:
                {
                    var sources = labelSegments.Select(s => s.Source)
                        .Distinct(StringComparer.Ordinal).ToList();
                    var sourceText = sources.Count == 1 ? sources[0] : "mixed";
                    var firstRange = attempt.RangesConsidered.FirstOrDefault();
                    var rangeText = firstRange is not null
                        ? $" (first range {firstRange.StartMs}-{firstRange.EndMs}ms)"
                        : string.Empty;
                    warnings.Add(
                        $"speaker attribution: label {label} left unknown — audio unavailable for " +
                        $"{attempt.NoAudioCount} of {attempt.RangesConsidered.Count} sampled range(s) " +
                        $"on source '{sourceText}'{rangeText}");
                }
                break;

            // provider_failed, ambiguous_label: already warned elsewhere.
            // below_threshold, margin_too_small, manual_locked, matched: no summary warning.
        }
    }

    private SpeakerAssignment? PersistAutomaticAssignment(
        string sessionId, SpeakerResolution resolution, List<string> warnings)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var written = _store.TryUpsertAutomaticAssignment(new SpeakerAssignment
            {
                Id = Ids.NewSpeakerAssignmentId(),
                SessionId = sessionId,
                SpeakerLabel = resolution.SpeakerLabel,
                SpeakerId = resolution.SpeakerId,
                SpeakerName = resolution.SpeakerName,
                Confidence = resolution.Confidence,
                Source = resolution.Source,
                Locked = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return written ? null : _store.GetAssignment(sessionId, resolution.SpeakerLabel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Persisting the assignment is best-effort; the attribution artifact is
            // still returned. A store failure does not compromise the transcript.
            warnings.Add($"speaker assignment could not be persisted for {resolution.SpeakerLabel}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The captured result of attempting voiceprint identification for one label
    /// (issue #50). Carries the ranges the selector produced, the sample outcome, the
    /// ranked candidates (when the provider scored), how many ranges had no usable
    /// audio, and whether the provider failed — everything the diagnostics block needs.
    /// </summary>
    private sealed record IdentificationAttempt(
        IReadOnlyList<SpeakerSampleRange> RangesConsidered,
        SpeakerSampleOutcome SampleOutcome,
        IReadOnlyList<SpeakerCandidate> Candidates,
        int NoAudioCount,
        bool ProviderFailed)
    {
        /// <summary>
        /// The attempt for a label that was not scored: no ranges selected, no candidates,
        /// no audio failure, no provider failure. Used for manual-locked and ambiguous labels.
        /// </summary>
        public static IdentificationAttempt Empty { get; } = new(
            [], SpeakerSampleOutcome.NoRanges, [], NoAudioCount: 0, ProviderFailed: false);
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
