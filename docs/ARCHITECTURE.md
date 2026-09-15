# Technical Architecture

**Architecture version:** 0.3

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

A second architectural rule is equally important:

> Reuse mature OSS for commodity infrastructure, but keep MeetCap-specific durability, state-machine, timeline, artifact, and speaker-registry semantics inside MeetCap.

---

## 2. Target stack

### Runtime

- C# 14
- .NET 10 LTS
- Windows 10 22H2 / Windows 11

### CLI / configuration / logging

- System.CommandLine for command parsing and command hierarchy
- Tomlyn for TOML configuration
- Serilog for structured logging

### Windows audio

- NAudio 3 / WASAPI
- microphone capture
- system loopback capture
- process-specific loopback where supported by NAudio/Windows
- preserve NAudio timing metadata behind a MeetCap-owned capture abstraction

MeetCap MUST NOT reimplement raw COM/WASAPI plumbing that NAudio already provides unless a demonstrated requirement cannot be met through NAudio.

### Local persistence

- filesystem for audio and processing artifacts
- Microsoft.Data.Sqlite for indexes, state, jobs, and speaker registry metadata
- FluentMigrator or an equivalently thin migration layer for schema evolution

### External media tools

- FFmpeg / FFprobe
- FFMpegCore as the .NET wrapper for inspection, normalization, extraction, and conversion

FFmpeg binaries remain an external/runtime dependency whose distribution licensing must be reviewed separately from FFMpegCore.

### Provider resilience

- Polly for transient HTTP retry/backoff/timeout/circuit-breaker behavior inside provider adapters

Polly is not a replacement for the persistent ASR job queue.

### Cloud speech

- Volcengine BigASR / recording-file ASR as default
- request speaker information where supported
- streaming ASR behind the same abstraction but disabled by default

### Speaker identity

Default MVP identity stack:

```text
Volcengine BigASR
  -> transcript + timestamps + anonymous speaker labels
  -> select clean per-speaker audio segments
  -> sherpa-onnx
  -> 3D-Speaker ERes2Net-base
  -> local Speaker Registry
  -> persistent identity candidates
```

Important boundary:

- Volcengine-provided speaker labels solve **anonymous diarization** for the MVP.
- sherpa-onnx + 3D-Speaker solve **speaker embedding / verification / identification**.
- MeetCap owns registry persistence, thresholds, candidate policy, manual confirmation, and manual locks.

sherpa-onnx diarization is an optional offline fallback/extension, not the default MVP path.

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
  MeetCap.Speakers.SherpaOnnx/

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
Persistence / WindowsAudio / ASR providers / Speaker providers
 ^
 |
CLI composition root
```

`MeetCap.Core` MUST NOT reference NAudio, Volcengine SDK/JSON types, sherpa-onnx types, SQLite-specific types, FFmpeg wrapper types, Polly-specific types, or CLI-library types.

This rule is machine-checked by `tests/MeetCap.Core.Tests/Architecture/CoreDependencyBoundaryTests.cs`, so adopting an OSS building block cannot quietly move infrastructure types into the domain project.

Implemented so far:

```text
Core                    domain types and abstractions only, no package references
Persistence             Tomlyn config store, SQLite migrations and repositories
WindowsAudio            NAudio 3 endpoint enumeration and WASAPI capture
AudioPipeline           bounded queue, chunk spool, session artifacts, recovery scan
Cli                     composition root: config, status, devices, start, stop
```

`MeetCap.AudioPipeline` sits above `Core` and `Persistence`: it owns the capture
lifecycle and writes the session artifacts and their index. It does not reference
`MeetCap.WindowsAudio`; the CLI composition root is the only place that joins a
concrete audio platform to the pipeline, which is what keeps the pipeline testable
without audio hardware.

---

## 4. Runtime components

```text
+----------------------------+
| MeetCap CLI                |
| System.CommandLine         |
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
| NAudio    |    | NAudio      |
+-----+-----+    +------+------+
      |                 |
      +--------+--------+
               v
+----------------------------+
| Durable Audio Spool        |
| MeetCap-owned              |
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
| SQLite + MeetCap state     |
+-------------+--------------+
              |
              v
+----------------------------+
| Volcengine File ASR        |
| Polly inside HTTP adapter  |
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
                         |
                         v
              anonymous speaker labels
                         |
                         v
                clean segment selector
                         |
                         v
              sherpa-onnx + 3D-Speaker
                         |
                         v
                 local registry match
