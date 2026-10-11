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

## Remaining release gates

- The 82-job outage recovery upload was rejected by automatic permission review because it
  requires explicit permission to send potentially sensitive recorded audio to Volcengine.
  No recovery command ran. Authorization has been requested; local work continued.
- Real provider validation of normalized live batches and continuously persisted dual-track
  transcripts remains required. Large-file TOS round-trip was not rerun.
- The previous cross-meeting holdout had 0/18 accepted known samples at 0.82; independent
  calibration/testing remains open. Default thresholds were not changed to fit those samples.
- Sony Bluetooth backwards-clock behavior was not reproduced on this hardware. #40 remains
  open for endpoint metadata/reconnect verification; aggregation addresses log flooding only.
- #1/#9 must remain open until their actual end-to-end gates pass. No stable release was made.
