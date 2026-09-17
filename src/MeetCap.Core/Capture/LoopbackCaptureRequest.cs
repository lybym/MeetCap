namespace MeetCap.Core.Capture;

/// <summary>
/// How the loopback track captures what the machine plays. System loopback is the
/// required baseline; process loopback is an additionally supported capture-source
/// option on capable systems, never a separate meeting mode
/// (docs/ARCHITECTURE.md section 7, docs/ROADMAP.md M5).
/// </summary>
public enum LoopbackMode
{
    /// <summary>
    /// Capture everything the render endpoint plays. The baseline loopback source,
    /// available wherever a render endpoint exists.
    /// </summary>
    System,

    /// <summary>
    /// Capture only a target process's audio where the running Windows/NAudio
    /// combination supports it. Additive to <see cref="System"/>, not a new mode.
    /// </summary>
    Process,
}

/// <summary>
/// Wire names for <see cref="LoopbackMode"/> matching
/// <c>capture.online.loopback_mode</c> (docs/CONFIGURATION.md).
/// </summary>
public static class LoopbackModes
{
    public const string System = "system";
    public const string Process = "process";

    public static readonly IReadOnlyList<string> All = new[] { System, Process };

    public static string ToWireName(this LoopbackMode mode) => mode switch
    {
        LoopbackMode.System => System,
        LoopbackMode.Process => Process,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown loopback mode."),
    };

    public static bool TryParse(string? value, out LoopbackMode mode)
    {
        switch (value)
        {
            case System:
                mode = LoopbackMode.System;
                return true;
            case Process:
                mode = LoopbackMode.Process;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static LoopbackMode Parse(string value)
        => TryParse(value, out var mode)
            ? mode
            : throw new ArgumentException(
                $"Unknown loopback mode '{value}'. Allowed: {System}, {Process}.",
                nameof(value));
}

/// <summary>
/// Everything the platform audio boundary needs to build the loopback capture source,
/// carried as a MeetCap-owned value so no NAudio type crosses the assembly boundary
/// (docs/DEVELOPMENT.md section 4). The render endpoint is resolved upstream; the
/// process name is resolved to an OS process id by the boundary that owns platform
/// concerns.
/// </summary>
/// <remarks>
/// Mic and loopback are never mixed before ASR (docs/ARCHITECTURE.md section 6), and
/// one capture callback must never wait for the other track
/// (docs/RELIABILITY.md section 3), so a loopback request is a complete, independent
/// description of one track rather than a reference to the microphone's source.
/// </remarks>
public sealed record LoopbackCaptureRequest(
    CaptureDeviceInfo RenderDevice,
    LoopbackMode Mode,
    string ProcessName)
{
    public LoopbackMode Mode { get; } = Mode;
    public string ProcessName { get; } = ProcessName ?? string.Empty;

    /// <summary>True when this request targets a single process rather than the whole render endpoint.</summary>
    public bool IsProcessLoopback => Mode == LoopbackMode.Process;
}
