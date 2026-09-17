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
Asr                     live ASR batching, the persistent job state machine driver,
                        transcript assembly, and recording import
Asr.Volcengine          file-ASR adapter, provider JSON parsing, Polly inside HTTP
Cli                     composition root: config, status, devices, start, stop, import, asr
```

`MeetCap.AudioPipeline` sits above `Core` and `Persistence`: it owns the capture
lifecycle and writes the session artifacts and their index. It does not reference
`MeetCap.WindowsAudio`; the CLI composition root is the only place that joins a
concrete audio platform to the pipeline, which is what keeps the pipeline testable
without audio hardware.

`MeetCap.Asr` depends on `Core` and `AudioPipeline` and consumes the recording artifact
contract (the durable chunk, the WAV writers, the session event sink). The dependency points
one way only: `MeetCap.AudioPipeline` still reaches no ASR, cloud, HTTP or speaker assembly, and
the closed-chunk hand-off is a Core-owned event rather than a reference
(`docs/ARCHITECTURE.md` section 9.3,
`tests/MeetCap.AudioPipeline.Tests/CaptureIndependenceTests.cs`).

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

### 7.2 M5 implementation: dual-track capture

An online session runs the same boundary twice — once for the microphone track and once
for the loopback track — through `IAudioDeviceEnumerator.EnumerateRenderDevices` /
`GetDefaultRenderDevice` / `FindRenderDevice` (render endpoints) and
`IAudioCaptureSourceFactory.CreateLoopback`. The loopback source is built with NAudio 3's
`WasapiRecorderBuilder.WithLoopbackCapture` (system, the baseline) or
`.WithProcessLoopback` (process, the additive option), which produces the same
span-based `WasapiRecorder` the microphone uses, so the loopback track reuses the
`NAudioCaptureSource` adapter and no raw `ActivateAudioInterfaceAsync` / COM plumbing is
recreated (section 2).

`RecordingSession` owns one `CaptureTrack` per source. Each `CaptureTrack` is a
self-contained copy of the M1/M2 capture-consumer-recovery runtime: its own capture
source, bounded queue, `ChunkSpool`, `CaptureTimeline`, `CaptureBacklogMonitor` and
device-loss recovery. The microphone and loopback queues are independent, so one capture
callback never waits for the other (docs/RELIABILITY.md section 3), and a track that
loses its device and cannot recover ends only itself — the other track keeps recording
(docs/RELIABILITY.md section 8). The session ends when every track has ended, a stop is
requested, or a storage failure occurs; a single track's fatal loss marks that track
degraded and records `end_reason` on its `track_health` entry without ending the session.

The M4 batch builder already groups chunks per source, so each track is independently
transcribed through file ASR and `asr/batches/<source>/` keeps the two tracks separate.
`TranscriptMerger` (MeetCap.Core.Transcripts) merges the two tracks' normalized segments
onto one session-relative timeline ordered by `start_ms`, preserving `source` and
overlapping speech rather than deleting it (section 16).

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

### 9.3 M4 implementation: the closed-chunk hand-off

`RecordingSession.ChunkClosed` publishes one `ClosedAudioChunk` for every chunk the spool closes
durably, on the recording consumer thread. It carries the session id, the track, the 1-based
sequence, the durable path and its session-relative form, the chunk's own `start_ms` / `end_ms`,
its audio byte count, and the track format — and nothing else. No provider, HTTP, or job type
crosses it, which is what lets the recording assembly raise it while
`tests/MeetCap.AudioPipeline.Tests/CaptureIndependenceTests.cs` continues to prove that
`MeetCap.AudioPipeline` cannot reach the ASR stack.

Two rules keep the guarantee intact:

- **Only a durable chunk is announced.** The event is raised after the header has been patched,
  flushed, validated, and renamed out of `.part`, and after the chunk index row says `closed`. A
  subscriber can therefore read the file it is told about.
- **A subscriber cannot fail the recording.** If a handler throws, the failure is written as an
  explicit `capture.discontinuity` event naming the chunk and the consumer is skipped. The audio
  is already durable and a transcript is optional (`docs/RELIABILITY.md` section 2).

The session's own `ISessionEventSink` is exposed as `RecordingSession.Events`, so a consumer that
appends to `events.jsonl` during the meeting shares the recorder's append lock. The sink is
disposed when the session object is disposed, not when `RunAsync` returns, because M4's post-stop
work (flushing the final batch and draining the queue) still writes session events; the recording
liveness marker is released by `ReleaseRecordingLock` at the end of `RunAsync` as before.

The chunk's identity is `LastClosed` on the spool rather than the caller's own close result:
`ChunkSpool.Append` rotates a chunk internally at a capacity boundary, so "what was just closed"
is something only the spool knows. The session remembers the last announced sequence number so a
rotation followed by the teardown close cannot announce one chunk twice.

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

### 10.1 M4 implementation

`MeetCap.Asr.Batching.AsrBatchBuilder` is the batch builder. It is driven by
`RecordingSession.ChunkClosed`, which `MeetCap.AudioPipeline` raises on the recording consumer
thread for every chunk it has just closed durably — including a rotation the spool performs
inside `Append`, not only an explicit close. The recording assembly announces durable audio and
nothing else; it has no ASR, HTTP, or provider dependency
(`tests/MeetCap.AudioPipeline.Tests/CaptureIndependenceTests.cs`).

```text
closed chunk (durable, indexed)
  -> grouped per source until its span covers asr.file_batch_seconds
  -> asr/batches/<source>/batch-NNNNNN.wav   (real WAV, header patched and validated)
  -> asr/batches/<source>/batch-NNNNNN.json  (batch/source timeline mapping)
  -> asr_jobs row (pending, provider_request_id already allocated)
