namespace MeetCap.AudioPipeline;

using System.Text.Json;
using System.Text.Json.Serialization;
using MeetCap.Core.Capture;

/// <summary>
/// Capture-track detail recorded in <c>session.json</c>: which endpoint produced the
/// track and in which native format.
/// </summary>
public sealed record CaptureTrackInfo(
    string Source,
    string DeviceId,
    string DeviceName,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string SampleFormat)
{
    public static CaptureTrackInfo From(AudioSource source, CaptureDeviceInfo device, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(format);

        return new CaptureTrackInfo(
            source.ToWireName(),
            device.Id,
            device.DisplayName,
            format.SampleRate,
            format.Channels,
            format.BitsPerSample,
            format.SampleFormatName);
    }
}

/// <summary>
/// The durable, per-track health snapshot recorded in <c>session.json</c> for an online
/// session (docs/ROADMAP.md M5). Each track's bounded-buffer accounting, gap totals and
/// degraded verdict are kept separate so the loss of one track is explicit and never
/// folded into the other (docs/RELIABILITY.md section 8).
/// </summary>
public sealed record TrackHealth(
    string Source,
    AudioBufferHealth BufferHealth,
    long GapTotalMs,
    int GapCount,
    bool Degraded,
    string? EndReason,
    int ChunksClosed,
    long ClosedDataBytes)
{
    public TrackHealth() : this(
        string.Empty,
        AudioBufferHealth.Empty,
        0,
        0,
        false,
        null,
        0,
        0)
    {
    }
}

/// <summary>
/// Durable session description written to <c>sessions/&lt;id&gt;/session.json</c>
/// (docs/DATA_MODEL.md section 3).
/// </summary>
/// <remarks>
/// <see cref="Tracks"/> keeps the documented shape (<c>["mic"]</c>) while
/// <see cref="Capture"/> carries the per-track device/format detail M1 needs. The
/// file is written atomically whenever the session's state changes, so a truncated
/// manifest never overwrites a good one.
/// </remarks>
public sealed class SessionManifest
{
    public required string SessionId { get; init; }

    public required string Title { get; init; }

    public required string Mode { get; init; }

    public required string SourceType { get; init; }

    public required string Status { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }

    public int ConfigVersion { get; init; }

    public IReadOnlyList<string> Tracks { get; init; } = Array.Empty<string>();

    /// <summary>The configured chunk length used by this session, in seconds.</summary>
    public int ChunkSeconds { get; init; }

    /// <summary>True when the session hit an audio discontinuity, device loss or storage problem.</summary>
    public bool Degraded { get; set; }

    /// <summary>Why the session ended, when it did not end through a clean stop request.</summary>
    public string? EndReason { get; set; }

    /// <summary>Set by startup recovery when this session was found not cleanly stopped.</summary>
    public DateTimeOffset? RecoveredAt { get; set; }

    /// <summary>
    /// How many discontinuities the capture timeline reported during this session. Written
    /// with the session so the missing audio is part of the durable record rather than only
    /// a line in the event log (docs/RELIABILITY.md section 7).
    /// </summary>
    public int GapCount { get; set; }

    /// <summary>Total audio time the capture timeline reported as missing, in milliseconds.</summary>
    public long GapTotalMs { get; set; }

    /// <summary>
    /// The bounded-buffer accounting for this session: queue bound, deepest backlog,
    /// packets the bound refused, and how long a stalled consumer held a backlog
    /// (docs/RELIABILITY.md section 4).
    /// </summary>
    /// <remarks>
    /// For a dual-track (online) session this is the aggregate across tracks; the
    /// per-track breakdown is in <see cref="TrackHealth"/> (docs/ROADMAP.md M5).
    /// </remarks>
    public AudioBufferHealth? CaptureHealth { get; set; }

    /// <summary>
    /// Per-track capture health and degraded state. An online session has one entry per
    /// track (mic and loopback); an offline session has one entry. Loss or degradation of
    /// one track is explicit here and never silently folded into the other track
    /// (docs/ROADMAP.md M5, docs/RELIABILITY.md section 8).
    /// </summary>
    public IReadOnlyList<TrackHealth> TrackHealth { get; set; } = Array.Empty<TrackHealth>();

    /// <summary>
    /// Set by startup recovery or <c>meetcap session repair</c> when the session's
    /// timeline still has a provable hole after every repair that could be attempted.
    /// A session with this flag must never be presented as fully recovered.
    /// </summary>
    public bool GapsRemain { get; set; }

    /// <summary>Where the remaining audio is missing, one line per gap.</summary>
    public IReadOnlyList<string> GapDetails { get; set; } = Array.Empty<string>();

    public IReadOnlyList<CaptureTrackInfo> Capture { get; set; } = Array.Empty<CaptureTrackInfo>();
}

/// <summary>Reads and atomically writes <c>session.json</c>.</summary>
public static class SessionManifestStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a manifest to its on-disk representation.</summary>
    public static string Serialize(SessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, s_options);
    }

    /// <summary>
    /// Writes the manifest atomically: a temporary file is fully written and flushed
    /// before it replaces the previous manifest.
    /// </summary>
    public static void Save(string path, SessionManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(manifest);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, Serialize(manifest));
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>
    /// Loads a manifest. Returns <c>false</c> for a missing or unreadable file rather
    /// than throwing, so one damaged session cannot abort a recovery scan.
    /// </summary>
    public static bool TryLoad(string path, out SessionManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                error = "session.json is missing";
                return false;
            }

            var json = File.ReadAllText(path);
            manifest = JsonSerializer.Deserialize<SessionManifest>(json, s_options);
            if (manifest is null)
            {
                error = "session.json contained no session object";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "session.json is not valid JSON: " + ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
