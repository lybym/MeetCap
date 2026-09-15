# MeetCap

MeetCap is a Windows-resident CLI for **durable meeting audio capture, file-first ASR, speaker attribution, and continuously persisted transcripts**.

The design deliberately prioritizes:

1. **Never lose the recording.**
2. **Final transcription accuracy over real-time latency.**
3. **File ASR over streaming ASR whenever practical.**
4. **Local, inspectable artifacts over opaque application state.**
5. **Configuration-file-driven behavior.**
6. **CLI-first operation; no GUI in the MVP.**

## Supported meeting modes

MeetCap has exactly two recording modes in the first product scope:

- `offline`: in-person meeting/discussion, microphone only.
- `online`: online meeting, microphone + Windows loopback captured as separate tracks.

Hybrid meetings are explicitly out of scope.

Imported recordings remain a first-class workflow:

```powershell
meetcap import .\meeting.m4a --title "Project Review"
```

## Planned CLI

```powershell
meetcap start "Weekly Meeting" --mode offline
meetcap start "Remote Review" --mode online
meetcap stop
meetcap status
meetcap import .\recording.m4a
meetcap devices
meetcap speakers list
meetcap config path
meetcap config validate
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

## Technology direction

- C# 14
- .NET 10 LTS
- Windows 10 22H2 / Windows 11
- NAudio / WASAPI
- SQLite
- FFmpeg
- Volcengine Seed-ASR / recording-file ASR
- Local speaker-embedding provider behind an abstraction
- JSONL + Markdown artifacts

See `docs/ARCHITECTURE.md` for the authoritative architecture.

## Repository status

This repository starts documentation-first. Code should be added milestone by milestone according to `docs/ROADMAP.md`; agents should not implement later milestones opportunistically.
