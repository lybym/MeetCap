# Technical Architecture

**Architecture version:** 0.2

---

## 1. Architectural goal

MeetCap is a **Windows local-first audio-capture system** whose first responsibility is durable recording.

Cloud ASR, speaker identification, terminology correction, and later LLM features are downstream consumers of captured audio.

The dependency direction is therefore:

```text
Capture
  -> Durable Spool
  -> ASR Queue
  -> Transcript
  -> Speaker Attribution
  -> Optional AI post-processing
```

Never reverse this dependency.

---

## 2. Target stack

### Runtime

- C# 14
- .NET 10 LTS
- Windows 10 22H2 / Windows 11

### Windows audio

- WASAPI
- NAudio modern `WasapiRecorder` / builder APIs where practical

### Local persistence

- filesystem for audio and processing artifacts
- SQLite for indexes, state, jobs, and speaker registry metadata

### External tools

- FFmpeg for normalization, concatenation, inspection, and optional compression

### Cloud speech

- Volcengine recording-file ASR as default
- streaming ASR behind the same abstraction but disabled by default

### Speaker embedding

- provider abstraction
- first implementation may use a local model/sidecar
- no speaker provider may block the capture pipeline

---

## 3. Proposed solution structure

```text
src/
  MeetCap.Cli/
  MeetCap.Core/
  MeetCap.Persistence/
  MeetCap.WindowsAudio/
  MeetCap.AudioPipeline/
  MeetCap.Asr/
  MeetCap.Asr.Volcengine/
  MeetCap.Speakers/

tests/
  MeetCap.Core.Tests/
  MeetCap.Persistence.Tests/
  MeetCap.AudioPipeline.Tests/
  MeetCap.IntegrationTests/
```

Dependency rule:

```text
Core
 ^  ^  ^
 |  |  |
Persistence / WindowsAudio / ASR providers
 ^
 |
CLI composition root
```

`MeetCap.Core` MUST NOT reference NAudio, Volcengine SDK types, SQLite-specific types, or CLI libraries.

---

## 4. Runtime components

```text
+----------------------------+
| MeetCap CLI                |
| command/control surface    |
+-------------+--------------+
              |
              v
+----------------------------+
| Session Manager            |
| state + orchestration      |
+-------------+--------------+
              |
      +-------+--------+
      |                |
      v                v
+-----------+    +-------------+
| Mic       |    | Loopback    |
| Capture   |    | Capture     |
+-----+-----+    +------+------+
      |                 |
      +--------+--------+
               v
+----------------------------+
| Durable Audio Spool        |
| 60s recoverable chunks     |
+-------------+--------------+
              |
              v
+----------------------------+
| ASR Batch Builder          |
| e.g. 5 min batches         |
+-------------+--------------+
              |
              v
+----------------------------+
| Persistent ASR Job Queue   |
+-------------+--------------+
              |
              v
+----------------------------+
| Volcengine File ASR        |
+-------------+--------------+
              |
              v
+----------------------------+
| Transcript Normalizer      |
+-------------+--------------+
              |
      +-------+--------+
      |                |
      v                v
 JSONL append     Speaker pipeline
```

---

## 5. Session modes

### Offline

```text
mic capture -> spool -> file ASR -> diarization -> speaker matching
```

Only one audio source.

### Online

```text
mic capture ------------------+
                              +-> independent ASR -> unified timeline
loopback capture -------------+
```

The two tracks remain independent.

### Import

```text
existing file
 -> inspect
 -> normalize if required
 -> file ASR
 -> transcript pipeline
```

Import uses the same `Session` abstraction with `source_type=import`.

---

## 6. Why tracks are never mixed before ASR

Mixing destroys useful source information.

Online mode naturally provides:

```text
mic       ~= local user
loopback  ~= remote participants
```

Keeping them separate improves:

- source attribution;
- speaker matching;
- duplicate detection;
- debugging;
- reprocessing;
- cost analysis;
- future AEC strategies.

A derived mixed file MAY be exported later, but it is not a primary recording artifact.

---

## 7. Capture pipeline

### Non-blocking rule

The WASAPI callback may only:

1. copy/hand off the captured buffer;
2. attach timing metadata;
3. return quickly.

It MUST NOT:

- call HTTP;
- run FFmpeg;
- run ASR;
- run speaker embedding;
- write SQLite transactions synchronously if they can block for material time;
- wait for another track.

Use a bounded producer/consumer path.

If a downstream consumer is slow, recording has priority.

---

## 8. Timing

Every captured packet should preserve:

```text
source
session-relative monotonic time
device position, when available
QPC timestamp, when available
byte/sample count
format
```

Do not build the unified timeline only from wall-clock timestamps.

The timeline normalizer is responsible for mapping each source to session-relative milliseconds.

---

## 9. Durable audio spool

Default chunk size:

```text
60 seconds
```

Example:

```text
sessions/<session-id>/
  audio/
    mic/
      000001.wav
      000002.wav
      000003.wav.part
    loopback/
      000001.wav
      000002.wav
```

Chunk lifecycle:

```text
OPEN
 -> periodic flush
 -> CLOSE
 -> validate header/length
 -> atomic rename from .part
 -> CLOSED
```

Only `CLOSED` chunks are eligible for ASR batching.

Startup recovery scans `.part` files and either repairs them or records an explicit loss event.

---

## 10. ASR batch builder

Capture chunks and ASR batches are different concepts.

Default:

```text
capture durability unit:  60 s
ASR context unit:        300 s
```

The batch builder groups closed chunks by source:

```text
mic chunks 1..5      -> mic-batch-0001
loopback chunks 1..5 -> loopback-batch-0001
```

