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

Their location is resolved from configuration first (`[media] ffmpeg_binary_folder`), then from
well-known install locations, and only then from `PATH`. Nothing falls back to a bare `ffmpeg`
command name, so a missing toolchain is reported before any capture or transcription work
starts.

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
  MeetCap.Cli.Tests/
  MeetCap.AudioPipeline.Tests/
  MeetCap.Asr.Tests/
  MeetCap.Asr.Volcengine.Tests/
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

M2 adds the accounting and the stalled-consumer reporting on top of that bound: how deep the
queue actually got, what it had to drop and how long a consumer stopped draining are recorded
with the session, and a stalled consumer is reported while capture keeps priority. See
section 9.2.

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
  and the first buffer of the new stream starts a new chunk;
- a reopened endpoint that reports a **different mix format** cannot be folded into the
  running session, because the chunk headers, the chunk index and the timeline are
  already written against the session's format. The session ends as degraded with an
  explicit `capture.format_changed` event (naming both formats) and a non-zero exit,
  instead of writing new bytes under the old header.

M2 makes the missing time explicit on both sides of a crash: the live timeline counts every
discontinuity once as `CaptureTimeline.GapTotalMs` / `GapCount` and persists it in
`session.json`, and startup recovery re-derives the same question from the chunk index with the
gap audit. See section 9.2 and `docs/DATA_MODEL.md` section 5.1.

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
.part alongside a closed .wav -> never overwrite the WAV; retain the .part as .collided, mark corrupt
session not cleanly stopped  -> status INTERRUPTED, manifest records recovered_at
session already terminal     -> repair the stray artifact, keep the terminal status and stopped_at
session with a held liveness marker -> skip: a live recording owns its own chunk surface
```

The scan is safe to run on every command because it decides whether a session is
recoverable from session state and liveness, not from artifact presence alone:

- only a session whose status is `CREATED`, `RECORDING` or `FINALIZING` — or a session
  directory with no row at all — is treated as "not cleanly stopped". A session that is
  already `COMPLETED` or `INTERRUPTED` keeps its status, its `stopped_at` and its
  `duration_ms` even when recovery repairs a stray artifact it found next to the durable
  audio, because it *was* cleanly stopped.
- a session whose liveness marker (`recording.lock`) is held by a running process is
  skipped entirely. The session preparation step (`CaptureService.PrepareSession`) claims
  that marker *before* it writes the manifest and the session row, so a session that is
  visible to the scan is always already owned — there is no window in which a published
  `CREATED` session looks abandoned. The marker is released when the recording finishes,
  or if the caller abandons the prepared session without running it. The operating system
  releases the handle when the process exits for any reason, so a killed recorder is still
  recovered. Without it, `meetcap status` (a different process) would rewrite a healthy
  live session to `INTERRUPTED`, stamp false recovery events on its clean event log, and
  make `meetcap stop` — which only finds `CREATED`/`RECORDING` sessions — unable to stop a
  recording that is still running.

Previously closed chunks are never reopened or rewritten, which is what makes a forced
kill unable to damage them.

### 9.2 M2 implementation: bounded-buffer accounting, gap audit and session repair

The bound from section 7.1 is now accounted for, not only enforced. `RecordingSession` creates
one `CaptureBacklogMonitor` from the queue capacity it actually uses
(`max(8, capture.buffer_seconds × 100)` packets, unchanged from M1), the capture callback
records every packet the queue accepted or refused, and the consumer records every packet it
drained. The counters are updated from both threads with interlocked operations, and the
snapshot answers the questions a log line cannot: the queue bound actually used
(`capacity_packets`), the deepest backlog reached (`peak_queued_packets`), the packets the bound
refused (`dropped_packets`, `overflow_events`), the stalled-consumer observations
(`stall_events`, `longest_stall_ms`) and the computed `is_degraded` verdict. The snapshot type
is `AudioBufferHealth`; it is surfaced on `RecordingSessionOutcome.CaptureHealth` and
persisted as `capture_health` in `session.json`, so "how close did the queue come to its
bound" survives as part of the durable record instead of being reconstructed from logs
(`docs/DATA_MODEL.md` section 3). `ChunkSpool` is unchanged, and M2 adds no configuration key:
the threshold below is derived from the existing `capture.buffer_seconds`.

A downstream consumer that stops draining is reported while recording keeps priority:

```text
every 250 ms housekeeping tick
  queue non-empty and no chunk closed since the last tick
    -> accumulate the stalled time
    -> once it reaches capture.buffer_seconds (in milliseconds)
         -> session marked degraded
         -> one capture.consumer_stalled event, rate-limited to one per second while the
            stall lasts (the stall counter counts one event per stalled period)
