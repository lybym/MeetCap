# MeetCap MVP PRD

**Version:** 0.3  
**Status:** Product baseline  
**Platform:** Windows  
**Product form:** Resident CLI  
**Primary objective:** Reliable meeting capture and accurate post/near-live transcription

---

## 1. Problem

Existing meeting-transcription SaaS products create three recurring problems:

1. Usage quota and cost are controlled by a SaaS package instead of transparent API consumption.
2. Speaker labels are temporary and must repeatedly be mapped to real people.
3. Company names, project names, product names, abbreviations, and other domain terms frequently need a second correction pass.

MeetCap turns the workflow into a local-first pipeline that the user owns:

```text
Windows audio
  -> durable local recording
  -> file-first ASR
  -> anonymous speaker labels
  -> local speaker identity matching
  -> persistent transcript files
  -> optional terminology correction / meeting analysis later
```

---

## 2. Product principles

Priority order is normative:

```text
P0  Recording integrity
P1  Recoverable local artifacts
P2  Final transcription accuracy
P3  Cost efficiency
P4  Speaker attribution
P5  Near-live transcript freshness
P6  Additional AI analysis
```

If two requirements conflict, the higher priority wins.

### Hard rules

1. Network/API failures MUST NOT stop recording.
2. ASR MUST NOT run on the audio-capture callback/thread.
3. The system MUST preserve raw audio and raw ASR results.
4. File ASR is the default ASR path.
5. Streaming ASR is optional and non-default.
6. Configuration is persisted in a human-editable configuration file.
7. No GUI is required for MVP.
8. No hybrid-meeting mode exists in the first product scope.
9. Mature OSS SHOULD be reused for commodity capabilities when it does not compromise MeetCap reliability semantics.
10. MeetCap-specific durability, job-state, timeline, artifact, and speaker-registry semantics remain explicit product-owned logic.

---

## 3. Supported workflows

### 3.1 Offline meeting

Definition:

> The user and other participants are physically in the same room.

Capture:

```text
Microphone
    |
    v
durable audio chunks
    |
    v
Volcengine file ASR
    |
    +--> transcript + timestamps
    +--> anonymous speaker labels
    |
    v
local speaker identity matching
```

Only the microphone track is captured.

### 3.2 Online meeting

Definition:

> The user attends a remote meeting on the Windows computer; remote participants are played through the computer.

Capture:

```text
Microphone ----------------> local track
Windows loopback ----------> remote track
```

The tracks MUST remain separate and MUST NOT be mixed before ASR.

Semantic assumptions:

- `mic` is primarily the local user.
- `loopback` contains remote participants.
- remote participants receive provider anonymous speaker labels and are later matched against the local speaker registry.

System loopback is sufficient for the first online-capture milestone. Process-specific loopback is a capture-source option, not a separate product mode.

### 3.3 Imported recording

The user can process an already-existing audio/video recording without recording a live session.

Example:

```powershell
meetcap import .\meeting.m4a --title "AI Toy Review"
```

The imported file enters the same post-capture pipeline:

```text
import
 -> inspect / normalize
 -> file ASR
 -> anonymous speaker labels
 -> local speaker identity matching
 -> transcript artifacts
```

This function is first-class and MUST remain available even after live capture is implemented.

---

## 4. Explicitly unsupported workflow

### Hybrid meeting

Hybrid means local participants in a room plus remote participants in the same session.

It is out of scope because it introduces acoustic echo ambiguity, local/remote duplicate speech, source-attribution complexity, speaker identity conflicts across tracks, and stronger AEC requirements.

A session has exactly one mode:

```text
offline
online
import
```

Mode cannot change after the session starts.

---

## 5. Resident CLI behavior

MeetCap should be able to remain running on Windows, but **resident does not mean recording 24/7**.

The durable recording session begins only after an explicit start/import action.

Expected commands:

