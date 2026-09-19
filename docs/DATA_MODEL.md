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

`recording.lock` is a liveness marker, not an artifact: it is claimed while the session is
being prepared, before `session.json` and the `sessions` row become visible, and held open
exclusively until the recording finishes or the prepared session is abandoned without
running. The operating system releases the handle when that process exits for any reason.
Recovery scans use it to tell a recording that is still in progress apart from a session
abandoned by a killed process, so `meetcap status` can never rewrite a live session to
`INTERRUPTED` — including in the window between the session being published and capture
starting (`docs/ARCHITECTURE.md` section 9.1). It is deleted at the end of a clean session;
an undeletable leftover carries no meaning because it is not held.

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
overflow, a stalled downstream consumer or a storage problem. `end_reason` names why the
session ended (`stop_requested`, `device_lost`, `disk_exhausted`, `storage_error`,
`capture_start_failed`, `interrupted`).

M2 adds the session's own gap and bounded-buffer accounting to the same document. The fields
are additive: a reader that does not know them still reads the manifest, and a manifest written
before M2 — or by a recording that never reached its teardown — simply lacks `capture_health`,
`gaps_remain` and `gap_details`. Their absence means "not recorded", never "no problems".

```json
{
  "gap_count": 1,
  "gap_total_ms": 1250,
  "capture_health": {
    "capacity_packets": 500,
    "peak_queued_packets": 37,
    "dropped_packets": 0,
    "overflow_events": 0,
    "longest_stall_ms": 0,
    "stall_events": 0,
    "gap_total_ms": 1250,
    "gap_count": 1,
    "is_degraded": true
  },
  "track_health": [
    {
      "source": "mic",
      "buffer_health": { "capacity_packets": 500, "peak_queued_packets": 20, "dropped_packets": 0, "overflow_events": 0, "longest_stall_ms": 0, "stall_events": 0, "gap_total_ms": 0, "gap_count": 0, "is_degraded": false },
      "gap_total_ms": 0,
      "gap_count": 0,
      "degraded": false,
      "end_reason": null,
      "chunks_closed": 4,
      "closed_data_bytes": 11520000
    },
    {
      "source": "loopback",
      "buffer_health": { "capacity_packets": 500, "peak_queued_packets": 37, "dropped_packets": 0, "overflow_events": 0, "longest_stall_ms": 0, "stall_events": 0, "gap_total_ms": 1250, "gap_count": 1, "is_degraded": true },
      "gap_total_ms": 1250,
      "gap_count": 1,
      "degraded": true,
      "end_reason": "device_lost",
      "chunks_closed": 3,
      "closed_data_bytes": 8640000
    }
  ],
  "gaps_remain": false,
  "gap_details": []
}
```

- `gap_count` and `gap_total_ms` are what the capture timeline observed while it was alive:
  one count per discontinuity — a device-position skip or a measured device outage — and the
  milliseconds they add up to (`docs/ARCHITECTURE.md` section 8.1). The `session.stopped`
  event carries the same total as `gap_ms`.
