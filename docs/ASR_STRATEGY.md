# ASR Strategy

## 1. Decision

MeetCap is **file-ASR first**.

Streaming ASR is not the default product path.

Reasoning:

- second-level subtitles are not required;
- file ASR is cheaper in current Volcengine public pricing;
- file recognition matches meeting-recording use cases;
- final accuracy and recording reliability matter more than immediate text;
- file-ASR jobs can be queued and retried independently of capture.

## 2. Current provider assumptions

At the time this document was written, Volcengine documentation describes recording-file ASR for meeting records, streaming ASR for real-time text, speaker diarization, timestamps, standard recording-file service, idle file ASR for batch/non-real-time work, turbo file ASR, and hotword capabilities.

Current public product pricing shows approximately:

```text
Seed-ASR 2.0 recording-file recognition: 0.8 RMB/hour
Seed-ASR 2.0 streaming recognition:      1.0 RMB/hour
```

Pricing, service capabilities, and resource IDs are provider configuration, not hard-coded product guarantees.

References:

- https://www.volcengine.com/docs/6561/1354871
- https://www.volcengine.com/docs/6561/1840838
- https://www.volcengine.com/docs/6561/1631584
- https://www.volcengine.com/docs/6561/155739

## 3. Default live-session algorithm

```text
NAudio/WASAPI capture
    |
    v
60-second durable chunks
    |
    v
closed chunks
    |
    v
5-minute ASR batch builder
    |
    v
persistent file-ASR job
    |
    v
Volcengine submit/query
    |
    +--> transcript + timestamps
    +--> anonymous speaker labels (where supported)
    |
    v
raw response
    |
    v
normalized segments
    |
    v
append transcript
```

This produces a transcript delayed by minutes, which is acceptable.

## 4. Capture chunk != ASR batch

A long ASR batch provides context and fewer requests. A short capture chunk reduces crash exposure.

Default:

```text
capture chunk:   60 s
ASR batch:      300 s
```

Both are configurable.

## 5. ASR tiers

### Standard
Default for live sessions and ordinary imports.

### Idle
Optional for historical archives, overnight imports, and cost-sensitive bulk transcription. Provider documentation allows long completion windows.

### Turbo
Optional for urgent turnaround. Not required for MVP.

### Streaming
Deferred/optional and never a dependency of recording.

## 6. Retry behavior

Transient failure:

```text
pending -> submit -> transient failure -> retry_wait -> submit
```

Permanent auth/configuration errors become `failed`.

Retry state must survive restart.

Polly SHOULD handle transient request execution concerns such as exponential backoff, jitter, timeout, and circuit-breaker behavior inside the Volcengine adapter.

The persistent SQLite ASR job state machine remains authoritative across process restarts. Polly does not replace it.

## 7. No automatic streaming fallback

Forbidden:

```text
file ASR fails -> automatically open streaming ASR
```

Correct:

```text
file ASR fails -> queue/retry
```

## 8. Final full-session pass

`final_full_session_pass = false` by default to avoid billing audio twice.

If enabled, the application may perform a final long-context file-ASR pass after stop. Earlier batched raw results remain preserved.

## 9. Online dual-track billing

Online mode submits mic and loopback separately, so billable audio can approach twice wall-clock duration. This is acceptable initially because source attribution is more important than premature VAD optimization.

## 10. Speaker diarization vs persistent identity

These are separate responsibilities.

### Anonymous diarization

Default MVP source:

```text
Volcengine BigASR
  -> timestamps
  -> speaker_0 / speaker_1 / ...
```

When supported by the configured service tier/API, MeetCap requests speaker information and preserves it in normalized segments.

Provider speaker labels are anonymous and session/provider scoped. They are not persistent identities and must never be treated as stable names.

Do not add a second default diarization engine until real recordings demonstrate the need.

### Persistent speaker identity

Identity remains local:

```text
provider anonymous speaker label
 -> collect clean utterances
 -> select useful 5-15s samples where practical
 -> sherpa-onnx
 -> 3D-Speaker ERes2Net-base embedding
 -> local Speaker Registry
 -> ranked candidates
 -> threshold/margin policy
 -> manual confirmation where needed
```

The initial reference model is:

```text
3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx
```

This path is intended for enrollment, embedding extraction, verification, and identification. It is not the default MVP diarization path.

Manual speaker assignment always wins over automatic inference.

## 11. Hotwords

Hotword configuration must be possible without code changes. Raw ASR responses remain immutable.

## 12. Import behavior

Imported media should prefer one provider file request when within provider limits. If splitting is required, preserve original timestamps and split mapping.

Media inspection/normalization should be implemented through the `MeetCap.AudioPipeline` abstraction using FFprobe/FFmpeg, preferably via FFMpegCore in the .NET implementation.

## 13. Observability

For every ASR job record:

- provider;
- tier;
- source track;
- duration;
- submit/complete time;
- retries;
- provider request ID;
- success/error code;
- whether speaker info was requested/returned;
- estimated cost;
- raw response path.

Never log credentials.
