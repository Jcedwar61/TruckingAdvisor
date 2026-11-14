using System.IO.Compression;

namespace TruckingAdvisor.Telemetry;

/// <summary>
/// Records raw telemetry blocks to a compressed file and reads them back. A recording holds only
/// what the game's telemetry plugin publishes (truck, speed, cargo, cities, ...): no personal data.
/// Used to reproduce bugs: a recorded drive can be replayed in the app (--replay file) and in tests.
///
/// Format: GZip stream of "TAREC1\n", then frames of [int64 unix ms][int32 length][bytes].
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    private static readonly byte[] Magic = "TAREC1\n"u8.ToArray();
    private readonly BinaryWriter _writer;
    private readonly FileStream _file;
    private readonly long _maxBytes;
    private readonly object _lock = new();
    private bool _closed;

    public string Path { get; }
    public int Frames { get; private set; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    /// <summary>True once the size limit was reached and the recording closed itself.</summary>
    public bool Full { get; private set; }

    public TelemetryRecorder(string path, long maxBytes = 200_000_000)
    {
        Path = path;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _file = File.Create(path);
        _writer = new BinaryWriter(new GZipStream(_file, CompressionLevel.Fastest));
        _writer.Write(Magic);
    }

    public void Write(DateTimeOffset time, ReadOnlySpan<byte> block)
    {
        lock (_lock)
        {
            if (_closed) return;
            _writer.Write(time.ToUnixTimeMilliseconds());
            _writer.Write(block.Length);
            _writer.Write(block);
            Frames++;
            if (_file.Length > _maxBytes) { Full = true; Close(); }
        }
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _writer.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_lock) Close();
    }

    /// <summary>Reads every frame of a recording, oldest first.</summary>
    public static IEnumerable<(DateTimeOffset Time, byte[] Block)> Read(string path)
    {
        using var file = File.OpenRead(path);
        foreach (var frame in Read(file)) yield return frame;
    }

    public static IEnumerable<(DateTimeOffset Time, byte[] Block)> Read(Stream stream)
    {
        using var reader = new BinaryReader(new GZipStream(stream, CompressionMode.Decompress));
        byte[] magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a TruckingAdvisor telemetry recording.");
        while (true)
        {
            long ms;
            int length;
            byte[] block;
            try
            {
                ms = reader.ReadInt64();
                length = reader.ReadInt32();
                if (length is <= 0 or > 1_000_000) yield break;
                block = reader.ReadBytes(length);
                if (block.Length != length) yield break; // cut off mid-frame (app closed while recording)
            }
            catch (EndOfStreamException) { yield break; }
            catch (InvalidDataException) { yield break; }
            yield return (DateTimeOffset.FromUnixTimeMilliseconds(ms), block);
        }
    }
}