- `capture_health` is the `AudioBufferHealth` record: the queue bound this session actually
  used (`capacity_packets`, `AudioBufferHealth.CapacityPackets` in C#), the deepest backlog it
  reached (`peak_queued_packets`), the packets the bound refused (`dropped_packets` and the
  `overflow_events` that produced them), the stalled-consumer observations (`stall_events`,
  `longest_stall_ms`), the same gap totals again, and the `is_degraded` verdict, which is
  computed from those counts and written anyway so a reader does not have to re-derive it. The
  record's property names are the on-disk names, because the manifest serializer writes the
  record directly and there is no second hand-written JSON form to drift from them. It is
  written when the recording session writes its final manifest. For a dual-track (online)
  session `capture_health` is the aggregate across tracks; the per-track breakdown is
  `track_health` (docs/ROADMAP.md M5).
- `track_health` is one entry per captured track (mic, and loopback for an online session).
  Each entry carries the track's own `buffer_health`, gap totals, `degraded` verdict,
  `end_reason`, `chunks_closed` and `closed_data_bytes`. The loss or degradation of one track
  is explicit here and never silently folded into the other
  (docs/RELIABILITY.md section 8). A reader that wants the simple "is anything wrong" verdict
  reads `capture_health.is_degraded`; a reader that wants "which track" reads `track_health`.
- `gaps_remain` and `gap_details` are always present, defaulting to `false` and `[]`; startup
  recovery and `meetcap session repair` set them after auditing the session: `gaps_remain` is
  true while the timeline still holds a provable gap after every repair that could be
  attempted, and `gap_details` holds one `SessionAudit.DescribeGaps()` line per gap
  (`docs/RELIABILITY.md` section 6).
- Recovery does **not** rewrite `gap_count` / `gap_total_ms`. Those fields mean "what the live
  capture timeline measured while recording", and the audit's finding is a different fact, kept
  in `gaps_remain` / `gap_details`. Overwriting one with the other would change what the field
  means and can produce a self-contradictory pair — `gaps_remain: true` next to a
  `gap_total_ms` of `0`, which an unreadable chunk with no recorded position legitimately
  produces. A reader that wants "is anything missing" reads `gaps_remain`; a reader that wants
  "what did the recorder itself measure" reads `gap_total_ms`.

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
{"event":"capture.consumer_stalled","at_ms":120000,"source":"mic","count":480,"detail":"the recording consumer has not drained the queue for 5000 ms while 480 of 500 packet slots are still occupied; audio capture is unaffected and the backlog stays inside its configured bound."}
{"event":"capture.gap","at_ms":300000,"source":"mic","gap_start_ms":240000,"gap_end_ms":300000,"gap_ms":60000,"reason":"chunk_unreadable","count":1,"detail":"chunk(s) 000005=corrupt are not durable audio, so this stretch of the track timeline has no readable bytes."}
{"event":"capture.gap","at_ms":125000,"source":"mic","gap_start_ms":120000,"gap_end_ms":125000,"gap_ms":5000,"device_position_frames":5760000,"qpc_position_ticks":1200000000,"detail":"the device skipped audio between buffers."}
{"event":"session.repair.incomplete","at_ms":300000,"gap_ms":60000,"count":1,"reason":"gap_detected","detail":"recovery could not make this session whole; 1 gap(s), 60000 ms of audio missing (1 from unreadable artifacts). The audio that is missing has no durable chunk and was not invented."}
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

M2 adds two events to the same vocabulary:

```text
capture.consumer_stalled   the bounded queue stayed occupied for at least
                           capture.buffer_seconds while no chunk closed
session.repair.incomplete  recovery could not make a session whole
```

M4 adds the ASR batch vocabulary:

```text
asr.batch.closed           a batch window was materialized into a durable WAV plus its
                           timeline manifest, immediately before its job is queued
asr.batch.failed           a batch window could not be built, so its audio never
                           reached the provider. `reason` is `chunk_unreadable` when one
                           capture chunk could not be read (that chunk is dropped from
                           the window and the rest is retried in the same call),
                           `batch_write_failed` when the batch WAV or its timeline
                           manifest could not be written (nothing is dropped; the whole
                           window stays pending and is retried), or `job_queue_failed`
                           when the batch is durable on disk but its job row could not
                           be written (the batch is re-queued by `meetcap asr resume`)
asr.batch.discarded        recovery discarded an unfinished batch .part; the capture
                           chunks it would have held are still durable
```

`asr.batch.closed` is written before `asr.job.queued` because the durable batch artifact is what
the job names (`docs/ARCHITECTURE.md` section 10.1). Both are written only when a job is actually
created, so a recovery pass that finds an already-queued batch does not restate them.

`asr.batch.failed` is the record that a stretch of the session timeline has no transcript. It
never means the audio is gone: the capture chunk it names is still under `audio/`, and the event
says so. It exists because the alternative — a window that quietly produces no batch — is a
silence in the transcript that nothing explains. For `batch_write_failed`, the batch WAV is
removed along with the manifest it cannot be paired with, because a WAV with no manifest states no
timeline and `RecoverFinalizedBatches` refuses it; if even that removal fails, the same event names
the leftover artifact so it is not left silently on disk.

`session.repair.incomplete` is written by startup recovery and by `meetcap session repair`
when the session's timeline still holds a provable gap after every repair that could be
attempted — including for a session that had already stopped cleanly and merely held a stray
artifact. Its `reason` is `gap_detected`, or `audit_failed` when the chunk index itself could
not be read. It is the durable statement that recovery did not make the session whole, so no
consumer of the event log has to infer that from a missing chunk. It is written once per
verdict, not once per pass: recovery re-runs on every command, so the event is only appended
when the reason, the gap count and the missing milliseconds differ from a copy already in the
log. A new event therefore means the picture actually changed.

The event object also gained an optional `reason` field: a machine-readable classification for
events whose name alone is not specific enough. `capture.gap` uses it for the gap reason
(`not_captured`, `chunk_unreadable` or `chunk_missing`, section 5.1) and `session.recovered`
sets `reason=incomplete` when the session still has a known gap.

### 4.1 `capture.gap` has two producers, and they must name the same hole

A gap is observed twice: the live recording measures it while it is running, and recovery
re-derives it from the chunk index afterwards (section 5.1). The event therefore carries the
gap's own interval in `gap_start_ms` / `gap_end_ms` — where audio stopped and where it resumed
— and `at_ms` remains the position on the timeline the event is placed at, which for the live
writer is the resume position.

Those are deliberately not the same coordinate. The live writer's `start_ms`, when it sets it
at all, is the position of the *buffer* it is writing (where audio resumes), while the audit's
span runs from the end of the last durable chunk to the start of the next one.

Recovering a session must be able to tell that its own gap and the live recording's gap are one
hole rather than two, so recovery compares the two intervals under a **bounded** rule: a recorded
gap accounts for an audit gap only when it **covers** the audit's span, boundary for boundary,
within a small quantisation tolerance (50 ms, `SessionRecoveryScanner.GapMatchToleranceMs`). A
device-position skip is bounded by the gap in device positions the recording actually observed, so
it cannot cover a stretch the chunk index shows holds no readable bytes — which is why an
unreadable or missing chunk is always reported even when an unrelated live gap starts inside it.

Two weaker rules were tried and rejected, and the reasons are worth keeping:

- matching on a single position (`(start_ms, source)`, or the recorded resume position) misses the
  hole when the two producers place it differently, so one hole is reported twice;
- matching on any intersection suppresses an audit gap that a *shorter* live gap merely overlaps.
  That drops the only durable record of the rest of the loss from the event log and leaves the log
  contradicting the session's own `gaps_remain`, which is worse than the duplicate it prevents.

Boundary equality within the tolerance also covers the case where both producers agree exactly,
which is what a real recording produces: the audit's boundaries come from the same chunk
`start_ms` / `end_ms` values the live timeline wrote.

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

### 5.1 Gap audit (M2)

The durable answer to "which stretches of this session hold no audio" is derived from the
index above by `SessionGapAuditor`: the audit reads the session's `audio_chunks` rows, states
what is provably not there, and mutates nothing. It is deliberately a different mechanism from
the live timeline's gap accounting (`CaptureTimeline`, whose persisted form is `gap_count` /
`gap_total_ms` in section 3; the mechanism is `docs/ARCHITECTURE.md` section 8.1): the live
counter measures discontinuities as they happen, the audit reconstructs the same question from
the artifacts that survived, and recovery uses the one that does not depend on the process
having stayed alive.

