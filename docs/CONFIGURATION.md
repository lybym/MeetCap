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
```

Future process-specific loopback may add `process_name` without creating a new meeting mode.

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

## 8. Volcengine

```toml
[asr.volcengine]
app_id = "your-app-id"
credential = "env:MEETCAP_VOLCENGINE_ACCESS_TOKEN"
resource_id = "volc.bigasr.auc"
hotword_table_id = ""
```

Credential references may include `env:` and `credman:` schemes.

## 9. Speaker

```toml
[speakers]
enabled = true
provider = "local"
owner_name = ""
auto_suggest = true
match_threshold = 0.82
match_margin = 0.08
manual_assignment_locked = true
```

Initial thresholds are placeholders until calibrated on real recordings.

## 10. Transcript

```toml
[transcript]
write_jsonl = true
write_markdown = true
live_markdown = true
include_source = true
include_timestamps = true
```

Raw normalized JSONL remains internally mandatory.

## 11. Storage

```toml
[storage]
data_root = "%LOCALAPPDATA%\\MeetCap"
minimum_free_space_gb = 5
```

## 12. Reload behavior

A running session uses a configuration snapshot captured at session start. Editing the file affects the next session only.

## 13. Config migration

The file contains `config_version = 1`. Breaking changes require migration or an actionable validation error.
