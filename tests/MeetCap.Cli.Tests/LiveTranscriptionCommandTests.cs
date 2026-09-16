using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// The M4 exit criteria exercised through the real command tree: a live offline meeting
/// produces transcript updates while every cloud call is a file-ASR call, a lost network does
/// not disturb recording and the queue resumes afterwards, and a process that ends with work
/// outstanding can be resumed from its durable state
/// (<c>docs/ROADMAP.md</c> M4).
/// </summary>
/// <remarks>
/// The provider's HTTP boundary is scripted (<see cref="ScriptedAsrHttpHandler"/>), so the
/// command composition, the SQLite job queue, the batch builder, the retained raw response,
/// the normalizer, and the transcript writer are all the shipped implementations
/// (<c>docs/DEVELOPMENT.md</c> section 7). Audio comes from the scripted capture source, not
/// from a microphone, so this is automated coverage and not real Windows audio validation.
/// </remarks>
public class LiveTranscriptionCommandTests
{
    [Fact]
    public async Task Start_WithLiveAsr_ProducesBatchedFileAsrTranscriptDuringTheMeeting()
    {
        using var harness = CliHarness.Create();
        harness.WriteLiveAsrConfig(chunkSeconds: 1, fileBatchSeconds: 2);

        var startTask = Task.Run(() => harness.Run("start", "Weekly Review"));
        var sessionDirectory = WaitForSessionDirectory(harness.DataRoot);
        Assert.NotNull(sessionDirectory);
        var sessionId = Path.GetFileName(sessionDirectory!);
        WaitForSessionStatus(harness.DataRoot, sessionId, SessionStatus.Recording);

        // The transcript advances during the meeting: a closed window is batched, queued,
        // submitted, and rendered while capture is still running.
        WaitForSucceededJob(harness.DataRoot, sessionId);
        WaitForFile(Path.Combine(sessionDirectory!, "transcript", "raw.jsonl"));

        var inMeetingTranscript = File.ReadAllLines(
            Path.Combine(sessionDirectory!, "transcript", "raw.jsonl"));
        Assert.NotEmpty(inMeetingTranscript);
        Assert.True(harness.AsrHttp.Submits > 0, "expected a file-ASR submission during the meeting");

        var stop = harness.Run("stop");

        // Bounded: if the recorder or the final drain ever fails to finish, the test must fail
        // with a clear message rather than hanging the suite and the CI job.
        var start = await AwaitBounded(
            startTask,
            TimeSpan.FromSeconds(60),
            "meetcap start did not finish after meetcap stop");

        Assert.True(
            stop.ExitCode == 0 && start.ExitCode == 0,
            "start/stop did not both succeed" + Describe(start, stop));

        // Every cloud call went to the file-ASR submit/query endpoints.
        Assert.True(harness.AsrHttp.Queries > 0, "expected at least one file-ASR query" + Describe(start, stop));
        Assert.Contains("asr: file ASR, batch window 2s", start.Output, StringComparison.Ordinal);

        // The durable batch artifacts exist, one per closed window, plus a flushed partial one.
        var batchFiles = Directory.GetFiles(
            Path.Combine(sessionDirectory!, "asr", "batches", "mic"),
            "*.wav");
        Assert.NotEmpty(batchFiles);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(sessionDirectory!, "asr", "batches", "mic"),
            "*.part"));

        foreach (var batch in batchFiles)
        {
            // A batch is a real, independently readable 16 kHz-ready WAV, and its timeline
            // manifest is what maps it back to the capture chunks it was built from.
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(batch), 0, 4));
            var manifest = Path.ChangeExtension(batch, ".json");
            Assert.True(File.Exists(manifest), $"expected a batch manifest next to {batch}");
        }

        // The transcript exists while the meeting has just ended, and carries the provider's
        // text, its anonymous speaker label, and session-relative timestamps.
        var rawTranscript = Path.Combine(sessionDirectory!, "transcript", "raw.jsonl");
        Assert.True(File.Exists(rawTranscript), "expected transcript/raw.jsonl to exist");
        var rawLines = File.ReadAllLines(rawTranscript);
        Assert.NotEmpty(rawLines);
        Assert.Contains("\"speaker_label\":\"speaker_1\"", rawLines[0], StringComparison.Ordinal);
        Assert.Contains("\"start_ms\":", rawLines[0], StringComparison.Ordinal);

        var markdown = Path.Combine(sessionDirectory!, "transcript", "live.md");
        Assert.True(File.Exists(markdown), "expected transcript/live.md to exist");
        Assert.Contains(harness.AsrHttp.RecognizedText, File.ReadAllText(markdown), StringComparison.Ordinal);

        // Every batch became a persistent job, and the session is finished because the queue
        // is terminal.
        var database = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        var jobs = database.AsrJobs.ListBySession(sessionId);
        Assert.NotEmpty(jobs);
        Assert.All(jobs, job => Assert.Equal(Core.Asr.AsrJobStatus.Succeeded, job.Status));
        Assert.Equal(SessionStatus.Completed, database.Sessions.Find(sessionId)!.Status);

        Assert.Contains("asr batches:", start.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkLoss_DoesNotStopRecordingAndTheQueueResumesAfterwards()
    {
        using var harness = CliHarness.Create();
        harness.WriteLiveAsrConfig(chunkSeconds: 1, fileBatchSeconds: 2, pollTimeoutSeconds: 5);

        // The network is gone before the meeting starts and stays gone while it runs.
        harness.AsrHttp.Offline = true;

        var startTask = Task.Run(() => harness.Run("start", "Offline Review"));
        var sessionDirectory = WaitForSessionDirectory(harness.DataRoot);
        Assert.NotNull(sessionDirectory);
        WaitForSessionStatus(harness.DataRoot, Path.GetFileName(sessionDirectory!), SessionStatus.Recording);

        WaitForChunkCount(sessionDirectory!, minimumChunks: 3);

        var stop = harness.Run("stop");
        var start = await AwaitBounded(
            startTask,
            TimeSpan.FromSeconds(90),
            "meetcap start did not finish after meetcap stop during the scripted outage");

        var sessionId = Path.GetFileName(sessionDirectory!);

        // Recording was never disturbed: the session completed cleanly and every chunk is
        // durable (docs/RELIABILITY.md section 1).
        Assert.Equal(0, stop.ExitCode);
        Assert.True(start.ExitCode == 0, "start did not exit cleanly" + Describe(start, stop));
        Assert.Contains("chunks closed:", start.Output, StringComparison.Ordinal);
        var chunks = Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "mic"), "*.wav");
        Assert.NotEmpty(chunks);
        Assert.Empty(Directory.GetFiles(Path.Combine(sessionDirectory!, "audio", "mic"), "*.part"));

        // The audio was still batched locally; only the cloud call failed, so the queue holds
        // work instead of the transcript.
        Assert.Equal(0, harness.AsrHttp.Submits);

        var database = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"));
        var pending = database.AsrJobs.ListBySession(sessionId);
        Assert.NotEmpty(pending);
        Assert.All(
            pending,
            job => Assert.True(
                Core.Asr.AsrJobStatuses.IsResumable(job.Status),
                $"expected job '{job.Id}' to still need work but it is {job.Status}"));

        // status reports the queue and the degradation without touching the recording.
        var status = harness.Run("status");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("asr queue:", status.Output, StringComparison.Ordinal);
        Assert.Contains("asr state: behind", status.Output, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(sessionDirectory!, "transcript", "raw.jsonl")));

        // The network comes back: `meetcap asr resume` is the restart entry point and finishes
        // the work the recording left behind.
        harness.AsrHttp.Offline = false;
        var resume = harness.Run("asr", "resume", "--force", "--session", sessionId);
        Assert.True(
            resume.ExitCode == 0,
            $"resume failed (exit {resume.ExitCode})\n{resume.Output}\n{resume.Error}\n" +
            $"jobs=[{string.Join(",", database.AsrJobs.ListBySession(sessionId).Select(j => j.Id + ":" + j.Status + ":" + j.ErrorCode))}]");

        var resumed = database.AsrJobs.ListBySession(sessionId);
        Assert.All(resumed, job => Assert.Equal(Core.Asr.AsrJobStatus.Succeeded, job.Status));

        var rawTranscript = Path.Combine(sessionDirectory!, "transcript", "raw.jsonl");
        Assert.True(File.Exists(rawTranscript), "expected transcript/raw.jsonl after recovery");
        Assert.NotEmpty(File.ReadAllLines(rawTranscript));
        Assert.Contains(
            harness.AsrHttp.RecognizedText,
            File.ReadAllText(Path.Combine(sessionDirectory!, "transcript", "live.md")),
            StringComparison.Ordinal);

        // The queue drains again in `status` once the work is done.
        var after = harness.Run("status");
        Assert.Equal(0, after.ExitCode);
        Assert.Contains("0 outstanding", after.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_ResumesBatchesQueuedByThePreviousProcess()
    {
        using var harness = CliHarness.Create();
        harness.WriteLiveAsrConfig(chunkSeconds: 1, fileBatchSeconds: 2, pollTimeoutSeconds: 5);

        harness.AsrHttp.Offline = true;

        var startTask = Task.Run(() => harness.Run("start", "Interrupted Review"));
        var sessionDirectory = WaitForSessionDirectory(harness.DataRoot);
        Assert.NotNull(sessionDirectory);
        WaitForSessionStatus(harness.DataRoot, Path.GetFileName(sessionDirectory!), SessionStatus.Recording);
        WaitForChunkCount(sessionDirectory!, minimumChunks: 3);

        harness.Run("stop");
        var start = await AwaitBounded(
            startTask,
            TimeSpan.FromSeconds(90),
            "meetcap start did not finish after meetcap stop");

        // "Restart" is exactly what `meetcap asr resume` models: a fresh process reading the
        // durable queue. It must address the same provider tasks rather than re-submitting.
        var providerRequestIds = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"))
            .AsrJobs
            .ListBySession(Path.GetFileName(sessionDirectory!))
            .Select(job => job.ProviderRequestId)
            .ToArray();
        Assert.NotEmpty(providerRequestIds);

        harness.AsrHttp.Offline = false;
        var resumed = harness.Run("asr", "resume", "--force");
        Assert.Equal(0, resumed.ExitCode);
        Assert.Equal(0, start.ExitCode);

        var after = new MeetCapDatabase(Path.Combine(harness.DataRoot, "meetcap.db"))
            .AsrJobs
            .ListBySession(Path.GetFileName(sessionDirectory!));
        Assert.All(after, job => Assert.Equal(Core.Asr.AsrJobStatus.Succeeded, job.Status));

        // The provider request id was allocated once and persisted before the first submit, so
        // the resuming process reused it instead of inventing a second billable task.
        Assert.Equal(
            providerRequestIds.OrderBy(id => id, StringComparer.Ordinal),
            after.Select(job => job.ProviderRequestId).OrderBy(id => id, StringComparer.Ordinal));

        var rawTranscript = Path.Combine(sessionDirectory!, "transcript", "raw.jsonl");
        Assert.True(File.Exists(rawTranscript), "expected transcript/raw.jsonl after restart resume");

        // `transcript/raw.jsonl` is derived from each job's retained normalized artifact, so it
        // can be rebuilt without replaying the meeting.
        var normalizedArtifacts = Directory.GetFiles(
            Path.Combine(sessionDirectory!, "asr", "jobs"),
            "normalized.jsonl",
            SearchOption.AllDirectories);
        Assert.NotEmpty(normalizedArtifacts);
        Assert.Equal(
            normalizedArtifacts.Sum(path => File.ReadAllLines(path).Length),
            File.ReadAllLines(rawTranscript).Length);
    }

    private static string Describe(CliResult start, CliResult stop) =>
        $"\n--- start (exit {start.ExitCode}) ---\n{start.Output}\n{start.Error}" +
        $"\n--- stop (exit {stop.ExitCode}) ---\n{stop.Output}\n{stop.Error}";

    private static async Task<CliResult> AwaitBounded(Task<CliResult> task, TimeSpan timeout, string description)    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"{description} (waited {timeout}).");
        }

        return await task.ConfigureAwait(false);
    }

    private static string? WaitForSessionDirectory(string dataRoot)
    {
        var sessionsRoot = Path.Combine(dataRoot, "sessions");
        var deadline = Environment.TickCount64 + 30_000;

        while (Environment.TickCount64 < deadline)
        {
            if (Directory.Exists(sessionsRoot))
            {
                var directory = Directory.GetDirectories(sessionsRoot).FirstOrDefault();
                if (directory is not null && File.Exists(Path.Combine(directory, "session.json")))
                {
                    return directory;
                }
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private static void WaitForSessionStatus(string dataRoot, string sessionId, string status)
    {
        var database = new MeetCapDatabase(Path.Combine(dataRoot, "meetcap.db"));
        var deadline = Environment.TickCount64 + 30_000;

        while (Environment.TickCount64 < deadline)
        {
            if (database.IsInitialized() &&
                string.Equals(database.Sessions.Find(sessionId)?.Status, status, StringComparison.Ordinal))
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"meetcap start did not reach status {status} in time.");
    }

    /// <summary>
    /// Waits until the recording has closed enough chunks for at least one ASR batch window.
    /// </summary>
    private static void WaitForChunkCount(string sessionDirectory, int minimumChunks)
    {
        var directory = Path.Combine(sessionDirectory, "audio", "mic");
        var deadline = Environment.TickCount64 + 60_000;

        while (Environment.TickCount64 < deadline)
        {
            if (Directory.Exists(directory) &&
                Directory.GetFiles(directory, "*.wav").Length >= minimumChunks)
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException(
            $"the recording did not close {minimumChunks} chunk(s) in time under {directory}.");
    }

    /// <summary>
    /// Waits until at least one of the session's ASR jobs reaches a terminal state, which is
    /// what "the transcript advances during the meeting" means observably.
    /// </summary>
    private static void WaitForSucceededJob(string dataRoot, string sessionId)
    {
        var database = new MeetCapDatabase(Path.Combine(dataRoot, "meetcap.db"));
        var deadline = Environment.TickCount64 + 60_000;

        while (Environment.TickCount64 < deadline)
        {
            var jobs = database.AsrJobs.ListBySession(sessionId);
            if (jobs.Any(job => job.Status == Core.Asr.AsrJobStatus.Succeeded))
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException(
            $"no ASR job for session {sessionId} succeeded while the recording was still running.");
    }

    private static void WaitForFile(string path)
    {
        var deadline = Environment.TickCount64 + 30_000;

        while (Environment.TickCount64 < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"'{path}' did not appear in time.");
    }
}
