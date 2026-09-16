namespace MeetCap.Core.Capture;

/// <summary>
/// A capture endpoint as MeetCap sees it. <see cref="Id"/> is the stable Windows
/// endpoint id, which is what <c>capture.*.microphone_device_id</c> persists
/// (docs/CONFIGURATION.md section 5).
/// </summary>
public sealed record CaptureDeviceInfo(string Id, string FriendlyName, bool IsDefault)
{
    /// <summary>Human-facing label, falling back to the id when the name is empty.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? Id : FriendlyName;
}
