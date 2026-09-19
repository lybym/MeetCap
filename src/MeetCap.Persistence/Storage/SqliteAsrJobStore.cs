namespace MeetCap.Persistence.Storage;

using System.Globalization;
using MeetCap.Core.Asr;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed persistent ASR job queue. This is the authoritative queue across
/// process restarts (<c>docs/ARCHITECTURE.md</c> section 12).
/// </summary>
public sealed class SqliteAsrJobStore : IAsrJobStore, IAsrQueueInspector
{
    private const string SelectColumns =
        "id, session_id, source, tier, provider, start_ms, end_ms, input_artifact, status, " +
        "provider_request_id, attempt_count, next_retry_at, request_metadata_path, raw_response_path, " +
        "normalized_result_path, error_code, error_message, duration_ms, speaker_info_requested, " +
        "speaker_info_returned, estimated_cost_cny, submitted_at, completed_at, created_at, updated_at, " +
        "provider_log_id, audio_transport, tos_bucket, tos_object_key, tos_cleanup_pending";

    private static readonly string[] s_resumableStatuses = new[]
    {
        AsrJobStatuses.ToWire(AsrJobStatus.Pending),
        AsrJobStatuses.ToWire(AsrJobStatus.Submitting),
        AsrJobStatuses.ToWire(AsrJobStatus.Submitted),
        AsrJobStatuses.ToWire(AsrJobStatus.Polling),
        AsrJobStatuses.ToWire(AsrJobStatus.RetryWait),
    };

    private readonly string _dbPath;

