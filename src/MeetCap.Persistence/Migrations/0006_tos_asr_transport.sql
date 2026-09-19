-- Issue #29: stable non-secret identity for private temporary TOS audio transport.
ALTER TABLE asr_jobs ADD COLUMN audio_transport TEXT NOT NULL DEFAULT 'inline' CHECK (audio_transport IN ('inline', 'tos'));
ALTER TABLE asr_jobs ADD COLUMN tos_bucket TEXT;
ALTER TABLE asr_jobs ADD COLUMN tos_object_key TEXT;
ALTER TABLE asr_jobs ADD COLUMN tos_cleanup_pending INTEGER NOT NULL DEFAULT 0 CHECK (tos_cleanup_pending IN (0, 1));
