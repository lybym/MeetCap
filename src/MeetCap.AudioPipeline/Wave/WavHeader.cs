namespace MeetCap.AudioPipeline.Wave;

using System.Buffers.Binary;
using System.Text;
using MeetCap.Core.Capture;

/// <summary>
/// The canonical 44-byte RIFF/WAVE header MeetCap writes for every capture chunk.
/// </summary>
/// <remarks>
/// Chunks are deliberately plain, header-only WAV files. docs/RELIABILITY.md
/// section 5 requires a closed chunk to be independently readable, and a format that
/// any tool can open is what makes "validate then atomically rename" meaningful.
/// <para>
/// Only the 16-byte <c>fmt </c> form is emitted, which covers PCM and IEEE float and
/// keeps recovery trivial: the data length is always <c>fileLength - 44</c>, so a
/// truncated <c>.part</c> file can be repaired without guessing.
/// </para>
/// </remarks>
public static class WavHeader
{
    /// <summary>Bytes in a MeetCap WAV header.</summary>
    public const int Size = 44;

    private const int RiffChunkSizeOffset = 4;
    private const int DataChunkSizeOffset = 40;

    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;

    /// <summary>Largest data length a 32-bit RIFF size field can describe.</summary>
    public const long MaxDataBytes = uint.MaxValue - 36L;

    /// <summary>Builds a header describing <paramref name="dataBytes"/> of audio.</summary>
    public static byte[] Build(AudioFormat format, long dataBytes)
    {
        ArgumentNullException.ThrowIfNull(format);
        ValidateDataLength(dataBytes);

        var header = new byte[Size];
        WriteHeader(header, format, dataBytes);
        return header;
    }

    /// <summary>Rewrites the two size fields of an existing header in place.</summary>
    public static void PatchDataLength(Span<byte> header, long dataBytes)
    {
        if (header.Length < Size)
        {
            throw new ArgumentException($"A WAV header must be at least {Size} bytes.", nameof(header));
        }

        ValidateDataLength(dataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[RiffChunkSizeOffset..], (uint)(36 + dataBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(header[DataChunkSizeOffset..], (uint)dataBytes);
    }

    /// <summary>
    /// Parses a header. Returns an invalid result rather than throwing so recovery
    /// can report a corrupt chunk without aborting the whole scan.
    /// </summary>
    public static WavHeaderInfo Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < Size)
        {
            return WavHeaderInfo.Invalid($"file is shorter than a {Size}-byte WAV header");
        }

        if (!Matches(header, 0, "RIFF"))
        {
            return WavHeaderInfo.Invalid("missing RIFF signature");
        }

        if (!Matches(header, 8, "WAVE"))
        {
            return WavHeaderInfo.Invalid("missing WAVE signature");
        }

        if (!Matches(header, 12, "fmt "))
        {
            return WavHeaderInfo.Invalid("missing fmt chunk");
        }

        var fmtSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (fmtSize < 16)
        {
            return WavHeaderInfo.Invalid($"unsupported fmt chunk size {fmtSize}");
        }

        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(header[20..]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);

        if (!Matches(header, 36, "data"))
        {
            var dataTag = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
            return WavHeaderInfo.Invalid($"expected a data chunk at offset 36 but found tag {dataTag}");
        }

        var declaredDataBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[DataChunkSizeOffset..]);

        var sampleFormat = formatTag switch
        {
            FormatPcm => AudioSampleFormat.Pcm,
            FormatIeeeFloat => AudioSampleFormat.IeeeFloat,
            _ => (AudioSampleFormat?)null,
        };

        if (sampleFormat is null)
        {
            return WavHeaderInfo.Invalid($"unsupported WAV format tag {formatTag}");
        }

        if (channels == 0 || sampleRate == 0)
        {
            return WavHeaderInfo.Invalid("header declares zero channels or a zero sample rate");
        }

        if (bitsPerSample is not (8 or 16 or 24 or 32 or 64))
        {
            return WavHeaderInfo.Invalid($"unsupported bit depth {bitsPerSample}");
        }

        AudioFormat format;
        try
        {
            format = new AudioFormat((int)sampleRate, channels, bitsPerSample, sampleFormat.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return WavHeaderInfo.Invalid(ex.Message);
        }

        return WavHeaderInfo.Valid(format, declaredDataBytes);
    }

    private static void WriteHeader(Span<byte> header, AudioFormat format, long dataBytes)
    {
        Encoding.ASCII.GetBytes("RIFF", header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[RiffChunkSizeOffset..], (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVE", header[8..]);
        Encoding.ASCII.GetBytes("fmt ", header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[20..],
            format.SampleFormat == AudioSampleFormat.Pcm ? FormatPcm : FormatIeeeFloat);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)format.AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)format.BitsPerSample);
        Encoding.ASCII.GetBytes("data", header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[DataChunkSizeOffset..], (uint)dataBytes);
    }

    private static bool Matches(ReadOnlySpan<byte> buffer, int offset, string ascii)
    {
        if (buffer.Length < offset + ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (buffer[offset + i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateDataLength(long dataBytes)
    {
        if (dataBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBytes), dataBytes, "Data length must not be negative.");
        }

        if (dataBytes > MaxDataBytes)
        {
            // A single chunk this large cannot be described by a RIFF size field. It
            // also cannot happen with the 60-second default, so treat it as a defect
            // instead of silently writing an unreadable header.
            throw new ArgumentOutOfRangeException(
                nameof(dataBytes),
                dataBytes,
                $"A WAV chunk cannot exceed {MaxDataBytes} data bytes.");
        }
    }
}

/// <summary>Outcome of parsing a WAV header.</summary>
public sealed class WavHeaderInfo
{
    private WavHeaderInfo()
    {
    }

    public bool IsValid { get; private init; }

    public AudioFormat? Format { get; private init; }

    public long DeclaredDataBytes { get; private init; }

    public string? Error { get; private init; }

    public static WavHeaderInfo Valid(AudioFormat format, long declaredDataBytes) => new()
    {
        IsValid = true,
        Format = format,
        DeclaredDataBytes = declaredDataBytes,
    };

    public static WavHeaderInfo Invalid(string error) => new()
    {
        IsValid = false,
        Error = error,
    };
}
