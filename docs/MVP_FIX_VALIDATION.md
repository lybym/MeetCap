# MVP acceptance fixes — 2026-10-11

Scope: #41 (parent #1, architecture #9), and diagnostic aggregation from #40.
Base: released 0.3.0 / main `0b8ab1dde60dc5c5950312a0d6951e5fc51ce20f`.

## Reproduced defects and changes

- Imported audio was absent from the speaker extractor, which read only `asr/batches`.
  Attribution now follows the import's durable artifact mapping and reads just the selected
  PCM/float WAV frames. Live extraction selects the correct source track. Cross-track label
  collisions stay unknown with a warning rather than inferring identity from the wrong track.
- Native five-minute 48 kHz stereo float batches exceeded the 20 MiB transport policy.
  The ASR worker now prepares an atomic, verified 16 kHz mono PCM16 derivative before choosing
  inline/TOS. Native durable artifacts, queue identity and offsets are unchanged. Preparation
  failures use the persistent retry queue. Imported inputs retain their existing preparation.
- Incompatible stored model name/version/dimension vectors are excluded from candidate scoring.
  The misleading 192-dimension model comment is removed; the actual tested base model outputs 512.
- Backwards-device-position diagnostics now emit the first observation immediately, then
  aggregate observations in five-second session-time windows, flushing the pending tail at
  stream restart or graceful completion. `reason=device_position_backwards` and `count` identify
  these records. Sum `count` to recover observation totals; line count is no longer that total.
  The timeline, degraded verdict, raw audio and individual real-gap events are untouched.
  A forced kill can lose the pending diagnostic window; recording recovery is unaffected.

## Real local Windows validation

Windows build 26300, .NET SDK 10.0.401 / runtime 10.0.12, FFmpeg 9.0.2, real ERes2Net-base model.
Evidence is outside the repository at `C:\CODE\artifacts\meetcap-fixes-20261011`.

- A fresh 21,310 ms real Realtek microphone capture completed cleanly: three closed WAVs,
  zero gaps, dropped packets or stalls, peak queue 1/500. ASR/speakers were disabled.
  The initial sandbox run could not start capture; the permitted local run succeeded.

- A previously captured five-minute native file (115,200,044 bytes, 300,000 ms) passed through
  the production normalizing provider and real FFmpeg boundary. Output was 9,600,078 bytes,
  16 kHz mono `pcm_s16le`, 300,000 ms. The real inline publisher accepted it without TOS.
  Original SHA256 remained unchanged. This local check made **zero HTTP requests** and does
  not substitute for the real provider/live continuous-transcript gate.
- A copy of the earlier real imported session reached native speaker inference. It resolved
  one label as its enrolled same-recording pseudonym at 0.998244 similarity; another remained
  unknown. Manual assignment to a different enrolled name survived two attribution runs.
  Raw transcript SHA256 remained unchanged. Same-recording enrollment is a pipeline smoke
  test, **not** cross-meeting identity accuracy.
- Automated regressions cover native preparation, failed/partial derivative retry, import/poll
  independence from media tools, imported and track-specific range reading, float stereo
  downmixing, non-finite/truncated rejection, ambiguous-track unknowns, and diagnostic totals.
  Existing capture/gap/recovery, process-QPC and provider tests remain part of the full suite.
  Full Debug and Release suites: 847 passed each, zero failures. Compiler: zero warnings/errors.

## Sampled cloud recovery and job budget (2026-10-11)

After explicit upload authorization, the user reduced the recovery to a sample to save API
fees. An isolated SQLite backup retained only four existing jobs and their batch artifacts:
one retry-wait and one pending job per mic/loopback track. The original queue was untouched.
Each batch contained 45 seconds (180 seconds total). No other batch was submitted.

- The real Release CLI and Volcengine completed all four jobs; submit/completion events total
  four each. Native 17,280,044-byte float WAVs became verified 1,440,078-byte 16 kHz mono PCM16
  derivatives. Request, response, normalized result, raw JSONL and live Markdown were retained.
- All four provider responses contained empty text. Both mic samples were digitally silent;
  loopback samples had low signal energy but no recognized speech. This verifies sampled
  queue/transport recovery, **not** speech recognition, speaker labels or transcript timestamps.
  No further audio was submitted to seek a better result.
- A second resume reported no jobs needing work; submit/completion counts stayed at four.
  All 324 original WAV hashes were unchanged, and the original queue remained 49 retry-wait /
  33 pending. The sample session's completed state refers only to its four-job copy.
- No API key appeared in the sample logs or report artifacts. Evidence:
  `C:\CODE\artifacts\meetcap-fixes-20261011\outage-sample\audit.json`.
- Inspection also found #43: force mode could add up to N deferred retry jobs after already
  selecting N due jobs. The fix fills only the remaining `--max-jobs` budget. Seven regression
  cases cover negative/zero/small/full limits, due-first ordering, provider submissions and
  unchanged unselected jobs; four cases failed before the fix. The ASR suite passed 83 tests,
  and the full Release suite passed 854 tests with zero failures.
  This caps distinct jobs, not polling requests or provider transport retries.

## Remaining release gates at this historical checkpoint

The items below describe the earlier checkpoint, before later #40/#9 fixes and real
live speech/identity audits. Use [MVP_ACCEPTANCE.md](MVP_ACCEPTANCE.md) for the reconciled
0.3.1 preparation matrix; historical unrun statements below are not the current verdict.

- Full 82-job outage recovery was intentionally not attempted after the user's sampling request.
  The sampled real provider transport passed; continuously persisted dual-track transcripts
  with actual recognized speech remain required. Large-file TOS round-trip was not rerun.
- A new, globally meeting-disjoint 97-clip experiment completed initial calibration and a
  frozen independent test; see `SPEAKER_CALIBRATION.md`. Candidate 0.46 / 0.01 accepted 10/12
  known test clips with 0/24 unknown false accepts; default 0.82 / 0.08 accepted 0/12.
  Real CLI attribution with a gallery-only registry reproduced these counts on all 36
  manually constructed transcript fixtures, preserving raw/WAV hashes; no ASR was performed.
  Only two known people appear in this test, labels were not independently relistened, and
  broader product calibration remains open. Product defaults were not changed.
- Sony Bluetooth backwards-clock behavior was not reproduced on this hardware. #40 remains
  open for endpoint metadata/reconnect verification; aggregation addresses log flooding only.
- #1/#9 must remain open until their actual end-to-end gates pass. No stable release was made.