```

---

## 5. Session modes

### Offline

```text
mic capture
 -> durable spool
 -> Volcengine file ASR + anonymous speaker labels
 -> local speaker identity matching
```

Only one audio source.

### Online

```text
mic capture ------------------+
                              +-> independent ASR -> unified timeline
loopback capture -------------+
```

The two tracks remain independent.

The mic track is primarily the local user; the loopback track contains remote participants. Provider diarization is especially valuable on the loopback track.

### Import

```text
existing file
 -> inspect/normalize via FFprobe/FFmpeg
 -> file ASR
 -> anonymous speaker labels
 -> speaker identity matching
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

## 7. Capture abstraction and NAudio boundary

`MeetCap.WindowsAudio` wraps NAudio and exposes MeetCap-owned capture contracts.

The capture callback / async source may only:

1. copy or hand off the captured buffer;
2. attach timing metadata;
3. return quickly.

It MUST NOT:

- call HTTP;
- run FFmpeg;
- run ASR;
- run speaker embedding;
- write blocking SQLite transactions;
- wait for another track.

Use a bounded producer/consumer path.

If a downstream consumer is slow, recording has priority.

Process-specific loopback is a capture-source option, not a new product mode:

```text
online.loopback.mode = system
online.loopback.mode = process
```

Where NAudio/Windows support process loopback, MeetCap should use that implementation rather than recreate the underlying Windows activation plumbing.

### 7.1 M1 implementation

`MeetCap.WindowsAudio` is the only project that references NAudio. It uses NAudio 3's
`WasapiRecorderBuilder` in shared mode and exposes:

```text
IAudioDeviceEnumerator      active capture endpoints and the system default
IAudioCaptureSourceFactory  creates a capture source for a resolved endpoint
IAudioCaptureSource         Format, Device, PacketAvailable, Stopped, Start, Stop
```

`AudioPacket` is the MeetCap-owned packet that crosses that boundary. It carries the
source, the native `AudioFormat`, a private copy of the bytes, the device position in
frames, the device QPC timestamp when one was supplied, the observation time, and
MeetCap-owned buffer flags. No NAudio type escapes the assembly, and no conversion or
resampling happens on the capture thread: the device's own mix format is recorded.

The recording pipeline owns the bounded queue:

```text
capture callback  ->  bounded packet queue  ->  consumer  ->  chunk spool
                                                          ->  SQLite chunk index
                                                          ->  events.jsonl
```

The queue is bounded from `capture.buffer_seconds`. `TryWrite` is used, so a full queue
can never block the capture callback: the drop is counted and reported as an explicit
`capture.buffer_overflow` degraded event instead of being hidden.

A capture device that disappears ends only the current capture segment. The session
closes the audio already captured, waits, re-resolves the configured endpoint, and
restarts capture behind the same session; a device that cannot be recovered ends the
session with everything already captured still closed and durable.

---

## 8. Timing

Every captured packet should preserve, when available:

```text
source
session-relative monotonic time
device position
QPC timestamp
byte/sample count
format
```

NAudio-provided timing metadata should be carried through the MeetCap abstraction rather than independently re-derived when unnecessary.

Do not build the unified timeline only from wall-clock timestamps.

### 8.1 Session timeline

The first observed packet defines the session origin. Every later packet is placed by
device position, so session-relative milliseconds come from the device rather than from
a wall-clock read. QPC is preserved on the packet and as a per-chunk start anchor, so a
recorded timeline can be cross-checked afterwards.

Discontinuities are never smoothed over:

- a device position that jumps forward produces a `capture.gap` event carrying the
  missing duration, and later audio stays where the device says it belongs;
- a device position that moves backwards (a restarted stream) is clamped so the session
  timeline stays monotonic, and is reported as a `capture.discontinuity`;
- the device's own buffer flags are surfaced as `capture.discontinuity`;
- after a device loss and recovery, the measured outage is inserted as an explicit gap,
  and the first buffer of the new stream starts a new chunk.

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

This lifecycle is MeetCap-owned domain logic and must not be delegated to a generic recorder/task framework.

Only `CLOSED` chunks are eligible for ASR batching.

Startup recovery scans `.part` files and either repairs them or records an explicit loss event.

### 9.1 M1 implementation

Each chunk is a plain 44-byte-header WAV file in the device's native format. The header
is complete from the first byte, describing zero data, so a process kill at any point
leaves a file whose audio length is exactly `fileLength - 44`. Closing a chunk patches
the two size fields, flushes to disk, validates the header against the actual length and
format, and only then renames out of `.part`. A chunk that fails validation stays as
`.part` and the session reports the failure rather than declaring it durable.