```

Three properties of that ordering are deliberate:

- **The audio artifact exists before anything references it.** The batch WAV is written through
  a `.part` file, validated, renamed, and only then does the job row appear. A crash can
  therefore leave an orphaned batch or an unfinished `.part`, but never a queued job whose audio
  is missing (`docs/RELIABILITY.md` sections 5 and 6).
- **The batch manifest is what preserves the source mapping.** It records the source, the batch
  number, the batch's own span on the session timeline, and every capture chunk it was built
  from with that chunk's own `start_ms` / `end_ms`. A batch therefore spans a hole in the
  timeline without hiding it: the window closes on captured audio time, not on wall-clock span,
  so a device outage produces a batch whose declared span is longer than the audio inside it,
  and the manifest states where each chunk really sits.
- **Recovery is idempotent.** The job id is derived from the batch artifact path and its session
  (`job_<session>_<source>_batch-NNNNNN`), so re-running recovery against a batch file that already
  has a job is a no-op. The session is part of the identity because batch numbering restarts at 1
  per track per session while `asr_jobs` is one table shared by every session in a data root; the
  idempotency check itself matches `input_artifact` within the session, which also recognises rows
  written before the id carried those scopes. `RecoverFinalizedBatches` re-queues a finalized batch whose job row is missing and
  discards any `.part` left by an unfinished batch; the chunks it would have contained are still
  durable and are picked up by the next window. It runs from both `meetcap start` and
  `meetcap asr resume`, because the documented restart entry point has to be able to see the whole
  crash surface (section 12.1).

Batch numbering is per track, starts above whatever a previous process already wrote, and skips
a number whose file already exists, so a restarted recorder cannot overwrite a batch.

Capture chunk duration and batch duration stay independent: `capture.chunk_seconds` is the
durability unit and `asr.file_batch_seconds` is the provider context unit, and neither is
derived from the other.

The batch WAV is an intermediate artifact. The capture chunks, the provider's raw response, and
the per-job `normalized.jsonl` are the durable record; `transcript/raw.jsonl` is derived from
those (`docs/DATA_MODEL.md` sections 6 and 12).

### 10.2 A window that cannot be built must not stop the track

Materializing a window reads the durable capture chunks and writes two files, so it can fail for
reasons the recorder never controls: a chunk that an aborted write left unreadable, a chunk in a
different format, a disk that refuses one of the writes. The distinction that matters is whether a
*chunk* is at fault or a *write* is:

```text
a capture chunk cannot be read (missing, short, not a WAV, wrong format)
  -> asr.batch.failed (reason=chunk_unreadable), naming the batch and the chunk
  -> that one chunk is dropped from the window and counted (UnreadableChunks / DroppedAudioMs)
  -> the remaining chunks are retried in the same call, so their audio still reaches the provider
  -> the dropped chunk stays on disk: this is a transcript gap, not lost audio

the batch WAV or its timeline manifest cannot be written (disk, permissions, capacity)
  -> asr.batch.failed (reason=batch_write_failed), naming the batch
  -> nothing is dropped; the whole window stays pending and is retried
  -> a batch WAV whose manifest could not be written is removed from disk, because
     TryReadBatchManifest refuses a manifest-less WAV and RecoverFinalizedBatches could never
     queue it (if even the removal fails, the event names the leftover artifact)

