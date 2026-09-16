-- Migration 0003: ASR job queue for M3 (recording import and Volcengine file ASR).
-- Scope per docs/DATA_MODEL.md section 6 (ASR job) and section 11 (minimum tables).
-- audio_chunks, speakers, speaker_embeddings and speaker_assignments are added by
-- the milestones that implement them; no table is created ahead of its feature.
--
-- Version note: this takes 0003, not 0002, because 0002 is claimed by the
-- concurrently developed 0002_audio_chunks.sql (M1, issue #3). Two scripts sharing a
-- version would make the second one look already applied, so its tables would never be
-- created at runtime; the ASR job migration therefore leaves 0002 free.

-- Persistent ASR job state. This is MeetCap-owned domain state with a real state
-- machine (docs/ARCHITECTURE.md section 12). It is deliberately not a generic job
-- framework: no Hangfire/Quartz table and no Polly-owned state.
-- Retry state (attempt_count, next_retry_at) is durable so incomplete work resumes
-- after a process restart.
CREATE TABLE IF NOT EXISTS asr_jobs (
    id                     TEXT    PRIMARY KEY,
    session_id             TEXT    NOT NULL,
    source                 TEXT    NOT NULL,
    tier                   TEXT    NOT NULL,
    provider               TEXT    NOT NULL,
    start_ms               INTEGER NOT NULL DEFAULT 0,
    end_ms                 INTEGER NOT NULL DEFAULT 0,
    input_artifact         TEXT    NOT NULL,
    status                 TEXT    NOT NULL CHECK (status IN (
                                       'pending', 'submitting', 'submitted', 'polling',
                                       'succeeded', 'retry_wait', 'failed', 'cancelled')),
    provider_request_id    TEXT,
    attempt_count          INTEGER NOT NULL DEFAULT 0,
    next_retry_at          TEXT,
    request_metadata_path  TEXT,
    raw_response_path      TEXT,
    normalized_result_path TEXT,
    error_code             TEXT,
    error_message          TEXT,
    duration_ms            INTEGER NOT NULL DEFAULT 0,
    speaker_info_requested INTEGER NOT NULL DEFAULT 0 CHECK (speaker_info_requested IN (0, 1)),
    speaker_info_returned  INTEGER NOT NULL DEFAULT 0 CHECK (speaker_info_returned IN (0, 1)),
    estimated_cost_cny     REAL    NOT NULL DEFAULT 0,
    submitted_at           TEXT,
    completed_at           TEXT,
    created_at             TEXT    NOT NULL,
    updated_at             TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_asr_jobs_status ON asr_jobs (status);
CREATE INDEX IF NOT EXISTS ix_asr_jobs_session_id ON asr_jobs (session_id);
CREATE INDEX IF NOT EXISTS ix_asr_jobs_next_retry_at ON asr_jobs (next_retry_at);
