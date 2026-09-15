# Roadmap

The roadmap is ordered by dependency and risk.

Agents MUST NOT jump ahead because a later feature is easy or interesting.

The central development rule is:

> Prove recording reliability before spending meaningful effort on transcription intelligence.

---

# M0 - Repository and executable skeleton

## Goal

Produce a runnable Windows CLI with configuration loading and session metadata.

## Deliverables

- .NET 10 solution
- projects defined in `ARCHITECTURE.md`
- CLI entry point
- structured logging
- config loader for `%APPDATA%\MeetCap\config.toml`
- SQLite bootstrap/migrations
- `meetcap config init`
- `meetcap config validate`
- `meetcap status`
- CI build/test on Windows

## Exit criteria

```text
dotnet test
```

passes on Windows and:

```powershell
meetcap config init
meetcap config validate
```

work on a clean machine.

No audio capture yet.

---

# M1 - Offline microphone capture

## Goal

Make the application trustworthy as a recorder.

## Deliverables

- list audio devices
- select configured microphone
- `meetcap start --mode offline`
- microphone WASAPI capture
- session-relative timing
- 60-second recoverable chunks
- `.part` lifecycle
- chunk validation
- `meetcap stop`
- crash-startup scan
- disk-space warning

## Mandatory tests

- 2-hour microphone recording
- default-device change
- microphone unplug/replug
- forced process kill
- low disk-space simulation

## Exit criteria

No cloud services are needed to complete an offline recording.

A forced process termination does not invalidate previously closed chunks.

---

# M2 - Durable spool and recovery hardening

## Goal

Prove "record first" invariants.

## Deliverables

- bounded capture buffers
- backpressure metrics
- packet timing metadata
- gap events
- startup recovery
- session repair command
- `events.jsonl`
- explicit degraded-state reporting

## Exit criteria

Network, ASR, and speaker modules can all be disabled and recording remains stable.

---

# M3 - Import + file ASR

## Goal

Preserve the original file-transcription workflow and integrate Volcengine file ASR before live ASR batching.

## Deliverables

```powershell
meetcap import .\meeting.m4a
```

- media inspection
- normalization only when required
- persistent ASR job queue
- Volcengine file-ASR provider
- submit/query handling
- retries
- raw provider response
- normalized transcript JSONL
- Markdown transcript
- speaker labels returned by ASR where available

## Exit criteria

Existing recordings can be converted to final transcript artifacts without any live-capture code path.

This milestone is the first end-to-end ASR proof.

---

# M4 - File-first transcription during live recording

## Goal

Continuously produce transcript artifacts without defaulting to streaming ASR.

## Deliverables

- ASR batch builder
- configurable `file_batch_seconds`
- default 300-second batch window
- submit batches as they close
- append `transcript/live.md`
- append `transcript/raw.jsonl`
- queue when offline
- resume after network recovery
- flush remaining batch at stop

## Exit criteria

A 2-hour offline meeting produces transcript updates during the meeting while all ASR calls use file ASR.

Disabling the network for 30 minutes does not affect audio capture and queued batches complete after recovery.

---

# M5 - Online meeting dual-track capture

## Goal

Capture online meetings without mixing local and remote audio.

## Deliverables

- system loopback enumeration
- online mode
- simultaneous mic + loopback capture
- independent chunk streams
- independent file-ASR batches
- unified timeline merger
- source labels in transcript

## Exit criteria

A 2-hour online meeting produces:

```text
audio/mic/*
audio/loopback/*
```

and a merged transcript with correct source attribution.

No hybrid mode is introduced.

---

# M6 - Speaker registry and voiceprint matching

## Goal

Reduce repeated manual speaker labeling across meetings.

## Deliverables

- speaker entity
- embedding provider abstraction
- enroll command
- extract clean speaker samples
- multiple embeddings per person
- candidate similarity ranking
- manual assignment command
- manual lock
- local-only voiceprint persistence

## Exit criteria

Previously enrolled people appear as useful candidates in later recordings and manual assignment always overrides automatic inference.

Do not require 100% automatic naming.

---

# M7 - ASR quality controls

## Goal

Improve recognition quality without changing recording reliability.

## Deliverables

- hotword-table configuration
- request-level hotwords if supported
- glossary file
- provider request tracing without secrets
- selectable standard/idle/turbo service tier
- optional full-session final re-ASR flag
- cost accounting per ASR job

## Exit criteria

Technical terms can be improved through configuration without code changes.

---

# M8 - Optional streaming ASR

## Goal

Add low-latency text only if real usage proves it valuable.

## Status

Deferred by product decision.

## Rules

- disabled by default;
- never required for recording;
- never automatic fallback for file ASR;
- must share normalized transcript model;
- must not change capture pipeline;
- final file-ASR output may supersede streaming text while preserving both raw artifacts.

---

# M9 - Post-processing / meeting intelligence

Deferred until capture + ASR + speaker attribution are stable.

Possible items:

- terminology correction
- meeting summary
- decisions
- action items
- project-specific templates
- cross-meeting search

These are not prerequisites for MVP.

---

# Release gate for MVP

The first MVP release requires M0 through M6.

M7 may partially land before the release but MUST NOT delay core reliability unless it fixes real transcription quality.

M8 and M9 are explicitly post-MVP.
