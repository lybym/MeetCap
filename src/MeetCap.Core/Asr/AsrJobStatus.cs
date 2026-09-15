namespace MeetCap.Core.Asr;

/// <summary>
/// Persistent ASR job states (<c>docs/ARCHITECTURE.md</c> section 12).
/// </summary>
/// <remarks>
/// This state machine is MeetCap-owned domain state that survives process
/// restart. Polly handles transient HTTP execution inside the provider adapter and
/// is explicitly not a substitute for it.
/// </remarks>
public enum AsrJobStatus
{
    /// <summary>Queued, not yet submitted.</summary>
    Pending,

    /// <summary>A submit request is in flight (or was in flight when the process died).</summary>
    Submitting,

    /// <summary>The provider accepted the task; its request id is recorded.</summary>
    Submitted,

    /// <summary>Polling the provider for the result.</summary>
    Polling,

    /// <summary>Terminal success.</summary>
    Succeeded,

    /// <summary>Transient failure; the job becomes eligible again at <c>next_retry_at</c>.</summary>
    RetryWait,

    /// <summary>Terminal failure (permanent provider or configuration error, or retries exhausted).</summary>
    Failed,

    /// <summary>Terminal cancellation.</summary>
    Cancelled,
}

/// <summary>Wire/SQLite representation of <see cref="AsrJobStatus"/>.</summary>
public static class AsrJobStatuses
{
    private static readonly Dictionary<AsrJobStatus, string> s_wire = new()
    {
        [AsrJobStatus.Pending] = "pending",
        [AsrJobStatus.Submitting] = "submitting",
        [AsrJobStatus.Submitted] = "submitted",
        [AsrJobStatus.Polling] = "polling",
        [AsrJobStatus.Succeeded] = "succeeded",
        [AsrJobStatus.RetryWait] = "retry_wait",
        [AsrJobStatus.Failed] = "failed",
        [AsrJobStatus.Cancelled] = "cancelled",
    };

    private static readonly Dictionary<string, AsrJobStatus> s_parse = s_wire
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>The stable lowercase wire value stored in SQLite and written to artifacts.</summary>
    public static string ToWire(AsrJobStatus status) => s_wire[status];

    public static bool TryParse(string? value, out AsrJobStatus status)
    {
        if (value is not null && s_parse.TryGetValue(value, out status))
        {
            return true;
        }

        status = AsrJobStatus.Failed;
        return false;
    }

    public static AsrJobStatus Parse(string value) =>
        TryParse(value, out var status)
            ? status
            : throw new FormatException(
                $"'{value}' is not a valid ASR job status. Allowed: {string.Join(", ", s_wire.Values)}.");

    /// <summary>True when no further transition may occur.</summary>
    public static bool IsTerminal(AsrJobStatus status) => status is
        AsrJobStatus.Succeeded or AsrJobStatus.Failed or AsrJobStatus.Cancelled;

    /// <summary>True when the job still needs work, i.e. it must be resumed after a restart.</summary>
    public static bool IsResumable(AsrJobStatus status) => !IsTerminal(status);
}
