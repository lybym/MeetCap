namespace MeetCap.Core.Sessions;

/// <summary>
/// Persistence contract for the session entity. Implemented by
/// <c>MeetCap.Persistence</c> over SQLite; the domain and the CLI depend only on
/// this interface (<c>docs/DEVELOPMENT.md</c> section 4).
/// </summary>
public interface ISessionStore
{
    void Create(Session session);

    /// <summary>Returns the session, or <c>null</c> when no such id exists.</summary>
    Session? Get(string sessionId);

    void Update(Session session);

    /// <summary>Count of sessions in a non-terminal state.</summary>
    int CountActive();
}