An audit returns a `SessionAudit`: the gaps, plus any problem that stopped the audit from being
honest. Each gap is an `AudioGap` with `Sequence` (the chunk the gap is attached to), `Source`,
`StartMs` / `EndMs` (`GapMs` is their difference), `Reason`, `Detail` and `MissingSequences`.
`RecoveryIncomplete` is true when there is a gap or an audit problem, and that is the flag a
repair must never report past (`docs/RELIABILITY.md` section 6).

`Reason` is one of exactly three values:

```text
not_captured       the audio was never captured: the device skipped it, the session was
                   interrupted, or the bounded queue had to drop it
chunk_unreadable   a chunk exists for this position but is not durable audio
chunk_missing      the index knows a chunk at this position, but no file is on disk
```

Only `closed` and `recovered` chunks are durable. The rules that produce those values:

- a run of non-durable chunks plus any adjacent timeline hole is **one** gap, spanning from the
  end of the last durable chunk to the start of the next durable one, rather than several
  overlapping gaps that would misstate how much audio is missing;
- the reason is `chunk_unreadable` or `chunk_missing` when a non-durable chunk caused the
  stretch, and `not_captured` otherwise. `chunk_missing` is used only when **every**
  non-durable chunk in the run is `missing`;
- a chunk number the track skipped while its timeline stayed contiguous is still reported: a
  zero-length gap (`start_ms` = `end_ms`) whose `MissingSequences` names the skipped numbers,
  because a chunk number that never reached disk is exactly the loss that must not be hidden;