the job row cannot be written (the window is already durable on disk)
  -> asr.batch.failed (reason=job_queue_failed)
  -> nothing is dropped; the durable batch is re-queued by `meetcap asr resume`
```

Two rules follow, and both are load-bearing:

- **The track never stops.** Dropping the offending chunk is what lets the loop make progress.
  Without it one unreadable chunk would re-run the same failing build for every later chunk —
  permanently stopping that track's transcription for the rest of the meeting.
- **A transcript gap is never a failed recording.** The builder contains every one of those
  failures and records it; no exception leaves `CompleteBatch`, so nothing reaches the recording's
  chunk-close path, the session is not marked degraded or interrupted, and `meetcap start` still
  exits 0 (`docs/RELIABILITY.md` sections 1 and 2). `meetcap start`'s stop summary names the
  unreadable chunk count and the milliseconds that were never transcribed.

**What is bounded, and what is not.** The two failure kinds retain different amounts, and the
difference is deliberate rather than an omission:

| Failure | Retained pending window | Why |
| --- | --- | --- |
| chunk unreadable | falls back to at most one `file_batch_seconds` window | the offending chunk is dropped and the rest is retried immediately, so the window empties and the next window starts clean |
| write failure | grows by the chunks that close between attempts | nothing may be discarded, so the audio is kept and retried; the window is retried at each later threshold crossing rather than re-based or capped |

The write-failure case is therefore *not* bounded by `file_batch_seconds`. Its bound is the number
of chunks the recording produced: what accumulates is per-chunk metadata (a path, a sequence and
two timestamps), never audio, so a two-hour recording at 60 s chunks holds about 120 records in the
worst case. The operator-visible consequence is stated rather than hidden: when the write site
recovers, that accumulated window is materialized and submitted as **one** request covering the
whole failed stretch, which is the oversized-first-request shape
`docs/ASR_STRATEGY.md` section 12 discusses for imports. Failure windows are also never assigned a
new batch number, so a long outage leaves no gaps in the batch numbering and no half-written files
behind.

Two smaller guarantees keep the loop honest:

- the retry loop asserts that a chunk-attributable failure actually removed a chunk; if it did not,
  the builder records the fact and returns instead of retrying the same failing build. Termination
  is therefore local to the loop rather than dependent on `PendingBatch.Drop`'s exact-path match.
- the pending window's own duration is what the manifest and the job record, so an accumulated
  window's span is stated in its artifacts and never inferred.

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

The provider reports timestamps relative to the artifact it was given. `AsrFileRequest` and
`AsrNormalizationContext` therefore carry `StartOffsetMs`, the artifact's own start position on
the session timeline, and the normalizer adds it to every provider timestamp. It is zero for an
import (the file *is* the session) and the batch's start position for a live batch, which is what
keeps a segment from the third batch of a meeting from being written at the start of the meeting
(`docs/DATA_MODEL.md` section 6). The raw response is retained unchanged, so the offset is applied
at normalization time and can be re-applied by re-parsing it.

The Volcengine provider requests anonymous speaker information where supported. Domain code consumes normalized `TranscriptSegment` objects and never assumes provider speaker IDs are persistent identities.

The adapter also owns the transport classification: `VolcengineAsrProvider` wraps the exceptions
its Polly pipeline rethrows (`HttpRequestException`, `TimeoutRejectedException`, a cancelled
request) in `AsrTransientException`, so a lost network reaches the durable job state machine as a
retryable failure instead of escaping as an unhandled exception
(`docs/RELIABILITY.md` section 9).

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

### 12.1 M4 implementation: the live queue

`MeetCap.Asr.LiveTranscription` is what makes the queue move while a meeting is still running.
It owns two behaviours:

- a background drain that calls `AsrJobProcessor.RunDueAsync` every
  `LiveTranscriptionOptions.PollInterval` (500 ms) for the live session, started by
  `meetcap start` as its own task. It never runs on the capture callback, and a transport
  failure simply ends that drain — the jobs keep their durable state and their `next_retry_at`
  schedule, which is what paces them;
- a stop-time pass: `Stop()` asks the loop to finish its current drain, the remaining partial
  batch is flushed so the audio after the last full window still reaches the provider, and the
  queue is drained once more.

Drains are serialized by a gate, so the background loop and the stop-time drain can never
observe one job in an intermediate state and leave it there. A drain also keeps advancing a
single job through `submitted` to its result: one pass advances a job by one step, so stopping
at a bare `submitted` would leave that job's audio submitted but never collected.

`meetcap asr resume --force` processes jobs whose durable `next_retry_at` is still in the
future. That is the operator's statement that the reason for the backoff is over — the network
is back — and the schedule is otherwise respected, so repeated commands cannot create a retry
storm.

`meetcap asr resume` also runs the batch-recovery pass of section 10.1 before it drives the
queue. The restart entry point has to see the whole crash surface, not only the half that already
has a job row: a process killed between writing a batch WAV and creating its job leaves audio
that `asr_jobs` knows nothing about, and requiring a *new* recording to recover the previous one
was not a recovery path an operator could find. `meetcap status` reports the same orphans as
`asr orphaned:` lines (and still exits 0), so they are visible before anyone thinks to look for
them.

A transport failure is classified by the provider adapter, not leak out of it:
`VolcengineAsrProvider` wraps the exceptions its Polly pipeline rethrows
(`HttpRequestException`, `TimeoutRejectedException`, a cancelled request) in
`AsrTransientException`. Without that, a lost network would surface as an unhandled exception
instead of a durable `retry_wait` job.

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

Since M4 a live session that owns post-capture work takes the documented post-capture edge
instead of claiming to be finished:

```text
CREATED -> RECORDING -> FINALIZING -> PROCESSING -> COMPLETED
```

`meetcap start` declares that the session owns post-capture work when it wires live file-ASR
batching, so a clean stop lands on `PROCESSING`, and the ASR job processor completes the session
when its last job reaches a terminal state. A session that queued no batch at all is completed
explicitly once the queue is known to be empty.

`PROCESSING` is deliberately **not** recording-owned: `SessionStatus.RecordingOwned` — the list
`meetcap stop` and startup recovery use to decide which session a recorder still owns — contains
only `CREATED`, `RECORDING`, and `FINALIZING`. `PROCESSING` means capture is over and only the
ASR queue is still moving, so no recording process owns it; treating it as recording-owned would
make `meetcap stop` wait for a transcription queue.

`INTERRUPTED` is terminal and is entered only when startup recovery finds a session that
was never cleanly stopped, or when the recording process itself cannot finish (capture
never started, or the last chunk could not be closed). It is never used for a degraded
but complete recording.

A device loss that is recovered does **not** interrupt a session: the session still
reaches `COMPLETED`, and the outage appears as a `degraded` flag plus explicit
`capture.device_lost` / `capture.device_restored` / `capture.gap` events. A clean stop that
still owns ASR work reaches `PROCESSING` first and `COMPLETED` when the queue is terminal
(section 20, above).

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

### 21.1 Live recording (M4)

A live offline session runs the same file-ASR pipeline, fed by the capture spool instead of by an
imported file:

```text
load + validate config                    # invalid configuration stops here
resolve provider (app id + credential)    # invalid credentials stop here, before any session
startup recovery scan
create the session row + session.json + recording.lock
attach the ASR batch builder to RecordingSession.ChunkClosed
re-queue finalised batches whose job row is missing
recording loop (Ctrl+C, `meetcap stop`, or an unrecoverable capture/storage failure)
  per durably closed chunk:  -> append to the batch window of its track
  when a window closes:      -> asr/batches/<source>/batch-NNNNNN.wav (+ .json)
                             -> asr_jobs row (pending)
  background drain:          -> submit -> poll -> retain raw response -> normalize
                             -> asr/jobs/<job-id>/{request.json,response.json,normalized.jsonl}
                             -> transcript/raw.jsonl (rebuilt from every job's normalized.jsonl)
                             -> transcript/live.md
stop: final partial batch flushed, queue drained once, session PROCESSING
mark the session COMPLETED once every job reached a terminal success
```

The differences from an import are the ones that matter for reliability:

- the provider is resolved *before* the session exists, so a credential problem fails visibly and
  leaves no session behind. `asr.enabled = false` is the supported way to record without a
  transcript, and it skips the ASR stack entirely;
- the session stays `PROCESSING` when it stops with work outstanding, so it is never presented as
  finished while the queue still holds jobs;
- a failed ASR job does not make the recording unclean. `meetcap start` reports it, names the
  session event log, and still exits 0: the exit code is reserved for the recording
  (`docs/RELIABILITY.md` section 1);
- `meetcap status` reports the queue depth and whether transcription is behind, without treating a
  backlog as a recording failure (section 12 and `docs/ASR_STRATEGY.md` section 13).

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