```

The event carries `source`, `at_ms`, `count` (packets still queued) and a `detail` naming the
queued count against the capacity. Capture is never throttled: the queue still refuses packets
at its bound, and drops are still reported as `capture.buffer_overflow`. The accounting
observes; it never delays the callback (section 7 and `docs/RELIABILITY.md` section 4).

`CaptureTimeline` now counts each discontinuity exactly once as `GapTotalMs` and `GapCount`. A
device-position skip and a measured device outage both flow through `Observe`, so a
discontinuity can neither be counted twice (once from the position and once from the measured
outage) nor be smoothed away. `PacketTiming.GapMs` stays the per-buffer value, and
`PacketTiming.GapStartMs` / `GapEndMs` carry the hole's own interval — where audio stopped and
where it resumed — separately from `StartMs`, which is where the buffer that follows the hole
sits. Recording writes that interval onto the live `capture.gap` event, which is what lets
recovery recognise its own finding as the same hole (`docs/DATA_MODEL.md` section 4.1).
`RecordingSession.Complete()` assigns `gap_count`, `gap_total_ms` and `capture_health` on the
manifest, and the `session.stopped` event carries `gap_ms`. `gaps_remain` and `gap_details` are
always present in the document (`false` and `[]` by default) and are set by recovery or
`meetcap session repair` when their audit still finds a gap; recovery does not overwrite
`gap_count` / `gap_total_ms`, because those mean "what the live timeline measured"
(`docs/DATA_MODEL.md` section 3).

The durable half of the same question is `SessionGapAuditor`, which derives the missing
stretches of a session timeline from the `audio_chunks` index and never mutates anything
(`docs/DATA_MODEL.md` section 5.1). It reports `AudioGap` records with an explicit reason, and
its rules keep the failure modes apart:

```text
run of non-durable chunks + adjacent timeline hole
                             -> ONE gap, from the last durable chunk's end to the next
                                durable chunk's start
a non-durable chunk caused it -> chunk_unreadable / chunk_missing
otherwise                     -> not_captured
skipped chunk number, contiguous timeline
                             -> still reported, as a zero-length span with MissingSequences
                                populated
