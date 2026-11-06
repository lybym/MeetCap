# Exploratory ERes2Net-base calibration — 2026-10-11

This is a local-data calibration result, **not a new product default** or a claim about
general Chinese meeting accuracy. #9 remains open for broader identity validation.

## Data separation

Existing `voiceprint-builder` accepted labels and eligible clean 5–12 second mono PCM16
WAVs were reused without modifying the source project. Blank/unstable IDs, unnamed groups,
overlap and missing clips were excluded. Labels were not independently relistened.

The native MeetCap Release CLI extracted 97 base-model embeddings into an isolated database:

- Gallery: six enrolled people, three clips each, from two earlier meetings (18 clips).
- Calibration: 31 known clips from six people, plus 12 unenrolled clips from two people.
- Independent test: 12 known clips from two people, plus 24 unenrolled clips from four people.

Gallery, calibration and test have globally disjoint source meetings and unique WAV hashes.
Neither calibration nor test uses the meetings previously observed on 2026-10-09.
Unknown identities never enter the candidate gallery. Probe vectors stored by the extraction
CLI are excluded from scoring; they are not additional enrolled candidates.

Model SHA256: `1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b`,
actual dimension 512, sherpa-onnx 1.13.8. ERes2Net-large vectors were not used.

## Selection and frozen test

Scoring reproduces the reviewed provider rule: highest cosine similarity across each person's
gallery vectors, then threshold and best-versus-runner-up margin. This does not replace an
ASR-to-identity end-to-end test. All thresholds/margins were selected using calibration only:
grid threshold 0.30–0.85 / margin 0.00–0.20 in increments of 0.01, maximize correct acceptance
subject to zero observed wrong known or unknown acceptance. Ties prefer a higher threshold,
then a higher margin. The selected policy is determined exclusively by calibration results;
test scores and labels are not used to choose or revise it.

| Policy | Calibration known accepted correctly | Calibration unknown falsely accepted | Test known accepted correctly | Test unknown falsely accepted |
|---|---:|---:|---:|---:|
| Default 0.82 / 0.08 | 1/31 | 0/12 | 0/12 | 0/24 |
| Exploratory 0.46 / 0.01 | 31/31 | 0/12 | 10/12 | 0/24 |

All known top-ranked candidates were correct before rejection by policy. The exploratory
policy rejected two known test clips and assigned no wrong identities in this small sample.
Zero observed false accepts is **not** proof of a zero population false-accept rate.

The same frozen profile was then exercised through the real Release CLI's
`speakers attribute` on all 36 test clips with a separate gallery-only registry.
It produced the same 10/12 correct known accepts and 0/24 unknown false accepts;
WAV and raw-fixture hashes were unchanged. These sessions use manually constructed
transcript fixtures with real accepted identity labels, **not cloud ASR results**.
The executable extraction, ranking and threshold path was exercised locally; ASR was not.

## Using the result

For a separate evaluation using this same base model and a compatible enrolled registry,
these are the candidate settings:

```toml
[speakers.identity]
match_threshold = 0.46
match_margin = 0.01
```

Keep the default profile until the risk of false attribution is assessed on broader,
independently checked people, microphones, noise and recordings. Do not apply these values
to ERes2Net-large or mix large/base vectors. Manual attribution remains authoritative.

Private evidence: `C:\CODE\artifacts\meetcap-fixes-20261011\calibration\plan.json`,
`results.json`, `logs/`, and `data/meetcap.db`. The reproducible local experiment is
`C:\CODE\artifacts\meetcap-fixes-20261011\calibrate-speakers.py`.

## 2026-11-05 field measurement (issue #49)

The 2026-10-11 exploratory calibration above used meeting-disjoint, curated clean clips. The
2026-11-05 real-meeting deployment measured the same model on uncurated meeting audio and
exposed that the shipped `0.82 / 0.08` default is unattainable in the field, not merely
conservative. MeetCap 0.3.1 (`b725ec1`), 2 h 40 m meeting, 17 import sessions, enrollment
from a curated clean-speech dataset (3 embeddings/person, 16 kHz mono PCM16 WAV), the
bundled sherpa-onnx runtime (model version 1.13.8) and the same ERes2Net ONNX
(SHA256-verified) as the enrollment library — one self-consistent vector space, no
cross-runtime skew.

Measured cosine (512-dim, all through the shipped runtime):

| Comparison | Cosine |
| --- | --- |
| Same person, same recording, different 15 s spans | ≈ 0.656 |
| Same person, cross-recording (enrolled from other days) vs a session probe | 0.55 – 0.58 |
| Different persons (probe vs 8 enrolled people) | ≤ 0.23 |
| Different persons (probe vs an 18-speaker library) | ≤ 0.38 |

With the default, `meetcap speakers attribute` reported `resolved: 0 named` on all 17
sessions. The decisive control — enrolling a 16 s span *from session X itself* as a
temporary speaker and re-running attribution on session X — still resolved nothing at 0.82
while the same probe scored 0.656 against that person's embedding. 0.82 therefore sits above
the same-speaker same-recording cosine this pipeline produces; the feature is effectively
disabled as shipped.

Changing only `match_threshold = 0.50` / `match_margin = 0.06` resolved five enrolled people
immediately across the same sessions (confidences 0.55–0.58, different-person candidates
≤ 0.4) — attribution went from all-unknown to working in one config edit. 0.50/0.06 is a
**measured starting candidate from one meeting and one enrollment environment**, not a
proven global threshold.

## Calibration gate (issue #49 review update, 2026-11-06)

The field measurement justifies replacing the uncalibrated 0.82 default, but 0.50 is not
shipped as the default from this evidence alone. The review update gates the change:

1. **Land issue #50** — candidate scores and outcome reasons exposed by the attribution
   path — so the per-label same/different score distributions can be read back from
   `meetcap speakers attribute` instead of only from local probe dumps.
2. **Run held-out, different-day recordings** across multiple known speakers **and**
   multiple known impostors; document false-match and missed-match counts at several
   threshold+margin pairs.
3. **Choose a conservative default** with documented confidence limits, and leave operator
   override intact.

Until then, `0.50 / 0.06` is published only as an explicitly opt-in experiment profile:

```toml
[speakers.identity]
# Opt-in experiment profile only (issue #49). NOT the shipped default and not an
# unqualified recommendation. Lowering the threshold without the calibration above
# risks silently assigning wrong personal identities.
match_threshold = 0.50
match_margin = 0.06
```

The shipped default remains `0.82 / 0.08`. The calibration procedure and threshold/margin
interaction are documented in `docs/CONFIGURATION.md` sections 9.2.1–9.2.2; the measured
separation and over-filter rationale are in `docs/ASR_STRATEGY.md` section 10.
