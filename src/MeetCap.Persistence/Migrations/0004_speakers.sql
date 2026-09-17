-- Migration 0004: Speaker registry and voiceprint matching for M6 (issue #8).
-- Scope per docs/DATA_MODEL.md sections 8 (Speaker), 9 (Speaker embedding) and
-- 10 (Speaker attribution), and section 11 (minimum tables).
--
-- speakers, speaker_embeddings and speaker_assignments are added by this migration
-- now that M6 implements them. No table is created ahead of its feature
-- (section 11); 0001-0003 are already claimed by sessions, audio_chunks, and asr_jobs.
--
-- Voiceprints and name mappings are sensitive local identity-related data. They
-- are stored in the local meetcap.db and are never uploaded to cloud storage by
-- default (docs/ARCHITECTURE.md section 22).

-- Speaker entity (section 8). display_name is unique so enrollment cannot create
-- a silent duplicate; aliases is a JSON array string for storage simplicity.
CREATE TABLE IF NOT EXISTS speakers (
    id           TEXT    PRIMARY KEY,
    display_name TEXT    NOT NULL UNIQUE,
    aliases      TEXT    NOT NULL DEFAULT '[]',
    active       INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL
);

-- Speaker embedding (section 9). Multiple embeddings per person are stored
-- rather than one permanent vector. embedding_blob is the raw float vector as
-- little-endian float32 bytes. A foreign key to speakers with ON DELETE CASCADE
-- means deleting a person removes their voiceprints, which is what keeps stale
-- embeddings from surviving a person who was removed.
CREATE TABLE IF NOT EXISTS speaker_embeddings (
    id                  TEXT    PRIMARY KEY,
    speaker_id          TEXT    NOT NULL,
    model_name          TEXT    NOT NULL,
    model_version       TEXT    NOT NULL,
    dimension           INTEGER NOT NULL,
    embedding_blob      BLOB    NOT NULL,
    source_session_id   TEXT,
    source_segment_ids  TEXT    NOT NULL DEFAULT '[]',
    quality_score       REAL,
    created_at          TEXT    NOT NULL,
    FOREIGN KEY (speaker_id) REFERENCES speakers (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_speaker_embeddings_speaker_id ON speaker_embeddings (speaker_id);

-- Speaker assignment (section 10). One row per (session, speaker_label): the
-- binding of an anonymous provider label to a person for one session. The
-- UNIQUE constraint makes upsert idempotent, so a manual assignment or a
-- re-run of the voiceprint pipeline replaces rather than duplicates. speaker_id
-- is nullable (an unknown label has no person) and uses ON DELETE SET NULL so
-- removing a person from the registry demotes their assignments to unknown
-- without orphaning the rows.
CREATE TABLE IF NOT EXISTS speaker_assignments (
    id            TEXT    PRIMARY KEY,
    session_id    TEXT    NOT NULL,
    speaker_label TEXT    NOT NULL,
    speaker_id    TEXT,
    speaker_name  TEXT,
    confidence    REAL,
    source        TEXT    NOT NULL CHECK (source IN (
                                        'manual', 'voiceprint', 'owner_assumption', 'unknown')),
    locked        INTEGER NOT NULL DEFAULT 0 CHECK (locked IN (0, 1)),
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL,
    UNIQUE (session_id, speaker_label),
    FOREIGN KEY (speaker_id) REFERENCES speakers (id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_speaker_assignments_session_id ON speaker_assignments (session_id);
