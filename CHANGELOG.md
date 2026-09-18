# Changelog

All notable changes to MeetCap are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

MeetCap distinguishes between *implemented and automatically covered* (exercised
by the automated test suite against scripted boundaries) and *verified end to end*
(exercised on real Windows hardware with real provider credentials). Until the
manual checklist in `docs/M1_WINDOWS_VALIDATION.md` has been run on real hardware,
milestones are described as the former, never the latter
(`docs/DEVELOPMENT.md` section 7).

## [0.1.0] - 2026-09-18

The first MVP-track release. Milestones M0 through M6 are implemented and
automatically covered. This release does **not** claim end-to-end verification on
real hardware; see *Known limitations* below.

### Implemented milestones

- **M0 — Repository and executable skeleton.** .NET 10 solution and project
  layout, `System.CommandLine` CLI entry point, Serilog structured logging,
  Tomlyn-backed configuration loader for `%APPDATA%\MeetCap\config.toml`,
  Microsoft.Data.Sqlite bootstrap, and SQL migrations applied on first run.
  Commands: `meetcap config init`, `meetcap config validate`, `meetcap config
  show`, `meetcap config path`, `meetcap status`.

- **M1 — Offline microphone capture.** NAudio 3 / WASAPI microphone capture
  behind `MeetCap.WindowsAudio`, 60-second recoverable WAV chunks with a `.part`
  lifecycle, chunk validation, crash-startup scan, device enumeration, and
  `meetcap start --mode offline` / `meetcap stop`. `meetcap devices` lists
  active capture endpoints.

- **M2 — Durable spool and recovery hardening.** Bounded capture buffers with
  backpressure metrics (`CaptureBacklogMonitor`), explicit gap accounting
  (`CaptureTimeline` gap events), a durable gap audit (`SessionGapAuditor`),
  recovery honesty (startup audit writes `capture.gap` events for holes the live
  recording never saw and refuses to claim success when a gap remains), and
  `meetcap session repair` which exits non-zero when a known gap remains. CLI
  reporting of buffer peaks, drops, stalls, and audio gaps on `meetcap start`
  and `meetcap status`.

- **M3 — Recording import and file ASR.** `meetcap import <file> [--title]`
  inspects media with FFprobe and normalizes with FFmpeg only when required,
  copies the original (never modified), queues a persistent Volcengine file-ASR
  job, and writes `transcript/raw.jsonl` plus `transcript/live.md`. Polly-based
  transient HTTP resilience inside the provider adapter; persistent retry/job
  state independent from Polly. `meetcap asr resume` is the restart entry point
  for the durable job queue. Provider-returned anonymous speaker labels are
  preserved as session-scoped data, never as persistent human identities.

- **M4 — File-first transcription during live recording.** `AsrBatchBuilder`
  groups durably closed chunks per source until `asr.file_batch_seconds` (default
  300 s) is covered, materializes a real batch WAV, and queues the persistent
  job. `MeetCap.Asr.LiveTranscription` submits and polls while the meeting runs
  plus the stop-time flush/drain. A 30-minute network outage does not disturb
  capture; queued batches complete after recovery; jobs sit in durable
  `retry_wait` rather than burning their attempt budget. `meetcap asr resume
  --force` processes jobs whose retry backoff has not come due. `meetcap status`
  exposes ASR queue depth and degraded transcription state. CI exercises the
  two-hour meeting and thirty-minute outage exit criteria against a scripted
  capture source and a scripted provider transport.

- **M5 — Online meeting dual-track capture.** `meetcap start --mode online`
  runs microphone and system loopback as independent capture tracks, each with
  its own capture callback, bounded queue, chunk spool, timeline, and
  device-loss recovery; one track ending does not stop the other. Separate
  chunk trees persist under `audio/mic/` and `audio/loopback/`; per-track health
  is recorded as `track_health` in `session.json`. `TranscriptMerger` merges the
  two tracks' segments onto one session-relative timeline ordered by `start_ms`,
  preserving `source` and overlapping speech. Process loopback is supported as an
  additive `loopback_mode` option where the Windows/NAudio environment allows it.

