# Reliability Requirements

Recording reliability is the highest-priority product requirement. This document is normative.

## 1. Core invariant

> No network, ASR, speaker, transcript-rendering, or AI failure may directly stop healthy audio capture.

## 2. Failure domains

Separate `capture`, `storage`, `ASR`, `speaker inference`, `transcript rendering`, and `CLI/control`.

A failure in one domain must not cascade unnecessarily.

## 3. Capture-thread rules

The audio callback/thread must not:

- perform HTTP;
- invoke cloud SDKs;
- call FFmpeg;
- run ML inference;
- wait on SQLite locks;
- perform large synchronous JSON writes;
- wait for the second audio source.

It hands data to a bounded recording pipeline and returns.

## 4. Bounded memory

All queues must have explicit bounds. Unbounded in-memory audio queues are forbidden.

When downstream work cannot keep up:

1. recording-to-disk has priority;
2. optional analysis work is delayed/dropped;
3. the system emits a degraded event;
4. it must not silently consume unbounded RAM.

## 5. Chunk durability

Closed chunks are immutable.

```text
create .part
write
flush periodically
close
repair/finalize header if needed
validate
atomic rename
mark CLOSED
```

Only closed chunks enter downstream ASR batching.

## 6. Crash recovery

On startup:

1. locate sessions not cleanly stopped;
2. scan `.part` chunks;
3. repair recoverable WAV headers;
4. mark irrecoverable chunks;
5. emit recovery events;
6. rebuild missing ASR queue state from durable metadata where possible.

Never pretend a recovered session is clean if a gap is known.

## 7. Time gaps

Use capture timing metadata to detect discontinuities and record explicit gap events. Do not hide missing audio by only shifting later timestamps.

## 8. Device loss

If the configured device disappears:

- keep the session alive where possible;
- mark the track degraded;
- attempt configured recovery;
- record every device transition.

Loss must be visible and never silently ignored.

## 9. Network loss

Expected behavior:

```text
network down
 -> audio capture unaffected
 -> ASR jobs remain pending/retry_wait
 -> transcript may stop advancing
 -> network returns
 -> queue resumes
```

This is normal operation, not a fatal session error.

## 10. Disk-space policy

Before start, check configured minimum free space. During recording, periodically check remaining space and warn early.

If disk is exhausted, fail visibly and preserve already closed chunks. Never auto-delete older recordings unless explicitly configured.

## 11. Process termination test

Every release candidate must pass:

1. start recording;
2. close multiple chunks;
3. kill the process without graceful shutdown;
4. restart MeetCap;
5. run recovery;
6. verify all previously closed chunks;
7. quantify any loss in the active chunk.

## 12. Long-duration test

Minimum MVP soak test:

```text
2 hours offline
2 hours online
```

Later target: 8 hours continuous capture.

Measure dropped/gap duration, buffer backlog, memory growth, handles/threads, chunk count, ASR queue depth, and disk write errors.

## 13. Reliability scope stop rule

If fixing a rare failure would require a new distributed transaction protocol, external message broker, complex journal, or large recovery state machine, first verify that the required reliability property belongs to the current milestone.

Prefer simple durable files plus SQLite for this local, single-user system.

---

## 14. M2 status

This section records implementation status. It adds no requirement and weakens none of the
sections above.

Implemented, and covered by the automated suite that runs in CI (`.github/workflows/ci.yml`,
`dotnet test` on `windows-latest`):

- **Bounded memory (section 4).** The packet queue keeps its M1 bound
  (`max(8, capture.buffer_seconds × 100)` packets), and that bound is now accounted for:
  `CaptureBacklogMonitor` records accepted, drained and refused packets, the peak backlog and
  the stalled-consumer observations, and `RecordingSession` persists the `AudioBufferHealth`
  snapshot as `capture_health` in `session.json`. Backpressure is visible three ways:
  `capture.buffer_overflow` when the bound refuses a packet, `capture.consumer_stalled` when the
  queue stays occupied for `capture.buffer_seconds` without a chunk closing, and the
  `capture buffer:` line every `meetcap start` prints. Recording has priority in all three
  cases; capture is never throttled.
  Covered by `tests/MeetCap.Core.Tests/Capture/CaptureBacklogMonitorTests.cs` and by the queue
  overflow, stalled-consumer and unused-buffer cases in
  `tests/MeetCap.AudioPipeline.Tests/RecordingSessionTests.cs`.
- **Crash recovery and gap honesty (sections 6 and 7).** Recovery repairs `.part` chunks as
  before, then audits the repaired session's chunk index with `SessionGapAuditor`, writes an
  explicit `capture.gap` for every hole the live recording never saw (idempotently), and writes
  `session.repair.incomplete` whenever a gap remains. A repaired session with a known hole is
  therefore never presented as whole. The live timeline counts each discontinuity exactly once
  (`CaptureTimeline.GapTotalMs` / `GapCount`) and persists it with the session, so missing audio
  is a fact in the durable record rather than a shifted timestamp.
  Covered by `tests/MeetCap.Core.Tests/Capture/CaptureTimelineTests.cs`,
  `tests/MeetCap.AudioPipeline.Tests/SessionGapAuditorTests.cs` and
  `tests/MeetCap.AudioPipeline.Tests/SessionRecoveryScannerTests.cs`.
