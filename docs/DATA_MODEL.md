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

`status` values:

```text
CREATED       row written, capture not started yet
RECORDING     capture running
FINALIZING    stop requested, artifacts being closed
PROCESSING    post-capture work outstanding (arrives with ASR, M3/M4)
COMPLETED     every required recording artifact closed after a clean stop
INTERRUPTED   terminal, but the session did not complete cleanly
```

`INTERRUPTED` is reached two ways, and both are always visible in the session's own
artifacts rather than inferred:

- startup recovery found the session was never cleanly stopped (for example the process
  was killed), so `stopped_at` stays `null` and the manifest records `recovered_at`;
- the recording process had to abandon the session, because capture never started, the
  final chunk could not be closed, or the capture device came back at a different
  format (`device_format_changed`), so `stopped_at` is set.

A terminal session is never reclassified. Recovery may repair a stray artifact found in
a `COMPLETED` session's directory, but `COMPLETED` keeps its status, its `stopped_at` and
its `duration_ms`: it *was* cleanly stopped.

A device that disappears and comes back does **not** make a session `INTERRUPTED`: the
session still completes, and the outage is recorded as a degraded flag plus explicit
events. See `docs/RELIABILITY.md` section 8.

`config_snapshot` is a JSON object holding the capture-relevant settings the session
started with (`data_root`, `chunk_seconds`, `buffer_seconds`, `flush_interval_ms`,
`minimum_free_space_gb`, `microphone_device_id`, `config_version`). It is written into a
local artifact, so it never contains secret material.

## 2. Session directory

```text
sessions/<session-id>/
  session.json
  events.jsonl
  stop.request          (present only while a stop is being requested)
  recording.lock        (held exclusively while a recording is in progress)

  audio/
    mic/
      000001.wav
      000002.wav
    loopback/
      000001.wav
    import/
      meeting.m4a
      normalized.wav

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

`stop.request` is a control marker, not an artifact: `meetcap stop` runs in a different
process from `meetcap start`, so it signals the running recorder by writing this file,
which the recording loop polls. It carries no session state and is removed when the
session ends.

`recording.lock` is a liveness marker, not an artifact: the recording process holds it
open exclusively for as long as the session owns the recording surface, and the operating
system releases the handle when that process exits for any reason. Recovery scans use it
to tell a recording that is still in progress apart from a session abandoned by a killed
process, so `meetcap status` can never rewrite a live session to `INTERRUPTED`
(`docs/ARCHITECTURE.md` section 9.1). It is deleted at the end of a clean session; an
undeletable leftover carries no meaning because it is not held.

`audio/import/` holds the materialized import source and, when normalization was required,
the normalized derivative. `transcript/raw.jsonl` is the mandatory normalized transcript;
`transcript/live.md` is its Markdown rendering. `final.jsonl` / `final.md` arrive with speaker
attribution and optional post-processing (M6+); an M3 import session stops at `raw.jsonl` +
`live.md`.

## 3. `session.json`

```json
{
  "session_id": "ses_20260915T140000Z_a1b2c3d4",
  "title": "Weekly Meeting",
  "mode": "offline",
  "source_type": "live",
  "status": "RECORDING",
  "started_at": "2026-09-15T14:00:00.0000000+00:00",
  "stopped_at": null,
  "config_version": 1,
  "tracks": ["mic"],
  "chunk_seconds": 60,
  "degraded": false,
  "end_reason": null,
  "recovered_at": null,
  "capture": [
    {
      "source": "mic",
      "device_id": "{0.0.1.00000000}.{6f2a...}",
      "device_name": "Microphone (USB Audio)",
      "sample_rate": 48000,
      "channels": 2,
      "bits_per_sample": 32,
      "sample_format": "ieee_float"
    }
  ]
}
```

`tracks` stays the documented list of captured track names. `capture` is additive: it
records which endpoint produced each track and in which native format, which is what
makes a recorded session auditable after the device list has changed. The manifest is
written atomically (write to `session.json.tmp`, then replace), so a truncated manifest
never overwrites a good one.

`degraded` is true when the session saw an audio discontinuity, a device loss, a queue
overflow or a storage problem. `end_reason` names why the session ended
(`stop_requested`, `device_lost`, `disk_exhausted`, `storage_error`,
`capture_start_failed`, `interrupted`).

Import sessions add the source artifact mapping (M3), which is what preserves provenance
without ever mutating the user's original file:

```json
{
  "source_artifacts": [
    {
      "role": "original",
      "original_path": "C:\\recordings\\meeting.m4a",
      "stored_path": "C:\\...\\sessions\\ses_01...\\audio\\import\\meeting.m4a",
      "file_name": "meeting.m4a",
      "byte_length": 12345678,
      "sha256": "..."
    }
  ]
}
```

`role` is `original` or `normalized`. The mapping lives in the durable session document
rather than a new table: section 11 forbids creating tables ahead of their feature, and
section 6 already indexes the artifact each ASR job consumed through `input_artifact`.

## 4. `events.jsonl`

Append-only operational events, one JSON object per line.

```json
{"event":"session.started","at_ms":0}
{"event":"audio.chunk.opened","at_ms":0,"source":"mic","chunk":"000001.wav","start_ms":0,"device_position_frames":0,"qpc_position_ticks":123456}
{"event":"audio.chunk.closed","source":"mic","chunk":"000001.wav","start_ms":0,"end_ms":60000}
{"event":"asr.job.queued","job_id":"job_01","source":"mic","start_ms":0,"end_ms":300000}
{"event":"capture.device_lost","source":"loopback","at_ms":934522}
```

`at_ms` is the session-relative position on the capture timeline, never a wall-clock
reading. Fields an event does not define are omitted rather than written as `null`.

The capture vocabulary written by M1 is:

```text
session.started            session.stop_requested    session.stopping
session.stopped            session.recovered

