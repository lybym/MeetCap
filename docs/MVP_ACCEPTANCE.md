# MVP acceptance evidence — 0.3.1 preparation

This is the reconciled Issue [#1](https://github.com/lybym/MeetCap/issues/1) gate matrix.
M0–M6 are implemented. The evidence below combines published 0.3.0 hardware runs with
focused later validation; it does **not** claim that the final 0.3.1 release binary has
completed new two-hour soaks or a new 30-minute outage. Audio, identities, credentials
and private audit files remain outside Git. Evidence is from one Windows machine,
not a certification of all endpoints or general speech/identity accuracy.

| Required gate | Observed evidence | Scope and limits |
| --- | --- | --- |
| Two-hour offline capture | Published 0.3.0, 2026-10-08: 121 WAVs, 7,206.820 s audio | ASR and speakers disabled; recording integrity, not speech-content ground truth |
| Two-hour online mic + loopback capture | Same published build: 121 WAVs per track; mic 7,206.330 s, loopback 7,206.350 s | Across both soaks: 363 valid WAVs, no `.part`, gaps, dropped packets or stalled consumers |
| Forced-kill recovery | Three already closed hashes unchanged; active chunk repaired; repeat repair idempotent | Published 0.3.0 local durable recovery |
| 30-minute network outage and queue recovery | 30 min 3 s recording, 242 chunks / 82 jobs; no drops or stalls. Later isolated SQLite sample completed four existing 45 s jobs and an idempotent resume | Full 82-job drain intentionally omitted at user's request; original 49 retry / 33 pending jobs and 324 WAV hashes unchanged. Sample had empty text |
| Import → real file ASR → transcript | Real 6 min 43 s MP3, retry → succeeded, three segments, speaker labels 1/2 and timestamps preserved | Published 0.3.0 flow; original hash unchanged |
| Live capture → file batches → continuously persisted speech transcript | 2026-10-11: two authorized real submits, 55.419625 s total; five mic and three loopback speech segments, text persisted while recorder alive | Zero gaps/drops/stalls. Degraded metadata verdict retained: 7,041 backwards position observations in 16 aggregates. Two additional jobs blocked locally by the explicit cost guard; no further uploads |
| Provider anonymous labels and timestamps | Real import/live results retain labels, provider job IDs, raw responses and valid within-job timestamp bounds | Labels are scoped to provider job and source; collisions remain unknown, as fixed under #9 |
| Local enrolled speaker matched in a later meeting | Real later-meeting loopback audio / cloud label, six-person earlier-meeting registry, local native sherpa-onnx + ERes2Net-base match at cosine 0.637423 | Frozen exploratory threshold 0.46 / margin 0.01; no cloud calls during matching. Both-track label collision stays unknown. Default 0.82 / 0.08 remains conservative and unvalidated for general accuracy |
| Manual assignment remains authoritative | A deliberately different manual identity survived two automatic reruns; raw transcript hash unchanged | Real cloud-derived session copy; manual mapping does not infer global provider-label identity |
| Demonstrated timing defects fixed | Automated tests cover 45 s frame-based windows, a 9,174 ms physical gap, intra-WAV ambiguity, missing-chunk retry, ordinary packet-gap spool rotation and a 664 ms shared-QPC offset | No silent padding, raw timestamps or original audio rewriting. See current implementation/CI and focused smoke below |

Historical long-run source: published `v0.3.0`, commit
`0b8ab1dde60dc5c5950312a0d6951e5fc51ce20f`, ZIP SHA256
`4b895dccf050e8ef80f1d19f26e11f9b47dfcf6a7646d8eca188cecd5044bf41`.
The private original audit is dated 2026-10-08; later sampled recovery and live speech
audits are dated 2026-10-11. [MVP_FIX_VALIDATION.md](MVP_FIX_VALIDATION.md) retains its
earlier checkpoint. [SPEAKER_CALIBRATION.md](SPEAKER_CALIBRATION.md) records the small
held-out identity experiment and its limits; the 97-clip study has only two known test
people and source labels were not independently relistened.

The optional large-file TOS round trip, wider identity calibration, physical power-off,
format-change recovery and additional real meeting applications are not claimed here.
M7–M9 remain outside the explicit MVP gate unless they repair a demonstrated defect.

## Upgrade and timeline recovery

New capture WAVs rotate on every reportable gap, and ASR requests contain a contiguous
source span. Actual frames, rather than truncated millisecond differences, decide the
configured batch window. A durable chunk is never split solely for ASR, so a window
can exceed the requested duration by less than one chunk. Retained write-failure
windows are retried separately when storage recovers.

Online recording samples one shared system QPC origin before opening either track.
First-frame offsets survive independent callback/consumer order; subsequent microphone
placement keeps its frame clock and loopback keeps QPC. Initial offsets are not counted
as a measured device outage. Offline recording keeps the first-packet origin.

Legacy audio may already contain a hole inside one WAV. Its exact sample placement
cannot be reconstructed from that WAV alone. Such chunks are excluded from new
batches with an explicit diagnostic; pending ambiguous batches fail with
`asr.timeline_ambiguous` before billing. Already accepted tasks are polled without
resubmission, and their raw response is saved before normalization is refused.
Original chunks, batches and prior successful transcripts stay intact. Recover speech
by inspecting the gap events and importing explicitly selected contiguous spans;
an import has a new timeline and does not restore old session placement automatically.

## Focused verification on 2026-10-12

The Release warnings-as-errors build passed with zero warnings/errors and the full
automated suite passed 895 tests. Tests include actual SQLite restoration of the two
contiguous-span jobs (pending and retry-wait), retained provider request ID/retry time,
and idempotent orphan recovery. A dry render of the stable workflow's real release-note
block includes the source SHA rather than a literal PowerShell environment variable.

An isolated approximately 20-second real Windows online capture with continuous
playback and ASR/speakers disabled used the local snapshot of capture code commit
`71548a2795908366ab93169995de9a9a8b716231`. It completed with 19,360 ms microphone audio
in four chunks and 19,470 ms loopback audio in six chunks. Initial offsets were mic
189 ms and loopback 91 ms. The physical QPC difference was -97.9819 ms, the timeline
difference -98 ms (0.0181 ms error). Both tracks contained nonzero samples, with no
dropped packets, overflow, stalled consumers, unfinished parts or unusable-timeline
events. Every WAV's timeline span matched its frame duration exactly.

The session stayed truthfully degraded and the CLI returned 1: loopback observed two
ordinary 2 ms gaps (4 ms total), microphone none. Those gaps sat between WAVs rather
than being compressed inside a file. There were zero ASR jobs and zero API requests.
This is a focused capture/timing smoke, not a new long-run soak or cloud speech test.
The smoke binary was built before its source commit and reports informational version
`0.3.1+3641f0a`; the capture code is unchanged in the subsequent exact-frame/SQLite-test
commit `db4969b88025011be1c84216e2ccf40994cb220a`. The published stable binary must be
rebuilt and have its own release provenance verified.

The final local runtime also replayed previously saved real native chunks through the
production batch builder/WAV/manifest writer with an in-memory audit job receiver and
**zero HTTP**. Original WAV hashes were unchanged in every case:

- The 45 s window created a job on the third 15 s chunk ending at 44,999 ms, followed
  by the 25.420 s tail, rather than waiting for a fourth chunk.
- The Bluetooth 9,174 ms hole produced separate jobs at 0–13,519 ms and
  22,693–43,042 ms; no chunk was excluded in this case.
- A legacy first chunk spanning 0–5,668 ms with 668 ms of internal missing time was
  excluded with the explicit ambiguity diagnostic. Only the remaining
  5,668–19,550 ms span was queued. The exclusion counter was one; its reported
  affected timeline span was 5,668 ms, not a claim that the WAV held that much audio.

This replay verifies real source timing and durable materialization; it is not a
SQLite queue recovery or cloud transcription run. The independent SQLite regression
above covers pending/retry persistence and idempotency.
