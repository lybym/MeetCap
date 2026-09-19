-- Migration 0006: stable, non-secret identity for the private TOS audio transport
-- (issue #29). Scope per docs/DATA_MODEL.md section 6.2.
--
-- The durable job row has to remember four things about an oversized submission:
--
--   audio_transport      inline | tos, so a reader knows what the row means
--   tos_bucket           nullable; the bucket holding the temporary object
--   tos_object_key       nullable; the key identifying that object
--   tos_cleanup_pending  0 | 1, the durable cleanup debt
--
-- The presigned URL is deliberately absent. It is a time-limited credential: it is never
-- stored in SQLite, never written into request.json, and never logged. A restart re-signs
-- from bucket + key instead (docs/DATA_MODEL.md section 6.2).
--
-- SQLite has no "ALTER TABLE ... ADD COLUMN IF NOT EXISTS", and SqliteMigrator requires
-- every script to be re-runnable, not merely skipped: a second process that read
-- schema_migrations before this version was recorded runs the script too. A bare ALTER
-- TABLE would then fail with "duplicate column name" and abort that process, so this
-- migration rebuilds the table with every statement guarded -- the same shape migration
-- 0005 uses, for the same reason.
--
-- A re-run must not lose anything either. It is reached only because another process
-- already committed this rebuild, and between that commit and this run's DROP TABLE the
-- rest of MeetCap may have staged a real object and recorded real transport state. The
-- copy therefore reads each of this migration's own columns through SqliteMigrator's
-- schema-conditional placeholder, written here so that it cannot match -- the sequence is
-- substituted over the whole script text, comments included -- which expands to the column
-- when the schema has it and to NULL when it does not. A first run copies NULL, i.e. the
-- default below; a re-run carries the stored value, so a cleanup still owed to TOS is not
-- forgotten and the bucket and key that identify the object survive.
--
-- The copy also has to carry every *other* column, including ones a later migration adds.
-- A DROP TABLE removes columns it has never heard of, so this rebuild declares the
-- post-#26 schema (migration 0005, which itself declares these same four columns) and
-- carries provider_log_id through the placeholder for the same reason. Any future
-- migration that adds a column to `asr_jobs` has to add it here and in 0005 too;
-- docs/DATA_MODEL.md section 14 states the rule.

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
    provider_log_id        TEXT,
    audio_transport        TEXT    NOT NULL DEFAULT 'inline' CHECK (audio_transport IN ('inline', 'tos')),
    tos_bucket             TEXT,
    tos_object_key         TEXT,
    tos_cleanup_pending    INTEGER NOT NULL DEFAULT 0 CHECK (tos_cleanup_pending IN (0, 1))
);

INSERT OR IGNORE INTO asr_jobs_new (
    id, session_id, source, tier, provider, start_ms, end_ms, input_artifact, status,
    provider_request_id, attempt_count, next_retry_at, request_metadata_path, raw_response_path,
    normalized_result_path, error_code, error_message, duration_ms, speaker_info_requested,
    speaker_info_returned, estimated_cost_cny, submitted_at, completed_at, created_at, updated_at,
    provider_log_id, audio_transport, tos_bucket, tos_object_key, tos_cleanup_pending)
SELECT
    id, session_id, source, tier, provider, start_ms, end_ms, input_artifact, status,
    provider_request_id, attempt_count, next_retry_at, request_metadata_path, raw_response_path,
    normalized_result_path, error_code, error_message, duration_ms, speaker_info_requested,
    speaker_info_returned, estimated_cost_cny, submitted_at, completed_at, created_at, updated_at,
    {{asr_jobs.provider_log_id}},
    COALESCE({{asr_jobs.audio_transport}}, 'inline'),
    {{asr_jobs.tos_bucket}},
    {{asr_jobs.tos_object_key}},
    COALESCE({{asr_jobs.tos_cleanup_pending}}, 0)
FROM asr_jobs;

DROP TABLE IF EXISTS asr_jobs;
ALTER TABLE asr_jobs_new RENAME TO asr_jobs;

CREATE INDEX IF NOT EXISTS ix_asr_jobs_status ON asr_jobs (status);
CREATE INDEX IF NOT EXISTS ix_asr_jobs_session_id ON asr_jobs (session_id);
CREATE INDEX IF NOT EXISTS ix_asr_jobs_next_retry_at ON asr_jobs (next_retry_at);
