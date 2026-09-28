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

One exception is recorded, and it is not a completed checklist: two of the section 13.5
process-loopback timeline rows were run on real hardware on 2026-09-19 against the
Issue #33 fixed build and passed (section 14.1). A second real-hardware acceptance pass,
against the published `v0.3.0-rc.1` release artifact on 2026-09-28, is recorded in
sections 14.2 and 16.3: it verifies the section 16.2 Bluetooth recovery and fatal-side rows
and the section 13.5 negative rows, and it **fails** the section 13.5 content row — the
process-loopback track records digital silence while the target plays (issue #38).
Everything else in this document remains unrun.

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

> **Partial run recorded (2026-09-19, Issue #33).** The two timeline rows below — stable process
> audio is not marked degraded, and the QPC-placed chunks cover the whole session span — were run
> on real hardware against a fixed build with an actively playing target process, and **passed**.
> Every other row here, and every row in the rest of section 13, is still unrun: those need a real
> meeting application and endpoint changes this run did not perform. The full record, including what
> was and was not covered, is in section 14.1.
>
> **Acceptance run against `v0.3.0-rc.1` (2026-09-28, section 14.2).** The negative rows and both
> timeline rows were re-run against the published RC artifact; the content row **failed**: the
> process-loopback track records digital silence on the acceptance machine while the target plays
> (issue #38), so the real-drop row could not be exercised either. No checkbox below is ticked; the
> per-row result is pinned in section 14.2.
>
> **Issue #38 note.** The timeline rows above can pass on a track that carries no audio at all, so
> they are no longer accepted on their own: the content rows below are the ones that decide whether
> process loopback works, and `session.json` now carries the counters they are read from. The
> investigation into the failed rc.1 run, and the re-validation of the same target class outside the
> confined acceptance session, are section 14.3 below.

- [ ] On a supported Windows/NAudio environment, only the named meeting application's audio
      appears on the loopback track; other system audio does not.
- [ ] **The loopback track actually contains the target's audio.** `session.json` reports the
      loopback entry with a non-zero `track_health[].audio_content.non_zero_samples` and a
      `peak_abs_sample` that matches what the target played, and the WAV decodes to non-zero
      audio. This row is what issue #38 was: every other row passed while this one was false.
      ```powershell
      (Get-Content <data-root>\sessions\<id>\session.json | ConvertFrom-Json).track_health
      # expect loopback: audio_content.all_silent = false, non_zero_samples > 0
      ```
- [ ] **A process-loopback track that carried no decodable content is reported, not hidden.** If the
      target renders nothing for the whole session, the track is `degraded` with
      `degraded_reason: "silent_process_loopback"`, `events.jsonl` carries exactly one
      `capture.silent_track` for it, and the stop summary prints the counts and the alternative
      `loopback_mode = "system"`. A track that received no samples at all is the same report with
      `degraded_reason: "empty_process_loopback"`. In both cases the session still `COMPLETED` and
      the track's chunks (if any) are still closed and indexed — the missing content is stated,
      never fatal, and never a silent `healthy` summary (docs/RELIABILITY.md section 17). The
      baseline system-loopback and microphone tracks are not flagged by this rule, because all-zero
      audio there is legitimate.
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
| 1. 2-hour soak | **partial** (4.7 min healthy; section 14.2) | full 2 h not run; 2026-09-28, rc.1 artifact |
| 2. Default-device change | | |
| 3. Unplug / replug | | |
| 4. Forced kill | **PASS** (section 14.2) | hashes identical, `.part` repaired readable, idempotent, live-status safe; 2026-09-28 |
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
| 13.1 M5 system loopback | **PASS** (synthetic tone source, not a meeting app; section 14.2) | rc.1 artifact, 2026-09-28 |
| 13.2 M5 overlapping speech | | |
| 13.3 M5 one track degrades | | |
| 13.4 M5 headphones vs speakers | | |
| 13.5 M5 process loopback | **FAIL** (content row; sections 14.1–14.2, issue #38); rc.2 acceptance pending | timeline and negative rows pass 2026-09-19 (section 14.1) and 2026-09-28 (section 14.2); the rc.1 loopback track delivered digital silence on the acceptance machine while the target played (issue #38); the cause investigation and the same-binary re-validation are section 14.3 |
| 13.6 M5 two sessions, one data root | | |
| 15.1 #29 config redaction | | |
| 15.2 #29 oversized input over TOS | | |
| 15.3 #29 restart re-signing | | |
| 15.4 #29 cleanup failure semantics | | |
| 15.5 #29 TOS outage isolation | | |
| 15.6 #29 20 MiB boundary | | |
| 16.2 #34 Bluetooth reconnect recovers | **partial** (section 16.3) | recovery + gap accounting verified on real headset 2026-09-28; loopback track recorded no audio after its restore |
| 16.2 #34 unrecovered outage is a gap | **PASS** (section 16.3) | `device_lost_fatal` + `capture.gap not_captured` + non-zero `gap_count` verified 2026-09-28 |

M1, M2 and M4 may be described as verified on real hardware only when every row above is filled
in and passing, or when the residual failure is written down here as a known limitation. The M4
rows additionally require a real API key: without one, 12.2-12.4, 12.6 and 12.8 cannot be run and
must be recorded as not run rather than as passed.

Status as of issue #26: **not run**. The automated suite covers the provider contract with a
scripted transport (endpoint, headers, body shape, status handling, secret redaction, log-id
retention), but no real Seed-ASR 2.0 Standard HTTP request has been made from this environment,
so end-to-end provider verification is explicitly **not** claimed
(`docs/DEVELOPMENT.md` section 7).

### 14.1 Issue #33 process-loopback timeline rows — partial run, 2026-09-19

Issue #33's acceptance criterion *"reproduce and verify process loopback with an actively playing
target process"* needs the process-loopback path to be exercised on real hardware against a real
render endpoint and a real playing process, which is what section 13.5's timeline rows check. That
run was performed on the machine below, against the fixed build, and is recorded here in full.

Environment (section 1 fields):

| Field | Value |
| --- | --- |
| Date | 2026-09-19 |
| Tester | coding agent, unattended session (`ISSUE_AGENT-33-S05`) |
| Windows version / build | Windows 11 Home, build 26200 (10.0.26200), AMD64 |
| MeetCap commit | `7709b118ed4a14a7a93d98bb9eace7f1309df9f5` (`fix/33-process-loopback-timeline`) |
| Machine | DESKTOP-2H6MG5P |
| Microphone (from `meetcap devices`) | `麦克风阵列 (Realtek(R) Audio)`, id `{0.0.1.00000000}.{c3842cfa-9574-418e-8c47-18da833f7eab}` |
| Render endpoint (loopback source) | `扬声器 (Realtek(R) Audio)`, id `{0.0.0.00000000}.{97aa8bbe-5462-4856-916e-4ee6569b8d1e}` |
| `capture.chunk_seconds` | 60 |
| Target process | `ffplay.exe` (`C:\Program Files\ffmpeg\bin\ffplay.exe`) looping a tone WAV from `.manual-validation/process-audio-data/tone.wav` |
| Build run | `dotnet build src/MeetCap.Cli/MeetCap.Cli.csproj -c Release -o <temp>`, so the live soak's locked `bin/Release` was not touched |

Method: `capture.online.loopback_mode = "process"`, `capture.online.process_name = "ffplay"`,
ASR and speakers disabled, `meetcap start … --mode online` for 58.2 s with the target actively
playing, then `meetcap stop`. Session `ses_20260919T092817Z_3910e1e6`; artifacts under an
out-of-tree data root (temp), not the repository's `.manual-validation` tree.

Result rows:

| Row | Result |
| --- | --- |
| Stable process audio does not mark the loopback track degraded: `degraded: false`, `gap_count: 0`, no `end_reason`, and no repeated `capture.discontinuity` for the loopback source | **PASSED.** `session.json`: session `degraded: false`, `end_reason: stop_requested`, `gap_count: 0`, `gap_total_ms: 0`; `loopback` track health `degraded: false`, `gap_count: 0`, `gap_total_ms: 0`, `end_reason` absent, `chunks_closed: 1`, 20,543,544 bytes. `events.jsonl` holds exactly one `capture.discontinuity` in the whole session and it is the microphone's first-buffer `DataDiscontinuity` stream flag; the loopback source produced none. `session.started` records the declaration this fix added: `source='loopback' … clock='qpc'` alongside `source='mic' … clock='device_position'`. |
| The loopback track's chunks cover the same session span as the microphone track's | **PASSED.** `session.stopped` reports `end_ms: 58230`, and the loopback chunk index row is `start_ms: 0, end_ms: 58230`, identical to the microphone's. Both WAVs decode to exactly 58.23 s of audio at their own native formats (loopback 44,100 Hz stereo float, mic 48,000 Hz stereo float). The loopback track therefore spans the session rather than stopping at the first buffer. |

Additional facts recorded, because they are what the fix's residual risk was about:

- The loopback chunk index row carries `device_position_frames = 0` with a real
  `qpc_position_ticks = 880015035985`. That is the shape the fix is built on: this capture path
  reports no device position of its own, and on this run the fixed build placed the track by QPC
  for the whole session.
- Both tracks closed cleanly on one chunk, no `.part` was left behind, `dropped_packets: 0`,
  `stall_events: 0`, and `meetcap start` exited `0` printing a per-track line. This is the online
  happy path, not the `system` baseline: the baseline is not claimed here.

**Explicitly not covered by this run** — these are still unrun and must not be read as passed:

- **The other five rows of section 13.5.** Rows 1-5 (only the named application's audio appears;
  process loopback is still the same `online` session; an invalid `loopback_mode` fails before any
  session is written; a missing target process exits `1` with the actionable reason and leaves the
  session `INTERRUPTED`; an unsupported Windows/NAudio combination fails actionably) were not
  attempted.
- **A real drop while the target plays.** The remaining section 13.5 row — stopping the target
  process's audio for a second and confirming one `capture.gap` carrying the missing milliseconds
  — was not attempted. The gap path is covered by the automated suite
  (`ProcessLoopbackTrack_MissingAudioInItsOwnClock_IsStillReportedAsAGap`) and is **not** claimed
  as hardware-verified.
- **Sections 13.1-13.4 and 13.6**, and every earlier scenario in this document.
- **A real meeting application.** The target was `ffplay`, the reproduction's own stand-in, not a
  meeting client whose process tree has to be resolved.

No section 13.5 checkbox was ticked by this run: the rows are still `- [ ]` above, because the run
covered some of what they ask and not the rest, and a ticked box would claim the whole row. The
per-row result is pinned here instead.

### 14.2 v0.3.0-rc.1 process-loopback and system-loopback rows — acceptance run, 2026-09-28

Run against the **published RC artifact** (not a source build), as part of the v0.3.0-rc.1 stable
release acceptance. All sessions used an out-of-tree data root, retained with the rest of the
acceptance evidence.

Environment (section 1 fields):

| Field | Value |
| --- | --- |
| Date | 2026-09-28 |
| Tester | coding agent, unattended session (DSH `session-7eeb9e88`) |
| Windows version / build | Windows 11 Home, build 26200 (10.0.26200), AMD64 |
| MeetCap artifact | GitHub Release `v0.3.0-rc.1` zip, SHA256 `522fa569cc946a9bcd69af842e8f39817ea6a69816bcf20bff550e6e8681b858`; `meetcap --version` = `0.3.0+677ae2cde62fd07fb3b5f77e702e64ab36d84a63` |
| Machine | DESKTOP-2H6MG5P |
| Microphone | `麦克风阵列 (Realtek(R) Audio)`, `{0.0.1.00000000}.{c3842cfa-9574-418e-8c47-18da833f7eab}` |
| Render endpoint | `扬声器 (Realtek(R) Audio)`, `{0.0.0.00000000}.{97aa8bbe-5462-4856-916e-4ee6569b8d1e}` |
| `capture.chunk_seconds` | 30 |
| Target process | Microsoft Edge (temp profile) looping a generated 440 Hz 48 kHz stereo sine WAV; the tone's presence on the endpoint was verified by a system-loopback control in the same minutes (RMS −7.13 dB) at locked master volume |

Method note: `capture.online.process_name = "msedge"`. The 2026-09-19 run used `ffplay`; on this
date a machine-local security policy hides non-store-signed executables (`ffplay`, `python`) from
the process enumeration performed by unsigned applications, so the doc's own
meeting-application-shaped target (`msedge`) was substituted. That constraint is machine-local and
is not claimed as product behavior.

Per-row results (the section 13.5 rows):

| Row | Result |
| --- | --- |
| Stable process audio does not mark the loopback track degraded (`degraded: false`, `gap_count: 0`, no `end_reason`, no per-buffer `capture.discontinuity`) | **PASS on timing, vacuous on content.** Every process-loopback session completes cleanly: `degraded: false`, `gap_count: 0`, `gap_total_ms: 0`, no `end_reason`, `session.started` declares `clock='qpc'` for the loopback track, and the only `capture.discontinuity` in each session is the microphone's first-buffer stream flag. The QPC timeline is exact: consecutive chunk anchors differ by exactly 3,000,000 ticks per 30 s chunk. But the track's audio is **digital silence** (issue #38), so the row's intent — "its audio is fine" — is not met. |
| The loopback track's chunks cover the same session span as the microphone track's | **PASS on timing, vacuous on content.** Chunk index `0→30000→48000` identical to the microphone's; WAVs decode to 30.000 s + 18.000 s = 48.000 s = `session.stopped` `end_ms`. Content is silence (issue #38). |
| Only the named meeting application's audio appears on the loopback track | **FAIL.** The track carries no audio at all — the named application's audio is absent (issue #38). For the record, no out-of-tree audio leaked either: an 880 Hz control tone from a separate process, verified present on the endpoint, was never captured by any process-loopback run. |
| Process loopback does not create a new meeting mode | **PASS.** Same `--mode online` session; `config_snapshot.online.loopback_mode = "process"`. |
| An invalid `capture.online.loopback_mode` value fails before any session is created | **PASS.** `loopback_mode = "bogus"` → exit 1, actionable message naming the allowed values, and no new session directory. |
| Target process not running → exit 1 with the actionable reason, session left `INTERRUPTED` | **PASS.** With no `msedge` running: exit 1, actionable stderr naming the target and the `system` fallback, session `INTERRUPTED` with `end_reason: capture_start_failed` and an empty `audio/loopback/`. |
| Unsupported Windows/NAudio combination fails actionably, not silently | **NOT EXERCISABLE on this machine — and that is the defect.** The activation succeeds here, so the actionable path never fires; instead the track silently records zeros (issue #38). |
| A real drop stays observable: one `capture.gap` with the missing milliseconds, track degraded | **BLOCKED by issue #38.** The silent stream keeps delivering packets even when the target process tree is killed, so no packet-level gap can arise; content-level drops cannot be detected on a track that never had content. |

**Finding recorded as [issue #38](https://github.com/lybym/MeetCap/issues/38):** the
process-loopback track records digital silence while the target plays. The issue contains a
minimal NAudio 3.1.0 reproducer (no MeetCap code): the `WasapiRecorderBuilder.WithProcessLoopback`
stream delivers packets at the normal 10 ms cadence with an advancing QPC and `peakAbsSample = 0`
across every candidate process root, while a system-loopback control over the same endpoint
captures the same playback at −3.1 dB to −7.1 dB at locked master volume. MeetCap's call shape
matches the documented builder API; the defect sits in the NAudio wrapper or the Windows 26200
audio engine, and the failure is fully silent at the product level.

Section 13.1 (system-loopback baseline) was exercised in the same pass and **passed**:
`PLS_system_loopback_baseline` and two further control sessions record `mode: online`, both
tracks, independent `audio/mic/` and `audio/loopback/` chunk trees, per-source chunk events, both
`track_health` entries non-degraded on a clean stop, start exit 0 with per-track lines, and the
loopback track carries the played tone (RMS −11.18 dB). The source was a synthetic tone, not a
real meeting application; sections 13.2, 13.4 and 13.6 remain unrun.

Two M1/M2 mechanical rows were exercised in the same pass against the same artifact:

- **Scenario 1, offline soak — partial (4.7 min of the required 2 h).**
  `ses_20260928T072149Z_4abf724b`, offline on the Realtek mic array, 48 kHz float:
  `COMPLETED`, `degraded: false`, `gap_count: 0`, 19 contiguous chunks `0→281720 ms` with no
  `capture.gap` / `capture.buffer_overflow` / `capture.device_lost` events and no `.part` left
  behind; only the microphone's first-buffer stream-flag discontinuity appears. The 2-hour
  duration was not run, so the row stays partial.
- **Scenario 4, forced kill — PASS.** `ses_20260928T072751Z_5c853b64`: three chunks closed and
  hashed, the process was killed (its own PID, `Stop-Process -Force`), a `000004.wav.part` was
  left; `meetcap status` reported the incomplete session and repaired the chunk; the three
  closed chunks hash **identically** before and after; the repaired `000004.wav` decodes
  (ffprobe, 4.762 s); `session.json` is `INTERRUPTED` with `recovered_at` set; a second `status`
  reports nothing further (idempotent). The live-recording half also passed: `meetcap status`
  during a running session reports the active session, finds nothing to recover, writes no
  `session.recovered`/`audio.chunk.corrupt` events, and the session stops cleanly afterwards.
### 14.3 Issue #38 process-loopback content — investigation and re-validation, 2026-09-28

Section 14.2 above records the rc.1 acceptance run in which every process-loopback row passed on
timing and the track carried nothing but digital zeros. This section records what was established
about that failure on the same machine, later the same day, and it is the basis for the fix that
ships with the `0.3.0-rc.2` release candidate. Section 14.2 belongs to the `0.3.0-rc.1` provenance:
it is present on this branch and not on `main`, whose copy of this document starts this material at
section 14.3.

Environment (section 1 fields):

| Field | Value |
| --- | --- |
| Date | 2026-09-28 (same day as section 14.2, no reboot in between) |
| Machine | DESKTOP-2H6MG5P (same machine as section 14.2) |
| Windows version / build | Windows 11 Home, build 26200 (10.0.26200), AMD64 |
| Render endpoint | `扬声器 (Realtek(R) Audio)`, `{0.0.0.00000000}.{97aa8bbe-5462-4856-916e-4ee6569b8d1e}` (same as section 14.2) |
| NAudio | 3.1.0 (the version in `0.3.0-rc.1`) |
| Target process | Microsoft Edge 154.0.4258.37, isolated profile, looping a 440 Hz 48 kHz stereo WAV (`docs/../rc` validation tree, same generated tone as section 14.2) |

Method. The tone's presence on the endpoint was verified by a system-loopback control immediately
before the process-loopback captures (`peak 0.623`, `nonZeroSamples 767960/768000`). Process
loopback was then pointed at the browser main process, so `IncludeTargetProcessTree` covered
Chromium's audio service, and the same captures were repeated with three independent readers of the
process-loopback stream: the **unaltered `naprobe.exe` binary whose output is quoted in section
14.2**, NAudio 3.1.0 built with the same builder shape MeetCap uses, and a MeetCap-owned raw-WASAPI
probe that does not reference NAudio at all.

| Capture | Result |
| --- | --- |
| `naprobe.exe` (unchanged rc.1 reproducer) over every `msedge` root | **AUDIO for the playing tree**: `pid=1840: packets=298 bytes=1051344 peak=0.762047 -> AUDIO`. `pid 1840` is the browser's `utility --utility-sub-type=audio.mojom.AudioService` child, i.e. the process hosting the WASAPI render stream. The other roots reported `peak=0 -> silence`, and they were the idle Edge trees (no rendered audio in them), which is what process loopback is supposed to do. |
| NAudio 3.1.0, `WithProcessLoopback(pid, IncludeTargetProcessTree)`, `WithSharedMode`, `WithEventSync`, default 44.1 kHz float format and 100 ms buffer — byte-for-byte the shape section 14.2 failed with | `packets=799`, `silentFlagged=0`, `peak=0.738909` → **non-zero content** |
| Raw-WASAPI probe, same 44.1 kHz float `WAVEFORMATEX` (tag 3), `LOOPBACK | EVENTCALLBACK`, 100 ms buffer, `device_position_frames = 0` and 10 ms QPC cadence — the same shape section 14.2 recorded | `packets=799`, `peak=0.738907`, `nonZeroSamples=701944/704718` → **non-zero content** |
| Raw-WASAPI probe, render-endpoint mix format (48 kHz float) | `packets=799`, `peak=0.737548` → non-zero content |
| System-loopback control, same minutes | `peak=0.623016` → non-zero content |
| Same session, non-Edge targets (a Win32 media player playing the same tone; a second `pwsh.exe` process rendering through winmm) | Non-zero content through both NAudio and the raw probe |

What this establishes, and what it rules out:

- **NAudio 3.1.0's process-loopback wrapper is not the cause.** The same package, the same builder
  call and the same parameters captured the target's audio; the activation path matches the
  documented contract and the `naprobe.exe` output above is the same binary whose silence supplied
  the issue's minimal-reproducer evidence.
- **A MeetCap-owned backend would not have changed the outcome.** The raw probe walks the same
  engine path (the activation, the virtual device and the tap are the operating system's, not the
  wrapper's) and produced the same timing shape with the same content. This is why the shipped fix
  is content telemetry and a silent-track verdict rather than a wrapper replacement.
- **The Windows 26200 process-loopback engine is not the cause**, on this evidence: it delivered
  non-zero content for the same target class on the same build.
- **The acceptance run's session confinement is the remaining explanation, and it was directly
  observable.** The rc.1 run was executed inside a confined agent session. Later in the same
  machine state, that confinement could be observed denying exactly the class of operation a
  cross-process audio tap needs: Chromium could not start at all (its IPC channel creation was
  denied by the session policy, so `msedge.exe` aborted during startup), `git`'s HTTPS transport
  could not create its helper's pipes, and the build and test hosts could not spawn their child
  processes. None of those denials existed in the session that produced the table above.

Residual uncertainty, stated rather than implied: the failure was *silent*, so the precise
engine-side step the confinement broke (handle duplication, shared-section mapping, or the
process-tree enumeration the tap performs) could not be observed from inside the confined session,
and the conclusion here is behavioural — the same code, target and machine produce silence or
content depending on the session's confinement — not a step-level diagnosis. A second machine or
OS build was **not** available, so the `NOT RUN` verdict for that separation stands.
Section 14.2's parenthetical claim that a machine-local policy hides `ffplay` and `python` from
process enumeration is not supported by a later check: both are installed on this machine
(`C:\Program Files\ffmpeg\bin\ffplay.exe`, the `python.exe` execution alias), which is consistent
with the confined session's process operations misbehaving rather than with a machine policy. That
sentence is left in section 14.2 as provenance for what the acceptance run reported; the corrected
reading is this one.

**Pending, appended when the release candidate is published:** the same rows run against the
`v0.3.0-rc.2` release artifact, which is the artifact `docs/RELIABILITY.md` section 17's
`audio_content` counters exist for.

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

## 16. Issue #34 additions — recovery after a Bluetooth device reconnect

Issue #34's Acceptance Criteria split cleanly into what the automated suite can prove and what
only real hardware can. The recovery window, the per-track reconnect behaviour, the gap
quantification and the reporting are covered by the automated suite
(`tests/MeetCap.AudioPipeline.Tests/DeviceRecoveryWindowTests.cs`). The criterion *"verify with a
real Bluetooth microphone and loopback render endpoint on Windows"* requires a physical
Bluetooth audio device and has **not** been run.

### 16.1 Why the real-hardware criterion is not run here

The machine this work was prepared on has no Bluetooth audio endpoint at all: `meetcap devices`
lists two capture endpoints (Realtek onboard microphone array, Steam Streaming Microphone) and
four render endpoints (Realtek speakers, an AMD HDMI output, Steam Streaming Microphone and
Steam Streaming Speakers). None of them is a Bluetooth device, and disconnecting/reconnecting a
Bluetooth headset cannot be exercised without one.

The issue's own reference artifacts (session `ses_20260919T082247Z_2bd30003`) were produced on a
machine that did have the Sony WF-1000XM5 configured, and are retained read-only under
`.manual-validation/hotplug-data/` as the reproduction evidence for the original defect. They
are input to this fix, not evidence of it.

```text
Manual real-Bluetooth reconnect test (issue #34): NOT RUN — no Bluetooth audio endpoint on this machine
```

### 16.2 The run to perform on a machine with the headset

Same shape as the issue's reproduction, with the fix in place and the longer recovery window
configured:

```toml
[capture]
device_recovery_seconds = 20      # the default; a shorter value makes the run cheaper

[capture.online]
microphone_device_id = "{0.0.1.00000000}.{e285b8cf-2437-48f2-a08a-aa2be8a05d51}"
loopback_mode = "system"
render_device_id = "{0.0.0.00000000}.{97644e66-70d1-4286-8854-979a505009d3}"

[asr]
enabled = false
```

```powershell
meetcap start "BT reconnect" --mode online
# confirm both tracks are recording, then disconnect the headset from Windows Bluetooth
# wait ~10 s (well past the old three-retry budget), then reconnect it
meetcap stop
```

Checklist for the issue:

- [ ] `events.jsonl` contains exactly one `capture.device_lost` per track and **no**
      `capture.device_lost_fatal`, because the headset returned inside the window.
- [ ] `events.jsonl` contains one `capture.device_restored` per track, and each carries a
      `gap_ms` that is approximately the real disconnect duration.
- [ ] Each track resumed with a new WAV chunk after the restore, so both tracks wrote audio on
      both sides of the outage (`audio/mic/` and `audio/loopback/`).
- [ ] `session.json` reports `gap_count >= 1` and `gap_total_ms` approximately the real
      disconnect duration, rather than `0`.
- [ ] The session did **not** end with `end_reason = "device_lost"`, and the unaffected track
      kept recording throughout.

A second run with the headset left switched off until the window expires checks the fatal side:

- [ ] `capture.device_lost_fatal` is written once for the track that stayed gone, and
      `capture.gap` with `reason = "not_captured"` quantifies the unrecovered outage.
- [ ] `session.json` reports that outage in `gap_count` / `gap_total_ms` rather than `0`
      (this is the reporting the issue observed as broken).
- [ ] The track's already-captured audio is durable as a closed `.wav` with no `.part` left.

### 16.3 v0.3.0-rc.1 Bluetooth reconnect rows — acceptance run, 2026-09-28

Run against the **published RC artifact** on the section 14.2 machine, with a real Bluetooth
endpoint this time: a Sony **WF-1000XM5** whose endpoint ids are exactly the ones section 16.2's
example config names (`{0.0.1.00000000}.{e285b8cf-...}` capture, `{0.0.0.00000000}.{97644e66-...}`
render). Both tracks were pinned to the headset per section 16.2, `chunk_seconds = 15`, ASR and
speakers disabled, a tone playing through the headset to keep the A2DP stream alive. All sessions
used the out-of-tree data root, retained.

Disconnect/reconnect mechanism: PnP-disable of the `BTHENUM\DEV_88C9E8FD1ECC` device node **plus**
its A2DP MEDIA devnode and the `BTHHFENUM` Hands-Free audio devnode, then PnP-enable. Verified
first: disabling only the `BTHENUM` device node does **not** remove the audio endpoints (the first
attempted run, `BT1_recover_default20`, saw no device loss at all and is discarded as a method
failure, not a product result). The Windows-side times of each disable/enable are retained in the
acceptance log.

Runs:

| Run | `device_recovery_seconds` | Outage | Result |
| --- | --- | --- | --- |
| BT-2 recovery | default (config snapshot records `20`) | ~10 s disabled + ~7 s re-enumeration | `capture.device_lost` and `capture.device_restored` on **both** tracks; loopback `gap_ms = 17111` (matches the real outage), mic `gap_ms = 1008`; a `capture.gap` was placed for each (mic placed by the next buffer, `reason = "the device skipped audio between buffers"`; loopback terminal, `reason = "not_captured"`); `meetcap start` summary reports `audio gaps: 2 (18119 ms missing)`; session `COMPLETED` with `end_reason: stop_requested` — **no** `device_lost_fatal`. The mic resumed with new chunks after its restore (28.5 s–46.6 s). Observation recorded honestly: the loopback track captured no audio after its `device_restored` — its only chunk ends exactly at the loss moment. |
| BT-3 unrecovered | default (20) | disabled, never reconnected; stop issued after the window expired | `capture.device_lost_fatal` written once for the loopback track ("could not be recovered; the track is ending and its final chunk is closed and durable"); `capture.gap` with `reason = "not_captured"`, `gap_ms = 20124`, "the capture device did not return within the recovery window"; summary reports `audio gaps: 1 (20124 ms missing)` — **`gap_count` is not 0**, which is exactly the reporting the issue observed as broken. The mic track (whose HFP endpoint survived this disable) kept recording the whole session, demonstrating that one track's unrecoverable loss ends only that track; the session itself stays alive until stopped and completes `COMPLETED`. |
| BT-4 explicit non-default window | `10` (config snapshot records `10`) | ~16 s (disable latency exceeded the plan) | outage longer than the window → `device_lost_fatal` at window expiry + `capture.gap not_captured gap_ms = 10071`. Together with BT-2 (a ~17 s outage recovered under the 20 s default) this shows the non-default value is honored in both directions. |
| BT-5 stop inside the window | attempted with 60 | — | dedicated run not achieved: endpoint re-registration latency repeatedly pushed the stop past the window. The shape-(b) terminal event ("the recording was stopped while this track was still recovering its capture device, with recovery time left in its window") was however produced, with the exact documented language, by BT-2's loopback track. |

Reopened-endpoint format change: **NOT RUN.** No `capture.format_changed` event occurred in any
run, and no stable way to manufacture the A2DP/HFP format switch was found; per the rules above
this is recorded as not run, not as passed.

Honest caveats. (1) The disconnect is a software PnP disable, not a physical headset power-off; it
removes the same endpoints Windows removes on hardware disconnect, but it is not literally the
manual gesture. (2) The HFP (capture) endpoint survived some disable windows — Windows kept the
active session alive — so the "the other track kept recording" evidence in BT-3 comes from that
survival rather than from a second, separate healthy device. (3) The automated
`DeviceRecoveryWindowTests` suite remains the only coverage for the attempt-budget derivation and
window edge cases these runs cannot reach.

Checklist status: no §16.2 checkbox is ticked. Block 1's row "each track resumed with a new WAV
chunk after the restore, so both tracks wrote audio on both sides" did not fully pass (the
loopback track wrote nothing after its restore), so the block is left unticked with the per-row
evidence pinned here; block 2's three rows are fully evidenced by BT-3 and are marked **PASS** in
the section 14 table.

---
