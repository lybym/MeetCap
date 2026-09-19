namespace MeetCap.AudioPipeline;

using System.Buffers;
using System.Text;
using System.Text.Json;
using MeetCap.Core.Capture;

/// <summary>
/// Renders the capture-relevant part of the effective configuration as the
/// <c>config_snapshot</c> stored on a session (docs/DATA_MODEL.md section 1).
/// </summary>
/// <remarks>
/// Only non-secret capture settings are included. docs/ARCHITECTURE.md section 22
/// forbids copying credentials into session artifacts, and the snapshot is written to
/// a local artifact that may be shared for support, so the redaction is structural
/// rather than best-effort.
/// </remarks>
public static class CaptureConfigSnapshot
{
    public static string ToJson(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("data_root", settings.DataRoot);
            writer.WriteString("mode", settings.Mode);
            writer.WriteNumber("chunk_seconds", settings.ChunkSeconds);
            writer.WriteNumber("buffer_seconds", settings.BufferSeconds);
            writer.WriteNumber("flush_interval_ms", settings.FlushIntervalMs);
            // The recovery window is part of how this session behaved after a device loss, so
            // it belongs in the snapshot rather than only in the live configuration: a session
            // read months later has to be able to say how long its tracks kept retrying before
            // they reported the endpoint unrecoverable (issue #34, docs/CONFIGURATION.md
            // section 12).
            writer.WriteNumber("device_recovery_seconds", settings.DeviceRecoverySeconds);
            writer.WriteNumber("minimum_free_space_gb", settings.MinimumFreeSpaceGb);
            writer.WriteString("microphone_device_id", settings.MicrophoneDeviceId);
            writer.WriteNumber("config_version", settings.ConfigVersion);

            if (settings.Online is { } online)
            {
                writer.WriteStartObject("online");
                writer.WriteString("microphone_device_id", online.MicrophoneDeviceId);
                writer.WriteString("loopback_mode", online.LoopbackMode);
                writer.WriteString("render_device_id", online.RenderDeviceId);
                writer.WriteString("process_name", online.ProcessName);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
