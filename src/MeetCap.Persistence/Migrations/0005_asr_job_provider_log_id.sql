-- Migration 0005: retain the Volcengine X-Tt-Logid on the ASR job row (issue #26).
-- Scope per docs/DATA_MODEL.md section 6 (ASR job) and docs/ASR_STRATEGY.md section 13
-- (observability: the provider log id is one of the per-job fields).
--
-- The log id is what a Volcengine support request is traced by, so it is persisted with
-- the job instead of being left inside the retained raw response body (which holds the
-- response *body* only, never the headers). It is not a credential: the API key is never
-- written here.
--
-- SQLite has no "ALTER TABLE ... ADD COLUMN IF NOT EXISTS", and SqliteMigrator relies on
-- every script being re-runnable ("idempotent at the table level"), because a second
-- process that read schema_migrations before this version was recorded will run the
-- script too. A bare ALTER TABLE would therefore fail with "duplicate column name" and
-- abort that process. The table is rebuilt instead: every statement is guarded, and a
-- second run copies the (now identical) rows across and swaps again.
--
-- The rebuild is also what keeps the legacy `tier` column and every CHECK constraint
-- byte-for-byte identical to migration 0003. `tier` is retained as a schema-compatibility
-- field; it is no longer a routing input (docs/DATA_MODEL.md section 6).

CREATE TABLE IF NOT EXISTS asr_jobs_new (
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
    updated_at             TEXT    NOT NULL,
    provider_log_id        TEXT
);

INSERT OR IGNORE INTO asr_jobs_new (
    id, session_id, source, tier, provider, start_ms, end_ms, input_artifact, status,
    provider_request_id, attempt_count, next_retry_at, request_metadata_path, raw_response_path,
    normalized_result_path, error_code, error_message, duration_ms, speaker_info_requested,
    speaker_info_returned, estimated_cost_cny, submitted_at, completed_at, created_at, updated_at)
SELECT
    id, session_id, source, tier, provider, start_ms, end_ms, input_artifact, status,
    provider_request_id, attempt_count, next_retry_at, request_metadata_path, raw_response_path,
    normalized_result_path, error_code, error_message, duration_ms, speaker_info_requested,
    speaker_info_returned, estimated_cost_cny, submitted_at, completed_at, created_at, updated_at
FROM asr_jobs;

DROP TABLE IF EXISTS asr_jobs;
ALTER TABLE asr_jobs_new RENAME TO asr_jobs;

CREATE INDEX IF NOT EXISTS ix_asr_jobs_status ON asr_jobs (status);
CREATE INDEX IF NOT EXISTS ix_asr_jobs_session_id ON asr_jobs (session_id);
CREATE INDEX IF NOT EXISTS ix_asr_jobs_next_retry_at ON asr_jobs (next_retry_at);