- **`meetcap session repair` (section 6).** The same recovery pass is exposed as an operator
  action that exits non-zero when a known gap remains or the requested session was not found,
  and prints where the audio is missing. `meetcap status` deliberately still exits 0 in every
  case — it describes state, including a broken one — which is why the acting command is the one
  that carries the exit code. Covered by
  `tests/MeetCap.Cli.Tests/CaptureCommandTests.cs`.
- **One gap, one record (section 7).** A gap has two observers — the live timeline and the
  recovery audit — so `capture.gap` carries the hole's own interval (`gap_start_ms` /
  `gap_end_ms`) and recovery recognises its own finding against the recording's by a bounded
  coverage test rather than by a single position: a recorded gap accounts for an audit gap only
  when it covers that span to within 50 ms. Recovering an already-reported hole therefore does not
  report it a second time, a hole the recording's shorter gap merely overlaps is still reported,
  and a *different* hole that shares only a boundary position is not merged into it. Both weaker
  rules fail: a position key duplicates one hole, an unbounded intersection hides the uncovered
  part of the loss from the event log. `session.repair.incomplete` is likewise written once per
  verdict rather than once per pass. Covered by
  `tests/MeetCap.AudioPipeline.Tests/SessionRecoveryScannerTests.cs`,
  `tests/MeetCap.AudioPipeline.Tests/RecordingSessionTests.cs` and
  `tests/MeetCap.Core.Tests/Capture/CaptureTimelineTests.cs`.
- **Recording independence (section 1).** `MeetCap.AudioPipeline` references no ASR, cloud,
  HTTP or speaker assembly and exposes no provider type, so the M2 exit criterion is enforced
  structurally and not only behaviourally. Covered by
  `tests/MeetCap.AudioPipeline.Tests/CaptureIndependenceTests.cs`.

Known coverage limits of the gap audit, stated rather than implied:

- `SessionGapAuditor.Audit(sessionId)`'s failure path — a `SqliteException` while reading the
  index, which turns into `SessionAudit.Problems` and therefore into an incomplete repair — is
  **not covered by a test**. Provoking it needs an unreadable or schema-less database, which a
  healthy run cannot produce, and the exit-code contract it feeds *is* covered through the
  `gaps_remain` path. It must not be read as verified.
- The audit reads spans from the chunk index, so it sees a hole *between* durable chunks. A
  device skip inside a single chunk's span is invisible to it and is reported only by the live
  timeline. This is a real limit of the audit's evidence, not a bug in it
  (`docs/DATA_MODEL.md` section 5.1).

Still not verified on real hardware:

- the section 11 process-termination test and the section 8 device scenarios are exercised in
  CI against a scripted capture source and a hand-built "not cleanly stopped" session, not
  against a real microphone or a real forced kill;
- the section 12 two-hour soak has not been run. `docs/M1_WINDOWS_VALIDATION.md` is still
  **not yet run**: it holds M1's scenarios and, in section 10, M2's own checklist (buffer
  accounting, a slow consumer, explicit gaps, and `meetcap session repair`);
- the slow-consumer behaviour is produced with an injected consumer delay, not with a genuinely
  slow disk.

M2 is therefore **implemented and automatically covered**, not verified end to end
(`docs/DEVELOPMENT.md` section 7).

---

## 15. M4 status

This section records implementation status. It adds no requirement and weakens none of the
sections above.

Implemented, and covered by the automated suite that runs in CI (`.github/workflows/ci.yml`,
`dotnet test` on `windows-latest`):

- **Network loss is a queue condition, not a recording failure (sections 1 and 9).**
  `meetcap start` runs file-ASR submission and polling on its own task, never on the capture
  callback. A transport failure is classified inside the provider adapter and lands in the
  durable `retry_wait` state with a `next_retry_at`; the recording is untouched, the batch
  artifacts stay on disk, and the queue resumes when the provider answers. A failed ASR job does
  not change `meetcap start`'s exit code, which stays reserved for the recording
  (`docs/ARCHITECTURE.md` sections 12.1 and 21.1). Covered by
  `tests/MeetCap.Cli.Tests/LiveTranscriptionCommandTests.cs`
  (`NetworkLoss_DoesNotStopRecordingAndTheQueueResumesAfterwards`) and
  `tests/MeetCap.Asr.Tests/AsrJobProcessorTests.cs`.
