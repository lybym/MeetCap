namespace MeetCap.Cli.Commands;

using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap devices</c>: what Windows currently offers as a capture
/// endpoint, which one is the system default, and which one the configuration selects.
/// </summary>
/// <remarks>
/// The command is deliberately read-only: it does not touch a session, so it stays
/// usable while a recording is running.
/// </remarks>
internal static class DevicesCommand
{
    public static Task<int> Run(CliContext context)
    {
        var load = context.ConfigurationStore.Load();
        context.Secrets.UpdateFrom(load.Configuration);

        var configuredDeviceId = load.Configuration.Capture.Offline.MicrophoneDeviceId;

        IReadOnlyList<CaptureDeviceInfo> devices;
        try
        {
            // Enumerating devices must not create a database or touch any session, so
            // this command talks to the platform directly.
            devices = CaptureService.OrderDevices(
                context.CreateCapturePlatform().Devices.EnumerateCaptureDevices());
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap devices: {ex.Message}");
            return Task.FromResult(1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            context.Error.WriteLine($"meetcap devices: the Windows audio endpoint API is unavailable: {ex.Message}");
            return Task.FromResult(1);
        }

        context.Out.WriteLine($"configured microphone device: {Describe(configuredDeviceId)}");

        if (devices.Count == 0)
        {
            context.Out.WriteLine("no active capture devices were found.");
            return Task.FromResult(0);
        }

        context.Out.WriteLine();
        context.Out.WriteLine("active capture devices:");

        foreach (var device in devices)
        {
            var markers = new List<string>();
            if (device.IsDefault)
            {
                markers.Add("system default");
            }

            if (IsConfigured(device, configuredDeviceId))
            {
                markers.Add("configured");
            }

            var suffix = markers.Count == 0 ? string.Empty : "  (" + string.Join(", ", markers) + ")";
            context.Out.WriteLine($"  {device.DisplayName}{suffix}");
            context.Out.WriteLine($"      id: {device.Id}");
        }

        Logger(context).LogInformation("devices: {Count} active capture device(s)", devices.Count);
        return Task.FromResult(0);
    }

    private static bool IsConfigured(CaptureDeviceInfo device, string configuredDeviceId)
        => !string.IsNullOrWhiteSpace(configuredDeviceId) &&
           string.Equals(device.Id, configuredDeviceId, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string configuredDeviceId)
        => string.IsNullOrWhiteSpace(configuredDeviceId) ||
           string.Equals(configuredDeviceId, AudioDeviceResolver.DefaultDeviceToken, StringComparison.OrdinalIgnoreCase)
            ? $"{AudioDeviceResolver.DefaultDeviceToken} (whatever Windows currently uses)"
            : configuredDeviceId;

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
}
