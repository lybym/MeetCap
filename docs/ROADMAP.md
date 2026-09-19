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

## Status

Implemented (issue #2). The .NET 10 solution, CLI entry point, configuration loader, and
SQLite bootstrap with SQL migrations are in place; `meetcap config init` / `config validate`
/ `config show` / `config path` and `meetcap status` are exercised in
`tests/MeetCap.Cli.Tests`.

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

## Status

Implemented. Automated coverage lives in `tests/MeetCap.AudioPipeline.Tests` (chunk
lifecycle, crash recovery, disk-space policy, device loss, bounded queue) and
`tests/MeetCap.Cli.Tests` (the `devices`, `start` and `stop` commands end to end against
a scripted capture source).

The mandatory tests below that need real audio hardware cannot run in CI. They are
recorded as a manual checklist in `docs/M1_WINDOWS_VALIDATION.md`, and M1 is **not**
claimed as verified end to end until that checklist has been run on real hardware
(`docs/DEVELOPMENT.md` section 7).

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

## Status

Implemented (issue #4).

What landed:

- bounded-buffer accounting: `CaptureBacklogMonitor` and `AudioBufferHealth` record the
  packets the queue accepted, dropped and drained, the peak backlog and the stall
  observations, and the snapshot is persisted as `capture_health` in `session.json`;
- stalled-downstream-consumer reporting: a queue that stays occupied for
  `capture.buffer_seconds` without a chunk closing marks the session degraded and writes
  `capture.consumer_stalled`; capture is never throttled;
- explicit gap accounting: `CaptureTimeline.GapTotalMs` / `GapCount` count every
  discontinuity exactly once, and `gap_count`, `gap_total_ms` and `gap_ms` are persisted;
- a durable gap audit: `SessionGapAuditor` and `SessionAudit` derive the missing stretches of
  a session timeline from the `audio_chunks` index with an explicit reason
  (`not_captured`, `chunk_unreadable`, `chunk_missing`), and mutate nothing;
- recovery honesty: startup recovery audits after repairing, writes idempotent `capture.gap`
  events for holes the live recording never saw, and writes `session.repair.incomplete`
  whenever a gap remains instead of claiming success;
- `meetcap session repair [--session <id>]`, which exits non-zero when a known gap remains or
  the requested session was not found;
- CLI reporting: `meetcap start` prints
  `capture buffer: peak N/M packets, dropped N, stalled N time(s) (longest N ms)` and
  `audio gaps: N (M ms missing)`, and `meetcap status` prints the `timeline:` / `gap:` lines
  for a recovered session that still has a gap;
- no new configuration key: the queue bound and the stall threshold both reuse
  `capture.buffer_seconds` (`docs/CONFIGURATION.md` section 6);
- no new SQLite table or migration (`docs/DATA_MODEL.md` section 14).

Automated coverage:

```text
tests/MeetCap.Core.Tests/Capture/CaptureTimelineTests.cs
tests/MeetCap.Core.Tests/Capture/CaptureBacklogMonitorTests.cs
tests/MeetCap.AudioPipeline.Tests/SessionGapAuditorTests.cs
tests/MeetCap.AudioPipeline.Tests/SessionRecoveryScannerTests.cs
tests/MeetCap.AudioPipeline.Tests/RecordingSessionTests.cs
tests/MeetCap.AudioPipeline.Tests/CaptureIndependenceTests.cs
tests/MeetCap.Cli.Tests/CaptureCommandTests.cs
```

`CaptureIndependenceTests` pins the milestone exit criterion structurally: the recording
assembly may not reference the ASR, cloud, HTTP or speaker stack, so recording keeps working
with every downstream module disabled.

What still requires a real Windows run: the 2-hour soak, a real microphone unplug/replug, a real
forced process kill, and a genuinely slow disk. M2's share of the manual checklist is
`docs/M1_WINDOWS_VALIDATION.md` section 10, and it has **not** been run yet. The suite drives a
scripted capture source and an injected consumer delay, so M2 is **implemented and automatically
covered**, not verified end to end (`docs/DEVELOPMENT.md` section 7,
`docs/RELIABILITY.md` section 14).

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

Preserve the original file-transcription workflow and integrate Volcengine Seed-ASR 2.0 Standard HTTP before live ASR batching.

## Deliverables

```powershell
meetcap import .\meeting.m4a
```

- media inspection through FFprobe/FFMpegCore
- FFmpeg normalization only when required
- persistent ASR job queue in SQLite
- Volcengine Seed-ASR 2.0 Standard HTTP provider
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

## Implementation status

Implemented (issue #5; provider contract corrected by issue #26):

The P0 provider-protocol correction (issue #26) has landed. Issue #5 implemented the durable
submit/query architecture; #26 replaced its legacy AppID/Access Token authentication, the
configurable resource ID, and the service-tier surface with the Seed-ASR 2.0 recording-file
Standard HTTP contract and `X-Api-Key`, and added `asr_jobs.provider_log_id` for the provider's
`X-Tt-Logid`. M3 is production-aligned with the current Volcengine interface at the
implementation level; real-credential validation is still outstanding (`docs/ROADMAP.md`
section 15.1).

- the 0.1.0 CLI has `meetcap import <file> [--title <title>]`; the `--tier` override was removed by issue #26 rather than kept as a one-value selector;
- `meetcap asr resume [--session <id>] [--max-jobs <n>]`, the restart entry point for the
  persistent job queue;
- `MeetCap.AudioPipeline` (FFprobe inspection, FFmpeg normalization only when required) with
  explicit toolchain location from `[media] ffmpeg_binary_folder`;
- `MeetCap.Asr` (persistent job state machine driver, transcript assembly, import
  orchestration) and `MeetCap.Asr.Volcengine` (submit/query adapter, Polly inside the HTTP
  layer, provider JSON parsing);
- `asr_jobs` migrations `0003_asr_jobs` and `0005_asr_job_provider_log_id` (version 0002 is
  claimed by the M1 capture migration);
- sanitized `request.json`, retained `response.json`, `normalized.jsonl`,
  `transcript/raw.jsonl`, `transcript/live.md`, and the `session.json` source artifact mapping
  (original + normalized, with SHA-256);
- source duration and `estimated_cost_cny` recorded per job.

Not implemented in this milestone, and deliberately out of scope:

- legacy console authentication, recording-file 1.0, idle routing, and flash/turbo routing are explicitly removed by #26 rather than promoted into supported modes;
- issue #29 large-file TOS transport is intentionally post-M3: it keeps one provider request
  but moves >20 MiB inputs through private TOS + presigned `audio.url`;
- automatic splitting of an import that exceeds the provider's actual file/duration limit remains out of scope;
- hotword tables (M7);
- any live-capture ASR batching (M4), loopback capture (M5), identity matching (M6),
  streaming ASR (M8), or LLM post-processing (M9).

Real Volcengine transcription has not been verified in CI: the environment has no
`MEETCAP_VOLCENGINE_API_KEY`, so the provider boundary is mocked in tests
(`docs/DEVELOPMENT.md` section 7). A manual Windows run with a real API key is still required
before this milestone is validated end to end.

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

## Implementation status

Implemented (issue #6):

- `MeetCap.Asr.Batching.AsrBatchBuilder`: groups durably closed capture chunks per source until
  the window covers `asr.file_batch_seconds` (default 300 s, independent of
  `capture.chunk_seconds`), materializes a real batch WAV plus its timeline manifest under
  `asr/batches/<source>/`, and only then queues the persistent job. `FlushPendingBatches` closes
  the remaining partial window at stop, and `RecoverFinalizedBatches` re-queues a finalized batch
  whose job row is missing and discards an unfinished `.part`;
- `MeetCap.Asr.LiveTranscription`: the background drain that submits and polls while the meeting
  runs, plus the stop-time flush and drain, serialized against the background loop;
- `RecordingSession.ChunkClosed` / `MeetCap.Core.Sessions.ClosedAudioChunk`: the durable-chunk
  hand-off, raised on the recording consumer thread after the chunk is validated and renamed;
- session-timeline offsets: a batch's provider timestamps are moved onto the session timeline by
  the batch's stored start position (`AsrFileRequest.StartOffsetMs`,
  `AsrNormalizationContext.StartOffsetMs`);
- `meetcap start` wiring: the ASR stack is built before the session exists, live batching is
  attached only when `asr.enabled = true`, the session goes `FINALIZING -> PROCESSING` on a clean
  stop and `COMPLETED` when its queue is terminal;
- `meetcap asr resume --force`: process jobs whose durable retry backoff has not come due yet,
  for an operator who knows the outage is over;
- `meetcap asr resume` also runs the batch-recovery pass, so a batch finalized before its job row
  existed is recoverable through the documented restart entry point and not only by starting a new
  recording;
- a window that cannot be built does not stop the track: the unreadable chunk is dropped with an
  explicit `asr.batch.failed` record and the rest of the window is retried, so the pending window
  stays bounded and later windows keep transcribing;
- `meetcap status`: `asr:`, `asr queue:` and `asr state: behind` lines, so queue depth and
  degraded transcription state are visible without reading SQLite;
- `transcript/raw.jsonl` and `transcript/live.md` advance during the meeting, rebuilt from each
  job's retained `normalized.jsonl` so re-completing a job cannot duplicate segments;
- the provider adapter classifies its own transport failures, so a lost network reaches the
  durable job state machine as a retryable failure instead of an unhandled exception.

No SQLite schema change: the batch is an artifact plus a manifest, and the M3 `asr_jobs` columns
already carry the batch path and its timeline position.

Not implemented in this milestone, and deliberately out of scope:

- streaming ASR of any kind, and any automatic streaming fallback (see M8);
- loopback capture and dual-track batching (M5);
- speaker identity matching, and any second diarization engine (M6/#8);
- LLM correction or summary (M9);
- deleting the intermediate batch WAV after its job succeeds.

Real-world validation has not been performed: the exit criteria above were exercised in CI
against a scripted capture source and a scripted provider transport, so the two-hour meeting and
the thirty-minute outage are automated coverage, not a measured soak. The manual Windows checklist
for M4 is `docs/M1_WINDOWS_VALIDATION.md` section 12, and it has not been run (its 12.2–12.4 and
12.6 rows need a real Volcengine credential). See `docs/RELIABILITY.md` section 15.

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

## Implementation status

Implemented (issue #7):

- `meetcap start --mode online` resolves the configured microphone and a render endpoint
  and runs both as independent capture tracks; `meetcap devices` lists render endpoints
  alongside capture endpoints so `capture.online.render_device_id` is discoverable;
- `IAudioDeviceEnumerator` exposes render endpoints and `AudioDeviceResolver.ResolveRender`
  resolves them, so loopback device selection mirrors microphone selection;
- `MeetCap.WindowsAudio.NAudioCaptureSourceFactory.CreateLoopback` builds the loopback
  source through NAudio 3's `WasapiRecorderBuilder.WithLoopbackCapture` (system, the
  baseline) and `.WithProcessLoopback` (process, the additive option), reusing the same
  span-based `WasapiRecorder` callback and the same MeetCap-owned `NAudioCaptureSource`
  adapter the microphone uses — no raw `ActivateAudioInterfaceAsync` / COM plumbing
  (docs/ARCHITECTURE.md section 2);
- `RecordingSession` orchestrates one `CaptureTrack` per source (mic for offline; mic +
  loopback for online). Each track owns its own capture callback, bounded queue, chunk
  spool, timeline and device-loss recovery, so one capture callback never waits for the
  other and a track that loses its device ends only itself while the other keeps
  recording (docs/RELIABILITY.md section 8);
- separate chunk trees persist under `audio/mic/` and `audio/loopback/`; the M4 batch
  builder already groups per source, so each track is independently transcribed through
  file ASR and `asr/batches/<source>/` keeps them separate;
- per-track health/degraded state is recorded as `track_health` in `session.json`, and
  `meetcap start` prints one line per track when the session ran more than one. The
  `session.stopped` event and the `asr batches:` summary stay session-wide totals with no
  source dimension, so per-track state is read from `session.json`, not from those lines
  (docs/DATA_MODEL.md section 3);
- `TranscriptMerger` merges the two tracks' segments onto one session-relative timeline
  ordered by `start_ms`, preserving `source` and overlapping speech rather than deleting
  it (docs/ARCHITECTURE.md section 16).

No SQLite schema change: the M1 `audio_chunks` `CHECK` already allowed `mic|loopback`, the
`sessions` `mode` `CHECK` already allowed `online`, and per-track health lives in
`session.json`. No new configuration key: `[capture.online]` was declared from M0 and is
now implemented.

Not implemented in this milestone, and deliberately out of scope:

- streaming ASR of any kind (M8);
- speaker identity matching / voiceprint (M6/#8);
- LLM correction or summary (M9);
- echo-duplicate detection across mic/loopback — the merger preserves overlapping speech
  and may mark probable echo duplicates later (docs/ARCHITECTURE.md section 16 step 6);
- advanced AEC; the `WasapiRecorderBuilder.WithEchoCancellationReferenceEndpoint` knob is
  left for a later milestone rather than turned on by default.

Real-world validation has not been performed: the dual-track behaviour is exercised in CI
against scripted mic + loopback capture sources, not against a real online meeting, a real
render endpoint, or real process loopback. The manual Windows checklist for M5 is
`docs/M1_WINDOWS_VALIDATION.md` section 13, and it has not been run (its rows need a real
machine with a render endpoint and, for the process row, a running meeting application).
The process-loopback path uses NAudio's `WithProcessLoopback`, so a real Windows/NAudio
environment is required to confirm it can target a meeting application's process tree
(`docs/DEVELOPMENT.md` section 7).

---

# M6 - Speaker registry and voiceprint matching

## Goal

Reduce repeated manual speaker labeling across meetings by resolving anonymous ASR speaker labels to persistent local identities.

## Default MVP pipeline

```text
Volcengine Seed-ASR 2.0
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

## Implementation status

Implemented (issue #8):

- `MeetCap.Core.Speakers`: speaker entity, embedding, candidate, assignment, attribution
  artifact, and the `ISpeakerIdentityProvider` / `ISpeakerStore` contracts. The
  `SpeakerMatchingPolicy` is pure domain logic that applies the normative priority
  `manual > high-confidence voiceprint > unknown` with configurable threshold and margin
  (docs/ARCHITECTURE.md section 17.3);
- `0004_speakers` migration (docs/DATA_MODEL.md section 14): creates the `speakers`,
  `speaker_embeddings`, and `speaker_assignments` tables with `CHECK` constraints on
  `source` and `active`, a `UNIQUE` on `display_name`, `ON DELETE CASCADE` for embeddings,
  and `ON DELETE SET NULL` for assignments, plus a `UNIQUE (session_id, speaker_label)`
  so upsert is idempotent;
- `MeetCap.Speakers`: the registry workflow (enrollment stores multiple embeddings per
  person), `CleanSampleSelector` (pure logic that groups adjacent segments into 5-15s
  clean ranges), `SpeakerAttributionService` (resolves each anonymous label through
  manual > voiceprint > unknown and produces attributed segments without modifying
  `raw_text`), and the per-session `attribution.json` artifact;
- `MeetCap.Speakers.SherpaOnnx`: the default local identity provider, backed by the
  sherpa-onnx .NET runtime and the 3D-Speaker ERes2Net-base ONNX model. The default
  Windows runtime does not require Python or PyTorch. A missing model is reported as a
  configuration error before any session state is touched (docs/ARCHITECTURE.md section 20);
- `meetcap speakers list`, `meetcap speakers enroll <name> --file <path>`,
  `meetcap speakers assign --session --label --name`, and
  `meetcap speakers attribute --session`: the CLI composition for enrollment, manual
  assignment (locked), and the attribution pipeline that writes `transcript/final.jsonl`,
  `transcript/final.md`, and `speakers/attribution.json`.

Automated coverage:

```text
tests/MeetCap.Core.Tests/Speakers/SpeakerMatchingPolicyTests.cs
tests/MeetCap.Persistence.Tests/Storage/SqliteSpeakerStoreTests.cs
tests/MeetCap.Persistence.Tests/Storage/SpeakerMigrationTests.cs
tests/MeetCap.Speakers.Tests/CosineSimilarityTests.cs
tests/MeetCap.Speakers.Tests/CleanSampleSelectorTests.cs
tests/MeetCap.Speakers.Tests/SpeakerRegistryTests.cs
tests/MeetCap.Speakers.Tests/SpeakerAttributionServiceTests.cs
tests/MeetCap.Cli.Tests/SpeakersCommandTests.cs
```

Not implemented in this milestone, and deliberately out of scope:

- real-world voiceprint validation with the actual 3D-Speaker model and real meeting audio
  (the CI environment has no model file, so the provider boundary is faked in tests;
  docs/DEVELOPMENT.md section 7);
- online-mode local-owner assumption / optional mic verification (the attribution service
  accepts a sample extractor delegate so this can be wired by the CLI composition root
  in a later iteration);
- cloud speaker registry (explicitly a non-goal);
- LLM correction or summary (M9);
- streaming ASR (M8).

---

# P1 - TOS large-file ASR transport (#29)

## Status

Planned on a separate branch; not part of the M0-M6 MVP release gate.

## Goal

Keep ordinary file ASR inline while giving oversized inputs a crash-safe, private object-storage
transport that follows the official Volcengine TOS .NET SDK contract.

## Deliverables

- fixed transport decision: <=20 MiB `audio.data`, >20 MiB TOS `audio.url`;
- official TOS .NET SDK for `PutObject(FileStream)`, presigned GET URL, and `DeleteObject`;
- private bucket/object only; no public-read fallback;
- optional `[asr.tos]` configuration with secret-resolver-backed AK/SK;
- randomized `meetcap-asr/` object keys;
- durable bucket/object-key state; never persist a presigned URL;
- new forward SQLite migration for TOS transport/cleanup state;
- idempotent terminal cleanup; cleanup failure does not fail a successful transcript;
- documented three-day lifecycle expiration safety net on the TOS prefix;
- contract tests with mocked TOS and a manual real-credential smoke-test checklist.

## Non-goals

- TOS for every ASR job;
- replacing local durable recordings;
- multipart upload;
- automatic provider-limit audio splitting;
- streaming ASR;
- automatic bucket/IAM/lifecycle provisioning.

## Exit criteria

A >20 MiB imported/ASR artifact can survive upload, process restart, fresh URL generation,
Seed-ASR submit/query, transcript persistence, and eventual TOS cleanup without exposing
credentials or signed URLs; a TOS outage does not stop live recording.

---
# M7 - ASR quality controls

## Goal

Improve recognition quality without changing recording reliability.

## Deliverables

- hotword-table configuration
- request-level hotwords if supported
- glossary file
- provider request tracing without secrets, including retained `X-Tt-Logid`
- Seed-ASR 2.0 Standard HTTP contract tests
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

As of release 0.1.0, M0 through M6 are implemented and automatically covered. The
real-hardware validation checklist in `docs/M1_WINDOWS_VALIDATION.md` (status: not yet run)
gates end-to-end verification, not implementation; milestones are therefore described as
*implemented and automatically covered*, not *verified end to end*
(`docs/DEVELOPMENT.md` section 7).

P0 issue #26 (Seed-ASR 2.0 + `X-Api-Key`-only provider contract) has landed, so the Volcengine
provider is production-aligned with the current official interface at the implementation level.
The manual real-credential smoke test in `docs/M1_WINDOWS_VALIDATION.md` is still open.

P1 issue #29 is a post-MVP large-file transport enhancement. It MUST NOT become a prerequisite
for ordinary 300-second live batches or block the M0-M6 release gate.

M7 may partially land before the release but MUST NOT delay core reliability unless it fixes real transcription quality.

M8 and M9 are explicitly post-MVP.
