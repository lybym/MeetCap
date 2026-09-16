using System.Buffers.Binary;
using System.Text;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// The chunk lifecycle from docs/ARCHITECTURE.md section 9: durable chunks are only
/// ever produced by patch-then-validate-then-rename, and everything else leaves a
/// recoverable <c>.part</c> behind.
/// </summary>
public class WaveChunkWriterTests : IDisposable
{
    private static readonly AudioFormat Mono48k = new(48_000, 1, 16, AudioSampleFormat.Pcm);

    /// <summary>One second of 48 kHz mono 16-bit audio.</summary>
    private const int OneSecondBytes = 96_000;

    private readonly string _directory;

    public WaveChunkWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "meetcap-wav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private string PartPath => Path.Combine(_directory, "000001.wav.part");

    private string FinalPath => Path.Combine(_directory, "000001.wav");

    [Fact]
    public void NewChunk_IsCreatedAsAPartFileWithACompletePlaceholderHeader()
    {
        using var writer = CreateWriter(capacityBytes: 2 * OneSecondBytes);

        Assert.True(File.Exists(PartPath));
        Assert.False(File.Exists(FinalPath));
        Assert.Equal(WavHeader.Size, new FileInfo(PartPath).Length);

        var parsed = WavHeader.Parse(ReadAll(PartPath));
        Assert.True(parsed.IsValid);
        Assert.Equal(0, parsed.DeclaredDataBytes);
    }

    [Fact]
    public void Append_StopsAtCapacitySoTheCallerCanRotateChunks()
    {
        using var writer = CreateWriter(capacityBytes: 100);

        var written = writer.Append(new byte[256]);

        Assert.Equal(100, written);
        Assert.Equal(100, writer.DataBytesWritten);
        Assert.True(writer.IsFull);
    }

    [Fact]
    public void Close_PatchesFlushesValidatesAndRenames()
    {
        var writer = CreateWriter(capacityBytes: 2 * OneSecondBytes);
        writer.Append(RandomBytes(OneSecondBytes));

        var result = writer.Close(DateTimeOffset.UnixEpoch);

        Assert.False(File.Exists(PartPath));
        Assert.True(File.Exists(FinalPath));
        Assert.Equal(FinalPath, result.FinalPath);
        Assert.Equal(OneSecondBytes, result.DataBytes);

        // Independently re-read the file rather than trusting the writer's own types.
        var bytes = ReadAll(FinalPath);
        Assert.Equal(WavHeader.Size + OneSecondBytes, bytes.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(36u + OneSecondBytes, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(OneSecondBytes, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    [Fact]
    public void Close_ValidatesTheWholeChunk()
    {
        var writer = CreateWriter(capacityBytes: 2 * OneSecondBytes);
        writer.Append(RandomBytes(OneSecondBytes));
        writer.Close(DateTimeOffset.UnixEpoch);

        var validation = WaveChunkValidator.ValidateClosedFile(FinalPath, Mono48k);

        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal(OneSecondBytes, validation.DataBytes);
    }

    [Fact]
    public void Dispose_WithoutClose_LeavesARecoverablePartFile()
    {
        var bytes = RandomBytes(2 * OneSecondBytes);
        var writer = CreateWriter(capacityBytes: 4 * OneSecondBytes);
        writer.Append(bytes);

        // This is what a killed process leaves behind: no rename, no patched header.
        writer.Dispose();

        Assert.True(File.Exists(PartPath));
        Assert.False(File.Exists(FinalPath));

        var fileBytes = ReadAll(PartPath);
        Assert.Equal(WavHeader.Size + bytes.Length, fileBytes.Length);

        // The data is all there; only the header's size fields are stale, which is
        // exactly what startup recovery repairs.
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(40));
        Assert.Equal(0u, declared);
        Assert.Equal(bytes, fileBytes[WavHeader.Size..]);
    }

    [Fact]
    public void Close_Twice_IsRejected()
    {
        var writer = CreateWriter(capacityBytes: 1_000);
        writer.Append(RandomBytes(100));
        writer.Close(DateTimeOffset.UnixEpoch);

        Assert.Throws<ObjectDisposedException>(() => writer.Close(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Append_AfterClose_IsRejected()
    {
        var writer = CreateWriter(capacityBytes: 1_000);
        writer.Append(RandomBytes(100));
        writer.Close(DateTimeOffset.UnixEpoch);

        Assert.Throws<ObjectDisposedException>(() => writer.Append(RandomBytes(10)));
    }

    [Fact]
    public void Constructor_RejectsCapacityThatIsNotFrameAligned()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WaveChunkWriter(PartPath, FinalPath, Mono48k, capacityBytes: 3, sequence: 1));
    }

    [Fact]
    public void Constructor_RejectsReusingAnExistingPartFile()
    {
        using (CreateWriter(capacityBytes: 1_000))
        {
            // Disposing keeps the .part file, which is the crash-equivalent outcome.
        }

        // Recreating the same chunk must not silently overwrite recoverable audio.
        Assert.Throws<IOException>(
            () => new WaveChunkWriter(PartPath, FinalPath, Mono48k, capacityBytes: 1_000, sequence: 1));
    }

    [Fact]
    public void ValidateClosedFile_DetectsATruncatedFile()
    {
        var writer = CreateWriter(capacityBytes: 2 * OneSecondBytes);
        writer.Append(RandomBytes(OneSecondBytes));
        writer.Close(DateTimeOffset.UnixEpoch);

        using (var stream = new FileStream(FinalPath, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(WavHeader.Size + 1_000);
        }

        var validation = WaveChunkValidator.ValidateClosedFile(FinalPath, Mono48k);

        Assert.False(validation.IsValid);
        Assert.Contains("declares", validation.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateClosedFile_DetectsAFormatMismatch()
    {
        var writer = CreateWriter(capacityBytes: 2 * OneSecondBytes);
        writer.Append(RandomBytes(OneSecondBytes));
        writer.Close(DateTimeOffset.UnixEpoch);

        var other = new AudioFormat(44_100, 1, 16, AudioSampleFormat.Pcm);
        var validation = WaveChunkValidator.ValidateClosedFile(FinalPath, other);

        Assert.False(validation.IsValid);
        Assert.Contains("does not match", validation.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateClosedFile_ReportsAMissingFile()
    {
        var validation = WaveChunkValidator.ValidateClosedFile(
            Path.Combine(_directory, "missing.wav"),
            Mono48k);

        Assert.False(validation.IsValid);
        Assert.Contains("does not exist", validation.Error!, StringComparison.Ordinal);
    }

    private WaveChunkWriter CreateWriter(long capacityBytes)
        => new(PartPath, FinalPath, Mono48k, capacityBytes, sequence: 1);

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static byte[] ReadAll(string path)
    {
        // The writer keeps the .part file open for writing, so read with read/write
        // sharing instead of taking an exclusive read handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
