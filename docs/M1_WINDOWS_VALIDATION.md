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
real Windows run, and specifically needs a **real Volcengine API key** and a **real network**:
the automated coverage substitutes both boundaries. It lives in
`tests/MeetCap.Asr.Tests/AsrBatchBuilderTests.cs`,
`tests/MeetCap.Asr.Tests/AsrJobProcessorTests.cs`,
`tests/MeetCap.Asr.Volcengine.Tests/VolcengineResponseNormalizerTests.cs`,
`tests/MeetCap.AudioPipeline.Tests/ChunkClosedSubscriberTests.cs` and
`tests/MeetCap.Cli.Tests/LiveTranscriptionCommandTests.cs`.

Prerequisites on top of section 2: a working `[asr.volcengine]` configuration (`api_key`, either
a literal or an `env:NAME` reference to the new-console API key), and enough provider quota to
submit the recorded windows. There is no `app_id`, `credential` or `resource_id` to set: the
legacy keys are rejected by `meetcap config validate` with a migration message (issue #26).

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

- [ ] `meetcap start` prints `asr: file ASR, batch window 30s`.
- [ ] **While the recording is still running**, `transcript/raw.jsonl` exists and grows, and
      `transcript/live.md` contains the recognized text.
- [ ] `asr/batches/mic/batch-00000N.wav` files appear, one per closed window, each a playable
      WAV whose duration matches its window, with a `batch-00000N.json` beside it.
- [ ] Every job's `asr/jobs/<job-id>/response.json` is the raw provider response and
      `normalized.jsonl` holds the normalized segments.
- [ ] Segments carry session-relative `start_ms` values that match where the speech actually
      happened in the meeting (check the third or fourth window specifically: a missing batch
      offset shows up as early timestamps repeated for later windows).
- [ ] Where the Seed-ASR 2.0 Standard HTTP interface supports it, segments carry an anonymous
      `speaker_label` such as `speaker_1`, and `speaker_id` / `speaker_name` /
      `speaker_confidence` are still `null`.
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
      by inspecting the retained `request.json` files, which name only the fixed submit endpoint,
      the model `bigmodel`, and the resource id `volc.seedasr.auc`).
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

Point `[asr.volcengine] api_key` at a wrong key and run the 12.2 scenario.

- [ ] `meetcap asr resume` exits non-zero and names the failing job.
- [ ] The session stays `PROCESSING` (audio recoverable), not `COMPLETED`.
- [ ] `meetcap start` still exits `0` when the *recording* was clean.
- [ ] The raw response that carried the rejection is retained in the job's `response.json`.
- [ ] The persisted `error_message` names the provider log id (`X-Tt-Logid=...`) when the provider
      returned one, and never contains the API key.
- [ ] No request in the exchange carried `X-Api-App-Key` or `X-Api-Access-Key` (capture the
      traffic with a proxy if confirmation is wanted; the adapter sends only `X-Api-Key`).

### 12.7 Artifact and disk footprint

- [ ] A session keeps `audio/mic/*.wav`, `asr/batches/mic/*.wav` and `asr/jobs/*/response.json`;
      it never deletes the source audio.
- [ ] The batch WAVs are real audio: open one in a player and confirm it plays at the session's
      format with no click or truncation at the chunk joins.
- [ ] `meetcap.db` carries exactly the two expected schema changes over M3: migration
      `0005_asr_job_provider_log_id` adds the nullable `provider_log_id` column to `asr_jobs`, and
      migration `0006_tos_asr_transport` adds `audio_transport`, `tos_bucket`, `tos_object_key` and
      `tos_cleanup_pending` (section 15). No other table or column is added, and every pre-existing
      `asr_jobs` row survives both rebuilds with its values intact.
- [ ] No `asr_jobs_new` table is left behind by either rebuild:
      `select name from sqlite_master where name like 'asr_jobs%';` lists `asr_jobs` only.
- [ ] `request.json` contains no `data` member (the inline audio is replaced by `inline_bytes`)
      and no API key.

