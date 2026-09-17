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

Implemented (M4):

```text
RecordingSession.ChunkClosed            one durably closed chunk, announced off the capture callback
  -> AsrBatchBuilder                    groups per source until the window covers file_batch_seconds
  -> asr/batches/<source>/batch-N.wav   real WAV, validated and renamed out of .part before any job exists
  -> asr/batches/<source>/batch-N.json  the batch/source timeline mapping
  -> asr_jobs row                       pending, provider_request_id allocated up front
  -> LiveTranscription                  background drain + stop-time flush and drain
  -> transcript/raw.jsonl + live.md     rebuilt from each job's normalized.jsonl
```

Three properties are worth stating because they are the difference between "works on a good
network" and "works in a meeting":

- nothing on this path runs on the capture callback. Batching happens on the recording consumer
  thread, submission and polling on the drain's own task, which is what
  `docs/RELIABILITY.md` section 1 requires;
- a provider batch's timestamps are moved onto the session timeline by the batch's own start
  position, so a two-hour meeting's transcript is ordered the way it was spoken
  (`docs/DATA_MODEL.md` section 6.1);
- a failed submission is never an unhandled exception. The adapter classifies its own transport
  failures, so the job lands in the durable `retry_wait` state and the recording is untouched.

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

Implemented division of responsibility (M3):

```text
Polly (inside MeetCap.Asr.Volcengine)
  bounded in-process retry with exponential backoff + jitter
  per-request timeout
  one provider request id across attempts, so a retry addresses the same task

asr_jobs (MeetCap-owned SQLite state)
  attempt_count, next_retry_at, status, provider_request_id, error_code/error_message
  resume across process restart via `meetcap asr resume`
```

A job left in `submitting` by a killed process is treated as accepted and resumed by polling,
because the provider request id was persisted before the request was sent. Re-submitting would
risk paying twice for the same audio. If the provider reports the task as unknown, the job
falls back to `retry_wait` and is submitted again.

M4 additions, stated precisely:

- the durable schedule is what paces retries, not the drain loop. While a recording runs, the
  queue is drained every 500 ms, but a job in `retry_wait` is only eligible once its own
  `next_retry_at` has passed, so backoff survives a network outage exactly as it does a restart;
- `meetcap asr resume --force` processes retry-wait jobs whose schedule has not come due yet.
  That is for the operator who knows the outage is over, and it is the only way the schedule is
  bypassed;
- `VolcengineAsrProvider` wraps the transport exceptions its Polly pipeline rethrows
  (`HttpRequestException`, `TimeoutRejectedException`, a cancelled request) in
  `AsrTransientException`, so a lost network reaches the durable state machine as a retryable
  failure rather than escaping as an unhandled exception;
- one drain advances a single job through `submitted` to its result. Stopping at a bare
  `submitted` would leave audio that the provider accepted but nobody collected.

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

Implemented (M3):

```text
inspect  -> FFprobe via FFMpegCore (MeetCap.AudioPipeline.FFmpegMediaPipeline)
plan     -> MeetCap.Core.Media.MediaNormalizationPlanner (pure policy, unit tested)
normalize only when the source is not already 16 kHz mono 16-bit PCM WAV
target   -> wav / pcm_s16le / 16000 Hz / 1 channel
verify   -> the normalized artifact is re-inspected before it is submitted
```

The source file is copied into `audio/import/` and never modified in place; the original and
normalized artifacts are both recorded in `session.json`. A source that is already in the
target shape is submitted unchanged, so MeetCap never re-encodes audio it does not have to.

The current implementation submits one provider file request per import and does not split
oversized inputs: exceeding the provider's single-request or inline-upload limit fails with an
actionable message. Split mapping with preserved original timestamps remains a later concern.

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

Implemented columns in `asr_jobs` (M3): `provider`, `tier`, `source`, `duration_ms`,
`submitted_at`, `completed_at`, `attempt_count`, `provider_request_id`, `error_code`,
`error_message`, `speaker_info_requested`, `speaker_info_returned`, `estimated_cost_cny`,
`raw_response_path`, `normalized_result_path`, `request_metadata_path`. A terminal job also
emits an `asr.job.completed` or `asr.job.failed` record in `events.jsonl`.

M4 adds the queue's own state to the report (`docs/ROADMAP.md` M4). `meetcap status` prints:

```text
asr: file ASR, batch window 300s
asr queue: 2 outstanding (1 pending, 0 in flight, 1 awaiting retry), 14 succeeded, 0 failed across 1 session(s)
asr state: behind (transcription is queued or failed; recording and audio artifacts are unaffected)
```

The per-status counts come from the same `asr_jobs` table the queue uses, so the report cannot
disagree with the queue. A backlog is described, never treated as a recording failure: a lost
network legitimately leaves jobs `pending` or `retry_wait` while the audio stays safe
(`docs/RELIABILITY.md` section 9). `meetcap start` reports the same picture at stop as
`asr batches: N queued, N job(s) advanced, N awaiting retry, N still running`, plus the exact
`meetcap asr resume` command when work remains.

Never log credentials.
