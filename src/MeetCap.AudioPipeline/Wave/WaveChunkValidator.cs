namespace MeetCap.AudioPipeline.Wave;

using MeetCap.Core.Capture;

/// <summary>Result of validating a finalized chunk.</summary>
public sealed record ChunkValidationResult(bool IsValid, long DataBytes, string? Error)
{
    public static ChunkValidationResult Invalid(string error) => new(false, 0, error);

    public static ChunkValidationResult Valid(long dataBytes) => new(true, dataBytes, null);
}

/// <summary>
/// Validates that a finalized chunk is independently readable
/// (docs/ARCHITECTURE.md section 9: "validate header/length" before a chunk counts
/// as durable).
/// </summary>
public static class WaveChunkValidator
{
    /// <summary>
    /// Strict check used at close time: the header must be well-formed, must describe
    /// the format the session is recording, and its declared data length must match
    /// the file exactly.
    /// </summary>
    public static ChunkValidationResult ValidateClosedFile(string path, AudioFormat expectedFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedFormat);

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (IOException ex)
        {
            return ChunkValidationResult.Invalid(ex.Message);
        }

        if (!info.Exists)
        {
            return ChunkValidationResult.Invalid("file does not exist");
        }

        if (info.Length < WavHeader.Size)
        {
            return ChunkValidationResult.Invalid(
                $"file is {info.Length} bytes, shorter than the {WavHeader.Size}-byte header");
        }

        if (!TryReadHeader(path, out var header, out var error))
        {
            return ChunkValidationResult.Invalid(error ?? "header could not be read");
        }

        if (!header.IsValid)
        {
            return ChunkValidationResult.Invalid(header.Error ?? "header is not a valid WAV header");
        }

        var actualFormat = header.Format!;
        if (actualFormat.SampleRate != expectedFormat.SampleRate ||
            actualFormat.Channels != expectedFormat.Channels ||
            actualFormat.BitsPerSample != expectedFormat.BitsPerSample ||
            actualFormat.SampleFormat != expectedFormat.SampleFormat)
        {
            return ChunkValidationResult.Invalid(
                $"header format '{actualFormat}' does not match the session format '{expectedFormat}'");
        }

        var actualDataBytes = info.Length - WavHeader.Size;
        if (header.DeclaredDataBytes != actualDataBytes)
        {
            return ChunkValidationResult.Invalid(
                $"header declares {header.DeclaredDataBytes} data bytes but the file holds {actualDataBytes}");
        }

        return ChunkValidationResult.Valid(actualDataBytes);
    }

    /// <summary>Reads just the header of a file, for inspection and recovery.</summary>
    public static bool TryReadHeader(string path, out WavHeaderInfo header, out string? error)
    {
        header = WavHeaderInfo.Invalid("header was not read");
        error = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(WavHeader.Size, stream.Length);
            if (length < WavHeader.Size)
            {
                header = WavHeaderInfo.Invalid($"file is shorter than {WavHeader.Size} bytes");
                error = header.Error;
                return true;
            }

            var buffer = new byte[WavHeader.Size];
            stream.ReadExactly(buffer);
            header = WavHeader.Parse(buffer);
            error = header.Error;
            return true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
