# Configuration Specification

All user-tunable persistent behavior is controlled through a configuration file.

Canonical path:

```text
%APPDATA%\MeetCap\config.toml
```

Data is stored separately under `%LOCALAPPDATA%` by default.

## 1. Rules

1. `config.toml` is the persistent source of truth.
2. There is no hidden settings database.
3. One-shot CLI flags may override a value for one command/session.
4. One-shot overrides do not rewrite `config.toml`.
5. Unknown keys should produce a warning or validation error.
6. Secrets should be referenced, not committed as plaintext when avoidable.
7. Effective configuration should be printable with secrets redacted.

Commands:

```powershell
meetcap config init
meetcap config path
meetcap config validate
meetcap config show
```

## 2. Precedence

```text
built-in defaults
  < config.toml
  < explicit one-shot CLI arguments
```

## 3. Sections

```text
[app]
[capture]
[capture.offline]
[capture.online]
[storage]
[media]
[asr]
[asr.volcengine]
[asr.tos]
[speakers]
[speakers.identity]
[speakers.sherpa_onnx]
[transcript]
[logging]
[retention]
```

## 4. Session mode

```toml
[capture]
default_mode = "offline"
```

Allowed: `offline`, `online`.

`import` is command-driven. Hybrid is invalid.

## 5. Device selection

Persist stable Windows endpoint IDs where possible.

```toml
[capture.offline]
microphone_device_id = "default"

[capture.online]
microphone_device_id = "default"
loopback_mode = "system"
render_device_id = "default"
process_name = ""
```

Allowed `loopback_mode` values:

```text
system
process
```

`process` is a capture-source option, not a separate meeting mode, and should use NAudio process-loopback support where available.

`system` is the baseline online path and requires no further input. `process_name` names the target process and is therefore required when `loopback_mode = "process"`; validation rejects `process` with an empty `process_name` rather than failing later during capture.

## 6. Durability

```toml
[capture]
chunk_seconds = 60
buffer_seconds = 5
flush_interval_ms = 1000
```

Validation must reject unsafe values.

What M1 does with them:

- `chunk_seconds` sets the chunk capacity in frames
  (`chunk_seconds × sample_rate × block_align`). A chunk is cut at exactly that boundary
  even when a device buffer straddles it, so `audio/mic/000001.wav` really is one chunk
  of audio rather than "roughly one".
- `buffer_seconds` bounds the in-memory packet queue between the capture callback and the
  disk writer. When the queue is full the callback is never blocked: the audio is
  dropped, counted, and reported as a `capture.buffer_overflow` degraded event.
- `flush_interval_ms` is how often the open chunk is flushed towards the operating
  system. It never affects durability of closed chunks, which are flushed to disk before
  they are renamed out of `.part`.

M2 added no configuration key, so this file and the configuration schema are unchanged. The
queue bound, the reported backlog depth and the stalled-consumer threshold all come from the
existing `buffer_seconds`:

- the bound remains `buffer_seconds × 100` packets with a floor of 8, and that is the capacity
  the session reports;
- `buffer_seconds` is also the wall-clock threshold after which a queue that has not drained is
  reported: once the queue has stayed populated that long without closing a chunk, the session
  is marked degraded and writes `capture.consumer_stalled`;
- `meetcap start` reports the same value on every run as
  `capture buffer: peak N/M packets, dropped N, stalled N time(s) (longest N ms)`.

There is deliberately no separate stall-timeout or backpressure key: the queue bound and the
stall threshold are one quantity, and separate keys would only let them disagree.

## 7. ASR