- `MissingSequences` lists the chunk numbers inside the gap with no durable audio — the
  sequences of the non-durable index entries plus any sequence number the track skipped — and
  `capture.gap` carries that count as `count`;
- an index that cannot be read at all is a problem, not "no gaps", so an unreadable
  `meetcap.db` can never be mistaken for a healthy session.

Gaps are always one event with an explicit reason, never a shifted timestamp. M2 adds **no**
SQLite table and no migration for any of this (sections 11 and 14).

One limitation is worth stating, because the audit is easy to over-trust. The audit reads spans
from the chunk index, so it can only see a hole that falls *between* durable chunks. A device
skip that happens inside the audio one chunk holds is invisible to it: the chunk's `start_ms` /
`end_ms` describe the timeline span it covers, not the continuity of the frames inside it. That
case is still reported — by the live timeline, while the recording runs, as `capture.gap` — and
the audit stays silent about it because the audio it would otherwise re-derive was never lost
between chunks. The two mechanisms cover different holes; neither replaces the other.

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
provider_log_id
duration_ms
speaker_info_requested
speaker_info_returned
estimated_cost_cny
submitted_at
completed_at
created_at
updated_at
audio_transport
tos_bucket
tos_object_key
tos_cleanup_pending
```

Status values (section 12 of `ARCHITECTURE.md`): `pending`, `submitting`, `submitted`,
`polling`, `succeeded`, `retry_wait`, `failed`, `cancelled`.

The existing `tier` column is retained as a schema-compatibility field. Since issue #26 every
Volcengine recording-file job uses the single fixed value `standard`; it is no longer a user
choice or routing input, and the domain type exposes it as a constant rather than a settable
property, so a stored legacy value is never read back as a route. This avoids a destructive
migration solely to remove a now-constant column.

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
  replaced by its byte count. Under issue #29, TOS-backed requests may record transport type plus
  stable bucket/object-key identity, but MUST NOT persist the presigned URL, its signature/query
  parameters, or TOS credentials.
- `provider_log_id` holds the provider's trace id for the last exchange (Volcengine
  `X-Tt-Logid`), added by migration `0005_asr_job_provider_log_id`. It is what a provider support
  request about a task is traced by, so it is recorded as soon as the submit answers and is
  replaced by the query's log id on completion. A response without the header never erases a
  recorded value. It is a diagnostic id, not a credential: the API key is never persisted in
  this or any other column, and `X-Tt-Logid` is also carried in provider error messages so a
  failed exchange is traceable from `error_message` alone.
- `raw_response_path` and `normalized_result_path` point at `response.json` and
  `normalized.jsonl`. The raw response is written before parsing so a parser fix never
  requires re-billing the same audio.
- `transcript/raw.jsonl` is derived, not appended: it is rebuilt by concatenating each job's
  `normalized.jsonl` in job order. Re-completing a job (which happens when a process dies
  between writing the transcript and persisting the terminal status) therefore replaces that
  job's contribution instead of duplicating its segments.

### 6.1 Live ASR batch artifact (M4)

A live session submits one provider request per ASR batch window, so the artifact each job
names is a batch built from the session's own capture chunks:

```text
sessions/<session-id>/
  asr/
    batches/
      mic/
        batch-000001.wav
        batch-000001.json
