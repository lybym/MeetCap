# MeetCap

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

## CLI

Implemented:

```powershell
meetcap devices                  # active capture endpoints, default and configured
meetcap start "Weekly Meeting" --mode offline
meetcap stop                     # signals the running recording to finish
meetcap status                   # config, data root, database, incomplete sessions
meetcap config init
meetcap config path
meetcap config validate
meetcap config show
```

Planned:

```powershell
meetcap start "Remote Review" --mode online
meetcap import .\recording.m4a
meetcap speakers list
```

## Documentation

- [PRD](docs/PRD.md)
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
- Volcengine BigASR / recording-file ASR for transcription and anonymous speaker labels
- sherpa-onnx + 3D-Speaker ERes2Net-base for local speaker embeddings and persistent identity matching
- JSONL + Markdown artifacts

The default MVP speaker path is:

```text
Volcengine BigASR
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

This repository starts documentation-first. Code is added milestone by milestone
according to `docs/ROADMAP.md`; agents should not implement later milestones
opportunistically.

M0 (repository and executable skeleton) and M1 (offline microphone capture) are
implemented. M1's hardware-dependent acceptance tests are still open and are tracked as
a manual checklist in `docs/M1_WINDOWS_VALIDATION.md`; nothing here claims M1 is verified
end to end on real audio hardware yet.