```powershell
meetcap start "Weekly Meeting" --mode offline
meetcap start "Remote Review" --mode online
meetcap stop
meetcap status

meetcap import .\recording.m4a --title "Imported Meeting"

meetcap sessions list
meetcap sessions show <session-id>

meetcap devices
meetcap speakers list
meetcap speakers enroll "Alice"
meetcap speakers assign <session-id> <speaker-label> "Alice"

meetcap config init
meetcap config path
meetcap config validate
```

---

## 6. File-first ASR requirement

The user does not require second-level real-time subtitles.

Therefore the default live-session strategy is:

```text
audio capture
 -> small durable local chunks
 -> aggregate a configurable file-ASR batch
 -> submit file ASR
 -> append transcript when the batch returns
```

Default design values:

```text
capture chunk:      60 seconds
file-ASR batch:    300 seconds
```

Result:

- recording is committed locally every minute;
- transcript can update approximately every few minutes;
- ASR uses the cheaper non-real-time path;
- network outages create a pending queue instead of stopping capture.

Streaming ASR MUST NOT be an automatic fallback for failed file ASR.

If file ASR fails:

```text
queue
 -> retry
 -> remain pending
```

---

## 7. File-ASR service tiers

Provider abstraction should support the following policy names:

```text
standard
idle
turbo
streaming
```

Product defaults:

```text
live recording:  standard file ASR
import:          standard file ASR
economy import:  idle file ASR, if enabled by configuration
streaming:       disabled
```

The implementation MUST isolate provider-specific request fields behind an ASR provider interface.

Volcengine BigASR is the default provider and SHOULD request anonymous speaker information where the selected API/service tier supports it.

---

## 8. Recording requirements

### 8.1 Recording is the source of truth

Cloud transcription is derivative.

A session is considered valuable even when every cloud API call fails, provided the audio was captured correctly.

### 8.2 Separate tracks

Online mode:

```text
audio/
  mic/
  loopback/
```

Offline mode:

```text
audio/
  mic/
```

Do not generate a mixed master track as a required artifact.

### 8.3 Durable chunks

Audio MUST be written in recoverable chunks rather than one multi-hour file.

Default:

```text
60 s per capture chunk
```

The current open chunk may use a `.part` suffix and MUST be recoverable or safely discardable without damaging previously closed chunks.

---

## 9. Speaker requirements

There are two separate concepts.

### Speaker diarization

Question:

> Which speech segments belong to the same voice in this recording?

Result:

```text
speaker_0
speaker_1
speaker_2
```

For the MVP, Volcengine ASR-provided anonymous speaker labels are the default diarization source when available.

### Speaker identification

Question:

> Who is speaker_1?

Result:

```text
speaker_1 -> Alice
```

This requires speaker embeddings and a persistent local speaker registry.

### MVP identity stack

```text
Volcengine anonymous speaker labels
 -> choose clean speaker samples
 -> sherpa-onnx
 -> 3D-Speaker ERes2Net-base
 -> local Speaker Registry
 -> ranked candidate identities
```

The default model baseline is:

```text
3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx
```

sherpa-onnx + 3D-Speaker is used for:

- speaker enrollment;
- embedding extraction;
- 1:1 verification;
- 1:N identification.

It is **not** the default MVP diarization engine.

A local diarization provider may be added later if real recordings demonstrate that provider diarization is inadequate.

### Registry policy

The model does not know human names. MeetCap owns:

- speaker entities;
- multiple embeddings per person;
- candidate ranking;
- thresholds and match margins;
- manual confirmation;
- manual assignment locks;
- local persistence.

Priority:

```text
manual assignment
> high-confidence historical match
> temporary ASR speaker label
```

The model MUST NOT silently overwrite a manual assignment.

Unknown/low-confidence matches remain unknown.

Voiceprints and name mappings are sensitive local data and remain local by default.

---

## 10. Configuration requirement

All behavior that a user is expected to tune MUST be represented in a configuration file.

Canonical location:

```text
%APPDATA%\MeetCap\config.toml
```

Examples:

