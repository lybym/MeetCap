namespace MeetCap.AudioPipeline.Wave;

/// <summary>Reads only a requested PCM/float WAV time range, downmixed to mono.</summary>
public static class WavSampleReader
{
    public static (float[] Samples, int SampleRate) ReadRange(string path, long startMs, long endMs)
    {
        if (startMs < 0 || endMs <= startMs) throw new FormatException("Invalid WAV sample range.");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new FormatException("Not a RIFF file.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new FormatException("Not a WAVE file.");
        int rate = 0, channels = 0, bits = 0, align = 0;
        ushort encoding = 0;
        long dataOffset = 0, dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var offset = stream.Position;
            if (size > stream.Length - offset) throw new FormatException("Truncated WAV chunk.");
            if (id == "fmt ")
            {
                if (size < 16) throw new FormatException("Invalid WAV format chunk.");
                encoding = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                align = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (encoding == 0xfffe)
                {
                    if (size < 40) throw new FormatException("Invalid extensible WAV format.");
                    stream.Position = offset + 24;
                    var subtype = new Guid(reader.ReadBytes(16));
                    encoding = subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1
                        : subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
                }
            }
            else if (id == "data")
            {
                dataOffset = offset;
                dataBytes = size;
            }

            stream.Position = offset + size + (size % 2);
        }

        if (rate <= 0 || channels <= 0 || align != channels * (bits / 8) || dataOffset == 0 ||
            !(encoding == 1 && bits is 16 or 24 or 32 || encoding == 3 && bits == 32) || dataBytes % align != 0)
            throw new FormatException("Unsupported or invalid PCM/float WAV.");

        var first = checked(startMs * rate / 1000);
        var last = checked(endMs * rate / 1000);
        if (last > dataBytes / align || last <= first) throw new FormatException("WAV does not cover the requested range.");
        var result = new float[checked((int)(last - first))];
        stream.Position = dataOffset + first * align;
        for (var i = 0; i < result.Length; i++)
        {
            double sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                var value = bits switch
                {
                    16 => reader.ReadInt16() / 32768f,
                    24 => ReadInt24(reader) / 8388608f,
                    32 when encoding == 3 => reader.ReadSingle(),
                    _ => reader.ReadInt32() / 2147483648f,
                };
                if (!float.IsFinite(value)) throw new FormatException("WAV contains non-finite samples.");
                sum += value;
            }
            result[i] = (float)(sum / channels);
        }
        return (result, rate);
    }

    private static int ReadInt24(BinaryReader reader)
    {
        var value = reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16;
        return (value << 8) >> 8;
    }
}