    public SqliteAsrJobStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
    }

    public void Create(AsrJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureProviderRequestId(job);

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            """
            INSERT INTO asr_jobs (
                id, session_id, source, tier, provider, start_ms, end_ms, input_artifact, status,
                provider_request_id, attempt_count, next_retry_at, request_metadata_path, raw_response_path,
                normalized_result_path, error_code, error_message, duration_ms, speaker_info_requested,
                speaker_info_returned, estimated_cost_cny, submitted_at, completed_at, created_at, updated_at,
                provider_log_id, audio_transport, tos_bucket, tos_object_key, tos_cleanup_pending)
            VALUES (
                @id, @sessionId, @source, @tier, @provider, @startMs, @endMs, @inputArtifact, @status,
                @providerRequestId, @attemptCount, @nextRetryAt, @requestMetadataPath, @rawResponsePath,
                @normalizedResultPath, @errorCode, @errorMessage, @durationMs, @speakerInfoRequested,
                @speakerInfoReturned, @estimatedCostCny, @submittedAt, @completedAt, @createdAt, @updatedAt,
                @providerLogId, @audioTransport, @tosBucket, @tosObjectKey, @tosCleanupPending)
            """,
            conn);

        Bind(cmd, job);
        cmd.ExecuteNonQuery();
    }

    public AsrJob? Get(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        if (!SqliteConnectionFactory.TableExists(conn, "asr_jobs"))
        {
            return null;
        }

        using var cmd = new SqliteCommand($"SELECT {SelectColumns} FROM asr_jobs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", jobId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<AsrJob> ListBySession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        if (!SqliteConnectionFactory.TableExists(conn, "asr_jobs"))
        {
            return Array.Empty<AsrJob>();
        }

        using var cmd = new SqliteCommand(
            $"SELECT {SelectColumns} FROM asr_jobs WHERE session_id = @sessionId ORDER BY created_at, id",
            conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        return ReadAll(cmd);
    }

    public IReadOnlyList<AsrJob> ListResumable(DateTimeOffset now, int limit, string? sessionId = null)
    {
        if (limit <= 0)
        {
            return Array.Empty<AsrJob>();
        }

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        if (!SqliteConnectionFactory.TableExists(conn, "asr_jobs"))
        {
            return Array.Empty<AsrJob>();
        }

        var statusList = string.Join(", ", s_resumableStatuses.Select((_, i) => $"@s{i}"));
        var sql =
            $"SELECT {SelectColumns} FROM asr_jobs " +
            $"WHERE status IN ({statusList}) " +
            "AND (next_retry_at IS NULL OR next_retry_at <= @now) ";

        if (sessionId is not null)
        {
            sql += "AND session_id = @sessionId ";
        }

        sql += "ORDER BY created_at, id LIMIT @limit";

        using var cmd = new SqliteCommand(sql, conn);
        for (var i = 0; i < s_resumableStatuses.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@s{i}", s_resumableStatuses[i]);
        }

        cmd.Parameters.AddWithValue("@now", now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@limit", limit);
        if (sessionId is not null)
        {
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
        }

        return ReadAll(cmd);
    }

    public void Update(AsrJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            """
            UPDATE asr_jobs SET
                session_id = @sessionId,
                source = @source,
                tier = @tier,
                provider = @provider,
                start_ms = @startMs,
                end_ms = @endMs,
                input_artifact = @inputArtifact,
                status = @status,
                provider_request_id = @providerRequestId,
                attempt_count = @attemptCount,
                next_retry_at = @nextRetryAt,
                request_metadata_path = @requestMetadataPath,
                raw_response_path = @rawResponsePath,
                normalized_result_path = @normalizedResultPath,
                error_code = @errorCode,
                error_message = @errorMessage,
                duration_ms = @durationMs,
                speaker_info_requested = @speakerInfoRequested,
                speaker_info_returned = @speakerInfoReturned,
                estimated_cost_cny = @estimatedCostCny,
                submitted_at = @submittedAt,
                completed_at = @completedAt,
                provider_log_id = @providerLogId,
                audio_transport = @audioTransport,
                tos_bucket = @tosBucket,
                tos_object_key = @tosObjectKey,
                tos_cleanup_pending = @tosCleanupPending,
                updated_at = @updatedAt
            WHERE id = @id
            """,
            conn);

        Bind(cmd, job);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"ASR job '{job.Id}' does not exist and cannot be updated.");
        }
    }

    public int CountByStatus(AsrJobStatus status)
    {
        using var conn = SqliteConnectionFactory.Open(_dbPath);
        if (!SqliteConnectionFactory.TableExists(conn, "asr_jobs"))
        {
            return 0;
        }

        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM asr_jobs WHERE status = @status", conn);
        cmd.Parameters.AddWithValue("@status", AsrJobStatuses.ToWire(status));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Counts every job by status group (docs/ROADMAP.md M4 status reporting).</summary>
    public AsrQueueStatus Inspect() => Count(null);

    /// <summary>Counts one session's jobs by status group.</summary>
    public AsrQueueStatus InspectSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Count(sessionId);
    }

    private AsrQueueStatus Count(string? sessionId)
    {
        using var conn = SqliteConnectionFactory.Open(_dbPath);
        if (!SqliteConnectionFactory.TableExists(conn, "asr_jobs"))
        {
            return AsrQueueStatus.Empty;
        }

        var sql = "SELECT status, COUNT(*) FROM asr_jobs";
        if (sessionId is not null)
        {
            sql += " WHERE session_id = @sessionId";
        }

        sql += " GROUP BY status";

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = new SqliteCommand(sql, conn))
        {
            if (sessionId is not null)
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        var activeSessions = 0;
        if (sessionId is null)
        {
            using var sessions = new SqliteCommand(
                "SELECT COUNT(DISTINCT session_id) FROM asr_jobs WHERE status IN (@pending, @submitting, " +
                "@submitted, @polling, @retryWait)",
                conn);
            sessions.Parameters.AddWithValue("@pending", AsrJobStatuses.ToWire(AsrJobStatus.Pending));
            sessions.Parameters.AddWithValue("@submitting", AsrJobStatuses.ToWire(AsrJobStatus.Submitting));
            sessions.Parameters.AddWithValue("@submitted", AsrJobStatuses.ToWire(AsrJobStatus.Submitted));
            sessions.Parameters.AddWithValue("@polling", AsrJobStatuses.ToWire(AsrJobStatus.Polling));
            sessions.Parameters.AddWithValue("@retryWait", AsrJobStatuses.ToWire(AsrJobStatus.RetryWait));
            activeSessions = Convert.ToInt32(sessions.ExecuteScalar());
        }

        int Get(AsrJobStatus status) =>
            counts.TryGetValue(AsrJobStatuses.ToWire(status), out var value) ? value : 0;

        return new AsrQueueStatus(
            Pending: Get(AsrJobStatus.Pending),
            InFlight: Get(AsrJobStatus.Submitting) + Get(AsrJobStatus.Submitted) + Get(AsrJobStatus.Polling),
            Retrying: Get(AsrJobStatus.RetryWait),
            Succeeded: Get(AsrJobStatus.Succeeded),
            Failed: Get(AsrJobStatus.Failed),
            Cancelled: Get(AsrJobStatus.Cancelled),
            Active: activeSessions);
    }

    private static void EnsureProviderRequestId(AsrJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ProviderRequestId))
        {
            throw new ArgumentException(
                $"ASR job '{job.Id}' must carry a provider request id: without it a restart " +
                "cannot avoid submitting the same audio twice.",
                nameof(job));
        }
    }

    private static void Bind(SqliteCommand cmd, AsrJob job)
    {
        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@sessionId", job.SessionId);
        cmd.Parameters.AddWithValue("@source", job.Source);
        cmd.Parameters.AddWithValue("@tier", job.Tier);
        cmd.Parameters.AddWithValue("@provider", job.Provider);
        cmd.Parameters.AddWithValue("@startMs", job.StartMs);
        cmd.Parameters.AddWithValue("@endMs", job.EndMs);
        cmd.Parameters.AddWithValue("@inputArtifact", job.InputArtifact);
        cmd.Parameters.AddWithValue("@status", AsrJobStatuses.ToWire(job.Status));
        cmd.Parameters.AddWithValue("@providerRequestId", Text(job.ProviderRequestId));
        cmd.Parameters.AddWithValue("@attemptCount", job.AttemptCount);
        cmd.Parameters.AddWithValue("@nextRetryAt", Timestamp(job.NextRetryAt));
        cmd.Parameters.AddWithValue("@requestMetadataPath", Text(job.RequestMetadataPath));
        cmd.Parameters.AddWithValue("@rawResponsePath", Text(job.RawResponsePath));
        cmd.Parameters.AddWithValue("@normalizedResultPath", Text(job.NormalizedResultPath));
        cmd.Parameters.AddWithValue("@errorCode", Text(job.ErrorCode));
        cmd.Parameters.AddWithValue("@errorMessage", Text(job.ErrorMessage));
        cmd.Parameters.AddWithValue("@durationMs", job.DurationMs);
        cmd.Parameters.AddWithValue("@speakerInfoRequested", job.SpeakerInfoRequested ? 1 : 0);
        cmd.Parameters.AddWithValue("@speakerInfoReturned", job.SpeakerInfoReturned ? 1 : 0);
        cmd.Parameters.AddWithValue("@estimatedCostCny", job.EstimatedCostCny);
        cmd.Parameters.AddWithValue("@submittedAt", Timestamp(job.SubmittedAt));
        cmd.Parameters.AddWithValue("@completedAt", Timestamp(job.CompletedAt));
        cmd.Parameters.AddWithValue("@providerLogId", Text(job.ProviderLogId));
        cmd.Parameters.AddWithValue("@audioTransport", job.AudioTransport);
        cmd.Parameters.AddWithValue("@tosBucket", Text(job.TosBucket));
        cmd.Parameters.AddWithValue("@tosObjectKey", Text(job.TosObjectKey));
        cmd.Parameters.AddWithValue("@tosCleanupPending", job.TosCleanupPending ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", job.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@updatedAt", job.UpdatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
    }

    private AsrJob Map(SqliteDataReader reader)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));

        var statusText = reader.GetString(reader.GetOrdinal("status"));
        if (!AsrJobStatuses.TryParse(statusText, out var status))
        {
            throw new InvalidOperationException(
                $"Stored ASR job '{id}' has unrecognised status '{statusText}'.");
        }

        // The column is nullable in the schema, so a hand-edited or externally written row
        // can still violate the invariant the domain type enforces. Fail loudly and name the
        // job, because the alternative -- carrying on with an unusable row -- would either
        // submit the same audio twice or drop the job from the queue silently.
        var requestId = ReadText(reader, "provider_request_id");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new InvalidOperationException(
                $"Stored ASR job '{id}' has no provider request id, so it cannot be resumed safely. " +
                $"Repair or delete that row in '{_dbPath}' before resuming the queue.");
        }

        return new AsrJob
        {
            Id = id,
            SessionId = reader.GetString(reader.GetOrdinal("session_id")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            // `tier` is a schema-compatibility column, not a routing input: AsrJob.Tier is a
            // constant, so the stored value is deliberately not read back (docs/DATA_MODEL.md
            // section 6).
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            StartMs = reader.GetInt64(reader.GetOrdinal("start_ms")),
            EndMs = reader.GetInt64(reader.GetOrdinal("end_ms")),
            InputArtifact = reader.GetString(reader.GetOrdinal("input_artifact")),
            AudioTransport = reader.GetString(reader.GetOrdinal("audio_transport")),
            TosBucket = ReadText(reader, "tos_bucket"),
            TosObjectKey = ReadText(reader, "tos_object_key"),
            TosCleanupPending = reader.GetInt32(reader.GetOrdinal("tos_cleanup_pending")) != 0,
            Status = status,
            ProviderRequestId = requestId,
            AttemptCount = reader.GetInt32(reader.GetOrdinal("attempt_count")),
            NextRetryAt = ReadTimestamp(reader, "next_retry_at"),
            RequestMetadataPath = ReadText(reader, "request_metadata_path"),
            RawResponsePath = ReadText(reader, "raw_response_path"),
            NormalizedResultPath = ReadText(reader, "normalized_result_path"),
            ErrorCode = ReadText(reader, "error_code"),
            ErrorMessage = ReadText(reader, "error_message"),
            ProviderLogId = ReadText(reader, "provider_log_id"),
            DurationMs = reader.GetInt32(reader.GetOrdinal("duration_ms")),
            SpeakerInfoRequested = reader.GetInt32(reader.GetOrdinal("speaker_info_requested")) != 0,
            SpeakerInfoReturned = reader.GetInt32(reader.GetOrdinal("speaker_info_returned")) != 0,
            EstimatedCostCny = reader.GetDouble(reader.GetOrdinal("estimated_cost_cny")),
            SubmittedAt = ReadTimestamp(reader, "submitted_at"),
            CompletedAt = ReadTimestamp(reader, "completed_at"),
            CreatedAt = ReadRequiredTimestamp(reader, "created_at"),
            UpdatedAt = ReadRequiredTimestamp(reader, "updated_at"),
        };
    }

    private IReadOnlyList<AsrJob> ReadAll(SqliteCommand cmd)
    {
        var jobs = new List<AsrJob>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(Map(reader));
        }

        return jobs;
    }

    private static object Text(string? value) => value is null ? DBNull.Value : value;

    private static object Timestamp(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static string? ReadText(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, string column)
    {
        var text = ReadText(reader, column);
        return text is null
            ? null
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static DateTimeOffset ReadRequiredTimestamp(SqliteDataReader reader, string column) =>
        ReadTimestamp(reader, column) ?? throw new InvalidOperationException($"Column '{column}' must not be null.");
}