Chunk boundaries are cut by frame capacity
(`chunk_seconds * sample_rate * block_align`), not by wall-clock time and not by
"whatever the device happened to deliver", so a boundary is exactly one chunk of audio
even when a buffer straddles it.

Startup recovery runs before a new recording starts and from `meetcap status`:

```text
.part with audio             -> patch header, validate, rename, mark recovered
.part without audio          -> discard it (it holds nothing), report audio.chunk.corrupt
.part that is not a WAV      -> leave the bytes in place, mark corrupt, report the reason
session not cleanly stopped  -> status INTERRUPTED, manifest records recovered_at
```

Previously closed chunks are never reopened or rewritten, which is what makes a forced
kill unable to damage them.

---

## 10. ASR batch builder

Capture chunks and ASR batches are different concepts.

Default:

```text
capture durability unit:  60 s
ASR context unit:        300 s
```

The batch builder groups closed chunks by source.

Batch construction MUST run outside the capture path.

At session stop, the remaining partial ASR window is submitted.

---

## 11. ASR provider interface

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

Provider-specific concepts such as endpoint URL, resource ID, request headers, hotword identifiers, and speaker-info flags must remain inside `MeetCap.Asr.Volcengine`.

The Volcengine provider should request anonymous speaker information where supported. Domain code consumes normalized `TranscriptSegment` objects and never assumes provider speaker IDs are persistent identities.

Polly policies live inside the provider execution layer. Persistent retry state lives in the ASR job store.

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

Do not replace this domain state machine with Hangfire, Quartz, or Polly.

Network unavailable:

```text
recording continues
ASR jobs accumulate
network returns
queue resumes
```

Do not fall back to streaming ASR automatically.

---

## 13. Media inspection and normalization

Import and batch preparation use FFprobe/FFmpeg through `MeetCap.AudioPipeline`.

`FFMpegCore` is the preferred .NET wrapper, but domain code must not depend on FFMpegCore-specific types.

Normalization runs only when required and never on the capture callback/thread.

---

## 14. Raw provider artifacts

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

This allows parser fixes without re-billing ASR, debugging provider changes, and re-running transcript assembly.

Secrets MUST be removed from stored requests.

---

## 15. Transcript model

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

`speaker_label` is anonymous and session/provider scoped. It is not a stable human identity.

Never overwrite `raw_text`.

---

## 16. Unified timeline

Online sessions generate segments independently from `mic` and `loopback`.

The merger:

1. converts track-relative times to session-relative times;
2. sorts by `start_ms`;
3. preserves `source`;
4. preserves provider anonymous speaker labels;
5. does not delete overlapping speech;
6. may mark probable echo duplicates later.

---

## 17. Speaker pipeline

### 17.1 Separation of responsibilities

There are two different problems:

```text
Diarization:     who spoke when?       -> speaker_0 / speaker_1 / ...
Identification:  who is speaker_1?     -> Alice / Bob / Unknown
```

They MUST remain separate in code and data.

### 17.2 MVP diarization

Default source:

```text
Volcengine file ASR
  -> timestamps
  -> anonymous speaker labels
```

MeetCap preserves these labels exactly as provider/session-scoped anonymous identities.

Optional future/fallback implementations may provide local diarization, including sherpa-onnx, but they are not required for MVP.

### 17.3 MVP identity matching

Default local identity provider:

```text
anonymous speaker segments
 -> choose clean 5-15s speech samples where practical
 -> sherpa-onnx SpeakerEmbeddingExtractor
 -> 3D-Speaker ERes2Net-base ONNX model
 -> one or more embeddings
 -> local registry similarity search
 -> ranked candidates
 -> threshold + margin policy
 -> optional manual confirmation
```

The initial reference model is:

```text
3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx
```

because it is a practical Chinese speaker-verification baseline with existing sherpa-onnx integration paths.

MeetCap should keep the provider behind an interface such as:

```csharp
public interface ISpeakerIdentityProvider
{
    Task<SpeakerEmbedding> ExtractAsync(...);
    Task<IReadOnlyList<SpeakerCandidate>> IdentifyAsync(...);
}
```

MeetCap owns:

- speaker entities;
- multiple embeddings per person;
- similarity threshold and match margin;
- enrollment workflow;
- local persistence;
- candidate ranking policy;
- manual assignment;
- manual lock semantics.

