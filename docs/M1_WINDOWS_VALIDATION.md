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

## 9. Result

| Scenario | Result | Notes |
| --- | --- | --- |
| 1. 2-hour soak | | |
| 2. Default-device change | | |
| 3. Unplug / replug | | |
| 4. Forced kill | | |
| 5. Low disk space | | |
| 6. Timing metadata | | |

M1 may be described as verified on real hardware only when every row above is filled in
and passing, or when the residual failure is written down here as a known limitation.
