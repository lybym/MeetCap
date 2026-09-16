# M1 Windows validation checklist

`docs/DEVELOPMENT.md` section 7 forbids claiming a hardware-dependent feature is
verified when the hardware was unavailable. This checklist is how M1 offline microphone
capture gets verified on a real Windows machine.

**Status: not yet run.** The automated suite in
`tests/MeetCap.AudioPipeline.Tests` and `tests/MeetCap.Cli.Tests` covers the chunk
lifecycle, crash recovery, disk-space policy, device loss and the CLI surface against a
scripted capture source, but it runs without a microphone. Until the scenarios below
have been executed on real hardware, M1 must be described as *implemented and
automatically covered*, not as *verified*.

Section 10 holds the same kind of checklist for the M2 additions from Issue #4 (bounded
buffer accounting, explicit gap reporting, `meetcap session repair`).

---

## 1. Environment to record

| Field | Value |
| --- | --- |
| Date | |
| Tester | |
| Windows version / build | |
| MeetCap commit | |
| Machine | |
| Microphone (id + name from `meetcap devices`) | |
| `capture.chunk_seconds` | |

---

## 2. Prerequisites

```powershell
dotnet build MeetCap.slnx -c Release
meetcap config init
meetcap config validate
meetcap devices
```

`meetcap devices` must list at least one active capture endpoint, and the configured
`capture.offline.microphone_device_id` must resolve to the device under test (either the
literal `default` token or a listed id).

---

## 3. Scenario 1 — 2-hour offline recording

Required by Issue #3: *"A 2-hour offline session completes without unexplained audio loss"*.

```powershell
meetcap start "M1 soak test" --mode offline
# wait 2 hours, then either press Ctrl+C or run `meetcap stop` from another shell
```

Record:

| Observation | Value |
| --- | --- |
| Exit code of `meetcap start` | |
| `session.json` `status` | |
| `duration_ms` | |
| chunk count | |
| total bytes under `audio/mic` | |

Check:

- [ ] `status` is `COMPLETED` and `stopped_at` is set.
- [ ] `duration_ms` is within a few seconds of the wall-clock duration.
- [ ] `audio/mic` holds the expected number of `*.wav` chunks and no `*.part` files.
- [ ] `audio/mic/000001.wav` … `NNNNNN.wav` are contiguous with no gaps in the sequence.
- [ ] A player or `ffprobe` can open each chunk and each reports the configured duration.
- [ ] `events.jsonl` contains **no** `capture.gap`, `capture.discontinuity`,
      `capture.buffer_overflow`, `capture.device_lost` or `storage.*` events.
      Any that appear must be explained before this scenario passes.
- [ ] Memory stayed flat over the two hours (Task Manager or Performance Monitor).
- [ ] Free space on the data-root volume decreased by roughly
      `duration × sample_rate × block_align` and never approached the configured minimum.

---

## 4. Scenario 2 — default-device change during and around a session

Required by Issue #3: *"Default-device change during/around sessions"*.

1. Start a session with `microphone_device_id = "default"`.
2. Change the Windows default recording device while the session is running.
3. Stop the session. Then change the default device again and start a new session.

Check:

- [ ] `session.json` `capture[0].device_id` / `device_name` names the endpoint that was
      in use when the session started. A running session does not silently follow a
      default change; if it does, the device transition is visible in `events.jsonl`.
- [ ] The second session captures the new default device.
- [ ] `meetcap devices` marks the new default with `(system default)`.
- [ ] If capture had to be reopened during the session and the endpoint came back at a
      different mix format, the session ends degraded with `capture.format_changed`
      naming both formats, and `meetcap start` exits non-zero. New-format audio must
      never be written under the session's original WAV header.

---

## 5. Scenario 3 — microphone unplug / replug

Required by Issue #3: *"Microphone unplug/replug behavior documented"*, and by
`docs/RELIABILITY.md` section 8: loss must be visible and never silently ignored.

