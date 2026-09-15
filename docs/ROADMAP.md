# Roadmap

The roadmap is ordered by dependency and risk.

Agents MUST NOT jump ahead because a later feature is easy or interesting.

The central development rule is:

> Prove recording reliability before spending meaningful effort on transcription intelligence.

A second implementation rule applies across milestones:

> Reuse mature OSS for commodity capabilities; keep MeetCap-specific durability, state-machine, timeline, artifact, and speaker-registry semantics explicit in MeetCap.

---

# M0 - Repository and executable skeleton

## Goal

Produce a runnable Windows CLI with configuration loading and session metadata.

## Deliverables

- .NET 10 solution
- projects defined in `ARCHITECTURE.md`
- System.CommandLine CLI entry point
- Serilog structured logging
- Tomlyn-backed config loader for `%APPDATA%\MeetCap\config.toml`
- Microsoft.Data.Sqlite bootstrap
- schema migrations using FluentMigrator or an equivalently thin migration layer
- `meetcap config init`
- `meetcap config validate`
- `meetcap config show`
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
- NAudio 3 / WASAPI capture behind `MeetCap.WindowsAudio`
- `meetcap start --mode offline`
- session-relative timing
- preserve useful device/QPC timing metadata exposed by NAudio
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

The spool, `.part -> CLOSED` lifecycle, crash recovery, and gap semantics remain MeetCap-owned logic.

---

# M3 - Import + file ASR

## Goal

Preserve the original file-transcription workflow and integrate Volcengine file ASR before live ASR batching.

## Deliverables

```powershell
meetcap import .\meeting.m4a
```

- media inspection through FFprobe/FFMpegCore
- FFmpeg normalization only when required
- persistent ASR job queue in SQLite
- Volcengine file-ASR provider
- submit/query handling
- Polly-based transient HTTP resilience inside the provider adapter
- persistent retry/job state independent from Polly
- raw provider response
- normalized transcript JSONL
- Markdown transcript
- request/preserve anonymous speaker labels and timestamps where Volcengine supports them

## Exit criteria

Existing recordings can be converted to final transcript artifacts without any live-capture code path.

This milestone is the first end-to-end ASR proof and the first proof that provider diarization labels can be preserved in the normalized transcript model.

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
- preserve anonymous provider speaker labels
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
- simultaneous mic + loopback capture using NAudio
- independent chunk streams
- independent file-ASR batches
- unified timeline merger
- source labels in transcript
- allow `system` loopback and, where supported, `process` loopback as capture-source options

## Exit criteria

A 2-hour online meeting produces:

```text
audio/mic/*
audio/loopback/*
```

and a merged transcript with correct source attribution.

No hybrid mode is introduced.

Process-specific loopback does not create a new meeting mode and should reuse NAudio support rather than custom raw WASAPI activation code where practical.

---

# M6 - Speaker registry and voiceprint matching

## Goal

Reduce repeated manual speaker labeling across meetings by resolving anonymous ASR speaker labels to persistent local identities.

## Default MVP pipeline

```text
Volcengine BigASR
  -> text + timestamps + anonymous speaker labels
  -> select clean speech for each anonymous speaker
  -> sherpa-onnx
  -> 3D-Speaker ERes2Net-base
  -> local Speaker Registry
  -> ranked identity candidates
```

sherpa-onnx + 3D-Speaker is used for speaker enrollment, embedding extraction, verification, and identification. It is **not** the default MVP diarization engine.

## Deliverables

- speaker entity
- `ISpeakerIdentityProvider` abstraction
- sherpa-onnx-backed identity provider
- 3D-Speaker ERes2Net-base ONNX model integration
- initial reference model: `3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx`
- `meetcap speakers list`
- `meetcap speakers enroll <name>`
- extract clean speaker samples
- multiple embeddings per person
- candidate similarity ranking
- configurable threshold and match margin
- manual assignment command
- manual lock
- local-only voiceprint persistence
- use provider-returned anonymous speaker labels as the default cluster source

## Identity policy

```text
manual assignment
> high-confidence historical match
> anonymous ASR speaker label
```

Unknown/low-confidence speakers remain unknown.

## Exit criteria

Previously enrolled people appear as useful candidates in later recordings and manual assignment always overrides automatic inference.

A local diarization pipeline is not required for MVP unless real-world validation demonstrates that Volcengine speaker separation is inadequate.

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
- measure anonymous speaker-label quality on real meetings

## Exit criteria

Technical terms can be improved through configuration without code changes.

Provider diarization quality is measured before introducing a second default diarization stack.

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

# Optional speaker diarization fallback

Only after real recordings justify it, evaluate a provider abstraction such as:

```text
SpeakerDiarizationProvider
  -> Volcengine ASR speaker labels       # default
  -> SherpaOnnx diarization              # optional local fallback
  -> other benchmark providers           # evaluation only
```

Do not introduce Python/PyTorch into the default Windows runtime merely to duplicate diarization already available from the configured ASR provider.

---

# Release gate for MVP

The first MVP release requires M0 through M6.

M7 may partially land before the release but MUST NOT delay core reliability unless it fixes real transcription quality.

M8 and M9 are explicitly post-MVP.
