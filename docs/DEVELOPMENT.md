# Development Rules for Coding Agents

This repository is expected to be developed with coding agents. These rules are intended to prevent scope drift.

## 1. Read before coding

For every task, read:

1. `docs/PRD.md`
2. `docs/ARCHITECTURE.md`
3. the relevant milestone in `docs/ROADMAP.md`
4. relevant specialist document such as `RELIABILITY.md` or `ASR_STRATEGY.md`

Do not infer product behavior from code alone when docs explicitly define it.

## 2. Milestone discipline

Implement only the active milestone and its prerequisites.

Forbidden pattern:

> While here, I also added streaming ASR / GUI / RAG / meeting summary.

If it is not required by the active milestone, do not add it.

## 3. Reliability stop rule

When a review finding appears to require a new journal, transaction protocol, persistent recovery subsystem, distributed coordination mechanism, or complex state machine, stop and verify that the reliability property is actually required by the current milestone.

Do not accidentally turn a local CLI into distributed infrastructure.

## 4. Provider isolation

Business/domain code must not depend directly on:

- Volcengine JSON field names;
- NAudio concrete event args;
- SQLite connection types;
- a particular speaker model;
- a future LLM vendor.

Use adapters/providers.

## 5. Raw artifact preservation

Never delete the raw source that generated a normalized artifact.

Examples that must remain:

- original recording;
- closed capture chunks;
- raw ASR response;
- raw normalized transcript.

Derived outputs may be regenerated.

## 6. Tests are part of the feature

A feature is incomplete without:

- unit tests for deterministic logic;
- integration tests where the provider boundary matters;
- documented manual Windows test when audio behavior cannot be fully automated.

Audio-capture work requires real Windows validation, not only mocks.

## 7. No fake success

If audio hardware, Windows APIs, or real ASR credentials are unavailable in CI:

- mock the boundary for CI;
- document missing real-world validation;
- do not claim the feature is verified end-to-end.

## 8. CLI behavior

Commands must:

- return non-zero exit code on failure;
- provide actionable errors;
- avoid dumping secrets;
- preserve room for future machine-readable output.

Do not turn normal commands into interactive wizards unless required.

## 9. Configuration

Do not introduce hidden persistent settings.

Any new user-tunable setting must:

1. be added to the configuration schema;
2. have a documented default;
3. be validated;
4. be added to `config.example.toml`;
5. be documented in `CONFIGURATION.md`.

## 10. Schema changes

SQLite schema changes require migrations. Artifact schema changes require explicit backward-compatibility consideration. JSONL fields should be additive when possible.

## 11. Issue before PR

Every implementation PR MUST have a pre-existing GitHub Issue that defines the work. Do not create implementation-first PRs and backfill Issues afterward.

The PR must link the Issue and remain within its defined scope.

## 12. Pull-request handoff

Every feature PR should state:

- milestone;
- linked Issue;
- scope implemented;
- scope intentionally not implemented;
- files/modules changed;
- tests run;
- real Windows/audio validation performed;
- known limitations;
- docs updated.

## 13. Definition of done

`Builds` is not done.

For the active milestone:

```text
implementation
+ automated tests
+ relevant Windows validation
+ docs synchronized
+ no out-of-scope feature creep
```

is done.
