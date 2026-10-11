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