### 12.8 Real Seed-ASR 2.0 Standard HTTP smoke test (required before claiming end-to-end)

This is the one scenario that cannot be covered by an automated test in this repository: the
interface document is the authority for header presence and sequence semantics, and only a real
key can prove the adapter against it. Run it **after** 12.2, against the real service.

```powershell
# [asr.volcengine] api_key = "env:MEETCAP_VOLCENGINE_API_KEY"
$env:MEETCAP_VOLCENGINE_API_KEY = "<new-console API key>"
meetcap config validate
meetcap import .\tests\audio\short-meeting.wav --title "Seed-ASR 2.0 smoke"
```

- [ ] `meetcap config validate` reports `Configuration valid` (no legacy-key error).
- [ ] `meetcap import` exits `0` and prints a `session:` line, a `asr job: ... succeeded` line, and
      a transcript path.
- [ ] The submitted request used `POST /api/v3/auc/bigmodel/submit` with `X-Api-Key`,
      `X-Api-Resource-Id: volc.seedasr.auc`, a UUID `X-Api-Request-Id`, and `X-Api-Sequence: -1`,
      and the query used `POST /api/v3/auc/bigmodel/query` with the **same** request id and no
      `X-Api-Sequence`.
- [ ] The response carried `X-Api-Status-Code: 20000000`, and `asr_jobs.provider_log_id` holds the
      returned `X-Tt-Logid`:
      `select id, status, provider_request_id, provider_log_id from asr_jobs;`
- [ ] `transcript/raw.jsonl` holds the recognized segments, and `request.json` contains no API key.
- [ ] Record the provider log id in the Notes column below as the evidence that the run reached
      Seed-ASR 2.0 Standard HTTP rather than a mock.

Checklist line for the issue:

```text
Manual real-credential smoke test against Seed-ASR 2.0 Standard HTTP: NOT RUN | PASSED (<X-Tt-Logid>)
```

---

## 13. M5 additions — online dual-track capture

Issue #7 (M5, *online meeting dual-track WASAPI capture*) adds behaviour that needs a **real
render endpoint** (speakers/headphones) and, for the process row, a **running meeting
application**. The automated coverage substitutes both boundaries with scripted capture
sources. It lives in `tests/MeetCap.AudioPipeline.Tests/DualTrackRecordingTests.cs`,
`tests/MeetCap.Core.Tests/Transcripts/TranscriptMergerTests.cs`,
`tests/MeetCap.Core.Tests/Capture/AudioDeviceResolverTests.cs` (render resolution) and
`tests/MeetCap.Cli.Tests/CaptureCommandTests.cs` (`start --mode online`).

Prerequisites on top of section 2: a machine with an active render endpoint, and (for 13.5)
a meeting application whose process name is set as `capture.online.process_name`.

### 13.1 System loopback records both tracks

```powershell
meetcap start "M5 online check" --mode online
# join an online meeting; speak into the mic while remote audio plays through the speakers
# for about three minutes, then `meetcap stop` from another shell
```

- [ ] `session.json` records `mode: online` and `tracks: ["mic", "loopback"]`.
- [ ] Independent chunk trees exist: `audio/mic/*.wav` and `audio/loopback/*.wav`, with no
      `.part` left behind.
- [ ] Each track's chunks are independently readable WAVs at the track's native format.
- [ ] `events.jsonl` carries `source: mic` and `source: loopback` chunk events.
- [ ] `track_health` in `session.json` has one entry per track; neither is degraded on a clean
      stop.
- [ ] `meetcap start` exits `0` and prints a per-track line for each track.

### 13.2 Overlapping speech is preserved

- [ ] Speak while remote audio is playing. The merged `transcript/raw.jsonl` contains both a
      `mic` and a `loopback` segment covering the same `start_ms`; neither is deleted
      (docs/ARCHITECTURE.md section 16).
- [ ] The merged timeline is ordered by `start_ms` across both tracks.