This preserves frequent durable commits while giving file ASR several minutes of context.

Batch construction MUST run outside the capture path.

At session stop, the remaining partial ASR window is submitted.

---

## 11. ASR strategy interface

Conceptual interface:

```csharp
public interface IAsrProvider
{
    Task<AsrSubmission> SubmitFileAsync(
        AsrFileRequest request,
        CancellationToken cancellationToken);

    Task<AsrResult> GetResultAsync(
        AsrSubmission submission,
        CancellationToken cancellationToken);
}
```

Provider-specific concepts such as:

- endpoint URL;
- resource ID;
- request header names;
- speaker-info flags;
- hotword-table identifiers;

must remain inside `MeetCap.Asr.Volcengine`.

Domain code consumes normalized `TranscriptSegment` objects.

---

## 12. Persistent ASR queue

ASR is a durable job system, not an in-memory task list.

State examples:

```text
pending
submitting
submitted
polling
succeeded
retry_wait
failed
cancelled
```

A process restart MUST resume incomplete jobs.

Network unavailable:

```text
recording continues
ASR jobs accumulate
network returns
queue resumes
```

Do not fall back to streaming ASR automatically.

---

## 13. Raw provider artifacts

For each ASR job, store the raw response before normalization.

Example:

```text
asr/
  jobs/
    <job-id>/
      request.json
      response.json
      normalized.jsonl
```

This allows:

- parser fixes without re-billing ASR;
- debugging provider changes;
- re-running transcript assembly;
- auditability.

Secrets MUST be removed from stored requests.

---

## 14. Transcript model

Normalized segment:

```json
{
  "segment_id": "seg_...",
  "session_id": "ses_...",
  "source": "loopback",
  "start_ms": 12340,
  "end_ms": 16420,
  "raw_text": "example",
  "speaker_label": "speaker_1",
  "speaker_id": null,
  "speaker_name": null,
  "speaker_confidence": null,
  "manual_speaker_lock": false
}
```

Never overwrite `raw_text`.

Later correction writes another field/artifact.

---

## 15. Unified timeline

Online sessions generate segments independently from `mic` and `loopback`.

The merger:

1. converts track-relative times to session-relative times;
2. sorts by `start_ms`;
3. preserves `source`;
4. does not delete overlapping speech;
5. may mark probable echo duplicates later.

No complex AEC is required for MVP.

---

## 16. Speaker pipeline

### Offline

```text
ASR diarization
 -> speaker_0/1/2...
 -> collect clean utterances
 -> embedding provider
 -> local registry comparison
 -> candidates
 -> manual confirmation
```

### Online

Mic:

```text
mic -> local-owner assumption / verification
```

Loopback:

```text
ASR diarization
 -> remote speaker clusters
 -> speaker registry matching
```

Manual mapping always wins.

---

## 17. Configuration

The canonical runtime configuration is:

```text
%APPDATA%\MeetCap\config.toml
```

Configuration is loaded through one configuration service.

No component may read ad-hoc environment variables directly except the configuration/secret resolver.

Precedence:

```text
built-in defaults
  < config.toml
  < explicit one-shot CLI arguments
```

One-shot CLI arguments apply to that command/session only and MUST NOT silently rewrite persistent configuration.

See `CONFIGURATION.md`.

---

## 18. Storage layout

```text
%LOCALAPPDATA%\MeetCap\
  meetcap.db
  sessions/
    <session-id>/
      session.json
      events.jsonl

      audio/
        mic/
        loopback/

      asr/
        jobs/
        batches/

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

User may override the data root in configuration.

---

## 19. Session state machine

```text
CREATED
   |
   v
RECORDING
   |
   | capture ends
   v
FINALIZING
   |
   | all required recording artifacts closed
   | ASR may still be pending
   v
PROCESSING
   |
   v
COMPLETED
```

Degraded conditions are orthogonal flags/events, not necessarily terminal states.

Examples:

```text
ASR_OFFLINE
LOOPBACK_DEVICE_LOST
LOW_DISK_SPACE
SPEAKER_PROVIDER_FAILED
```

If ASR fails but audio is safe, the session MUST remain recoverable.

---

## 20. Process-specific loopback

Architecture should permit:

```text
online.loopback.mode = system
online.loopback.mode = process
```

System loopback is sufficient for the first online-capture milestone.

Process loopback can later target a meeting application's process tree on supported Windows versions.

The provider must expose it as a capture-source option, not a separate product mode.

---

## 21. Import architecture

`meetcap import` should not bypass the core pipeline.

It creates a session:

```text
source_type = import
mode = import
```

and materializes/links the source under the session artifact directory.

Then it queues a file-ASR job.

This keeps imported and recorded sessions searchable and reprocessable using the same domain model.

---

## 22. Security and privacy

Local by default:

- recordings;
- speaker embeddings;
- speaker/name mappings;
- transcripts;
- SQLite database.

Only configured ASR audio/batches are sent to the ASR provider.

API credentials must not be:

- committed to Git;
- copied into session artifacts;
- written to normal logs.

Voiceprints are identity-related biometric data and should be treated as sensitive local data.

---

## 23. External technical references

- Microsoft WASAPI loopback recording: https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording
- NAudio modern WASAPI recording documentation: https://github.com/naudio/NAudio/blob/main/Docs/WasapiRecorder.md
- Volcengine ASR product capabilities: https://www.volcengine.com/docs/6561/1354871
- Volcengine recording-file idle API: https://www.volcengine.com/docs/6561/1840838
- Volcengine hotword platform: https://www.volcengine.com/docs/6561/155739