Contract (issue #26):

```toml
[asr]
enabled = true
strategy = "file"
file_batch_seconds = 300
retry_max_attempts = 8
retry_initial_seconds = 5
retry_max_seconds = 300
final_full_session_pass = false
```

MeetCap supports one recording-file service profile: **Seed-ASR 2.0 Standard HTTP**. There is no
`service_tier` setting and no `meetcap import --tier` override. Idle and flash/turbo are not
fallbacks or compatibility modes.

The app MUST NOT silently switch to streaming when file ASR fails.

Retry configuration defines provider-execution resilience. Persistent retry/job state remains
stored in the ASR job queue.

M4 gives these keys a live-recording meaning:

- `asr.enabled = false` is the supported way to record without a transcript. `meetcap start`
  skips the ASR stack entirely, so no provider API key is needed; the recording, chunk spool, and
  recovery are unaffected. With `enabled = true` the provider is built *before* the session
  exists, so a missing/unresolvable API key fails visibly and leaves no half-written session
  behind;
- `file_batch_seconds` is the live batch window (default 300 s). It is independent of
  `capture.chunk_seconds`: chunks are the durability unit and batches are the provider context
  unit. The window closes on captured audio time, so a batch that covers a device outage declares
  a longer span than the audio inside it and its `batch-NNNNNN.json` manifest states where each
  chunk really sits (`docs/DATA_MODEL.md` section 6.1);
- the retry keys pace the queue. A job in `retry_wait` is only eligible once its own durable
  `next_retry_at` has passed. `meetcap asr resume --force` is the explicit way to bypass that
  schedule when the outage is known to be over.

A batch window that cannot be built needs no key of its own either. An unreadable capture chunk
is handled inside the batch builder: the window is recorded as `asr.batch.failed`, the offending
chunk is dropped from it, and the rest of the window is retried, so the track keeps transcribing.
`meetcap start`'s stop summary names the unreadable chunk count and the milliseconds that were
never transcribed (`docs/ARCHITECTURE.md` section 10.2).

## 7.1 Media toolchain

```toml
[media]
ffmpeg_binary_folder = ""
ffmpeg_temporary_folder = ""
```

`ffmpeg_binary_folder` is the directory holding `ffmpeg.exe` and `ffprobe.exe`. Empty means
auto-detect, in this order: well-known install locations (`%ProgramFiles%\ffmpeg\bin`,
`%ProgramFiles(x86)%\ffmpeg\bin`, `%LOCALAPPDATA%\ffmpeg\bin`, `%ProgramData%\chocolatey\bin`),
then `PATH`.

Both values may use the `env:NAME` reference scheme, which keeps environment access inside the
configuration resolver. A non-empty value must be an absolute path; validation rejects a
relative value so a directory cannot silently resolve against the CLI's working directory.

When the toolchain cannot be located, an import fails with a message naming
`media.ffmpeg_binary_folder` and every location that was searched.

## 8. Volcengine

Contract (issue #26):

```toml
[asr.volcengine]
api_key = "env:MEETCAP_VOLCENGINE_API_KEY"
hotword_table_id = ""
request_speaker_info = true
cost_per_hour_cny = 0.8
poll_interval_seconds = 5
poll_timeout_seconds = 900
http_timeout_seconds = 30
```

Only the API key is user-supplied provider authentication. MeetCap fixes the rest of the wire
contract to the official Seed-ASR 2.0 recording-file Standard HTTP interface:

```text
authentication header: X-Api-Key
resource header:       X-Api-Resource-Id: volc.seedasr.auc
submit endpoint:        https://openspeech.bytedance.com/api/v3/auc/bigmodel/submit
query endpoint:         https://openspeech.bytedance.com/api/v3/auc/bigmodel/query
task id:                UUID persisted before submit and reused for query
audio transport:        <=20 MiB audio.data; >20 MiB TOS presigned audio.url (target #29)
model_name:             bigmodel
```

Header presence and `X-Api-Sequence` semantics on submit/query MUST match the official interface
document exactly:

https://docs.volcengine.com/docs/DoubaoVoice/task-submission-http-1?lang=zh

The provider captures `X-Api-Status-Code`, `X-Api-Message`, and `X-Tt-Logid`. The log id is
diagnostic metadata: it is stored on the job row as `provider_log_id` (and in the
`asr.job.submitted` / `asr.job.completed` records) so a provider-side incident can be traced,
and it is included in provider error messages so a failed exchange names its log id in the
persisted `error_message`. The API key is never persisted.

API-key references use the existing secret resolver. `env:` remains the recommended scheme.
`credman:` may be supported only when the resolver actually implements it; unsupported schemes
must fail with an actionable error rather than sending an empty key. Literal secrets are accepted
only where the product already permits them and must be redacted from effective-config output.

The following legacy keys were removed by issue #26. They are no longer part of the schema, so
`meetcap config validate` reports each one as a blocking error naming the migration to perform;
they are never reinterpreted under their old semantics:

```text
asr.service_tier
asr.volcengine.app_id
asr.volcengine.credential
asr.volcengine.resource_id
```

Likewise, the adapter MUST NOT send `X-Api-App-Key` or `X-Api-Access-Key`.

`user.uid` in the provider body is a non-secret MeetCap-owned identifier. The API key MUST NOT
be copied into `user.uid`, because sanitized request metadata is retained locally.

`cost_per_hour_cny` is used only to fill the per-job `estimated_cost_cny` field for local
accounting. Provider pricing is configuration, not a product guarantee.

`poll_interval_seconds` is the delay between provider result queries. `poll_timeout_seconds`
is the wall-clock budget one command invocation spends polling a single job; when it expires the
job stays persisted and `meetcap asr resume` continues it. `http_timeout_seconds` is the
per-request timeout enforced by the Polly timeout strategy inside the provider adapter.

`request_speaker_info = true` asks Seed-ASR 2.0 Standard HTTP for anonymous speaker information
when that capability is supported by the official interface.

Provider speaker labels are session-scoped anonymous labels. They are not persistent identities
and must not be treated as names.

Because these labels are the default MVP diarization source, `speakers.enabled = true` with
`request_speaker_info = false` produces a validation warning: recording still succeeds, but no
anonymous speaker clusters exist for the identity pipeline to match unless a local diarization
fallback is configured.

## 8.1 TOS large-file ASR transport (target after issue #29)

TOS is optional infrastructure for oversized file-ASR inputs. It is not required for normal
300-second live batches and does not replace the local session/audio artifact tree.

```toml
[asr.tos]
bucket = "meetcap-asr"
region = "cn-beijing"
endpoint = "https://tos-cn-beijing.volces.com"
access_key = "env:TOS_ACCESS_KEY"
secret_key = "env:TOS_SECRET_KEY"
```

Transport policy is fixed product behavior rather than additional tuning:

```text
<= 20 MiB -> Seed-ASR audio.data Base64
> 20 MiB  -> TOS .NET SDK PutObject(FileStream) -> private object -> presigned GET -> audio.url
```

The 20 MiB threshold, six-hour presigned-URL validity, object-key format, private ACL policy,
simple-vs-multipart choice, and cleanup policy are not user-configurable in this issue.

Validation rules:

- omitting `[asr.tos]` is valid while every submitted artifact remains at or below 20 MiB;
- once a larger artifact requires TOS, `bucket`, `region`, `endpoint`, `access_key`, and
  `secret_key` must all resolve before provider submission;
- `access_key` and `secret_key` use the existing secret resolver and are redacted by
  `meetcap config show`; they MUST NOT be written to session/job artifacts or normal logs;
- the bucket/object is private; MeetCap never requires public-read access;
- the deployment credential should be scoped to the configured bucket/prefix and only the
  required `tos:PutObject`, `tos:GetObject`, and `tos:DeleteObject` operations;
- MeetCap does not create buckets or mutate bucket IAM/lifecycle configuration.

TOS object keys use the dedicated `meetcap-asr/` prefix plus a randomized component before
date/job identity to avoid a purely increasing key sequence. Meeting titles and speaker names
must not be embedded in remote object keys.

The presigned GET URL is ephemeral and MUST NOT be persisted. Durable recovery stores the TOS
bucket and object key, then regenerates a URL through the official SDK if submission must be
continued after restart.

A TOS-backed terminal job attempts idempotent object deletion. A cleanup failure is observable
cleanup debt, not a transcription failure. Operators should configure a three-day lifecycle
expiration rule for the `meetcap-asr/` prefix as a safety net for orphaned objects.

Official references:

- https://docs.volcengine.com/docs/TorchObjectStorage/SDKOverview-6?lang=zh
- https://www.volcengine.com/docs/6349/1130432?lang=zh
- https://www.volcengine.com/docs/6349/1130151?lang=en
- https://www.volcengine.com/docs/6349/1130756?lang=zh
- https://www.volcengine.com/docs/6349/1167743?lang=zh

Until issue #29 lands, `main` remains inline-Base64-only; this section defines the target
configuration contract and must not be read as an implementation-status claim.

## 9. Speaker architecture

Speaker processing has two separate responsibilities:

```text
diarization     -> who spoke when? -> speaker_0 / speaker_1 / ...
identification  -> who is speaker_1? -> Alice / Bob / Unknown
```

For the MVP:

```text
Diarization default:
Volcengine Seed-ASR 2.0 anonymous speaker labels

Identity default:
sherpa-onnx + 3D-Speaker ERes2Net-base
```

### 9.1 General speaker policy

```toml
[speakers]
enabled = true
owner_name = ""
auto_suggest = true
manual_assignment_locked = true
```

Manual assignment is authoritative.

### 9.2 Identity matching

```toml
[speakers.identity]
provider = "sherpa_onnx_3dspeaker"
match_threshold = 0.82
match_margin = 0.08
sample_min_seconds = 5
sample_max_seconds = 15
```

Initial thresholds are placeholders until calibrated on real Chinese meeting recordings.

Validation rules:

- `provider` must be a supported identity provider (`sherpa_onnx_3dspeaker`).
- `match_threshold` and `match_margin` must be within `[0, 1]`.
- `sample_min_seconds` must be greater than `0`.
- `sample_max_seconds` must be greater than or equal to `sample_min_seconds`.

Low-confidence matches remain unknown.

### 9.3 sherpa-onnx + 3D-Speaker

```toml
[speakers.sherpa_onnx]
model = "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"
model_path = ""
```

The default identity implementation uses sherpa-onnx to run the 3D-Speaker ERes2Net-base embedding model locally.

`model_path` is empty by default, which means the model is resolved from the configured data root.

The default Windows runtime should not require Python or PyTorch.

A future local diarization fallback may also use sherpa-onnx, but this is not required for the MVP.

### 9.4 Implementation status

The speaker sections above are part of the configuration contract from M0 onward: the keys are declared, defaulted, validated, and shipped in `config.example.toml`.

Enrollment, embedding extraction, similarity search, and manual assignment are implemented in M6. M0 does not load a speaker model, and an unconfigured `model_path` is not an error until M6 resolves it. Speaker configuration can never fail recording.

## 10. Transcript

```toml
[transcript]
write_jsonl = true
write_markdown = true
live_markdown = true
include_source = true
include_timestamps = true
include_speaker_labels = true
```

Raw normalized JSONL remains internally mandatory.

Anonymous `speaker_label` and persistent `speaker_id` / `speaker_name` remain separate fields.

## 11. Storage

```toml
[storage]
data_root = "%LOCALAPPDATA%\\MeetCap"
minimum_free_space_gb = 5
```

Speaker embeddings and voiceprint/name mappings are stored locally by default and should be treated as sensitive identity-related data.

`minimum_free_space_gb` is enforced twice, as `docs/RELIABILITY.md` section 10 requires:

- **before start**: free space below the threshold fails `meetcap start` with a non-zero
  exit code, before any session directory is created;
- **while recording**: dropping below it writes a rate-limited `storage.low_disk_space`
  event and recording continues.

Independently of the configured value there is a 256 MB hard floor. Free space below it
writes `storage.disk_exhausted`, stops the session, and leaves every already closed chunk
readable. Older recordings are never deleted automatically
(`retention.automatic_delete` stays `false` by default).

## 12. Reload behavior

A running session uses a configuration snapshot captured at session start. Editing the file affects the next session only.

The snapshot is persisted (it is the session's `config_snapshot`), so it is captured with the
secret-bearing values already redacted, exactly as rule 7 requires of effective-config output:
a literal `asr.volcengine.api_key` is stored as `***`, never verbatim. Redaction replaces the
value *before* the snapshot is serialized rather than editing the serialized text afterwards,
because the JSON writer escapes `+`, `"`, `\` and every non-ASCII character: a key containing
any of them appears in the finished document only in escaped form, where a replacement over the
text would match nothing and persist the credential anyway.

## 13. Config migration

The file contains `config_version = 1`. Breaking changes require migration or an actionable validation error.

Issue #26 was such a breaking provider-config change. It keeps `config_version = 1` and
implements the rejection branch of this rule: `service_tier`, `app_id`, `credential`, and
`resource_id` are no longer schema keys, so `meetcap config validate` (and every command that
loads configuration) fails with a concrete migration instruction instead of silently applying
new defaults. A new database migration (`0005_asr_job_provider_log_id`) adds the
`provider_log_id` column; existing `asr_jobs` rows are copied across unchanged.

The pre-speaker-identity flat keys `speakers.provider`, `speakers.match_threshold`, and
`speakers.match_margin` are rejected even when `config_version = 1`; they cannot silently
fall back to the current identity defaults. Remove `speakers.provider` and explicitly set
`[speakers.identity] provider = "sherpa_onnx_3dspeaker"` after confirming it is appropriate
for the deployment. Move the two numeric values into `[speakers.identity]` as
`match_threshold` and `match_margin`, respectively.