```

`batch-NNNNNN.wav` is a real, independently readable WAV: the capture chunks of the track are
concatenated header-and-payload, the header is patched, validated and renamed out of `.part` by
the same writer the capture spool uses. There is no re-encode and no resampler, because every
chunk of one track carries the session's single native format
(`docs/ARCHITECTURE.md` section 10.1).

`batch-NNNNNN.json` is the batch/source timeline mapping, and it is what makes a batch auditable
back to the audio it was built from:

```json
{
  "session_id": "ses_20260915T140000Z_a1b2c3d4",
  "source": "mic",
  "batch": 1,
  "artifact": "asr/batches/mic/batch-000001.wav",
  "start_ms": 0,
  "end_ms": 240000,
  "duration_ms": 240000,
  "data_bytes": 7680000,
  "chunks": [
    { "sequence": 1, "artifact": "audio/mic/000001.wav", "start_ms": 0, "end_ms": 60000, "data_bytes": 1920000 }
  ]
}
```

The manifest also records `provider`, so an orphaned batch recovered by a later process is queued
for the provider the recording was actually configured with rather than for whatever the resuming
process happens to use. There is deliberately no `tier` field: issue #26 removed the service-tier
selector, so it could only ever repeat one constant. A manifest written before #26 may still carry
`tier`; the reader ignores it, and the recovered job records the single supported value.

`start_ms` / `end_ms` are the batch's own span on the session timeline, and each entry's
`start_ms` / `end_ms` is that capture chunk's own span. A window closes on captured audio time,
so a batch that covers a device outage declares a span longer than the audio inside it while the
per-chunk entries state where the audio really sits. A gap is therefore never hidden by shifting
timestamps (`docs/RELIABILITY.md` section 7).

`asr_jobs.input_artifact` stores the session-relative batch path, and the job's `start_ms` is the
batch's start position: that is the offset the normalizer adds to every provider timestamp so the
segments of the third batch land where they were spoken rather than at the start of the meeting
(section 7). The job id is derived from the batch artifact path **and its session**
(`job_<session>_<source>_batch-NNNNNN`), so recovery can tell whether a durable batch already has a
job without inventing a second billable task. Batch numbering restarts at 1 per track per session
while `asr_jobs` is one table shared by every session in a data root, so the session is part of the
identity: without it, a later session would match an earlier session's row by id and queue nothing.
Idempotency itself is decided by the session-scoped `input_artifact` match, which also recognises
rows written before the id carried the source or the session.

The batch WAV is an intermediate artifact, not the durable record. Its capture chunks, the
retained raw provider response and the job's `normalized.jsonl` are what the session keeps, and
`transcript/raw.jsonl` is derived from the normalized artifacts (section 12). A `.part` left by an
unfinished batch is discarded by recovery, because no job references it and its chunks are still
durable: that is a lost batch window, never lost audio.

This adds no SQLite table and no migration: the batch's own mapping lives in the artifact beside
it, and the job row already has the columns to point at it.

### 6.2 TOS large-file transport state

Issue #29 requires a new numbered migration rather than rewriting the existing `0003_asr_jobs`
migration. The durable job row must gain enough non-secret state to recover a TOS-backed
submission across process restarts. The implemented columns are:

```text
audio_transport      inline | tos, NOT NULL DEFAULT 'inline', CHECK constrained
tos_bucket           nullable; set for tos
tos_object_key       nullable; set for tos
tos_cleanup_pending  0 | 1, NOT NULL DEFAULT 0; the durable cleanup debt
```

`tos_cleanup_pending` is the implemented form of the `not_required | pending | deleted` cleanup
state sketched before the migration was written. It collapses `not_required` and `deleted` into
"no debt outstanding", which is the only distinction the cleanup pass acts on: the stable bucket
and object key are kept after a successful delete, so the "deleted" case is still auditable from
the row and from the `asr.audio.released` event without a third stored value.

The invariants this shape has to satisfy:

- the presigned URL is never the durable identifier and is never stored in SQLite;
- bucket + object key are persisted before provider submission so a restart can generate a new
  SDK presigned GET URL instead of uploading a duplicate object unnecessarily;
- TOS credentials never enter the database;
- cleanup is durable/idempotent: a successful transcript may coexist with outstanding cleanup
  debt until `DeleteObject` succeeds;
- cleanup debt is not a transcription failure and must not erase provider results;
- inline jobs do not manufacture fake TOS state.

The local `input_artifact` remains authoritative for the recording/batch itself. The TOS object
is only a temporary transport copy and can be deleted without changing transcript provenance.

Who writes these columns: `VolcengineAsrProvider` publishes the audio, chooses `audio.data` or
`audio.url` from the returned transport, and reports the stable identity on
`AsrSubmission.Audio`. `AsrJobProcessor` records it with
`AsrJobTransitions.RecordAudioTransport` before the job is marked `submitted`, so the identity is
durable before the provider is treated as having accepted the task. On a terminal state it calls
`IAsrProvider.ReleaseAudioAsync` and clears the debt with `AsrJobTransitions.MarkAudioReleased`
only when the delete is confirmed. `IAsrJobStore.ListCleanupPending` is how a later process finds
debt that is still outstanding; it is deliberately separate from `ListResumable`, so a succeeded
job that still owes a delete is not reported as outstanding work.

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

`start_ms` / `end_ms` are session-relative. The provider reports timestamps relative to the
artifact it was given, and `start_ms` for a segment is always a position on the session's own
timeline: for an import the artifact is the whole session, and for a live ASR batch the batch's
start position is added during normalization (section 6.1).

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

Per-session artifact (`speakers/attribution.json`, `docs/ARCHITECTURE.md` section 19):

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

The `speaker_assignments` SQLite table (migration `0004_speakers`, section 14) stores the
durable binding of each `(session_id, speaker_label)` to a person, so a manual assignment
or a re-run of the voiceprint pipeline is idempotent (the `UNIQUE (session_id,
speaker_label)` constraint makes upsert replace rather than duplicate). The `attribution.json`
artifact is the materialized view produced by `meetcap speakers attribute`, combining manual
assignments and voiceprint candidates through the matching policy
(`docs/ARCHITECTURE.md` section 17.3).

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

A skip is not the only protection, because a second process can read `schema_migrations`
before another process records a version and then run that script too. Every script must
therefore also be safe to re-run *and* leave existing data intact. When such a script has to
copy a column that only the later schema has — the usual case for the migration that adds that
column — it cannot say so in SQL, because SQLite cannot branch on column presence and a
statement naming an absent column fails while it is prepared. Those scripts use
`SqliteMigrator`'s schema-conditional placeholder `{{table.column}}`, which expands to the
column when the schema has it and to `NULL` when it does not.

Two properties of that placeholder are what a script author has to know. First, only a
mistyped *table* is loud: the placeholder is then left exactly as written, the script reaches
SQLite unexpanded and fails to prepare, and the migration aborts instead of quietly copying
`NULL`. A placeholder for a column the named table does not have expands to `NULL` even when
the author mistyped the name of a column that does exist, because `NULL` is also the correct
first-run value of the column the migration itself is adding — the mechanism cannot tell the
two apart, and neither can SQL. Second, the placeholder is substituted over the whole script
text, comments and string literals included, so `{{table.column}}` is a reserved sequence:
a script only writes it where it is meant to expand.

Every embedded `*.sql` resource under `Migrations` must carry a parseable version
(`Migrations.<digits>`), and no two may claim the same one. Both violations are rejected before
any DDL runs, because either one makes a migration silently not run: a duplicate looks
already-applied, and an unnumbered script is never considered at all.

- **0001_sessions** (M0): creates `schema_migrations` and the `sessions` table (section 1) with `CHECK` constraints on `mode` (`offline`/`online`/`import`) and `source_type` (`live`/`import`), plus convenience indexes on `status` and `started_at`.
- **0002_audio_chunks** (M1): creates the `audio_chunks` table (section 5) with `CHECK` constraints on `source` (`mic`/`loopback`), `sample_format` (`pcm`/`ieee_float`) and `status`, a unique key on `(session_id, source, sequence)`, a `REFERENCES sessions (id) ON DELETE CASCADE` clause, and indexes on `(session_id, source, sequence)` and `status`. Foreign keys are enabled per connection, so a chunk row cannot exist without its session.
- **0003_asr_jobs** (M3): creates the `asr_jobs` table (section 6) with a `CHECK` constraint on `status` and indexes on `status`, `session_id`, and `next_retry_at`.
- **0004_speakers** (M6): creates the `speakers` (section 8), `speaker_embeddings` (section 9),
  and `speaker_assignments` (section 10) tables. `speakers.display_name` is `UNIQUE` so
  enrollment cannot create a silent duplicate; `speaker_embeddings.speaker_id` has
  `REFERENCES speakers (id) ON DELETE CASCADE` so deleting a person removes their
  voiceprints; `speaker_assignments` has a `CHECK` on `source`
  (`manual`/`voiceprint`/`owner_assumption`/`unknown`) and `locked`, a `UNIQUE
  (session_id, speaker_label)` so upsert is idempotent, and `speaker_id` is nullable
  with `ON DELETE SET NULL` so an unknown label has no person and removing a person
  demotes their assignments rather than orphaning them.
- **0005_asr_job_provider_log_id** (issue #26): adds the nullable `provider_log_id` column to
  `asr_jobs` (section 6). SQLite has no `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`, and
  `SqliteMigrator` requires every script to be re-runnable (a second process that read
  `schema_migrations` before this version was recorded runs the script too), so the migration
  rebuilds `asr_jobs` instead: every statement is guarded, the rows are copied across unchanged
  by `INSERT OR IGNORE`, and a re-run is a no-op. A re-run is reached only because another
  process already committed this rebuild, so between that commit and this run's `DROP TABLE`
  the rest of MeetCap can have written real `X-Tt-Logid` values; the copy therefore carries
  `provider_log_id` through the schema-conditional placeholder, so a re-run preserves those
  values rather than resetting them to `NULL`, and a first run copies `NULL` because no log id
  exists yet. The legacy `tier` column and all `CHECK`
  constraints are reproduced byte-for-byte from `0003_asr_jobs`.
- **M2** requires no new migration. The bounded-buffer accounting and the gap totals live in
  `session.json` and `events.jsonl` (sections 3 and 4), and the gap audit (section 5.1) reads
  the M1 `audio_chunks` index. No table, column or constraint changed, so the applied schema is
  identical before and after M2.
- **M4** requires no new migration either. A live ASR batch is an audio artifact plus its
  timeline manifest under `asr/batches/` (section 6.1), and the job that consumes it uses the M3
  `asr_jobs` columns unchanged: `input_artifact` names the batch and `start_ms` carries its
  position on the session timeline. No table, column or constraint changed.
- Issue #29 adds TOS transport state to `asr_jobs` through **0006_tos_asr_transport**. Do not edit
  `0003_asr_jobs` in place: already-created data roots must migrate forward while preserving the
  existing provider request ID, retry state, and local input artifact. Like 0005, 0006 rebuilds
  `asr_jobs` (SQLite has no `ADD COLUMN IF NOT EXISTS`) with every statement guarded, so a replay
  is a no-op for the schema and a value-preserving copy for the rows.
- **A rebuild must declare every column the table can already have.** This is the rule the
  original 0006 exposed and CI caught. `DROP TABLE` removes columns it has never heard of, and a
  replay of 0005 — which the migrator's re-runnability contract allows at any later time — used to
  recreate `asr_jobs` from the 0003-plus-`provider_log_id` shape alone. Every column a *later*
  migration had added was therefore destroyed, and the next `SqliteAsrJobStore.Get` failed with
  `no such column: audio_transport`. Both 0005 and 0006 now declare the full post-#26 schema and
  copy the columns they do not own through the schema-conditional placeholder, so whichever one
  replays, the result is the same table. Any future migration that adds a column to `asr_jobs`
  must add it to both rebuilds as well. The regression tests are
  `SqliteMigratorTests.Migrate_ReRunOfBothAsrJobMigrations_PreservesEveryColumnAndValue` and
  `SqliteMigratorTests.Migrate_UpgradeFromAPreIssue29Database_AddsTheTransportColumnsWithInlineDefaults`.
- A migration may not simply skip itself when its column is already present either, and this is
  why the rebuild is unconditional rather than guarded by "does `provider_log_id` exist yet". On a
  fresh database 0006 commits first, so 0005's rebuild still has to run to add `provider_log_id`
  in a schema where 0006's columns already exist; a presence guard would either skip the column
  0005 exists to add or drop the ones 0006 added.
- Later milestones add new schema only with their implemented features. No table or column is
  created ahead of its feature (section 11).

Two scripts claiming one version would make the second look already applied and its tables would
never be created, so `SqliteMigrator` fails loudly if two embedded scripts resolve to the same
version — and equally loudly if an embedded `*.sql` under `Migrations` has no parseable version,
because such a script would otherwise never be applied at all.
