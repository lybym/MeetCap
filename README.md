# MeetCap

## Optional large-file ASR transport

For normalized WAV inputs larger than 20 MiB, configure `[asr.tos]` with a dedicated private
bucket/prefix. MeetCap uses the official Volcengine TOS .NET SDK to stream the file, submits a
six-hour presigned GET URL to Seed-ASR, and never stores the signed URL or credentials. Grant only
`tos:PutObject`, `tos:GetObject`, and `tos:DeleteObject` on `meetcap-asr/*`; configure a three-day
lifecycle expiration safety net for that prefix. MeetCap never changes bucket policy, IAM, or lifecycle rules.

MeetCap is a Windows-resident CLI for **durable meeting audio capture, file-first ASR, speaker attribution, and continuously persisted transcripts**.

The design deliberately prioritizes:

1. **Never lose the recording.**
2. **Final transcription accuracy over real-time latency.**
3. **File ASR over streaming ASR whenever practical.**
4. **Local, inspectable artifacts over opaque application state.**
5. **Configuration-file-driven behavior.**
6. **CLI-first operation; no GUI in the MVP.**
7. **Reuse mature OSS for infrastructure; keep MeetCap-specific reliability semantics in MeetCap.**

## Supported meeting modes

MeetCap has exactly two recording modes in the first product scope:

- `offline`: in-person meeting/discussion, microphone only.
- `online`: online meeting, microphone + Windows loopback captured as separate tracks.

Hybrid meetings are explicitly out of scope.

Imported recordings remain a first-class workflow:

```powershell
meetcap import .\meeting.m4a --title "Project Review"
```

## Implemented so far

M0 through M6 are implemented and automatically covered by the test suite:

```powershell
meetcap devices                  # active capture and render endpoints, default and configured
meetcap start "Weekly Meeting" --mode offline
meetcap start "Remote Review" --mode online
meetcap stop                     # signals the running recording to finish
meetcap status                   # config, data root, database, incomplete sessions, ASR queue
meetcap session repair           # repair/audit sessions a killed process left behind
meetcap config init
meetcap config path
meetcap config validate
meetcap config show
meetcap import .\meeting.m4a --title "Project Review"   # inspect, normalize, file ASR, transcript
meetcap asr resume                                     # continue queued/retried ASR jobs
meetcap speakers list                                 # enrolled speakers
meetcap speakers enroll "Alice" --file .\alice.wav    # store voiceprint embeddings
meetcap speakers assign --session <id> --label <l> --name "Alice"  # locked manual override
meetcap speakers attribute --session <id>             # resolve anonymous labels to identities
```

Offline capture (M1) writes recoverable WAV chunks, indexes them in `audio_chunks`, and
crash-recovers unfinished sessions on the next start. M2 makes that reliability auditable:
the capture queue reports its bound, its peak backlog, the audio it had to drop and any
stalled downstream consumer; discontinuities are quantified in `session.json` rather than
only logged; and `meetcap session repair` re-repairs and audits a session, exiting non-zero
when the timeline still has a known gap instead of claiming success. An import (M3):

1. inspects the file with FFprobe and normalizes it with FFmpeg only when required;
2. copies the source into `sessions/<id>/audio/import/` (the original file is never modified);
3. creates a normal session with `source_type=import` and queues a persistent file-ASR job;
4. retains the raw provider response and writes `transcript/raw.jsonl` plus `transcript/live.md`.

M4 (file-first transcription during live recording) batches durably closed chunks per source
until the batch window is covered, queues a persistent file-ASR job, and drains it in the
background while the meeting runs. A network outage does not disturb capture; queued batches
complete after recovery, and `meetcap asr resume` is the restart entry point for the durable
job queue.

M5 (online dual-track capture) runs the microphone and system loopback as independent capture
tracks, each with its own queue, chunk spool, and device-loss recovery, and merges their
segments onto one session-relative timeline preserving both `source` and overlapping speech.

M6 (speaker registry and voiceprint matching) enrolls multiple voiceprint embeddings per
person, resolves anonymous ASR speaker labels through the normative priority
`manual > high-confidence voiceprint > unknown`, and writes `transcript/final.jsonl` and
`transcript/final.md` with attributed speaker names.

Provider speaker labels are preserved exactly as anonymous, session-scoped data. They are never
treated as persistent human identities.

Real Volcengine transcription is not verified by CI: no credentials are available there, so the
provider boundary is mocked in tests. The real 3D-Speaker/sherpa-onnx model is likewise not
exercised in CI.