```

A gap is therefore always one event with a reason, never a hidden timestamp shift
(`docs/RELIABILITY.md` section 7).

Recovery is honest about what it could not fix. `SessionRecoveryScanner` audits the session
after repairing it, and then:

- writes an explicit `capture.gap` event for every gap the live recording never saw, naming the
  hole's own interval and recognising a hole the recording already reported by a **bounded
  coverage** test: a recorded gap accounts for an audit gap only when it covers the audit's span,
  boundary for boundary, to within 50 ms of quantisation tolerance
  (`SessionRecoveryScanner.GapMatchToleranceMs`, `docs/DATA_MODEL.md` section 4.1). A repeated
  scan therefore never appends the same gap twice, a hole a *shorter* live gap merely overlaps is
  still reported, and a hole that shares only a boundary position is not merged into it. Both
  weaker rules are wrong in opposite directions: a single-position key duplicates one hole, and an
  unbounded intersection hides part of the loss from the event log;
- marks the session degraded when its audit is incomplete or the manifest's `capture_health`
  is degraded, and records `gaps_remain` / `gap_details` on the manifest;
- writes `session.repair.incomplete` (`reason = gap_detected`, or `audit_failed` when the chunk
  index could not be read) whenever a gap remains — including for a session that had already
  stopped cleanly — because a repair that could not make the session whole must not report
  success (`docs/RELIABILITY.md` section 6). It is written once per verdict rather than once per
  pass, so recovery re-running on every command does not restate the same finding;
- writes `session.recovered` only when something was actually repaired or the session was not
  cleanly stopped, carrying `gap_ms` and `reason=incomplete` when a gap remains.

`meetcap session repair [--session <id>]` exposes that same pass as an operator action
(`CaptureService.RepairSession`, returning `SessionRepairOutcome`). It repairs one session or
every session under the data root, prints per-session status, detail and directory, the
`timeline:` summary and one `gap:` line per gap, and **exits 1 when a known gap remains or the
requested session was not found**, with an actionable error. Exit 0 means the session left no
known gap. The same accounting is visible without that command: `meetcap start` prints
`capture buffer: peak N/M packets, dropped N, stalled N time(s) (longest N ms)` on every run and
`audio gaps: N (M ms missing)` when gaps occurred, and `meetcap status` prints the `timeline:`
and `gap:` lines for a recovered session that still has a gap. `meetcap status` keeps exiting 0
in every case, including a session it could not make whole: it describes state, and a command
whose exit code is always 0 never has to be interpreted. `meetcap session repair` is the acting
command and carries the non-zero exit when recovery is incomplete
(`docs/DEVELOPMENT.md` section 8).

Shutdown ordering belongs to the same guarantee: the capture loop runs as a task that is
awaited, and the channel writer is completed only after that task has returned, so the packets
a slow consumer still holds are drained instead of racing a late capture callback against a
closed channel.

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

Implemented interface (`MeetCap.Core.Asr`):

```csharp
public interface IAsrProvider
{
    string Name { get; }

    Task<AsrSubmission> SubmitFileAsync(
        AsrFileRequest request,
        CancellationToken cancellationToken = default);