1. Start a session on a USB microphone.
2. Unplug the microphone while recording.
3. Wait, then plug it back in.

Check:

- [ ] `events.jsonl` contains `capture.device_lost` with the device's error detail.
- [ ] The chunk being written when the device vanished was closed and is readable.
- [ ] If capture came back, `events.jsonl` contains `capture.device_restored` and a
      `capture.gap` whose `gap_ms` is close to the measured outage.
- [ ] If capture could not come back, `events.jsonl` contains
      `capture.device_lost_fatal`, the session ends, and `session.json` shows
      `degraded: true` with `end_reason: "device_lost"`.
- [ ] `meetcap start` exits non-zero when the session ended degraded.
- [ ] No already-closed chunk was damaged.

Document the observed behaviour (restart, fatal, or silent switch) here:

```text
```

---

## 6. Scenario 4 — forced process kill

Required by Issue #3: *"Forced kill cannot corrupt previously closed chunks"* and
*"The active/incomplete chunk is detected on next startup"*. This is the
`docs/RELIABILITY.md` section 11 test.

1. Start a session and let at least three chunks close.
2. Note the file names and hashes of the closed chunks:

   ```powershell
   Get-ChildItem "$env:LOCALAPPDATA\MeetCap\sessions\<session-id>\audio\mic" |
       Get-FileHash | Format-Table
   ```

3. Kill the process without a graceful shutdown:

   ```powershell
   Stop-Process -Name meetcap -Force
   ```

4. Confirm the session directory contains a `*.part` file.
5. Run `meetcap status`.

Check:

- [ ] `meetcap status` reports the incomplete session and the repaired chunk.
- [ ] The previously closed chunks have **identical** hashes to step 2.
- [ ] The former `*.part` was renamed to `*.wav` and is readable by a player/`ffprobe`.
- [ ] `session.json` `status` is `INTERRUPTED` with `recovered_at` set.
- [ ] A second `meetcap status` reports no further findings (recovery is idempotent).

Also verify, **while a recording is still running**, that the recovery scan cannot touch
it (this is the live-recording half of `docs/ARCHITECTURE.md` section 9.1):

1. Start a session and let a chunk close.
2. Run `meetcap status` in another shell.

Check:

- [ ] `meetcap status` reports no incomplete sessions and the session as active.
- [ ] `events.jsonl` gained no `session.recovered` or `audio.chunk.corrupt` event.
- [ ] `meetcap stop` still stops the recording, and `meetcap start` exits 0.
- [ ] `events.jsonl` is readable while the session is recording (no sharing violation).

Quantify the loss in the active chunk:

| Metric | Value |
| --- | --- |
| Bytes in the recovered chunk | |
| Expected bytes for the elapsed time | |
| Loss (ms) | |

---

## 7. Scenario 5 — low disk space

Required by Issue #3: *"Warn on insufficient disk space before/during recording"* and
*"Low-disk-space simulation"*.

**Before start.** Point `storage.data_root` at a volume with less free space than
`storage.minimum_free_space_gb` and run `meetcap start`. Check:

- [ ] The command fails, exits non-zero, and names `storage.minimum_free_space_gb`.
- [ ] No session directory was created.

**During recording.** A small fixed-size VHD or a quota-limited volume works well; do not
fill the system volume.

- [ ] Crossing the configured minimum writes one `storage.low_disk_space` event and
      recording continues.
- [ ] Crossing the hard floor (256 MB) writes `storage.disk_exhausted`, stops the
      session, and leaves every closed chunk readable.
- [ ] No older recording was deleted.

---

## 8. Scenario 6 — timing metadata

Required by Issue #3: *"Verify timing metadata used by MeetCap is sourced/preserved
correctly from the NAudio capture abstraction where available"*.

Check, for a session of known duration `D`:

- [ ] The last chunk's `end_ms` in `events.jsonl` is within a few hundred milliseconds of
      `D`.