Manual mapping always wins.

Priority:

```text
manual assignment
> high-confidence historical match
> anonymous ASR speaker label
```

### 17.4 Online mode optimization

Mic:

```text
mic -> local-owner assumption / optional verification
```

Loopback:

```text
Volcengine anonymous speaker labels
 -> remote speaker clusters
 -> sherpa-onnx + 3D-Speaker identity matching
```

This avoids performing full local diarization on a track where the ASR provider already returns a speaker timeline.

---

## 18. Configuration

The canonical runtime configuration is:

```text
%APPDATA%\MeetCap\config.toml
```

Configuration is loaded through one configuration service implemented with Tomlyn behind MeetCap-owned configuration types.

No component may read ad-hoc environment variables directly except the configuration/secret resolver.

---

## 19. Storage layout

```text
%LOCALAPPDATA%\MeetCap\
  meetcap.db
  sessions/
    <session-id>/
      session.json
      events.jsonl
      stop.request          (present only while a stop is being requested)
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

`stop.request` is a control marker, not an artifact. `meetcap stop` runs in a different
process from `meetcap start`, so it signals the running recorder by writing this file,
which the recording loop polls. It carries no session state and is removed when the
session ends. See `docs/DATA_MODEL.md` section 2.

Speaker embeddings and name mappings remain local by default and are treated as sensitive identity-related data.

---

## 20. Session state machine

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
   v
PROCESSING
   |
   v
COMPLETED
```

M1 implements the subset that has no post-capture work yet:

```text
CREATED -> RECORDING -> FINALIZING -> COMPLETED     clean stop, every artifact closed
CREATED|RECORDING -> INTERRUPTED                    abandoned, never completed cleanly
```

`INTERRUPTED` is terminal and is entered only when startup recovery finds a session that
was never cleanly stopped, or when the recording process itself cannot finish (capture
never started, or the last chunk could not be closed). It is never used for a degraded
but complete recording.

A device loss that is recovered does **not** interrupt a session: the session still
reaches `COMPLETED`, and the outage appears as a `degraded` flag plus explicit
`capture.device_lost` / `capture.device_restored` / `capture.gap` events. `PROCESSING`
arrives with the ASR milestones, when a completed recording can still own outstanding
jobs.

Degraded conditions are orthogonal flags/events, not necessarily terminal states.

Examples:

```text
ASR_OFFLINE
DEVICE_LOST
LOW_DISK_SPACE
BUFFER_OVERFLOW
SPEAKER_PROVIDER_FAILED
```

If ASR or speaker matching fails but audio is safe, the session MUST remain recoverable.

---

## 21. Import architecture

`meetcap import` creates a normal MeetCap session with `source_type=import`, materializes or links the source under the session artifact directory, inspects/normalizes it using the media abstraction, and queues file ASR.

Imported and recorded sessions therefore share the same transcript and speaker-identity pipeline.

---

## 22. Security and privacy

Local by default:

- recordings;
- speaker embeddings;
- speaker/name mappings;
- transcripts;
- SQLite database.

Only configured ASR audio/batches are sent to the ASR provider.

API credentials must not be committed to Git, copied into session artifacts, or written to normal logs.

Voiceprints are identity-related biometric data and should be treated as sensitive local data.

---

## 23. OSS boundary summary

### Reuse

```text
System.CommandLine
Tomlyn
Serilog
NAudio 3
Microsoft.Data.Sqlite
FluentMigrator (or thin equivalent)
FFMpegCore + FFmpeg/FFprobe
Polly
sherpa-onnx
3D-Speaker ERes2Net-base
```

### MeetCap-owned

```text
DurableAudioSpool
CrashRecovery
ASR job state machine
TranscriptNormalizer
TimelineMerger
Speaker Registry semantics
Manual speaker-lock policy
Artifact contract
```

This boundary is intentional: commodity capabilities should be imported; product reliability semantics should remain explicit and testable in MeetCap.

---

## 24. External technical references

- Microsoft WASAPI loopback recording: https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording
- NAudio: https://github.com/naudio/NAudio
- FFMpegCore: https://github.com/rosenbjerg/FFMpegCore
- Polly: https://github.com/App-vNext/Polly
- sherpa-onnx: https://github.com/k2-fsa/sherpa-onnx
- 3D-Speaker: https://github.com/modelscope/3D-Speaker
- Volcengine ASR product capabilities: https://www.volcengine.com/docs/6561/1354871