### 13.3 One track degrades without corrupting the other

- [ ] While recording, disable/remove the render endpoint (or unplug headphones) so loopback
      loses its source. The loopback track reports `capture.device_lost_fatal` and ends
      degraded; the microphone track keeps recording.
- [ ] The microphone chunks after the loss are still durable; the loopback chunks before the
      loss are still durable.
- [ ] `track_health` marks only the loopback entry `degraded` with `end_reason: device_lost`;
      the mic entry stays healthy.
- [ ] `meetcap start` exits `1` and prints the per-track degraded line for the loopback track
      (`  loopback: N chunk(s), ... degraded (device_lost)`). A lost track makes the session
      degraded, so the run is not reported as clean (`docs/DEVELOPMENT.md` section 8); the
      microphone track's own chunks stay durable and the recording is otherwise intact.

### 13.4 Headphones vs speakers (acoustic duplicate pickup)

- [ ] With **headphones**, the loopback track carries remote audio only; the microphone carries
      local speech only (no acoustic bleed). Document the expectation.
- [ ] **Without headphones** (speakers), the microphone may pick up the remote audio that also
      appears on the loopback track. Confirm this is preserved (both tracks keep it) rather than
      silently deduplicated; echo marking is a later milestone.

### 13.5 Process loopback (supported systems only)

```powershell
# capture.online.loopback_mode = "process", capture.online.process_name = "WeMeet"
meetcap start "M5 process loopback" --mode online
```

- [ ] On a supported Windows/NAudio environment, only the named meeting application's audio
      appears on the loopback track; other system audio does not.
- [ ] Process loopback does not create a new meeting mode — it is the same `online` session,
      just a different `loopback_mode`.
- [ ] An invalid `capture.online.loopback_mode` value fails `meetcap start` before any
      session is created (no session directory, manifest or `audio/loopback/` is written).
- [ ] If the target process is not running, `meetcap start` exits `1` and prints the
      actionable reason on stderr (`Process loopback target '<name>' is not currently
      running ...`). The session *is* created first — the process target is resolved when the
      loopback source is built, after publication — so it is left `INTERRUPTED` with
      `end_reason: capture_start_failed` and an empty `audio/loopback/`.
- [ ] If the running Windows/NAudio combination does not support process loopback, the failure
      is actionable, not a silent fallback to system loopback.
- [ ] Stable process audio does **not** mark the loopback track degraded. `session.json` reports
      the loopback entry with `degraded: false`, `gap_count: 0` and no `end_reason`, and
      `events.jsonl` carries no repeated `capture.discontinuity` for the loopback source.
      Process loopback reports no device position of its own, so this track is placed by its QPC
      timestamp (docs/ARCHITECTURE.md section 8.1); one "device position moved backwards" event
      per buffer is the defect this row checks for, not an acceptable warning.
      ```powershell
      Select-String -Path <data-root>\sessions\<id>\events.jsonl -Pattern 'capture.discontinuity'
      # expect at most the one stream-start flags event per track, never one per buffer
      ```
- [ ] The loopback track's chunks cover the same session span as the microphone track's, so the
      QPC-placed timeline is complete rather than merely free of events. Compare the chunk index:
      ```powershell
      meetcap status --session <id>
      ```
- [ ] A real drop stays observable: stop the target process's audio for a second while capture
      runs, and the loopback track reports one `capture.gap` with the missing milliseconds and is
      marked degraded. Missing audio is reported, never absorbed by shifting later timestamps
      (docs/RELIABILITY.md section 7).

### 13.6 Two sessions into one data root both get their transcript

Record one online meeting, let it finish, then record a second one into the **same** data root with
`asr.enabled = true` and the same provider configuration. Batch numbering restarts per session while
`asr_jobs` is one table shared by every session, so this is the case where two sessions can collide
on one job primary key.

- [ ] The first session's stop summary reports its jobs advanced, and its `transcript/raw.jsonl`
      holds both `mic` and `loopback` segments.
