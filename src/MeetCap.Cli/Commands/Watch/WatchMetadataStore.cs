namespace MeetCap.Cli.Commands.Watch;

using System.IO;
using System.Text.Json;

/// <summary>
/// The watch trigger and stop metadata persisted beside a watched session as
/// <c>watch.meta.json</c>. A sidecar file rather than a new SQLite table or a changed
/// <c>session.json</c> schema: issue #52 adds no persisted schema, only the sessions the
/// watcher creates, and this provenance file travels with each one.
/// </summary>
internal sealed class WatchSessionMetadata
{
    public WatchTriggerRecord? Trigger { get; set; }

    public WatchStopRecord? Stop { get; set; }

    public sealed class WatchTriggerRecord
    {
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public DateTimeOffset ProcessStartTime { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
    }

    public sealed class WatchStopRecord
    {
        public string Reason { get; set; } = string.Empty;
        public DateTimeOffset StoppedAt { get; set; }
    }
}

/// <summary>
/// Reads and writes <c>watch.meta.json</c> in a session directory. The writer is
/// append-style: the trigger is written when the session starts, and the stop section is
/// merged in when the session finalizes, so a watcher that crashes mid-recording still
/// leaves the trigger provenance behind (the stop section is simply absent).
/// </summary>
internal static class WatchMetadataStore
{
    public const string FileName = "watch.meta.json";

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string PathFor(string sessionDirectory) => Path.Combine(sessionDirectory, FileName);

    /// <summary>Writes (or overwrites) the trigger section at session start.</summary>
    public static void WriteTrigger(string sessionDirectory, WatchTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        ArgumentNullException.ThrowIfNull(trigger);

        var metadata = Read(sessionDirectory) ?? new WatchSessionMetadata();
        metadata.Trigger = new WatchSessionMetadata.WatchTriggerRecord
        {
            ProcessName = trigger.ProcessName,
            ProcessId = trigger.ProcessId,
            ProcessStartTime = trigger.ProcessStartTime,
            ObservedAt = trigger.ObservedAt,
        };

        Write(sessionDirectory, metadata);
    }

    /// <summary>Merges the stop section into the existing metadata at session finalize.</summary>
    public static void WriteStop(string sessionDirectory, string reason, DateTimeOffset stoppedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);

        var metadata = Read(sessionDirectory) ?? new WatchSessionMetadata();
        metadata.Stop = new WatchSessionMetadata.WatchStopRecord
        {
            Reason = reason,
            StoppedAt = stoppedAt,
        };

        Write(sessionDirectory, metadata);
    }

    /// <summary>Reads the metadata, or <c>null</c> when no sidecar exists.</summary>
    public static WatchSessionMetadata? Read(string sessionDirectory)
    {
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        var path = PathFor(sessionDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WatchSessionMetadata>(json, s_options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or unreadable sidecar must not break finalization; treat as absent.
            return null;
        }
    }

    private static void Write(string sessionDirectory, WatchSessionMetadata metadata)
    {
        Directory.CreateDirectory(sessionDirectory);
        var json = JsonSerializer.Serialize(metadata, s_options);
        File.WriteAllText(PathFor(sessionDirectory), json);
    }
}