The normative Volcengine provider contract is Seed-ASR 2.0 recording-file Standard HTTP,
new-console `X-Api-Key` authentication, and fixed resource ID `volc.seedasr.auc`. That
contract is implemented by the provider adapter: the only user-supplied provider setting is
`asr.volcengine.api_key`, and the legacy AppID/Access Token pair, the configurable resource id,
and the service-tier selector are rejected with a migration message rather than reinterpreted.
The provider's `X-Tt-Logid` is retained per job for support tracing.

Large-file transport is separately specified by issue #29. The target keeps files at or below
20 MiB on inline `audio.data`; larger files are temporarily staged in a private Volcengine TOS
object using the official TOS .NET SDK and submitted through an SDK-generated presigned
`audio.url`. TOS is optional for ordinary 300-second live batches, never replaces local durable
audio, and is not implemented on `main` until #29 lands.

The hardware-dependent acceptance tests are tracked as a
manual checklist in `docs/M1_WINDOWS_VALIDATION.md`; nothing here claims any milestone is
verified end to end on real audio hardware or real provider credentials yet.

## Planned CLI

Later milestones are deferred by product decision: M7 (ASR quality controls), M8 (optional
streaming ASR), and M9 (post-processing / meeting intelligence). The release gate for the MVP
requires M0 through M6, which this release closes at the implementation level
(see `docs/ROADMAP.md`).

## Documentation

- [PRD](docs/PRD.md)
- [Changelog](CHANGELOG.md)
- [Technical Architecture](docs/ARCHITECTURE.md)
- [Roadmap](docs/ROADMAP.md)
- [Configuration](docs/CONFIGURATION.md)
- [ASR Strategy](docs/ASR_STRATEGY.md)
- [Data Model & Artifact Contract](docs/DATA_MODEL.md)
- [Reliability Requirements](docs/RELIABILITY.md)
- [Development Rules](docs/DEVELOPMENT.md)
- [M1 Windows validation checklist](docs/M1_WINDOWS_VALIDATION.md)

## Technology direction

- C# 14
- .NET 10 LTS
- Windows 10 22H2 / Windows 11
- NAudio 3 / WASAPI for microphone, system loopback, and supported process loopback capture
- System.CommandLine for CLI composition
- Tomlyn for TOML configuration
- Serilog for structured logging
- Microsoft.Data.Sqlite for local persistence
- FluentMigrator or an equivalently thin migration layer
- FFmpeg/FFprobe through FFMpegCore for media inspection and normalization
- Polly for transient provider HTTP resilience
- Volcengine Seed-ASR 2.0 recording-file **Standard HTTP** ASR for transcription and anonymous speaker labels; new-console `X-Api-Key` authentication only
- Volcengine TOS .NET SDK as the optional >20 MiB ASR transport target in issue #29 (private object + presigned GET URL; not the local recording store)
- sherpa-onnx + 3D-Speaker ERes2Net-base for local speaker embeddings and persistent identity matching
- JSONL + Markdown artifacts

The default MVP speaker path is:

```text
Volcengine Seed-ASR 2.0
  -> transcript + timestamps + anonymous speaker labels
  -> select clean speech for each anonymous speaker
  -> sherpa-onnx
  -> 3D-Speaker ERes2Net-base
  -> local Speaker Registry
  -> persistent speaker identity
```

sherpa-onnx is not the default MVP diarization engine. Local diarization remains an optional fallback/extension if real recordings demonstrate that provider diarization is insufficient.

MeetCap intentionally keeps the following as product-owned logic instead of outsourcing them to generic frameworks:

- durable audio spool and crash recovery;
- persistent ASR job state machine;
- transcript normalization and dual-track timeline merge;
- speaker registry semantics and manual-lock policy;
- artifact contract and local storage layout.

See `docs/ARCHITECTURE.md` for the authoritative architecture.

## Repository status

M0 through M6 are implemented and automatically covered. Code is added milestone by
milestone according to `docs/ROADMAP.md`; agents should not implement later milestones
opportunistically.

This is release **0.1.0**. The hardware-dependent acceptance tests are still open and are
tracked as a manual checklist in `docs/M1_WINDOWS_VALIDATION.md`; nothing here claims any
milestone is verified end to end on real audio hardware yet. See `CHANGELOG.md` for the
release notes and `docs/ROADMAP.md` for milestone status.
