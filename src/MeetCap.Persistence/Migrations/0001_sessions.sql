-- Migration 0001: foundational schema for M0 (repository and executable skeleton).
-- Scope per docs/DATA_MODEL.md section 1 (Session) and section 11 (minimum tables).
-- Only tables required by the active milestone are created here. Later milestones
-- add audio_chunks, asr_jobs, speakers, speaker_embeddings and speaker_assignments.

-- Tracks which migrations have been applied. The migrator creates this table itself
-- before applying migrations, but it is declared here so the schema is self-describing
-- and a clean database can be inspected without migrator internals.
CREATE TABLE IF NOT EXISTS schema_migrations (
    version    INTEGER PRIMARY KEY,
    applied_at TEXT    NOT NULL
);

-- Core session entity per docs/DATA_MODEL.md section 1.
CREATE TABLE IF NOT EXISTS sessions (
    id              TEXT    PRIMARY KEY,
    title           TEXT    NOT NULL,
    mode            TEXT    NOT NULL CHECK (mode IN ('offline', 'online', 'import')),
    source_type     TEXT    NOT NULL CHECK (source_type IN ('live', 'import')),
    status          TEXT    NOT NULL,
    started_at      TEXT,
    stopped_at      TEXT,
    duration_ms     INTEGER NOT NULL DEFAULT 0,
    config_snapshot TEXT    NOT NULL DEFAULT '{}',
    config_version  INTEGER NOT NULL DEFAULT 1,
    tracks          TEXT    NOT NULL DEFAULT '[]',
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_sessions_status ON sessions (status);
CREATE INDEX IF NOT EXISTS ix_sessions_started_at ON sessions (started_at);