- [ ] The **second** session's stop summary also reports jobs advanced — not
      `N queued, 0 job(s) advanced` — and its own `transcript/raw.jsonl` is written.
- [ ] Both sessions' `asr/jobs/` directories hold their own job artifacts, and each session's
      `session.json` reaches `COMPLETED`.
- [ ] Every row of `select id, session_id, input_artifact from asr_jobs;` belongs to the session
      whose `asr/batches/` tree holds that `input_artifact`. A batch counted as queued with no job
      row of its own is the silent-loss failure mode this check exists for
      (`docs/ARCHITECTURE.md` section 7.2).
- [ ] If a session still reported batches queued with no job, `meetcap asr resume --session <id>
      --force` either transcribes them or fails visibly; a `COMPLETED` session with no transcript
      and no explanation is a defect.

---

## 14. Result

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
| 12.8 Seed-ASR 2.0 real smoke test | | |
| 13.1 M5 system loopback | | |
| 13.2 M5 overlapping speech | | |
| 13.3 M5 one track degrades | | |
| 13.4 M5 headphones vs speakers | | |
| 13.5 M5 process loopback | | |
| 13.6 M5 two sessions, one data root | | |
| 15.1 #29 config redaction | | |
| 15.2 #29 oversized input over TOS | | |
| 15.3 #29 restart re-signing | | |
| 15.4 #29 cleanup failure semantics | | |
| 15.5 #29 TOS outage isolation | | |
| 15.6 #29 20 MiB boundary | | |

M1, M2 and M4 may be described as verified on real hardware only when every row above is filled
in and passing, or when the residual failure is written down here as a known limitation. The M4
rows additionally require a real API key: without one, 12.2-12.4, 12.6 and 12.8 cannot be run and
must be recorded as not run rather than as passed.

Status as of issue #26: **not run**. The automated suite covers the provider contract with a
scripted transport (endpoint, headers, body shape, status handling, secret redaction, log-id
retention), but no real Seed-ASR 2.0 Standard HTTP request has been made from this environment,
so end-to-end provider verification is explicitly **not** claimed
(`docs/DEVELOPMENT.md` section 7).

---

## 15. Issue #29 additions — TOS large-file transport

Issue #29 adds a transport for normalized WAV inputs above 20 MiB. The automated suite covers the
policy and the failure semantics with mocks and fakes: the 20 MiB decision, the actionable failure
when TOS is required but absent, the object-key layout, `audio.url` request shape, secret and
signed-URL redaction, persistence and recovery of the bucket/object key, idempotent cleanup,
cleanup-failure semantics, and that cleanup debt never counts as outstanding work. See
`tests/MeetCap.Asr.Volcengine.Tests/VolcengineTosAudioPublisherTests.cs`,
`tests/MeetCap.Asr.Volcengine.Tests/VolcengineAsrProviderTests.cs`,
`tests/MeetCap.Asr.Tests/AsrJobProcessorTests.cs` and
`tests/MeetCap.Persistence.Tests/Storage/SqliteMigratorTests.cs`.

Nothing below can be covered by CI: it needs a real private TOS bucket, a real AK/SK scoped to it,
and a real Seed-ASR key. Do not record these as passed without running them.

Prerequisites on top of section 2:

- a private TOS bucket in the region under test;
- an AK/SK pair allowed to `tos:PutObject`, `tos:GetObject` and `tos:DeleteObject` on that
  bucket's `meetcap-asr/*` prefix, and nothing else;
- a normalized WAV above 20 MiB (any 21 MiB mono 16-bit 48 kHz WAV will do);
- the three-day lifecycle expiration rule recommended in `docs/CONFIGURATION.md` section 8.1
  applied to `meetcap-asr/`.

### 15.1 Configuration and redaction

```powershell
# [asr.tos] bucket/region/endpoint set; access_key/secret_key as env: references
$env:TOS_ACCESS_KEY = "<AK>"
$env:TOS_SECRET_KEY = "<SK>"
$env:MEETCAP_VOLCENGINE_API_KEY = "<new-console API key>"
meetcap config show
```

