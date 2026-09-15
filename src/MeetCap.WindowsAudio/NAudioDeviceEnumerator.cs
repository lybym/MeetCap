namespace MeetCap.WindowsAudio;

using MeetCap.Core.Capture;
using NAudio.CoreAudioApi;

/// <summary>
/// Enumerates Windows capture endpoints with NAudio 3.
/// </summary>
/// <remarks>
/// This is the whole of MeetCap's device-enumeration dependency on NAudio: the rest
/// of the application only ever sees <see cref="CaptureDeviceInfo"/>
/// (docs/ARCHITECTURE.md section 3, docs/DEVELOPMENT.md section 4).
/// </remarks>
public sealed class NAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<CaptureDeviceInfo> EnumerateCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultDeviceId(enumerator);

        using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var devices = new List<CaptureDeviceInfo>(collection.Count);

        for (var i = 0; i < collection.Count; i++)
        {
            using var device = collection[i];
            var id = SafeId(device);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            devices.Add(new CaptureDeviceInfo(
                id,
                SafeFriendlyName(device),
                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
        }

        return devices;
    }

    public CaptureDeviceInfo? GetDefaultCaptureDevice()
    {
        using var enumerator = new MMDeviceEnumerator();

        foreach (var role in DefaultRolePreference)
        {
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, role, out var device))
            {
                continue;
            }

            using (device)
            {
                var id = SafeId(device);
                if (!string.IsNullOrEmpty(id))
                {
                    return new CaptureDeviceInfo(id, SafeFriendlyName(device), IsDefault: true);
                }
            }
        }

        return null;
    }

    public CaptureDeviceInfo? FindCaptureDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        // An endpoint id stays valid across sessions, but it only resolves while the
        // endpoint is an active capture device; a disabled or unplugged device must
        // fail loudly rather than silently falling back to another microphone.
        using var enumerator = new MMDeviceEnumerator();
        using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

        for (var i = 0; i < collection.Count; i++)
        {
            using var device = collection[i];
            var id = SafeId(device);
            if (id is not null && string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return new CaptureDeviceInfo(id, SafeFriendlyName(device), IsDefault: false);
            }
        }

        return null;
    }

    /// <summary>
    /// Windows "default device" for capture is the multimedia role; the
    /// communications role is the "default communication device" and is used as a
    /// fallback so a machine that only defines one of the two still resolves.
    /// </summary>
    private static readonly Role[] DefaultRolePreference =
    {
        Role.Multimedia,
        Role.Communications,
        Role.Console,
    };

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        foreach (var role in DefaultRolePreference)
        {
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, role, out var device))
            {
                continue;
            }

            using (device)
            {
                var id = SafeId(device);
                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }
            }
        }

        return null;
    }

    private static string? SafeId(MMDevice device)
    {
        try
        {
            return device.ID;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            var name = device.FriendlyName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Fall through to the id-based label.
        }

        try
        {
            return device.DeviceFriendlyName ?? device.ID;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return SafeId(device) ?? "unknown capture device";
        }
    }
}
