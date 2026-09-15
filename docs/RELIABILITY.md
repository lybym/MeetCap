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
