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

## 2. Fixed provider contract

MeetCap deliberately supports one Volcengine recording-file ASR contract:

```text
provider:       Volcengine / Doubao Voice
model:          Seed-ASR 2.0
mode:           recording-file Standard
transport:      HTTPS
lifecycle:      submit -> query
authentication: X-Api-Key only (new console)
resource id:    volc.seedasr.auc
submit:         POST https://openspeech.bytedance.com/api/v3/auc/bigmodel/submit
query:          POST https://openspeech.bytedance.com/api/v3/auc/bigmodel/query
audio transport: <= 20 MiB audio.data (Base64); > 20 MiB TOS presigned audio.url (#29 target)
```

The resource ID and endpoint family are provider protocol constants, not user-tunable settings.
MeetCap does not support the legacy `X-Api-App-Key + X-Api-Access-Key` scheme, recording-file
1.0 (`volc.bigasr.auc`), idle routing, or flash/turbo routing in this product contract.

The persistent provider request ID is a UUID allocated before submission and reused throughout
the submit/query lifecycle. Header presence and sequence semantics MUST match the official
Standard HTTP interface exactly. MeetCap also retains the provider's `X-Tt-Logid` for
diagnostics while never persisting the API key.

MeetCap keeps its local-first workflow. Under issue #29, normalized WAV artifacts at or below
20 MiB stay on the official `audio.data` Base64 path; larger inputs use a private Volcengine TOS
object uploaded with the official TOS .NET SDK, then submit an SDK-generated presigned GET URL
through `audio.url`. TOS is therefore a large-file transport fallback, not a prerequisite for
ordinary 300-second live batches and never a replacement for durable local audio.

Authoritative interface reference:

- https://docs.volcengine.com/docs/DoubaoVoice/task-submission-http-1?lang=zh

Related official references:

- https://www.volcengine.com/docs/6561/1354871?lang=zh
- https://www.volcengine.com/docs/6561/1354868?lang=zh
- https://docs.volcengine.com/docs/TorchObjectStorage/SDKOverview-6?lang=zh
- https://www.volcengine.com/docs/6349/1130432?lang=zh
- https://www.volcengine.com/docs/6349/1130151?lang=en

Implementation alignment landed with issue #26: the adapter sends only `X-Api-Key`, fixes
`X-Api-Resource-Id` to `volc.seedasr.auc`, speaks the submit/query endpoints above with no
tier routing, and retains the provider's `X-Tt-Logid` on the job row.
Large-file TOS transport remains tracked by issue #29; until it lands, the implementation on
`main` remains inline-Base64-only for file transport.

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

## 5. Fixed ASR service profile

MeetCap exposes no recording-file service-tier selector.

The supported profile is:

```text
Seed-ASR 2.0
recording-file Standard HTTP
X-Api-Key
volc.seedasr.auc
```

Idle, flash/turbo, recording-file 1.0, and legacy-console authentication are not compatibility
modes. A configuration or CLI surface MUST NOT silently route a Standard job to those services.

Streaming ASR remains a separately deferred product capability and is never a fallback for a
failed file-ASR job.

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
Volcengine Seed-ASR 2.0
  -> timestamps
  -> speaker_0 / speaker_1 / ...
```

MeetCap requests speaker information when supported by the Seed-ASR 2.0 Standard HTTP API and preserves it in normalized segments.

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

The current implementation submits one provider file request per import and remains
inline-Base64-only. Issue #29 changes the transport, not the one-request preference: inputs at or
below 20 MiB use `audio.data`; larger inputs use a private TOS object and presigned `audio.url`.
Automatic splitting remains a later concern, and provider duration/size limits still fail with an
actionable message rather than being silently bypassed.

### 12.1 Large-file transport target (issue #29)

The target transport decision is deterministic and intentionally not user-tunable:

```text
<= 20 MiB normalized WAV
  -> inline publisher
  -> audio.data Base64

> 20 MiB normalized WAV
  -> official Volcengine TOS .NET SDK PutObject(FileStream)
  -> private object under meetcap-asr/<random-shard>/...
  -> SDK-generated presigned GET URL
  -> audio.url
```

The TOS adapter lives outside `VolcengineAsrProvider`; the provider owns only the Seed-ASR
wire contract. Presigned URLs are ephemeral and MUST NOT be durable job identity. Recovery
persists stable bucket/object-key state and regenerates a URL when required.

The first implementation uses ordinary SDK upload, not multipart upload: the TOS .NET SDK
supports stream upload and its simple-upload ceiling is well above the Seed-ASR file range.
TOS credentials use the existing secret resolver and never enter request artifacts or logs.

After a TOS-backed job reaches a terminal state, MeetCap attempts idempotent `DeleteObject`
cleanup. Cleanup failure does not invalidate a successful transcript. Deployments should also
configure a three-day TOS lifecycle expiration on the dedicated `meetcap-asr/` prefix as an
orphan-cleanup safety net; MeetCap does not mutate bucket lifecycle policy itself.

## 13. Observability

For every ASR job record:

- provider;
- source track;
- duration;
- submit/complete time;
- retries;
- provider request ID;
- provider log ID (`X-Tt-Logid`);
- audio transport (`inline` or `tos`) and, for TOS, stable bucket/object-key identity only;
- never the TOS presigned URL, its signature/query parameters, or TOS credentials;
- success/error code;
- whether speaker info was requested/returned;
- estimated cost;
- raw response path.

The M3 schema contains `provider`, the schema-compatibility `tier`, `source`, `duration_ms`,
`submitted_at`, `completed_at`, `attempt_count`, `provider_request_id`, `error_code`,
`error_message`, `speaker_info_requested`, `speaker_info_returned`, `estimated_cost_cny`,
`provider_log_id`, `raw_response_path`, `normalized_result_path`, `request_metadata_path`. A
terminal job also emits an `asr.job.completed` or `asr.job.failed` record in `events.jsonl`, and
`asr.job.submitted` / `asr.job.completed` carry the provider log id.

Issue #26 is implemented: `tier` is no longer a product selector (new jobs record the single
fixed value `standard`), no request carries the legacy `X-Api-App-Key`/`X-Api-Access-Key`
headers, and `X-Tt-Logid` is persisted as `provider_log_id` — a diagnostic id, never a
credential. The API key never appears in `request.json`, `response.json`, `events.jsonl`,
`session.json`, SQLite diagnostic text, or normal logs, and `X-Tt-Logid` is also included in
provider error messages so a failed exchange can be traced from the persisted `error_message`
alone.

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
