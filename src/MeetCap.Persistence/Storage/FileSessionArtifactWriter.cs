namespace MeetCap.Persistence.Storage;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetCap.Core.Sessions;

/// <summary>
/// Filesystem implementation of <see cref="ISessionArtifactWriter"/>.
/// </summary>
/// <remarks>
/// SQLite indexes state; these files are the record. Artifacts are written even
/// when the database is unavailable, and <c>events.jsonl</c> is only ever appended
/// to, so the operational history of a session cannot be rewritten by a later step.
/// </remarks>
public sealed class FileSessionArtifactWriter : ISessionArtifactWriter
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public void EnsureLayout(SessionArtifactPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Defensive: a caller that writes artifacts without going through MeetCapDatabase
        // still gets the private-data marker.
        DataRootMarker.EnsureSelfIgnoring(paths.DataRoot);

        Directory.CreateDirectory(paths.SessionDirectory);
        Directory.CreateDirectory(paths.ImportAudioDirectory);
        Directory.CreateDirectory(paths.AsrJobsDirectory);
        Directory.CreateDirectory(paths.AsrBatchesDirectory);
        Directory.CreateDirectory(paths.TranscriptDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
    }

    public string WriteSessionDocument(SessionArtifactPaths paths, SessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(document);

        EnsureLayout(paths);
        File.WriteAllText(paths.SessionJson, JsonSerializer.Serialize(document, s_json), s_utf8);
        return paths.SessionJson;
    }

    public SessionDocument? ReadSessionDocument(SessionArtifactPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.SessionJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SessionDocument>(File.ReadAllText(paths.SessionJson), s_json);
    }

    public void AppendEvent(
        SessionArtifactPaths paths,
        string name,
        long atMs,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        EnsureLayout(paths);

        var json = new JsonObject
        {
            ["event"] = name,
            ["at_ms"] = atMs,
            ["at"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        if (details is not null)
        {
            foreach (var (key, value) in details)
            {
                json[key] = value is null ? null : JsonValue.Create(value);
            }
        }

        using var stream = new FileStream(paths.EventsJsonl, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, s_utf8);
        writer.WriteLine(json.ToJsonString());
    }
}
