using Microsoft.Data.Sqlite;

namespace MeetCap.Persistence.Tests.Storage;

/// <summary>
/// Disposable temporary data root for persistence tests. Pooling is disabled by the
/// production connection factory, but the pool is still cleared before deletion so a
/// lock on Windows cannot flake a test.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "meetcap-persistence-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DatabasePath = Path.Combine(Root, "meetcap.db");
    }

    public string Root { get; }

    public string DatabasePath { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(Root))
        {
            return;
        }

        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            Thread.Sleep(100);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
