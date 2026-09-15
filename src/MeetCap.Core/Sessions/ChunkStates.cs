namespace MeetCap.Core.Sessions;

/// <summary>
/// Chunk lifecycle values stored in <c>audio_chunks.status</c>
/// (docs/DATA_MODEL.md section 5 and docs/ARCHITECTURE.md section 9).
/// </summary>
public static class ChunkStates
{
    /// <summary>Being written right now; on disk as <c>&lt;sequence&gt;.wav.part</c>.</summary>
    public const string Open = "open";

    /// <summary>Finalized, validated and atomically renamed. Immutable from here on.</summary>
    public const string Closed = "closed";

    /// <summary>Repaired from a <c>.part</c> file left behind by an unclean shutdown.</summary>
    public const string Recovered = "recovered";

    /// <summary>Left behind but not repairable. The bytes are kept for inspection.</summary>
    public const string Corrupt = "corrupt";

    /// <summary>Indexed previously, but the file is no longer on disk.</summary>
    public const string Missing = "missing";

    public static readonly IReadOnlyList<string> All = new[] { Open, Closed, Recovered, Corrupt, Missing };

    public static bool IsKnown(string? state) => state is not null && All.Contains(state);

    /// <summary>States whose audio is durable and independently readable.</summary>
    public static bool IsDurable(string? state)
        => state is Closed or Recovered;
}
