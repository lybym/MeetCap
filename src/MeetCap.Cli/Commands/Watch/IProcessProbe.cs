namespace MeetCap.Cli.Commands.Watch;

using System.Diagnostics;

/// <summary>
/// Polls the operating system for currently-running watched main processes. Injected so
/// the watcher state machine is deterministic under test: production uses
/// <see cref="ProcessProbe"/> (a thin wrapper over <see cref="Process.GetProcessesByName"/>),
/// tests supply a scripted probe.
/// </summary>
/// <remarks>
/// The probe returns <em>main</em> processes only. Helper/child processes
/// (<c>CloudVideoNet.exe</c>, <c>crashpad_handler.exe</c>, <c>sdk_transcode_*.exe</c>,
/// <c>PageMonitorMgr.exe</c>) never appear, because they are not in the configured
/// <c>watch.processes</c> list and are never matched by name.
/// </remarks>
public interface IProcessProbe
{
    /// <summary>The watched main processes running right now, with PID and start time.</summary>
    IReadOnlyList<WatchedProcess> Snapshot();
}

/// <summary>
/// Production probe: <see cref="Process.GetProcessesByName"/> for each configured main
/// process name. No admin rights and no WMI infrastructure — a simple poll that adds at
/// most one poll interval of missed audio at meeting start, which the durable chunk
/// timeline already tolerates (docs/ARCHITECTURE.md, watch section).
/// </summary>
internal sealed class ProcessProbe : IProcessProbe
{
    private readonly IReadOnlyList<string> _names;

    public ProcessProbe(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        // Bare names are the contract; a user-supplied ".exe" suffix would not match
        // GetProcessesByName, so it is normalized here rather than silently never matching.
        _names = processNames
            .Select(NormalizeName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<WatchedProcess> Snapshot()
    {
        if (_names.Count == 0)
        {
            return Array.Empty<WatchedProcess>();
        }

        var found = new List<WatchedProcess>();
        foreach (var name in _names)
        {
            // GetProcessesByName matches the bare module name (without .exe) and is
            // case-insensitive on Windows, so "WeMeetApp" matches "WeMeetApp.exe".
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
            {
                // A transient platform failure must not kill the watcher; treat as "none seen".
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    // StartTime is the fingerprint's second component. It can throw if the
                    // process has already exited or is inaccessible; such a process is skipped
                    // rather than crashing the poll.
                    var start = new DateTimeOffset(process.StartTime);
                    found.Add(new WatchedProcess(name, process.Id, start));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                    or InvalidOperationException
                    or NotSupportedException)
                {
                    // The process disappeared between enumeration and the StartTime read, or
                    // access was denied. Skip it: a process we cannot fingerprint cannot be
                    // reliably distinguished from PID reuse, so it must not trigger.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return found;
    }

    internal static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed;
    }
}
