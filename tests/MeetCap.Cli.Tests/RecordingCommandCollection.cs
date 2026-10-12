using Xunit;

namespace MeetCap.Cli.Tests;

// These tests run the synchronous CLI while waiting synchronously for its worker
// tasks. Running many such recordings alongside other classes can starve the pool
// on small CI runners, before a device starts. Isolate this bounded orchestration
// rather than extending startup deadlines or changing production timing.
[CollectionDefinition("Recording commands", DisableParallelization = true)]
public sealed class RecordingCommandCollection;