audio.chunk.opened         audio.chunk.closed
audio.chunk.recovered      audio.chunk.corrupt

capture.gap                capture.discontinuity
capture.device_lost        capture.device_restored   capture.device_lost_fatal
capture.format_changed     capture.buffer_overflow

storage.low_disk_space     storage.disk_exhausted    storage.probe_failed
```

A degraded condition always produces at least one of these events; nothing about lost
audio is inferred from a missing chunk.

## 5. Audio chunk record

SQLite indexes:

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
bits_per_sample
sample_format
byte_length
status
device_position_frames
qpc_position_ticks
sha256 (optional after close)
created_at
closed_at
```

Status: `open`, `closed`, `recovered`, `corrupt`, `missing`.

`path` is relative to the session directory (`audio/mic/000001.wav`) so a moved data
root does not invalidate the index.

`device_position_frames` and `qpc_position_ticks` are the device-timing anchors for the
chunk's first frame. They are stored so a recorded timeline can be audited against what
the device actually reported (`docs/ARCHITECTURE.md` section 8).

`sha256` stays `NULL` in M1: a chunk is validated by checking its header against its
actual length, and content hashing is deferred until a milestone needs end-to-end
verification.

`sequence` is 1-based within a track, and `(session_id, source, sequence)` is unique.

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
request_metadata_path
raw_response_path
normalized_result_path
error_code
error_message
duration_ms
speaker_info_requested
speaker_info_returned
estimated_cost_cny
submitted_at
completed_at
created_at
updated_at
```

Status values (section 12 of `ARCHITECTURE.md`): `pending`, `submitting`, `submitted`,
`polling`, `succeeded`, `retry_wait`, `failed`, `cancelled`.

Notes:

- `provider_request_id` is allocated when the job is created and persisted **before** the
  first submit. It is the provider's task identifier, so a retry — including a retry after a
  process restart — addresses the same task instead of creating a second billable one. The
  column is nullable in SQLite for migration simplicity, but the invariant is enforced on write
  (`Create` rejects a blank value) and on read (a NULL row fails loudly, naming the job); the
  domain type declares the id non-nullable, so no MeetCap code path can produce an unset one.
- `input_artifact` is stored session-relative (for example `audio/import/normalized.wav`) so
  the artifact contract survives moving the data root.
- `attempt_count` is incremented when the job enters `submitting`, so a submit that never
  returns still consumes an attempt and cannot retry forever.
- `next_retry_at` makes the backoff durable: a restart resumes the same schedule.
- `request_metadata_path` points at `asr/jobs/<job-id>/request.json`, which is sanitized —
  credentials and authorization headers never appear in it, and the inline audio payload is
  replaced by its byte count.
- `raw_response_path` and `normalized_result_path` point at `response.json` and
  `normalized.jsonl`. The raw response is written before parsing so a parser fix never
  requires re-billing the same audio.
- `transcript/raw.jsonl` is derived, not appended: it is rebuilt by concatenating each job's
  `normalized.jsonl` in job order. Re-completing a job (which happens when a process dies
  between writing the transcript and persisting the terminal status) therefore replaces that
  job's contribution instead of duplicating its segments.

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

Every embedded `*.sql` resource under `Migrations` must carry a parseable version
(`Migrations.<digits>`), and no two may claim the same one. Both violations are rejected before
any DDL runs, because either one makes a migration silently not run: a duplicate looks
already-applied, and an unnumbered script is never considered at all.

- **0001_sessions** (M0): creates `schema_migrations` and the `sessions` table (section 1) with `CHECK` constraints on `mode` (`offline`/`online`/`import`) and `source_type` (`live`/`import`), plus convenience indexes on `status` and `started_at`.
- **0002_audio_chunks** (M1): creates the `audio_chunks` table (section 5) with `CHECK` constraints on `source` (`mic`/`loopback`), `sample_format` (`pcm`/`ieee_float`) and `status`, a unique key on `(session_id, source, sequence)`, a `REFERENCES sessions (id) ON DELETE CASCADE` clause, and indexes on `(session_id, source, sequence)` and `status`. Foreign keys are enabled per connection, so a chunk row cannot exist without its session.
- **0003_asr_jobs** (M3): creates the `asr_jobs` table (section 6) with a `CHECK` constraint on `status` and indexes on `status`, `session_id`, and `next_retry_at`.
- Later milestones add `speakers`, `speaker_embeddings` and `speaker_assignments` as their features are implemented, each as a new numbered migration. No table is created ahead of its feature (section 11).

Two scripts claiming one version would make the second look already applied and its tables would
never be created, so `SqliteMigrator` fails loudly if two embedded scripts resolve to the same
version — and equally loudly if an embedded `*.sql` under `Migrations` has no parseable version,
because such a script would otherwise never be applied at all.
