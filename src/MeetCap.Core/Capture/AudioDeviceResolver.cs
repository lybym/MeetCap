namespace MeetCap.Core.Capture;

using MeetCap.Core.Diagnostics;

/// <summary>
/// Turns the configured microphone setting into a concrete capture endpoint.
/// </summary>
/// <remarks>
/// Deterministic domain logic (docs/DEVELOPMENT.md section 6), kept here rather than
/// in the CLI so "what does <c>microphone_device_id</c> mean" is answered in one
/// place that both the commands and the recording pipeline share.
/// </remarks>
public static class AudioDeviceResolver
{
    /// <summary>
    /// The configured value that means "whatever Windows currently considers default".
    /// </summary>
    public const string DefaultDeviceToken = "default";

    /// <summary>
    /// Resolves <paramref name="configuredDeviceId"/> against the available
    /// endpoints. An empty value is treated as <c>default</c>.
    /// </summary>
    /// <exception cref="DeviceUnavailableException">
    /// No default endpoint exists, or the configured endpoint id is not an active
    /// capture device.
    /// </exception>
    public static CaptureDeviceInfo Resolve(IAudioDeviceEnumerator devices, string? configuredDeviceId)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var id = configuredDeviceId?.Trim();
        if (string.IsNullOrEmpty(id) ||
            string.Equals(id, DefaultDeviceToken, StringComparison.OrdinalIgnoreCase))
        {
            return devices.GetDefaultCaptureDevice()
                ?? throw new DeviceUnavailableException(
                    "No default capture device is available. Connect a microphone, or set " +
                    "capture.offline.microphone_device_id to one of the ids reported by 'meetcap devices'.");
        }

        return devices.FindCaptureDevice(id)
            ?? throw new DeviceUnavailableException(
                $"Configured capture device '{id}' is not an active capture endpoint. " +
                $"Run 'meetcap devices' and set capture.offline.microphone_device_id to a listed id. " +
                Describe(devices));
    }

    /// <summary>
    /// Deferred resolution for device-loss recovery: returns <c>null</c> instead of
    /// throwing so the caller can decide how many times to retry.
    /// </summary>
    public static CaptureDeviceInfo? TryResolve(IAudioDeviceEnumerator devices, string? configuredDeviceId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        try
        {
            return Resolve(devices, configuredDeviceId);
        }
        catch (DeviceUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves <paramref name="configuredDeviceId"/> against the available render
    /// endpoints — the speakers/headphones whose playback is captured as loopback
    /// (docs/ARCHITECTURE.md section 7). An empty value is treated as <c>default</c>.
    /// </summary>
    /// <exception cref="DeviceUnavailableException">
    /// No default render endpoint exists, or the configured render endpoint id is not
    /// an active render device.
    /// </exception>
    public static CaptureDeviceInfo ResolveRender(IAudioDeviceEnumerator devices, string? configuredDeviceId)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var id = configuredDeviceId?.Trim();
        if (string.IsNullOrEmpty(id) ||
            string.Equals(id, DefaultDeviceToken, StringComparison.OrdinalIgnoreCase))
        {
            return devices.GetDefaultRenderDevice()
                ?? throw new DeviceUnavailableException(
                    "No default render device is available. Connect speakers or headphones, " +
                    "or set capture.online.render_device_id to one of the ids reported by " +
                    "'meetcap devices'.");
        }

        return devices.FindRenderDevice(id)
            ?? throw new DeviceUnavailableException(
                $"Configured render device '{id}' is not an active render endpoint. " +
                $"Run 'meetcap devices' and set capture.online.render_device_id to a listed id. " +
                DescribeRender(devices));
    }

    /// <summary>
    /// Deferred resolution of a render endpoint for device-loss recovery: returns
    /// <c>null</c> instead of throwing so the caller can decide how many times to
    /// retry.
    /// </summary>
    public static CaptureDeviceInfo? TryResolveRender(IAudioDeviceEnumerator devices, string? configuredDeviceId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        try
        {
            return ResolveRender(devices, configuredDeviceId);
        }
        catch (DeviceUnavailableException)
        {
            return null;
        }
    }

    private static string Describe(IAudioDeviceEnumerator devices)
    {
        var available = devices.EnumerateCaptureDevices();
        if (available.Count == 0)
        {
            return "No capture devices are currently available.";
        }

        var names = available
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(d => $"{d.DisplayName} [{d.Id}]");
        return "Available: " + string.Join("; ", names);
    }

    private static string DescribeRender(IAudioDeviceEnumerator devices)
    {
        var available = devices.EnumerateRenderDevices();
        if (available.Count == 0)
        {
            return "No render devices are currently available.";
        }

        var names = available
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(d => $"{d.DisplayName} [{d.Id}]");
        return "Available render devices: " + string.Join("; ", names);
    }
}