- default meeting mode;
- input device;
- loopback mode / process preference;
- capture chunk duration;
- ASR batch duration;
- ASR service tier;
- retry policy;
- hotword table identifier;
- output root;
- speaker identity provider;
- speaker model path;
- speaker thresholds and match margin;
- retention policy;
- logging level.

CLI configuration commands may edit/validate the file, but there must be no hidden settings database that overrides it.

Secrets may be referenced from Windows Credential Manager or environment variables rather than stored as plaintext.

See `CONFIGURATION.md`.

---

## 11. Transcript requirements

The system generates both machine-readable and human-readable files.

### Machine-readable

```text
transcript/raw.jsonl
transcript/final.jsonl
```

### Human-readable

```text
transcript/live.md
transcript/final.md
```

JSONL is the primary integration surface for agents.

Markdown is the primary reading surface for humans.

Example JSONL:

```json
{"start_ms":12340,"end_ms":16420,"source":"loopback","speaker_label":"speaker_1","speaker_name":"Alice","text":"We need to check the final quotation."}
```

`speaker_label` is anonymous/session-scoped. `speaker_name` is a persistent local identity resolved later and may remain null.

---

## 12. MVP non-goals

The following are explicitly out of scope:

- GUI / tray application
- web dashboard
- mobile app
- hybrid meetings
- meeting bots
- automatic joining of Teams/Feishu/Tencent Meeting
- calendar integration
- always-on hidden recording
- real-time subtitles as a primary feature
- streaming ASR by default
- automatic email or IM distribution
- cross-meeting RAG
- meeting summarization as a blocking part of capture
- multi-user SaaS
- organization SSO
- complex RBAC
- mandatory Python/PyTorch runtime
- mandatory local diarization pipeline

---

## 13. OSS reuse policy

Preferred infrastructure choices for MVP implementation:

```text
System.CommandLine
Tomlyn
Serilog
NAudio 3
Microsoft.Data.Sqlite
FluentMigrator or thin equivalent
FFMpegCore + FFmpeg/FFprobe
Polly
sherpa-onnx
3D-Speaker ERes2Net-base
```

MeetCap should not reimplement these commodity capabilities without a demonstrated requirement.

MeetCap-owned domain logic includes:

```text
Durable audio spool
Crash recovery
Persistent ASR job state machine
Transcript normalization
Dual-track timeline merge
Speaker Registry semantics
Manual speaker-lock policy
Artifact contract
```

---

## 14. MVP success criteria

### Capture

- 2-hour offline recording test completes without unexplained audio loss.
- 2-hour online recording test captures microphone and loopback independently.
- Closing the network connection for 30 minutes does not interrupt local recording.
- Previously closed chunks remain valid after forced process termination.

### ASR

- Imported audio can be transcribed through file ASR.
- Live recording batches are queued and transcribed using file ASR.
- Failed jobs persist and retry after restart/network recovery.
- Raw provider response is saved.
- Anonymous speaker labels/timestamps are preserved where available.

### Speaker

- A user can enroll a named speaker with multiple local embeddings.
- Later sessions can return useful ranked candidates for known speakers.
- A user can manually bind an anonymous `speaker_N` to a person.
- Manual bindings are never overwritten automatically.
- Low-confidence voices remain unknown.

### Artifacts

A completed session contains enough local data to re-run later processing without repeating the original recording.

---

## 15. Definition of MVP complete

MVP is complete when this flow works reliably:

```text
meetcap start "Weekly Meeting" --mode offline
    |
    | 2-hour discussion
    v
durable 60 s local audio chunks
    |
    v
5-minute Volcengine file-ASR batches
    |
    +--> transcript + timestamps
    +--> anonymous speaker labels
    |
    v
sherpa-onnx + 3D-Speaker identity matching
    |
    v
transcript/final.md
```

And separately:

```text
meetcap import .\existing-meeting.m4a
    |
    v
file ASR
    |
    v
anonymous speaker labels
    |
    v
local identity matching
    |
    v
final transcript
```

Once these workflows pass the acceptance tests, stop adding features and validate with real meetings before expanding the scope.