- **M6 — Speaker registry and voiceprint matching.** Speaker entity, embedding,
  candidate, assignment, and attribution artifacts in `MeetCap.Core.Speakers`;
  `SpeakerMatchingPolicy` applies the normative priority
  `manual > high-confidence voiceprint > unknown` with configurable threshold and
  margin. `MeetCap.Speakers` provides enrollment (multiple embeddings per
  person), `CleanSampleSelector`, and `SpeakerAttributionService` which resolves
  anonymous labels and produces attributed segments without modifying `raw_text`.
  `MeetCap.Speakers.SherpaOnnx` is the default local identity provider, backed by
  the sherpa-onnx .NET runtime and the 3D-Speaker ERes2Net-base ONNX model; the
  default Windows runtime requires neither Python nor PyTorch. Commands:
  `meetcap speakers list`, `meetcap speakers enroll <name> --file <path>`,
  `meetcap speakers assign --session --label --name`,
  `meetcap speakers attribute --session` (writes `transcript/final.jsonl`,
  `transcript/final.md`, and `speakers/attribution.json`).

### CLI surface in this release

```powershell
meetcap devices
meetcap start "Title" --mode offline|online
meetcap stop
meetcap status
meetcap session repair
meetcap config init
meetcap config validate
meetcap config show
meetcap config path
meetcap import .\meeting.m4a --title "Title"
meetcap asr resume [--session <id>] [--max-jobs <n>] [--force]
meetcap speakers list
meetcap speakers enroll <name> --file <path>
meetcap speakers assign --session <id> --label <l> --name <n>
meetcap speakers attribute --session <id>
```

### Database migrations

SQLite schema is created and versioned by SQL migrations applied on first run by
`MeetCap.Persistence.Storage.SqliteMigrator`:

- `0001_sessions.sql` — `sessions` table (M0).
- `0002_audio_chunks.sql` — `audio_chunks` table (M1).
- `0003_asr_jobs.sql` — `asr_jobs` table (M3).
- `0004_speakers.sql` — `speakers`, `speaker_embeddings`, `speaker_assignments` (M6).

There is no previous stable release; 0.1.0 is the first release, so there is no
upgrade migration path. The database lives under the configured data root
(default `%LOCALAPPDATA%\MeetCap\meetcap.db`).

### Known limitations

- **No real-hardware validation.** Every milestone above is *implemented and
  automatically covered*, not *verified end to end*. The manual Windows checklist
  in `docs/M1_WINDOWS_VALIDATION.md` (2-hour offline/online soaks, 30-minute real
  network outage, forced-kill recovery, real Volcengine transcription, real
  3D-Speaker/sherpa-onnx model) has status *not yet run*.

- **No real Volcengine credentials exercised.** The provider boundary is mocked
  in the automated suite; real transcription has not been performed in CI.

- **No real 3D-Speaker model exercised.** The speaker-identity provider boundary
  is faked in tests; the CI environment has no model file.

- **MVP umbrella issue ([#1](https://github.com/lybym/MeetCap/issues/1))
  remains OPEN.** By its own definition, the MVP is *complete only when M0-M6 are
  finished and validated on real Windows hardware*; this release closes the
  implementation, not the validation.

- **Architecture-baseline issue ([#9](https://github.com/lybym/MeetCap/issues/9))
  remains OPEN by design.** It is cross-cutting and milestone-gated, deliberately
  referenced as "Refs" rather than "Closes" by feature work.

- **Two pre-existing CLI tests** (`UnknownVerb`, `GlobalDataRootOption`) fail
  under a `zh-CN` UI culture but pass under `en-US`; CI runs on
  `windows-latest` with `en-US` and is unaffected.

### CLI distribution and signing

This is a source-build release. There is no published binary package, installer,
or code-signed artifact in this release; `meetcap` is built from source with
`dotnet build MeetCap.slnx -c Release`. No code signing is performed.
