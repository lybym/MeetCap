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
