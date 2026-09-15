# Data Model and Artifact Contract

The filesystem is a first-class persistence layer. SQLite indexes state; it does not replace durable session artifacts.

## 1. Session

Conceptual fields:

```text
id
title
mode               offline | online | import
source_type        live | import
status
started_at
stopped_at
duration_ms
config_snapshot
created_at
updated_at
```

## 2. Session directory

```text
sessions/<session-id>/
  session.json
  events.jsonl

  audio/
    mic/
      000001.wav
      000002.wav
    loopback/
      000001.wav

  asr/
    batches/
    jobs/

  transcript/
    raw.jsonl
    live.md
    final.jsonl
    final.md

  speakers/
    attribution.json

  logs/
    session.log
```

For offline mode, `audio/loopback/` is absent.

## 3. `session.json`

```json
{
  "session_id": "ses_01...",
  "title": "Weekly Meeting",
  "mode": "online",
  "source_type": "live",
  "started_at": "2026-09-15T14:00:00+08:00",
  "stopped_at": null,
  "config_version": 1,
  "tracks": ["mic", "loopback"]
}
```

## 4. `events.jsonl`

Append-only operational events.

```json
{"event":"session.started","at_ms":0}
{"event":"audio.chunk.closed","source":"mic","chunk":"000001.wav","start_ms":0,"end_ms":60000}
{"event":"asr.job.queued","job_id":"job_01","source":"mic","start_ms":0,"end_ms":300000}
{"event":"capture.device_lost","source":"loopback","at_ms":934522}
```

## 5. Audio chunk record

SQLite should index:

```text
id
session_id
source
sequence
path
start_ms
end_ms
sample_rate
channels
sample_format
byte_length
status
sha256 (optional after close)
created_at
closed_at
```

Status: `open`, `closed`, `recovered`, `corrupt`, `missing`.

## 6. ASR job

Fields:

```text
id
session_id
source
tier
provider
start_ms
end_ms
input_artifact
status
provider_request_id
attempt_count
next_retry_at
raw_response_path
normalized_result_path
error_code
error_message
created_at
updated_at
```

## 7. Transcript segment

```json
{
  "segment_id": "seg_01...",
  "session_id": "ses_01...",
  "source": "loopback",
  "start_ms": 12340,
  "end_ms": 16420,
  "raw_text": "The quotation needs another review.",
  "speaker_label": "speaker_1",
  "speaker_id": null,
  "speaker_name": null,
  "speaker_confidence": null,
  "manual_speaker_lock": false,
  "provider_job_id": "job_01"
}
```

`raw_text` is immutable.

## 8. Speaker

```text
id
display_name
aliases
active
created_at
updated_at
```

## 9. Speaker embedding

```text
id
speaker_id
model_name
model_version
dimension
embedding_blob
source_session_id
source_segment_ids
quality_score
created_at
```

Store multiple embeddings per person.

## 10. Speaker attribution

Per-session artifact:

```json
{
  "speaker_0": {
    "speaker_id": "person_01",
    "speaker_name": "Alice",
    "confidence": 0.91,
    "source": "manual",
    "locked": true
  }
}
```

Possible source: `manual`, `voiceprint`, `owner_assumption`, `unknown`.

## 11. SQLite minimum tables

```text
sessions
audio_chunks
asr_jobs
speakers
speaker_embeddings
speaker_assignments
schema_migrations
```

Do not add tables for features not yet implemented.

## 12. Raw vs final artifacts

Preserve the chain:

```text
audio
 -> raw ASR response
 -> raw normalized transcript
 -> speaker-attributed transcript
 -> optional corrected transcript
 -> optional meeting analysis
```

Earlier layers are never overwritten by AI post-processing.

## 13. JSONL as agent interface

JSONL is the stable machine-consumption format. Agents should not need to parse application logs or SQLite internals. Schema changes require an explicit version strategy.

## 14. Migrations

SQLite schema is created and evolved by numbered, embedded SQL migration scripts applied by `SqliteMigrator` (`src/MeetCap.Persistence/Storage/SqliteMigrator.cs`). Applied versions are recorded in `schema_migrations`. Re-running migrations is idempotent; already-applied migrations are skipped.

- **0001_sessions** (M0): creates `schema_migrations` and the `sessions` table (section 1) with `CHECK` constraints on `mode` (`offline`/`online`/`import`) and `source_type` (`live`/`import`), plus convenience indexes on `status` and `started_at`.
- Later milestones add `audio_chunks`, `asr_jobs`, `speakers`, `speaker_embeddings` and `speaker_assignments` as their features are implemented, each as a new numbered migration. No table is created ahead of its feature (section 11).