- [ ] `meetcap config show` prints the `[asr.tos]` section with `access_key` and `secret_key` shown
      as `***`, and prints the Volcengine API key as `***`.
- [ ] No AK or SK value appears anywhere in the output, and neither does the value behind the
      `env:` reference if a literal credential is used.
- [ ] `meetcap import` logs do not contain the AK/SK either.

### 15.2 Oversized input over TOS

```powershell
# place an oversized normalized WAV under .\tests\audio\ or import the source and let it normalize
meetcap import .\big-meeting.wav --title "TOS large-file smoke"
```

- [ ] The job row records the transport and the stable identity:
      `select id, status, audio_transport, tos_bucket, tos_object_key, tos_cleanup_pending from asr_jobs;`
      shows `audio_transport = tos` with a non-null `tos_bucket` and `tos_object_key`.
- [ ] `tos_object_key` matches `meetcap-asr/<16 hex>/<yyyy>/<MM>/<dd>/<job>.wav` and contains no
      meeting title, speaker name, or credential.
- [ ] The object exists in the bucket **before** the provider submission is accepted (check the
      bucket between the `submitting` and `submitted` states, or from the object's own
      `LastModified`).
- [ ] The object is **not** public-read: an unauthenticated GET against the object URL fails,
      while the presigned URL succeeds until it expires.
- [ ] `request.json` for the job has `"transport":"tos"` with `tos_bucket`/`tos_object_key`, and
      contains no `X-Tos-Signature`, no signed query string, and no `data` member.
- [ ] `transcript/raw.jsonl` holds the recognized segments and the local input WAV is untouched.
- [ ] After the job reaches `succeeded`, `tos_cleanup_pending` is `0`, the object is gone from the
      bucket, and `events.jsonl` contains `asr.audio.released`.
- [ ] `select id, status from asr_jobs;` still shows `succeeded`: cleanup never changed the job's
      outcome.

### 15.3 Signed URL validity and restart re-signing

- [ ] With a job left `submitted` (kill the process between submit and poll), a later
      `meetcap asr resume` continues by polling and **does not** upload a second object; the
      bucket holds one object for that job.
- [ ] A restart never re-uses the old signed URL from durable state, because none is stored.

### 15.4 Cleanup failure is not a transcription failure

```powershell
# revoke tos:DeleteObject (or point [asr.tos] at a bucket the AK cannot write to), then resume
meetcap asr resume
```

- [ ] The job stays `succeeded`, the transcript stays complete and readable, and
      `tos_cleanup_pending` stays `1`.
- [ ] `events.jsonl` contains `asr.audio.release_failed`, and the error message contains no
      credential and no signed URL.
- [ ] Restoring `tos:DeleteObject` and running `meetcap asr resume` again releases the object and
      clears `tos_cleanup_pending`.
- [ ] Running `meetcap asr resume` when there is nothing left to do does not report the session as
      unfinished because of cleanup.

### 15.5 Offline / TOS outage isolation

- [ ] With TOS unreachable (block the endpoint or unset the AK), an oversized import leaves the
      recording and the local WAV durable and does not open a streaming request; the job is
      retryable or fails actionably.
- [ ] A live session keeps recording while such a job fails; capture is never blocked on TOS work.
- [ ] A small (<=20 MiB) import with `[asr.tos]` absent or empty still succeeds on the inline path.

### 15.6 20 MiB boundary

- [ ] An artifact of exactly 20 MiB is submitted inline as `audio.data` and creates no object in
      the bucket.
- [ ] An artifact of 20 MiB + 1 byte is submitted as `audio.url` and does create one.

Checklist line for the issue:

```text
Manual real-TOS + real-Seed-ASR >20 MiB smoke test: NOT RUN | PASSED (<tos_object_key>, <X-Tt-Logid>)
```

---
