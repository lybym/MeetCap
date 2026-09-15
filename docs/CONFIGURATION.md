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
[asr]
[asr.volcengine]
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

## 7. ASR

```toml
[asr]
enabled = true
strategy = "file"
file_batch_seconds = 300
service_tier = "standard"
streaming_enabled = false
retry_max_attempts = 8
retry_initial_seconds = 5
retry_max_seconds = 300
final_full_session_pass = false
```

Allowed service tiers: `standard`, `idle`, `turbo`.

The app MUST NOT silently switch to streaming when file ASR fails.

Retry configuration defines provider-execution resilience. Persistent retry/job state remains stored in the ASR job queue.

## 8. Volcengine

```toml
[asr.volcengine]
app_id = "your-app-id"
credential = "env:MEETCAP_VOLCENGINE_ACCESS_TOKEN"
resource_id = "volc.bigasr.auc"
hotword_table_id = ""
request_speaker_info = true
```

Credential references may include `env:` and `credman:` schemes.

`request_speaker_info = true` means MeetCap asks Volcengine for anonymous speaker information when the selected API/service tier supports it.

Provider speaker labels are session-scoped anonymous labels. They are not persistent identities and must not be treated as names.

Because these labels are the default MVP diarization source, `speakers.enabled = true` with `request_speaker_info = false` produces a validation warning: recording still succeeds, but no anonymous speaker clusters exist for the identity pipeline to match, so nobody can be identified unless a local diarization fallback is configured.

## 9. Speaker architecture

Speaker processing has two separate responsibilities:

```text
diarization     -> who spoke when? -> speaker_0 / speaker_1 / ...
identification  -> who is speaker_1? -> Alice / Bob / Unknown
```

For the MVP:

```text
Diarization default:
Volcengine BigASR anonymous speaker labels

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

## 12. Reload behavior

A running session uses a configuration snapshot captured at session start. Editing the file affects the next session only.

## 13. Config migration

The file contains `config_version = 1`. Breaking changes require migration or an actionable validation error.
