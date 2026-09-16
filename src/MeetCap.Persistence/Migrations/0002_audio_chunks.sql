-- Migration 0002: audio chunk index for M1 (reliable offline microphone capture).
-- Scope per docs/DATA_MODEL.md section 5 (Audio chunk record). The audio itself lives
-- under sessions/<id>/audio/<source>/ as recoverable WAV chunks; this table is the
-- index that makes those chunks queryable without walking the filesystem.
--
-- Only the tables M1 needs are created here. asr_jobs, speakers,
-- speaker_embeddings and speaker_assignments still arrive with their own milestones
-- (docs/DATA_MODEL.md section 11).

CREATE TABLE IF NOT EXISTS audio_chunks (
    id                     TEXT    PRIMARY KEY,
    session_id             TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    source                 TEXT    NOT NULL CHECK (source IN ('mic', 'loopback')),
    sequence               INTEGER NOT NULL,
    -- Path relative to the session directory, e.g. audio/mic/000001.wav.
    path                   TEXT    NOT NULL,
    start_ms               INTEGER NOT NULL,
    end_ms                 INTEGER NOT NULL,
    sample_rate            INTEGER NOT NULL,
    channels               INTEGER NOT NULL,
    bits_per_sample        INTEGER NOT NULL,
    sample_format          TEXT    NOT NULL CHECK (sample_format IN ('pcm', 'ieee_float')),
    byte_length            INTEGER NOT NULL,
    status                 TEXT    NOT NULL CHECK (status IN ('open', 'closed', 'recovered', 'corrupt', 'missing')),
    -- Device-timing anchors for the chunk's first frame: preserved so a recorded
    -- timeline can be audited against what the device actually reported
    -- (docs/ARCHITECTURE.md section 8).
    device_position_frames INTEGER,
    qpc_position_ticks     INTEGER,
    -- Optional content hash. M1 validates chunk headers and lengths instead, so this
    -- stays NULL until a later milestone needs end-to-end content verification.
    sha256                 TEXT,
    created_at             TEXT    NOT NULL,
    closed_at              TEXT,

    UNIQUE (session_id, source, sequence)
);

CREATE INDEX IF NOT EXISTS ix_audio_chunks_session_source ON audio_chunks (session_id, source, sequence);
CREATE INDEX IF NOT EXISTS ix_audio_chunks_status ON audio_chunks (status);
