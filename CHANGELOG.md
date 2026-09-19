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

## [Unreleased]

### Fixed

- **Windows process loopback no longer reports a false discontinuity for every buffer**
  ([#33](https://github.com/lybym/MeetCap/issues/33)). Process loopback captures a process tree
  rather than an endpoint's own stream, and on the machine that reproduced this the audio engine
  reported `device_position_frames = 0` for every one of 1797 buffers while the QPC timestamp
  advanced by exactly 10 ms per buffer. The track was placed by device position, so every buffer
  after the first read as a backwards jump: `events.jsonl` held one `capture.discontinuity`
  ("device position moved backwards") per buffer and the loopback track was reported
  `degraded (unknown)` even though its audio was fine. A capture source now declares the device
  timing it actually provides (`IAudioCaptureSource.Clock`, `CaptureClock.DevicePosition` or
  `CaptureClock.Qpc`), and `CaptureTimeline` is built with that clock: process loopback is placed
  by QPC, which reproduces the system-loopback baseline's timeline for the same audio
  (`docs/ARCHITECTURE.md` section 8.1). Session logs now record the clock per track in the
  `session.started` detail.

### Added

- **`capture.timeline_unusable`** session event: a track whose stream supplies neither a usable
  device position nor a QPC timestamp has no device timing to be placed by, so it ends with this
  one explicit event — carrying the reason and the baseline alternative — rather than writing
  audio at a wall-clock-derived position or silently degrading. The microphone track keeps
  recording and the failed track's already-captured chunk stays durable
  (`docs/ARCHITECTURE.md` section 8.1, `docs/DATA_MODEL.md` section 4).
- **Process-loopback failures name what to do next.** A process-loopback activation failure now
  says that the machine needs a Windows/NAudio combination supporting process loopback plus a
  running target process, and that the baseline `capture.online.loopback_mode = "system"` is
  still available.

### Notes

- Real drops stay observable on a process-loopback track: a stretch of missing QPC time is one
  `capture.gap` carrying the missing milliseconds, and a single missing 10 ms buffer is reported
  as 10 ms of missing audio rather than absorbed by the timeline.
- The residual Windows/NAudio limitation cannot be fixed in code and is documented rather than
  implied: QPC is a time, so a process-loopback timeline is resolved to the millisecond instead
  of to the exact frame (`docs/ARCHITECTURE.md` section 8.1).
- No persisted schema change: `track_health` in `session.json` and the `events.jsonl` vocabulary
  are extended by values, not by columns, so no migration is added.
- The manual M5 process-loopback checklist (`docs/M1_WINDOWS_VALIDATION.md` section 13.5) gained
  rows for this behaviour and is still **not run**; the automated coverage is
  `tests/MeetCap.Core.Tests/Capture/CaptureTimelineTests.cs`,
  `tests/MeetCap.AudioPipeline.Tests/ProcessLoopbackTimelineTests.cs` and
  `tests/MeetCap.WindowsAudio.Tests/NAudioLoopbackClockTests.cs`.

## [0.2.0] - 2026-09-19

The second MVP-track release. It adds one post-MVP transport feature and one
provider-contract realignment on top of 0.1.0. No milestone becomes newly
complete in this release: M0 through M6 remain *implemented and automatically
covered*, not *verified end to end*, and the MVP umbrella issue
([#1](https://github.com/lybym/MeetCap/issues/1)) and the architecture-baseline
issue ([#9](https://github.com/lybym/MeetCap/issues/9)) remain OPEN by design.

### Changed

- **Volcengine file ASR migrated to Seed-ASR 2.0 with `X-Api-Key`-only authentication**
  ([#26](https://github.com/lybym/MeetCap/issues/26)). The provider adapter now speaks one fixed
  contract: Seed-ASR 2.0 recording-file Standard HTTP, `POST
  /api/v3/auc/bigmodel/submit` then `POST /api/v3/auc/bigmodel/query`, `X-Api-Key`
  authentication, and the fixed resource id `volc.seedasr.auc`. Requests never carry
  `X-Api-App-Key` or `X-Api-Access-Key`, and the submit still identifies itself with
  `X-Api-Sequence: -1` while the query sends the same request id with no sequence header.

### Breaking changes

- **Provider configuration is reduced to `asr.volcengine.api_key`.** The keys
  `asr.service_tier`, `asr.volcengine.app_id`, `asr.volcengine.credential` and
  `asr.volcengine.resource_id` were removed. They are no longer schema keys, so
  `meetcap config validate` (and any command that loads configuration) fails with an
  actionable migration message instead of silently applying new defaults. Existing
  `config.toml` files must be edited:

  ```toml
  [asr.volcengine]
  api_key = "env:MEETCAP_VOLCENGINE_API_KEY"
  ```

- **`meetcap import --tier` was removed**, along with the `standard|idle|turbo` service-tier
  selector. MeetCap supports exactly one recording-file profile; idle and flash/turbo are not
  fallbacks or compatibility modes, and a Standard request is never silently routed to them.

### Added

- **`asr_jobs.provider_log_id`** (migration `0005_asr_job_provider_log_id`) retains the
  provider's `X-Tt-Logid` for support tracing. It is recorded as soon as submit answers,
  replaced by the query's log id on completion, and also written to the `asr.job.submitted` /
  `asr.job.completed` events and to provider error messages. The API key is never persisted.
  The migration rebuilds `asr_jobs` and copies every existing row across unchanged — including
  on a re-run, where it now keeps any `provider_log_id` written since the first run instead of
  resetting it.
- **TOS large-file ASR transport** ([#29](https://github.com/lybym/MeetCap/issues/29)).
  Normalized WAV inputs above 20 MiB are streamed to a private Volcengine TOS object with the
  official TOS .NET SDK and submitted to Seed-ASR as an SDK-generated six-hour presigned
  `audio.url`; inputs at or below 20 MiB keep using inline `audio.data`. A new
  `IAsrAudioPublisher` boundary keeps the storage transport out of `VolcengineAsrProvider`, and
  `VolcengineTosAudioPublisher` owns the only three TOS operations — streamed `PutObject`,
  `PreSignedURL`, idempotent `DeleteObject`. Object keys are
  `meetcap-asr/<stable 16-hex shard>/<yyyy>/<MM>/<dd>/<job>.wav`, and no ACL is set, so the
  object stays private. Optional `[asr.tos]` configuration (bucket, region, endpoint,
  `env:`-resolvable access_key/secret_key) enables the path; an empty section is valid, and a
  partly filled one is rejected before any session exists. Terminal jobs attempt an idempotent
  delete, and a delete failure stays recorded retryable cleanup debt rather than failing the
  transcript.
- **`asr_jobs` transport identity** (migration `0006_tos_asr_transport`): `audio_transport`
  (`inline`/`tos`), `tos_bucket`, `tos_object_key` and `tos_cleanup_pending`. The identity is
  committed before the submission counts as accepted and the presigned URL is never stored, so
  a restart can re-sign the URL from stable state and can finish the cleanup it still owes.
- **`asr.audio.released` / `asr.audio.release_failed`** session events make remote-object cleanup
  observable in `events.jsonl`, and a cleanup failure never changes a job's outcome.
- **TOS credentials are registered with `SecretRedactor`** alongside the Volcengine API key, so
  `meetcap config show`, the console log and `session.import.json` all redact them.

### Fixed

- **Migration `0005_asr_job_provider_log_id` no longer discards `provider_log_id` when it runs a
  second time.** The script is re-runnable by contract, because a second process can read
  `schema_migrations` before the version row is committed and then run it too; its rebuild
  copied every column except the one it had just added, so values written in between were
  silently reset to `NULL`. The copy now reads the column through a schema-conditional
  placeholder that expands to the column when the schema has it and to `NULL` when it does not,
  so a re-run preserves the stored log ids. `SqliteMigrator` applies each migration in an
  immediate transaction so that placeholder is resolved against the schema the script actually
  sees.
- **A replay of migration `0005` no longer destroys the columns migration `0006` added.**
  `DROP TABLE` removes columns it has never heard of, and 0005's rebuild recreated `asr_jobs` from
  the 0003-plus-`provider_log_id` shape only. Because the migrator's re-runnability contract
  allows a migration to be replayed at any later time, that rebuild dropped `audio_transport`,
  `tos_bucket`, `tos_object_key` and `tos_cleanup_pending`, and the next
  `SqliteAsrJobStore.Get` failed with `no such column: audio_transport` — which is how CI caught
  it. Both 0005 and 0006 now declare the full post-#26 schema and copy the columns they do not own
  through the same schema-conditional placeholder, so whichever one replays the result is the same
  table with the same values. Migration `0006` is also re-runnable now: it previously used bare
  `ALTER TABLE ... ADD COLUMN`, which fails with `duplicate column name` on a replay and would
  have aborted whichever process lost the race.
- **The session configuration snapshot no longer stores the API key, in any serialized form.**
  `sessions.config_snapshot` is persisted, and it was serialized from the effective configuration
  verbatim, so a literal `asr.volcengine.api_key` was written to SQLite even though the API key is
  never persisted (`docs/CONFIGURATION.md` rule 7). Every value is now redacted through the same
  `SecretRedactor` as `config show` *before* it is serialized: replacing the key in the finished
  JSON instead would miss a key containing a character the writer escapes (`+`, `"`, `\` or any
  non-ASCII character), which lands in the snapshot as `\uXXXX` and is decoded by any JSON parser.

### Notes

- `asr_jobs.tier` is retained as a schema-compatibility column written as the constant
  `standard`; it is no longer read as a routing input.
- The batch manifest (`asr/batches/<source>/batch-NNNNNN.json`) no longer records `tier`. A
  manifest written before this change is still readable: the extra field is ignored.
- Cleanup debt is deliberately not outstanding work: `CountOutstanding` and the resumable-job
  query ignore `tos_cleanup_pending`, so a finished session whose temporary object still has to be
  deleted is not reported as unfinished.
- **Known residual risk:** an object staged by a process that dies between the upload and the job
  row recording its identity is referenced by no job, so MeetCap cannot find it to delete it. The
  three-day lifecycle expiration on the `meetcap-asr/` prefix is the documented mitigation and
  remains a deployment requirement (`docs/CONFIGURATION.md` section 8.1,
  `docs/RELIABILITY.md` section 9.1).
- The manual real-credential smoke test against Seed-ASR 2.0 Standard HTTP is **not run** in this
  environment (`docs/M1_WINDOWS_VALIDATION.md` section 12.8), so end-to-end provider verification
  is still not claimed.
- The manual real-TOS plus real-Seed-ASR smoke test for the >20 MiB path is likewise **not run**
  (`docs/M1_WINDOWS_VALIDATION.md` section 15); CI covers that path with mocks and fakes only.

### Database migrations

Two forward migrations are added since 0.1.0. Both are applied automatically on first run; there is
no manual migration step.

- `0005_asr_job_provider_log_id.sql` — adds `asr_jobs.provider_log_id`. It rebuilds `asr_jobs` and
  copies every existing row unchanged, and it is re-runnable: a replay now preserves any
  `provider_log_id` written since the first run instead of resetting it to `NULL`.
- `0006_tos_asr_transport.sql` — adds `asr_jobs.audio_transport`, `tos_bucket`, `tos_object_key` and
  `tos_cleanup_pending`. It is also re-runnable (`ADD COLUMN` guarded by a schema check), because a
  second process can read `schema_migrations` before the version row is committed and run the script
  too.

Upgrading from 0.1.0 therefore needs no manual SQL, but it **does** need a configuration edit: see
*Breaking changes* above. A 0005 replay previously dropped the 0006 columns and broke
`meetcap asr resume` with `no such column: audio_transport`; both rebuilds now declare the full
post-#26 column set, and regression tests replay each migration, both in sequence, and the upgrade
path from a pre-#29 data root.

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