- **Crash recovery covers the batch hand-off (section 6).** The batch WAV is durable before its
  job row exists, so a crash can leave an orphaned batch but never a queued job whose audio is
  missing. `AsrBatchBuilder.RecoverFinalizedBatches` re-queues a finalized batch with no job and
  discards the `.part` of an unfinished one, using a job id derived from the batch artifact path
  so recovery is idempotent. It runs from both commands an operator would try — `meetcap start`
  and the documented restart entry point `meetcap asr resume`. Covered by
  `tests/MeetCap.Asr.Tests/AsrBatchBuilderTests.cs` and, through the CLI resume path, by
  `tests/MeetCap.Cli.Tests/LiveTranscriptionCommandTests.AsrResume_RecoversABatchThatWasFinalizedBeforeItsJobRowExisted`.
- **A batch window that cannot be built does not stop the track (section 4).** Every failure of the
  materialization path is contained and recorded as an explicit `asr.batch.failed`; no exception
  leaves `CompleteBatch`, so a transcript-layer write can never mark the recording degraded or
  interrupted. An unreadable chunk is dropped and the rest of its window is retried, so one bad
  chunk cannot block later windows. A write failure drops nothing: the window stays pending and is
  retried. Covered by
  `tests/MeetCap.Asr.Tests/AsrBatchBuilderTests.ABatchWindowThatCannotBeMaterializedDoesNotBlockLaterWindows`
  (chunk branch, `reason=chunk_unreadable`),
  `...RepeatedMaterializationFailureLeavesThePendingWindowBounded` (chunk branch, 20 windows),
  `...AManifestThatCannotBeWrittenIsContainedLikeAnyOtherWriteFailure` (manifest write branch,
  `reason=batch_write_failed`, and the same window is queued once the write site recovers) and
  `...APersistentWriteFailureKeepsEveryChunkAndSpansTheWholeFailedStretch` (write branch, 10
  windows: nothing dropped, no exception, and the accumulated window recovered afterwards).
  Boundedness is stated precisely rather than generally: the chunk branch leaves at most one
  `file_batch_seconds` window pending, while the write branch deliberately retains the window and
  therefore grows by the chunks that close between attempts — bounded by the number of chunks the
  recording produced (per-chunk metadata only, never audio), with the doc'd consequence that
  recovery submits the accumulated stretch as one request
  (`docs/ARCHITECTURE.md` section 10.2).
- **A closed chunk cannot be announced before it is durable (section 5).**
  `RecordingSession.ChunkClosed` is raised after the header patch, flush, validation and rename,
  and a subscriber's failure is reported as an explicit `capture.discontinuity` event instead of
  reaching the recording: the announcement runs outside the block whose failure is classified as
  a storage failure. Covered by
  `tests/MeetCap.AudioPipeline.Tests/ChunkClosedSubscriberTests.cs`
  (`RunAsync_AThrowingSubscriberCannotFailTheRecording` and
  `RunAsync_AThrowingSubscriberDoesNotStopLaterChunksFromBeingClosed`).
- **Audio capture is structurally independent of the ASR path (section 1).**
  `MeetCap.AudioPipeline` still references no ASR, cloud, HTTP or speaker assembly: the new
  hand-off is a Core-owned `ClosedAudioChunk` event. The direction of the new
  `MeetCap.Asr -> MeetCap.AudioPipeline` reference is asserted from the `MeetCap.Asr` side by
  `tests/MeetCap.Asr.Tests/MeetCap.Asr.Tests.csproj`'s declared references plus
  `tests/MeetCap.Asr.Tests/BatchReferenceDirectionTests.cs`; the opposite direction (a cycle)
  is asserted by
  `tests/MeetCap.AudioPipeline.Tests/CaptureIndependenceTests.cs`.

Known limits of this milestone, stated rather than implied:

- **No soak test has been run.** The issue's required validation — a two-hour offline meeting with
  ongoing five-minute batches, and a thirty-minute outage inside it — is exercised in CI against
  a scripted capture source and a scripted provider transport, not against a real microphone,
  a real network outage, or the real Volcengine service. `docs/M1_WINDOWS_VALIDATION.md` holds the
  M4 checklist as section 12 and it has **not been run**: the 12.2–12.4 and 12.6 rows additionally
  need a real credential, which this environment does not have. This is automated coverage, not
  real-world validation (`docs/DEVELOPMENT.md` section 7).
- **A ten-minute-plus poll still ends a command.** `poll_timeout_seconds` (default 900) bounds how
  long one invocation polls a single job. A batch whose provider task runs longer leaves the job
  in `polling` with its state persisted, and the next `meetcap asr resume` continues it.
- **Retry pacing depends on the drain running.** A process that is killed mid-outage leaves jobs in
  `retry_wait`; nothing resumes them until `meetcap asr resume` runs. That is the documented
  behaviour (section 9 and `docs/ARCHITECTURE.md` section 12) rather than an omission, but it does
  mean the transcript does not advance while MeetCap is not running.
- **The batch artifact is not deleted after its job succeeds.** An M4 session therefore keeps both
  its capture chunks and its batch WAVs. Disk-space policy (section 10) covers the failure mode,
  and reclaiming the intermediate artifacts is left to a later milestone.
