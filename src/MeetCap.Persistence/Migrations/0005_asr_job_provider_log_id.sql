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
-- second run copies the rows across and swaps again, leaving the table the same shape
-- and the same data the first run produced.
--
-- A re-run must also not *lose* anything. It is reached only because another process
-- already committed this rebuild, and between that commit and this run's DROP TABLE the
-- rest of MeetCap may have written real X-Tt-Logid values into provider_log_id — the very
-- diagnostic state docs/DATA_MODEL.md section 6 says is kept for provider support. So the
-- copy has to carry provider_log_id across, but on a first run the column does not exist
-- yet and a statement naming it would fail to prepare, which plain SQL cannot branch
-- around. The copy reads it through SqliteMigrator's schema-conditional placeholder
-- `{{<table>.<column>}}` -- written here so that it cannot match, because the sequence is
-- substituted over the whole script text, comments included -- which expands to the column
-- when it exists and to NULL when it does not. A first run therefore copies NULL (correct:
-- no log id exists yet) and a re-run copies the stored log id instead of silently resetting
-- it.
--
-- The rebuild is also what keeps the legacy `tier` column and every CHECK constraint
-- byte-for-byte identical to migration 0003. `tier` is retained as a schema-compatibility
-- field; it is no longer a routing input (docs/DATA_MODEL.md section 6).
--
-- This script declares the table's *later* columns (issue #29's transport identity) as
-- well, for the same reason: a rebuild is only ever safe when the schema it creates is a
-- superset of the schema it is replacing. The replay documented above can equally leave
-- this script running long after a newer migration already added its own columns, and DROP
-- TABLE does not care which migration added them -- it removes all of them. An earlier
-- revision of this file reproduced 0003 plus provider_log_id only, so exactly that replay
-- silently dropped audio_transport, tos_bucket, tos_object_key and tos_cleanup_pending and
-- every later read of the table failed with "no such column: audio_transport". The columns
-- are copied through the same schema-conditional placeholder, so a first run fills them
-- with the defaults migration 0006 would have applied anyway and a re-run carries the
-- stored values -- a cleanup still owed to TOS is not forgotten, and the stable bucket and
-- key that identify the object survive.
--
-- Any future migration that adds a column to `asr_jobs` must therefore add it here too.
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