- [ ] `audio_chunks` rows have non-null `device_position_frames`; the value increases by
      `sample_rate × duration` between consecutive chunks.
- [ ] `qpc_position_ticks` is non-null where the driver supplies it, and
      `qpc_position_ticks / 10_000` tracks `start_ms` within a small constant offset.
- [ ] `session.json` `capture[0]` records the native `sample_rate`, `channels`,
      `bits_per_sample` and `sample_format` that the device actually delivered.

Inspect with:

```powershell
sqlite3 "$env:LOCALAPPDATA\MeetCap\meetcap.db" `
  "SELECT sequence, start_ms, end_ms, device_position_frames, qpc_position_ticks, status FROM audio_chunks ORDER BY sequence;"
```

---

## 10. M2 additions — bounded buffer, explicit gaps and session repair

Issue #4 (M2, *durable spool and crash recovery*) adds behaviour that also needs a real
Windows run. The automated coverage is in `tests/MeetCap.Core.Tests/Capture/`,
`tests/MeetCap.AudioPipeline.Tests/SessionGapAuditorTests.cs`,
`tests/MeetCap.AudioPipeline.Tests/RecordingSessionTests.cs` and
`tests/MeetCap.Cli.Tests/CaptureCommandTests.cs`; what a scripted capture source cannot
show is how the accounting behaves against a real device and a real slow disk.

Run these after the M1 scenarios above.

### 10.1 Bounded buffer reported on a healthy session

```powershell
meetcap start "M2 buffer check" --mode offline
# record a minute, then `meetcap stop`
```

- [ ] `meetcap start` prints a `capture buffer:` line, and `peak` is a small fraction of
      the printed capacity on a healthy run.
- [ ] `dropped` is `0` and `stalled` is `0 time(s)`.
- [ ] `session.json` carries `capture_health` with the same `capacity_packets`, plus
      `peak_queued_packets`, `dropped_packets`, `overflow_events`, `longest_stall_ms`,
      `stall_events`, `gap_count` and `gap_total_ms`.
- [ ] No `capture.buffer_overflow` or `capture.consumer_stalled` event appears in
      `events.jsonl`.

### 10.2 Slow consumer does not grow memory or stop capture

Make the data-root volume genuinely slow (a busy USB 2.0 stick, or a disk under heavy
load) and record a burst of loud input.

- [ ] If the backlog is reported, `events.jsonl` contains `capture.consumer_stalled` with a
      `count` below the capacity, and/or `capture.buffer_overflow` with a `count`.
- [ ] The working set stays flat for the duration of the run; it must not track the
      backlog.
- [ ] Capture itself never stalls: the recording arrives with a normal duration and the
      session still reaches a terminal state.

### 10.3 Explicit gap accounting

Re-use M1 scenario 3 (unplug/replug) and scenario 6 (timing).

- [ ] `session.json` `gap_count` and `gap_total_ms` match the `capture.gap` events in
      `events.jsonl` (one timeline gap is counted once, never twice).
- [ ] `meetcap start` prints an `audio gaps:` line when a gap occurred.
- [ ] No later chunk's `start_ms` moved backwards to hide the missing audio.

### 10.4 `meetcap session repair`

```powershell
# after a forced kill with an active .part file
meetcap session repair
meetcap session repair --session <session-id>
```

- [ ] `meetcap session repair` prints `repair:`, the per-session status, `timeline:` and
      one `gap:` line per gap.
- [ ] It exits `0` when the repaired session has no gap, and the repaired chunk is readable.
- [ ] Destroy the active `*.part` (overwrite its first bytes) so it cannot be repaired, then
      run it again: it exits non-zero, prints the gap, and `events.jsonl` contains
      `session.repair.incomplete`.
- [ ] Running it twice does not append a second `capture.gap` for the same position.
- [ ] `meetcap session repair --session <unknown-id>` exits non-zero and says the session was
      not found.

---

## 12. M4 additions — live file-first transcription

Issue #6 (M4, *file-first transcription during live recording*) adds behaviour that also needs a
real Windows run, and specifically needs a **real Volcengine credential** and a **real network**:
the automated coverage substitutes both boundaries. It lives in
`tests/MeetCap.Asr.Tests/AsrBatchBuilderTests.cs`,
`tests/MeetCap.Asr.Tests/AsrJobProcessorTests.cs`,
`tests/MeetCap.Asr.Volcengine.Tests/VolcengineResponseNormalizerTests.cs`,
`tests/MeetCap.AudioPipeline.Tests/ChunkClosedSubscriberTests.cs` and
`tests/MeetCap.Cli.Tests/LiveTranscriptionCommandTests.cs`.

Prerequisites on top of section 2: a working `[asr.volcengine]` configuration
(`app_id` + a resolvable credential via `env:` or a literal), and enough provider quota to submit
the recorded windows.

### 12.1 Settings used by the scenarios below

```powershell
# A short window keeps the run cheap; production windows are 300 s.
#   [capture]  chunk_seconds = 10
#   [asr]      file_batch_seconds = 30
#   [transcript] live_markdown = true
```

### 12.2 A live meeting transcribes while it records

```powershell
meetcap start "M4 live check" --mode offline
# speak into the microphone for about three minutes, then `meetcap stop` from another shell
```

- [ ] `meetcap start` prints `asr: file ASR, batch window 30s, tier standard`.
- [ ] **While the recording is still running**, `transcript/raw.jsonl` exists and grows, and
      `transcript/live.md` contains the recognized text.
- [ ] `asr/batches/mic/batch-00000N.wav` files appear, one per closed window, each a playable
      WAV whose duration matches its window, with a `batch-00000N.json` beside it.
- [ ] Every job's `asr/jobs/<job-id>/response.json` is the raw provider response and
      `normalized.jsonl` holds the normalized segments.
- [ ] Segments carry session-relative `start_ms` values that match where the speech actually
      happened in the meeting (check the third or fourth window specifically: a missing batch
      offset shows up as early timestamps repeated for later windows).
- [ ] Where the configured tier supports it, segments carry an anonymous `speaker_label` such as
      `speaker_1`, and `speaker_id` / `speaker_name` / `speaker_confidence` are still `null`.
- [ ] `meetcap stop` prints `asr batches: N queued, ...`, the session reaches `COMPLETED` once the
      queue is terminal, and `meetcap start` exits `0`.

### 12.3 Thirty-minute network outage during a recording

```powershell
meetcap start "M4 outage check" --mode offline
# after two minutes, disable the network adapter (do not touch the microphone)
# leave it disabled for 30 minutes, still speaking
# re-enable the network, then `meetcap stop`
```

- [ ] Audio capture is never disturbed: `meetcap start` reports the full duration, `chunks closed`
      matches the elapsed time, and every `audio/mic/*.wav` is readable with no `.part` left.
- [ ] During the outage, `meetcap status` prints an `asr queue:` line with outstanding jobs and
      `asr state: behind`, and it still exits `0`.
- [ ] During the outage, `asr/batches/mic/` keeps growing: batching is local and unaffected.
- [ ] Jobs sit in `retry_wait` with a durable `next_retry_at`; they do **not** burn their whole
      attempt budget in a tight loop (inspect `attempt_count` in `meetcap.db`).
- [ ] After the network returns, `meetcap asr resume` (or a later drain) completes the queue and
      the transcript contains the audio recorded during the outage.
- [ ] No streaming endpoint was ever contacted (the adapter has no streaming call at all; confirm
      by inspecting the retained `request.json` files, which name only the submit/query tier).
- [ ] `meetcap start` exited `0` even though the queue was behind.

### 12.4 Restart with work outstanding

```powershell
meetcap start "M4 restart check" --mode offline
# record two minutes, disable the network, then kill the process: taskkill /F /IM meetcap.exe
# re-enable the network
meetcap asr resume
```

- [ ] `meetcap status` reports the session as `INTERRUPTED` (it was not cleanly stopped) and lists
      its outstanding jobs; the already-closed chunks are durable.
- [ ] `meetcap asr resume` picks up every job rather than creating a second billable task for the
      same audio: the `provider_request_id` in `asr_jobs` is unchanged for each job.
- [ ] `transcript/raw.jsonl` can be rebuilt from the per-job `normalized.jsonl` files, and its
      line count equals their total (the derived-vs-durable property of
      `docs/DATA_MODEL.md` section 6).
- [ ] `meetcap asr resume` reports a recovered batch when a batch WAV exists with no job row
      (reproduce by deleting one `asr_jobs` row while keeping its `asr/batches/mic/*.wav`).

### 12.5 An unreadable capture chunk degrades the transcript, not the recording

```powershell
# during a recording, corrupt one closed chunk (keep its length):
#   $f = "...\sessions\<id>\audio\mic\000002.wav"; $b = [IO.File]::ReadAllBytes($f)
#   $b[10] = 0xFF; [IO.File]::WriteAllBytes($f, $b)
# let the window that contains it close (or run `meetcap stop`)
```

- [ ] `events.jsonl` contains `asr.batch.failed` with `reason=chunk_unreadable`, naming the batch
      and stating that the capture chunk is still on disk.
- [ ] The **rest of that window is still submitted**: a subsequent batch contains the other
      chunks, so later windows keep working.
- [ ] The session still reaches a clean terminal state and `meetcap start` exits `0`. A transcript
      gap must never be reported as an interrupted recording.
- [ ] `meetcap start`'s stop summary names the unreadable chunk count and the milliseconds that
      were never transcribed.
- [ ] The corrupted chunk is still on disk under `audio/mic/` (the audio was never deleted).

### 12.6 Provider rejects a batch permanently

Point `[asr.volcengine] credential` at a wrong token and run the 12.2 scenario.

- [ ] `meetcap asr resume` exits non-zero and names the failing job.
- [ ] The session stays `PROCESSING` (audio recoverable), not `COMPLETED`.
- [ ] `meetcap start` still exits `0` when the *recording* was clean.
- [ ] The raw response that carried the rejection is retained in the job's `response.json`.

### 12.7 Artifact and disk footprint

- [ ] A session keeps `audio/mic/*.wav`, `asr/batches/mic/*.wav` and `asr/jobs/*/response.json`;
      it never deletes the source audio.
- [ ] The batch WAVs are real audio: open one in a player and confirm it plays at the session's
      format with no click or truncation at the chunk joins.
- [ ] `meetcap.db` carries no new table or column (the M3 `asr_jobs` schema is unchanged).

---

## 13. Result

| Scenario | Result | Notes |
| --- | --- | --- |
| 1. 2-hour soak | | |
| 2. Default-device change | | |
| 3. Unplug / replug | | |
| 4. Forced kill | | |
| 5. Low disk space | | |
| 6. Timing metadata | | |
| 10.1 M2 buffer accounting | | |
| 10.2 M2 slow consumer | | |
| 10.3 M2 explicit gaps | | |
| 10.4 M2 session repair | | |
| 12.2 M4 live transcription | | |
| 12.3 M4 30-minute outage | | |
| 12.4 M4 restart with work outstanding | | |
| 12.5 M4 unreadable chunk | | |
| 12.6 M4 provider rejection | | |
| 12.7 M4 artifact footprint | | |

M1, M2 and M4 may be described as verified on real hardware only when every row above is filled
in and passing, or when the residual failure is written down here as a known limitation. The M4
rows additionally require a real credential: without one, 12.2-12.4 and 12.6 cannot be run and
must be recorded as not run rather than as passed.