    Task<AsrPollResult> GetResultAsync(
        AsrSubmission submission,
        AsrFileRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAsrResponseNormalizer
{
    AsrNormalizationResult Normalize(string rawResponseJson, AsrNormalizationContext context);
}
```

`AsrPollResult` is one of `Pending`, `Completed`, `TaskNotFound`, or `Failed` with a
transient/permanent classification, so the durable state machine — not the transport — decides
what happens next.

Normalization is a separate contract from the HTTP poll for one reason: the raw provider
response is written to disk **before** it is parsed, so a parser fix never requires re-billing
the same audio (section 14).

Provider-specific concepts such as endpoint URL, resource ID, request headers, hotword identifiers, and speaker-info flags must remain inside `MeetCap.Asr.Volcengine`.

The provider request id is supplied by the caller — the persistent job — instead of being
generated per HTTP attempt. The same id is the provider's task identifier, so a retry after a
process restart addresses the same task rather than creating a second billable one.

The Volcengine provider requests anonymous speaker information where supported. Domain code consumes normalized `TranscriptSegment` objects and never assumes provider speaker IDs are persistent identities.

Polly policies live inside the provider execution layer and cover only transient HTTP
execution: bounded retry with exponential backoff and jitter, plus a per-request timeout.
Persistent retry state lives in the ASR job store.

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

Every state change is persisted before the next side effect, so a process killed at any point
resumes from the stored row rather than from memory. Recovery rules:

```text
submitting  -> submitted   # the provider request id was persisted before the request, so the
                           # submission is treated as accepted and recovery polls instead of
                           # re-submitting (re-submitting would risk paying twice)
polling     -> polling     # continue querying the same provider task
retry_wait  -> submitting  # only once the durable next_retry_at has passed
```

If the provider reports the task as unknown, the job falls back to `retry_wait` and is
submitted again. `meetcap asr resume` is the restart entry point and processes every job whose
durable state still needs work.

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

No component may read ad-hoc environment variables directly except the configuration/secret resolver. Configuration values that name an environment variable do so explicitly through the `env:NAME` reference scheme, which the resolver expands.

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
      recording.lock        (held exclusively while a recording is in progress)
      audio/
        mic/
        loopback/
        import/
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

`recording.lock` is a liveness marker, not an artifact: it is claimed while the session is
being prepared, before `session.json` and the `sessions` row are published, and held open
exclusively until the recording finishes or the prepared session is abandoned without
running. The operating system releases it when that process exits for any reason. It is
what lets `meetcap status` run the startup recovery scan without ever touching a recording
that is still in progress, and without mistaking a just-prepared session for an abandoned
one. See section 9.1 and `docs/DATA_MODEL.md` section 2.

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

`meetcap import <file> --title <title>` creates a normal MeetCap session with
`source_type=import`, materializes or links the source under the session artifact directory,
inspects/normalizes it using the media abstraction, and queues file ASR.

Implemented order of operations (M3):

```text
load + validate config                    # invalid configuration stops here
resolve provider (app id + credential)    # invalid credentials stop here
resolve FFmpeg/FFprobe
migrate database (creates the data root and its private-data marker)
inspect source with FFprobe               # an unusable file stops here, state-free
create the session row (mode=import, source_type=import, status=PROCESSING)
write session.json                        # the durable record, before any risky media work
emit session.created
copy source into audio/import/            # original is never modified
rewrite session.json with the original artifact mapping; emit session.source.imported
normalize into audio/import/normalized.wav  # only when the source is not already
                                            # 16 kHz mono 16-bit PCM WAV
re-inspect the normalized artifact
rewrite session.json with the normalized artifact; emit session.media.normalized
create the ASR job row (status=pending, provider_request_id already allocated)
emit asr.job.queued
drive the job: submit -> poll -> retain raw response -> normalize -> write
  asr/jobs/<job-id>/request.json        # sanitized, no credentials, no inline audio
  asr/jobs/<job-id>/response.json       # raw provider response, retained first
  asr/jobs/<job-id>/normalized.jsonl
  transcript/raw.jsonl                  # rebuilt from every job's normalized.jsonl, so
                                        # re-completing a job cannot duplicate segments
  transcript/live.md                    # Markdown transcript
mark the job succeeded, then emit asr.job.completed
mark the session COMPLETED once every job succeeded
```

Failure guarantees, stated precisely:

- Configuration, credential, tier, toolchain, and source-not-found failures happen before
  anything is created, so they leave no session state behind at all.
- A failure after `inspect` (copying or normalizing) leaves a **visible, recoverable** session:
  the row exists, `session.json` exists and reflects the artifacts materialized so far, and the
  session stays in `PROCESSING`. Nothing is orphaned or silently completed.
- A failed or interrupted ASR job leaves the session in `PROCESSING` so the audio stays
  recoverable and `meetcap asr resume` can continue it.

The per-job artifacts, not `transcript/raw.jsonl`, are the durable transcript source:
`raw.jsonl` is derived by concatenating each job's `normalized.jsonl` in job order. That keeps
the session transcript write idempotent, which is what makes the crash-and-resume path safe
when a process dies between writing the transcript and persisting the terminal job status.

Imported and recorded sessions therefore share the same transcript and speaker-identity pipeline.

Because the only supported input is a single provider file request, an import that exceeds the
provider's single-request or inline-upload limit fails with an actionable message instead of
being silently split; split mapping with preserved timestamps remains a later concern
(`ASR_STRATEGY.md` section 12).

---

## 22. Security and privacy

Local by default:

- recordings;
- speaker embeddings;
- speaker/name mappings;
- transcripts;
- SQLite database.

The repository `.gitignore` only ignores `/sessions/` at the repository root, because an
unanchored `sessions/` pattern also swallowed the `src/MeetCap.Core/Sessions` source folder. To
keep private data private wherever the data root actually is, MeetCap drops a self-ignoring
`.gitignore` (`*`) into the data root the first time it creates it. It never overwrites an
existing `.gitignore`, and the marker is best effort: a read-only data root must not fail a
command.

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
GapAudit
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
